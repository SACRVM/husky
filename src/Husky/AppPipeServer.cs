using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Husky.Protocol;

namespace Husky;

internal sealed class AppPipeServer : IAsyncDisposable
{
    private const int PipeBufferSize = 65_536;

    private readonly NamedPipeServerStream pipe;
    private readonly MessageReader reader;
    private readonly MessageWriter writer;
    private readonly SemaphoreSlim writeMutex = new(initialCount: 1, maxCount: 1);
    private readonly CancellationTokenSource lifetimeCts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> pendingPings = new();
    private Task? receiverLoop;
    private TaskCompletionSource? shutdownAckTcs;
    private string? shutdownAckExpectedFor;
    private bool disposed;

    public string PipeName { get; }
    public string LauncherVersion { get; }
    public ConnectedApp? ConnectedApp { get; private set; }

    /// <summary>
    /// Cached snapshot of the latest known update for this session — populated
    /// by the launcher's polling loop. <c>update-check</c> replies, and
    /// <c>update-now</c> triggers, are answered against this. Null means "no
    /// known update."
    /// </summary>
    public UpdateStatusPayload? CurrentUpdateStatus { get; private set; }

    /// <summary>
    /// Fires once for every message received from the hosted app — heartbeat,
    /// pong, shutdown-ack, or unknown — *before* type-specific dispatch. The
    /// watchdog uses this to refresh its <c>lastActivity</c> timestamp per
    /// LEASH §8.1.
    /// </summary>
    public Action? OnActivity { get; set; }

    /// <summary>
    /// Fires when the hosted app sends <c>update-now</c>. The launcher is
    /// expected to inspect <see cref="CurrentUpdateStatus"/> and start the
    /// update flow if an update is cached, or log a warning if not.
    /// </summary>
    public Action? OnUpdateNowRequested { get; set; }

    /// <summary>
    /// Fires when an app-side request is downgraded because the app did not
    /// declare a required capability — currently only the manual-updates gate
    /// (LEASH §3.5.13). The argument is a ready-to-render warning sentence
    /// (no timestamp, no source prefix). LauncherRuntime / AppSessionLauncher
    /// route this through <c>ConsoleOutput</c> so the human running Husky
    /// notices a misbehaving or mismatched-version app.
    /// </summary>
    public Action<string>? OnCapabilityWarning { get; set; }

    /// <summary>
    /// Fires when a <c>pong</c> arrives from the hosted app, carrying the
    /// freshly observed health status (<c>healthy</c> / <c>degraded</c> /
    /// <c>unhealthy</c>). The TUI header subscribes to this so the right
    /// slot reflects live state; line mode ignores it.
    /// </summary>
    public Action<string>? OnHealthChanged { get; set; }

    /// <summary>
    /// Fires when the hosted app sends <c>update-check</c>. The launcher is
    /// expected to poll the source synchronously, update
    /// <see cref="CurrentUpdateStatus"/>, and return the fresh payload to
    /// be sent in the <c>update-status</c> reply (LEASH §3.5.9). When the
    /// callback is unset or throws, the handler falls back to
    /// <see cref="CurrentUpdateStatus"/> so the RPC always resolves.
    /// </summary>
    public Func<CancellationToken, Task<UpdateStatusPayload>>? OnUpdateCheckRequested { get; set; }

    internal Task? ReceiverTask => receiverLoop;

    private AppPipeServer(string pipeName, NamedPipeServerStream pipe, string launcherVersion)
    {
        PipeName = pipeName;
        this.pipe = pipe;
        LauncherVersion = launcherVersion;
        reader = new MessageReader(pipe, leaveOpen: true);
        writer = new MessageWriter(pipe, leaveOpen: true);
    }

    public static AppPipeServer Create(string pipeName, string launcherVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherVersion);

        NamedPipeServerStream pipe = CreatePipe(pipeName);
        return new AppPipeServer(pipeName, pipe, launcherVersion);
    }

    public async Task AcceptAndHandshakeAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (receiverLoop is not null)
            throw new InvalidOperationException("Handshake has already been performed.");

        MessageEnvelope helloEnvelope = await ConnectAndReadHelloAsync(timeout, ct).ConfigureAwait(false);
        HelloPayload hello = ParseHelloPayload(helloEnvelope);

        if (hello.ProtocolVersion != ProtocolVersion.Current)
        {
            string reason =
                $"protocol version mismatch (got {hello.ProtocolVersion}, expected {ProtocolVersion.Current})";
            await SendWelcomeAsync(helloEnvelope.Id, accepted: false, reason: reason, ct).ConfigureAwait(false);
            throw new InvalidOperationException($"Refused hosted app: {reason}.");
        }

        await SendWelcomeAsync(helloEnvelope.Id, accepted: true, reason: null, ct).ConfigureAwait(false);

        IReadOnlyList<string> appCapabilities = hello.Capabilities ?? [];
        string effectiveMode = ResolveInitialUpdateMode(appCapabilities, hello.Preferences, hello.AppName);

        ConnectedApp = new ConnectedApp(
            Name: hello.AppName,
            // Defensive normalize: an older Husky.Client (≤0.1.3) may send a
            // 4-part FileVersion or '+commit' suffix. Strip to X.Y.Z so every
            // downstream display (TUI header, "is up." log) stays clean.
            Version: VersionFormat.ToDisplay(hello.AppVersion),
            Pid: hello.Pid,
            Capabilities: appCapabilities,
            UpdateMode: effectiveMode);
        receiverLoop = Task.Run(() => ReceiverLoopAsync(lifetimeCts.Token), CancellationToken.None);
    }

    private string ResolveInitialUpdateMode(
        IReadOnlyList<string> appCapabilities, HelloPreferences? preferences, string appName)
    {
        // LEASH §3.5.13 capability gating: a non-default updateMode is only
        // honoured when the app declared the manual-updates capability.
        bool supportsManual = appCapabilities.Contains(Protocol.Capabilities.ManualUpdates);
        string requested = preferences?.UpdateMode ?? UpdateModes.Auto;

        if (requested == UpdateModes.Manual && !supportsManual)
        {
            OnCapabilityWarning?.Invoke(
                $"{appName} requested manual update mode but did not declare the " +
                $"'{Protocol.Capabilities.ManualUpdates}' capability — falling back to auto.");
            return UpdateModes.Auto;
        }

        return requested == UpdateModes.Manual ? UpdateModes.Manual : UpdateModes.Auto;
    }

    // One deadline covers connect and hello (LEASH §5.4): an app that
    // connects and then hangs must not stall the start forever.
    private async Task<MessageEnvelope> ConnectAndReadHelloAsync(TimeSpan timeout, CancellationToken ct)
    {
        using CancellationTokenSource deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(timeout);

        bool connected = false;
        try
        {
            await pipe.WaitForConnectionAsync(deadlineCts.Token).ConfigureAwait(false);
            connected = true;
            return await ReadHelloAsync(deadlineCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(connected
                ? $"Hosted app connected but sent no hello within {timeout.TotalSeconds:F0}s."
                : $"Hosted app did not connect to the pipe within {timeout.TotalSeconds:F0}s.");
        }
    }

    private async Task<MessageEnvelope> ReadHelloAsync(CancellationToken ct)
    {
        MessageEnvelope? envelope = await reader.ReadAsync(ct).ConfigureAwait(false);
        if (envelope is null)
            throw new IOException("Hosted app closed the pipe before sending hello.");
        if (envelope.Type != MessageTypes.Hello)
            throw new InvalidOperationException(
                $"Expected '{MessageTypes.Hello}' from hosted app; got '{envelope.Type}'.");
        return envelope;
    }

    private static HelloPayload ParseHelloPayload(MessageEnvelope envelope)
    {
        HelloPayload? payload = envelope.Data?.Deserialize(HuskyJsonContext.Default.HelloPayload);
        if (payload is null)
            throw new InvalidOperationException("Hosted app's hello message is missing its payload.");
        return payload;
    }

    public async Task<bool> SendPingAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (receiverLoop is null)
            throw new InvalidOperationException("Cannot send ping before the handshake completes.");

        string id = Guid.NewGuid().ToString("D");
        TaskCompletionSource pongTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingPings[id] = pongTcs;

        try
        {
            MessageEnvelope envelope = new() { Id = id, Type = MessageTypes.Ping };
            await WriteAsync(envelope, ct).ConfigureAwait(false);

            try
            {
                await pongTcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException) { return false; }
        }
        finally
        {
            pendingPings.TryRemove(id, out _);
        }
    }

    public void SetCurrentUpdateStatus(UpdateStatusPayload? status)
    {
        CurrentUpdateStatus = status;
    }

    public async Task PushUpdateAvailableAsync(UpdateAvailablePayload payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (receiverLoop is null)
            throw new InvalidOperationException("Cannot push update-available before the handshake completes.");

        JsonElement data = JsonSerializer.SerializeToElement(payload, HuskyJsonContext.Default.UpdateAvailablePayload);
        MessageEnvelope envelope = new()
        {
            Type = MessageTypes.UpdateAvailable,
            Data = data,
        };

        await WriteAsync(envelope, ct).ConfigureAwait(false);
    }

    public async Task SendShutdownAsync(
        string reason,
        TimeSpan totalTimeout,
        TimeSpan ackTimeout,
        CancellationToken ct = default)
    {
        if (receiverLoop is null)
            throw new InvalidOperationException("Cannot send shutdown before the handshake completes.");

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        string id = Guid.NewGuid().ToString("D");
        TaskCompletionSource ackTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        shutdownAckTcs = ackTcs;
        shutdownAckExpectedFor = id;

        ShutdownPayload payload = new(reason, (int)totalTimeout.TotalSeconds);
        JsonElement data = JsonSerializer.SerializeToElement(payload, HuskyJsonContext.Default.ShutdownPayload);
        MessageEnvelope envelope = new()
        {
            Id = id,
            Type = MessageTypes.Shutdown,
            Data = data,
        };

        await WriteAsync(envelope, ct).ConfigureAwait(false);
        await ackTcs.Task.WaitAsync(ackTimeout, ct).ConfigureAwait(false);
    }

    private async Task SendWelcomeAsync(
        string? replyTo, bool accepted, string? reason, CancellationToken ct)
    {
        WelcomePayload payload = new(
            ProtocolVersion: ProtocolVersion.Current,
            LauncherVersion: LauncherVersion,
            Accepted: accepted,
            Reason: reason,
            Capabilities: accepted ? LauncherCapabilities.All : null);

        JsonElement data = JsonSerializer.SerializeToElement(payload, HuskyJsonContext.Default.WelcomePayload);
        MessageEnvelope envelope = new()
        {
            Id = Guid.NewGuid().ToString("D"),
            ReplyTo = replyTo,
            Type = MessageTypes.Welcome,
            Data = data,
        };

        await WriteAsync(envelope, ct).ConfigureAwait(false);
    }

    private async Task WriteAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        await writeMutex.WaitAsync(ct).ConfigureAwait(false);
        try { await writer.WriteAsync(envelope, ct).ConfigureAwait(false); }
        finally { writeMutex.Release(); }
    }

    private async Task ReceiverLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                MessageEnvelope? envelope;
                try
                {
                    envelope = await reader.ReadAsync(ct).ConfigureAwait(false);
                }
                catch (IOException) when (!ct.IsCancellationRequested)
                {
                    return;
                }

                if (envelope is null) return;

                // §8.1: every received message resets the watchdog's activity
                // timestamp, regardless of type.
                OnActivity?.Invoke();

                switch (envelope.Type)
                {
                    case MessageTypes.ShutdownAck
                        when envelope.ReplyTo is { Length: > 0 } shutdownReplyTo
                             && shutdownReplyTo == shutdownAckExpectedFor:
                        shutdownAckTcs?.TrySetResult();
                        break;

                    case MessageTypes.Pong
                        when envelope.ReplyTo is { Length: > 0 } pongReplyTo
                             && pendingPings.TryGetValue(pongReplyTo, out TaskCompletionSource? pongTcs):
                        ProcessPongHealth(envelope);
                        pongTcs.TrySetResult();
                        break;

                    case MessageTypes.UpdateCheck:
                        await HandleUpdateCheckAsync(envelope, ct).ConfigureAwait(false);
                        break;

                    case MessageTypes.UpdateNow:
                        OnUpdateNowRequested?.Invoke();
                        break;

                    case MessageTypes.SetUpdateMode:
                        await HandleSetUpdateModeAsync(envelope, ct).ConfigureAwait(false);
                        break;

                    // heartbeat / unknown types: drop per §3.6.
                }
            }
        }
        catch (OperationCanceledException) { /* normal */ }
    }

    private void ProcessPongHealth(MessageEnvelope envelope)
    {
        if (OnHealthChanged is not { } callback) return;

        PongPayload? payload;
        try
        {
            payload = envelope.Data?.Deserialize(HuskyJsonContext.Default.PongPayload);
        }
        catch
        {
            return;
        }
        if (payload?.Status is { Length: > 0 } status)
        {
            try { callback(status); }
            catch { /* subscriber exceptions don't take down the pipe loop */ }
        }
    }

    private async Task HandleUpdateCheckAsync(MessageEnvelope request, CancellationToken ct)
    {
        UpdateStatusPayload reply = await ResolveUpdateCheckReplyAsync(ct).ConfigureAwait(false);

        JsonElement data = JsonSerializer.SerializeToElement(reply, HuskyJsonContext.Default.UpdateStatusPayload);
        MessageEnvelope envelope = new()
        {
            Id = Guid.NewGuid().ToString("D"),
            ReplyTo = request.Id,
            Type = MessageTypes.UpdateStatus,
            Data = data,
        };

        await WriteAsync(envelope, ct).ConfigureAwait(false);
    }

    private async Task<UpdateStatusPayload> ResolveUpdateCheckReplyAsync(CancellationToken ct)
    {
        if (OnUpdateCheckRequested is { } refresh)
        {
            try
            {
                return await refresh(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Fall through to the cache so the RPC still resolves with
                // the best-known truth instead of stranding the caller.
            }
        }

        return CurrentUpdateStatus
            ?? new UpdateStatusPayload(Available: false, CurrentVersion: ConnectedApp?.Version ?? "0.0.0");
    }

    private async Task HandleSetUpdateModeAsync(MessageEnvelope request, CancellationToken ct)
    {
        SetUpdateModePayload? payload = request.Data?.Deserialize(HuskyJsonContext.Default.SetUpdateModePayload);
        string requested = payload?.Mode ?? UpdateModes.Auto;

        // Capability gate (§3.5.13): if the app didn't declare manual-updates,
        // any non-default request is silently downgraded to auto and the ack
        // echoes auto so the app sees the no-op.
        bool supportsManual = ConnectedApp?.SupportsManualUpdates == true;
        string accepted = (requested == UpdateModes.Manual && supportsManual)
            ? UpdateModes.Manual
            : UpdateModes.Auto;

        if (requested == UpdateModes.Manual && !supportsManual)
        {
            OnCapabilityWarning?.Invoke(
                $"{ConnectedApp?.Name ?? "app"} sent set-update-mode=manual without the " +
                $"'{Protocol.Capabilities.ManualUpdates}' capability — ignored, mode stays auto.");
        }

        if (ConnectedApp is not null)
        {
            ConnectedApp = ConnectedApp with { UpdateMode = accepted };
        }

        UpdateModeAckPayload ack = new(Mode: accepted);
        JsonElement data = JsonSerializer.SerializeToElement(ack, HuskyJsonContext.Default.UpdateModeAckPayload);
        MessageEnvelope envelope = new()
        {
            Id = Guid.NewGuid().ToString("D"),
            ReplyTo = request.Id,
            Type = MessageTypes.UpdateModeAck,
            Data = data,
        };

        await WriteAsync(envelope, ct).ConfigureAwait(false);
    }

    private static NamedPipeServerStream CreatePipe(string pipeName)
    {
        if (OperatingSystem.IsWindows())
            return CreateWindowsPipe(pipeName);

        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: PipeBufferSize,
            outBufferSize: PipeBufferSize);
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateWindowsPipe(string pipeName)
    {
        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Could not determine current user SID.");

        PipeSecurity security = new();
        security.AddAccessRule(new PipeAccessRule(
            owner,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: PipeBufferSize,
            outBufferSize: PipeBufferSize,
            pipeSecurity: security);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;

        try { lifetimeCts.Cancel(); }
        catch (ObjectDisposedException) { }

        if (receiverLoop is not null)
        {
            try { await receiverLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { /* loop did not drain — proceed */ }
            catch (OperationCanceledException) { /* normal */ }
        }

        reader.Dispose();
        await writer.DisposeAsync().ConfigureAwait(false);
        await pipe.DisposeAsync().ConfigureAwait(false);

        writeMutex.Dispose();
        lifetimeCts.Dispose();
    }
}
