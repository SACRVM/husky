using System.Text.Json;
using Husky;
using Husky.Protocol;

namespace Husky.Tests;

public sealed class AppPipeServerTests
{
    [Fact]
    public async Task Handshake_succeeds_when_app_sends_valid_hello_and_populates_ConnectedApp()
    {
        await using LauncherPipeHarness h = await LauncherPipeHarness.CreateConnectedAsync();

        Task accept = h.Server.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));

        await SendHelloAsync(h.ClientWriter, "test-app", appVersion: "1.2.3", pid: 999);

        MessageEnvelope welcome = await ReadAsync(h.ClientReader);
        Assert.Equal(MessageTypes.Welcome, welcome.Type);
        WelcomePayload? payload = welcome.Data?.Deserialize(HuskyJsonContext.Default.WelcomePayload);
        Assert.NotNull(payload);
        Assert.True(payload!.Accepted);
        Assert.Null(payload.Reason);
        Assert.Equal(ProtocolVersion.Current, payload.ProtocolVersion);
        Assert.Equal("1.0.0-test", payload.LauncherVersion);

        await accept;

        Assert.NotNull(h.Server.ConnectedApp);
        Assert.Equal("test-app", h.Server.ConnectedApp!.Name);
        Assert.Equal("1.2.3", h.Server.ConnectedApp.Version);
        Assert.Equal(999, h.Server.ConnectedApp.Pid);
    }

    [Fact]
    public async Task Welcome_advertises_launcher_capabilities_on_accepted_handshake()
    {
        await using LauncherPipeHarness h = await LauncherPipeHarness.CreateConnectedAsync();

        Task accept = h.Server.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));

        await SendHelloAsync(h.ClientWriter, "test-app", "1.0.0", 1);

        MessageEnvelope welcome = await ReadAsync(h.ClientReader);
        WelcomePayload payload = welcome.Data!.Value.Deserialize(HuskyJsonContext.Default.WelcomePayload)!;

        Assert.NotNull(payload.Capabilities);
        Assert.Contains(Capabilities.ManualUpdates, payload.Capabilities!);

        await accept;
    }

    [Fact]
    public async Task Welcome_omits_capabilities_when_handshake_is_rejected()
    {
        await using LauncherPipeHarness h = await LauncherPipeHarness.CreateConnectedAsync();

        Task accept = h.Server.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));

        await SendHelloAsync(
            h.ClientWriter, "test-app", "1.0.0", 1, protocolVersion: ProtocolVersion.Current + 1);

        MessageEnvelope welcome = await ReadAsync(h.ClientReader);
        WelcomePayload payload = welcome.Data!.Value.Deserialize(HuskyJsonContext.Default.WelcomePayload)!;

        Assert.False(payload.Accepted);
        Assert.Null(payload.Capabilities);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await accept);
    }

    [Fact]
    public async Task ConnectedApp_records_app_declared_capabilities_and_initial_mode()
    {
        await using LauncherPipeHarness h = await LauncherPipeHarness.CreateConnectedAsync();

        Task accept = h.Server.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));

        await SendHelloAsync(
            h.ClientWriter, "test-app", "1.0.0", 1,
            capabilities: [Capabilities.ManualUpdates],
            preferences: new HelloPreferences(UpdateMode: UpdateModes.Manual));

        await ReadAsync(h.ClientReader); // welcome
        await accept;

        Assert.True(h.Server.ConnectedApp!.SupportsManualUpdates);
        Assert.Equal(UpdateModes.Manual, h.Server.ConnectedApp.UpdateMode);
    }

    [Fact]
    public async Task ConnectedApp_falls_back_to_auto_when_app_lacks_manual_updates_capability()
    {
        // LEASH §3.5.13 capability gating: a manual mode preference is ignored
        // when the app did not declare manual-updates.
        await using LauncherPipeHarness h = await LauncherPipeHarness.CreateConnectedAsync();

        List<string> warnings = [];
        h.Server.OnCapabilityWarning = warnings.Add;

        Task accept = h.Server.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));

        await SendHelloAsync(
            h.ClientWriter, "test-app", "1.0.0", 1,
            capabilities: [],
            preferences: new HelloPreferences(UpdateMode: UpdateModes.Manual));

        await ReadAsync(h.ClientReader); // welcome
        await accept;

        Assert.False(h.Server.ConnectedApp!.SupportsManualUpdates);
        Assert.Equal(UpdateModes.Auto, h.Server.ConnectedApp.UpdateMode);
        Assert.Single(warnings);
        Assert.Contains("test-app", warnings[0]);
        Assert.Contains(Capabilities.ManualUpdates, warnings[0]);
    }

    [Fact]
    public async Task ConnectedApp_does_not_warn_when_capability_present_or_default_mode()
    {
        await using LauncherPipeHarness h = await LauncherPipeHarness.CreateConnectedAsync();

        List<string> warnings = [];
        h.Server.OnCapabilityWarning = warnings.Add;

        Task accept = h.Server.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));
        await SendHelloAsync(
            h.ClientWriter, "test-app", "1.0.0", 1,
            capabilities: [Capabilities.ManualUpdates],
            preferences: new HelloPreferences(UpdateMode: UpdateModes.Manual));
        await ReadAsync(h.ClientReader); // welcome
        await accept;

        Assert.Empty(warnings);
    }

    [Fact]
    public async Task ConnectedApp_defaults_to_auto_when_no_preferences_supplied()
    {
        await using LauncherPipeHarness h = await LauncherPipeHarness.CreateConnectedAsync();

        Task accept = h.Server.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));

        await SendHelloAsync(h.ClientWriter, "test-app", "1.0.0", 1);

        await ReadAsync(h.ClientReader); // welcome
        await accept;

        Assert.Equal(UpdateModes.Auto, h.Server.ConnectedApp!.UpdateMode);
    }

    [Fact]
    public async Task UpdateCheck_replies_with_cached_status_when_update_is_known()
    {
        await using LauncherPipeHarness h = await LauncherPipeHarness.CreateConnectedAsync();

        Task accept = h.Server.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));
        await SendHelloAsync(h.ClientWriter, "test-app", "1.0.0", 1,
            capabilities: [Capabilities.ManualUpdates]);
        await ReadAsync(h.ClientReader); // welcome
        await accept;

        h.Server.SetCurrentUpdateStatus(new UpdateStatusPayload(
            Available: true,
            CurrentVersion: "1.0.0",
            NewVersion: "2.0.0",
            DownloadSizeBytes: 12345));

        string requestId = Guid.NewGuid().ToString("D");
        await h.ClientWriter.WriteAsync(new MessageEnvelope
        {
            Id = requestId,
            Type = MessageTypes.UpdateCheck,
        });

        MessageEnvelope reply = await ReadAsync(h.ClientReader);
        Assert.Equal(MessageTypes.UpdateStatus, reply.Type);
        Assert.Equal(requestId, reply.ReplyTo);

        UpdateStatusPayload? payload = reply.Data?.Deserialize(HuskyJsonContext.Default.UpdateStatusPayload);
        Assert.NotNull(payload);
        Assert.True(payload!.Available);
        Assert.Equal("2.0.0", payload.NewVersion);
        Assert.Equal(12345L, payload.DownloadSizeBytes);
    }

    [Fact]
    public async Task UpdateCheck_replies_with_no_update_when_cache_is_empty()
    {
        await using LauncherPipeHarness h = await LauncherPipeHarness.CreateConnectedAsync();

        Task accept = h.Server.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));
        await SendHelloAsync(h.ClientWriter, "test-app", "1.4.2", 1,
            capabilities: [Capabilities.ManualUpdates]);
        await ReadAsync(h.ClientReader); // welcome
        await accept;

        await h.ClientWriter.WriteAsync(new MessageEnvelope
        {
            Id = Guid.NewGuid().ToString("D"),
            Type = MessageTypes.UpdateCheck,
        });

        MessageEnvelope reply = await ReadAsync(h.ClientReader);
        UpdateStatusPayload? payload = reply.Data?.Deserialize(HuskyJsonContext.Default.UpdateStatusPayload);

        Assert.False(payload!.Available);
        Assert.Equal("1.4.2", payload.CurrentVersion);
    }

    [Fact]
    public async Task UpdateCheck_invokes_OnUpdateCheckRequested_and_replies_with_its_payload()
    {
        // LEASH §3.5.9: when the runtime registers a fresh-poll callback, the
        // handler must use its result instead of any pre-set cache, so the RPC
        // returns the truth observed *now* rather than at the last polling tick.
        await using LauncherPipeHarness h = await LauncherPipeHarness.CreateConnectedAsync();

        h.Server.SetCurrentUpdateStatus(new UpdateStatusPayload(
            Available: false, CurrentVersion: "1.0.0"));

        UpdateStatusPayload fresh = new(
            Available: true,
            CurrentVersion: "1.0.0",
            NewVersion: "9.9.9",
            DownloadSizeBytes: 4242);
        int callCount = 0;
        h.Server.OnUpdateCheckRequested = _ =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(fresh);
        };

        Task accept = h.Server.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));
        await SendHelloAsync(h.ClientWriter, "test-app", "1.0.0", 1,
            capabilities: [Capabilities.ManualUpdates]);
        await ReadAsync(h.ClientReader); // welcome
        await accept;

        string requestId = Guid.NewGuid().ToString("D");
        await h.ClientWriter.WriteAsync(new MessageEnvelope
        {
            Id = requestId,
            Type = MessageTypes.UpdateCheck,
        });

        MessageEnvelope reply = await ReadAsync(h.ClientReader);
        UpdateStatusPayload? payload = reply.Data?.Deserialize(HuskyJsonContext.Default.UpdateStatusPayload);

        Assert.Equal(1, Volatile.Read(ref callCount));
        Assert.Equal(requestId, reply.ReplyTo);
        Assert.True(payload!.Available);
        Assert.Equal("9.9.9", payload.NewVersion);
        Assert.Equal(4242L, payload.DownloadSizeBytes);
    }

    [Fact]
    public async Task UpdateCheck_falls_back_to_cache_when_OnUpdateCheckRequested_throws()
    {
        // Source unreachable on the on-demand poll: the RPC must still resolve
        // with the best-known truth (last cached status) so the caller doesn't
        // observe a wire-level failure for a recoverable network blip.
        await using LauncherPipeHarness h = await LauncherPipeHarness.CreateConnectedAsync();

        h.Server.SetCurrentUpdateStatus(new UpdateStatusPayload(
            Available: true,
            CurrentVersion: "1.0.0",
            NewVersion: "1.1.0",
            DownloadSizeBytes: 100));
        h.Server.OnUpdateCheckRequested = _ => throw new HttpRequestException("boom");

        Task accept = h.Server.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));
        await SendHelloAsync(h.ClientWriter, "test-app", "1.0.0", 1,
            capabilities: [Capabilities.ManualUpdates]);
        await ReadAsync(h.ClientReader); // welcome
        await accept;

        await h.ClientWriter.WriteAsync(new MessageEnvelope
        {
            Id = Guid.NewGuid().ToString("D"),
            Type = MessageTypes.UpdateCheck,
        });

        MessageEnvelope reply = await ReadAsync(h.ClientReader);
        UpdateStatusPayload? payload = reply.Data?.Deserialize(HuskyJsonContext.Default.UpdateStatusPayload);

        Assert.True(payload!.Available);
        Assert.Equal("1.1.0", payload.NewVersion);
    }

    [Fact]
    public async Task UpdateNow_message_invokes_OnUpdateNowRequested()
    {
        await using LauncherPipeHarness h = await LauncherPipeHarness.CreateConnectedAsync();

        TaskCompletionSource triggered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Server.OnUpdateNowRequested = () => triggered.TrySetResult();

        Task accept = h.Server.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));
        await SendHelloAsync(h.ClientWriter, "test-app", "1.0.0", 1,
            capabilities: [Capabilities.ManualUpdates]);
        await ReadAsync(h.ClientReader); // welcome
        await accept;

        await h.ClientWriter.WriteAsync(new MessageEnvelope { Type = MessageTypes.UpdateNow });

        await triggered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task SetUpdateMode_acks_and_updates_mode_when_capability_present()
    {
        await using LauncherPipeHarness h = await LauncherPipeHarness.CreateConnectedAsync();

        Task accept = h.Server.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));
        await SendHelloAsync(h.ClientWriter, "test-app", "1.0.0", 1,
            capabilities: [Capabilities.ManualUpdates]);
        await ReadAsync(h.ClientReader); // welcome
        await accept;

        Assert.Equal(UpdateModes.Auto, h.Server.ConnectedApp!.UpdateMode);

        SetUpdateModePayload payload = new(Mode: UpdateModes.Manual);
        JsonElement data = JsonSerializer.SerializeToElement(payload, HuskyJsonContext.Default.SetUpdateModePayload);
        string requestId = Guid.NewGuid().ToString("D");
        await h.ClientWriter.WriteAsync(new MessageEnvelope
        {
            Id = requestId,
            Type = MessageTypes.SetUpdateMode,
            Data = data,
        });

        MessageEnvelope ack = await ReadAsync(h.ClientReader);
        Assert.Equal(MessageTypes.UpdateModeAck, ack.Type);
        Assert.Equal(requestId, ack.ReplyTo);

        UpdateModeAckPayload? ackPayload = ack.Data?.Deserialize(HuskyJsonContext.Default.UpdateModeAckPayload);
        Assert.Equal(UpdateModes.Manual, ackPayload!.Mode);
        Assert.Equal(UpdateModes.Manual, h.Server.ConnectedApp!.UpdateMode);
    }

    [Fact]
    public async Task SetUpdateMode_downgrades_manual_to_auto_when_capability_missing()
    {
        await using LauncherPipeHarness h = await LauncherPipeHarness.CreateConnectedAsync();

        List<string> warnings = [];
        h.Server.OnCapabilityWarning = warnings.Add;

        Task accept = h.Server.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));
        await SendHelloAsync(h.ClientWriter, "test-app", "1.0.0", 1, capabilities: []);
        await ReadAsync(h.ClientReader); // welcome
        await accept;

        SetUpdateModePayload payload = new(Mode: UpdateModes.Manual);
        JsonElement data = JsonSerializer.SerializeToElement(payload, HuskyJsonContext.Default.SetUpdateModePayload);
        await h.ClientWriter.WriteAsync(new MessageEnvelope
        {
            Id = Guid.NewGuid().ToString("D"),
            Type = MessageTypes.SetUpdateMode,
            Data = data,
        });

        MessageEnvelope ack = await ReadAsync(h.ClientReader);
        UpdateModeAckPayload? ackPayload = ack.Data?.Deserialize(HuskyJsonContext.Default.UpdateModeAckPayload);

        Assert.Equal(UpdateModes.Auto, ackPayload!.Mode);
        Assert.Equal(UpdateModes.Auto, h.Server.ConnectedApp!.UpdateMode);
        // The hello had no preference (defaults to auto), so the only warning
        // should come from the runtime set-update-mode gate.
        Assert.Single(warnings);
        Assert.Contains("set-update-mode", warnings[0]);
    }

    [Fact]
    public async Task PushUpdateAvailableAsync_sends_unsolicited_message()
    {
        await using LauncherPipeHarness h = await LauncherPipeHarness.CreateConnectedAsync();

        Task accept = h.Server.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));
        await SendHelloAsync(h.ClientWriter, "test-app", "1.0.0", 1,
            capabilities: [Capabilities.ManualUpdates]);
        await ReadAsync(h.ClientReader); // welcome
        await accept;

        UpdateAvailablePayload payload = new(
            CurrentVersion: "1.0.0",
            NewVersion: "1.1.0",
            DownloadSizeBytes: 999);

        await h.Server.PushUpdateAvailableAsync(payload, CancellationToken.None);

        MessageEnvelope received = await ReadAsync(h.ClientReader);
        Assert.Equal(MessageTypes.UpdateAvailable, received.Type);
        Assert.Null(received.Id);
        Assert.Null(received.ReplyTo);

        UpdateAvailablePayload? parsed = received.Data?.Deserialize(HuskyJsonContext.Default.UpdateAvailablePayload);
        Assert.Equal("1.1.0", parsed!.NewVersion);
    }

    [Fact]
    public async Task Welcome_replyTo_matches_hello_id()
    {
        await using LauncherPipeHarness h = await LauncherPipeHarness.CreateConnectedAsync();

        Task accept = h.Server.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));

        string helloId = await SendHelloAsync(h.ClientWriter, "test-app", "1.2.3", 1);
        MessageEnvelope welcome = await ReadAsync(h.ClientReader);

        Assert.Equal(helloId, welcome.ReplyTo);

        await accept;
    }

    [Fact]
    public async Task Handshake_rejects_app_with_mismatched_protocol_major_version()
    {
        await using LauncherPipeHarness h = await LauncherPipeHarness.CreateConnectedAsync();

        Task accept = h.Server.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));

        int wrongVersion = ProtocolVersion.Current + 1;
        await SendHelloAsync(
            h.ClientWriter, "test-app", "1.2.3", 1, protocolVersion: wrongVersion);

        // App should still receive a welcome, but with accepted=false.
        MessageEnvelope welcome = await ReadAsync(h.ClientReader);
        WelcomePayload? payload = welcome.Data?.Deserialize(HuskyJsonContext.Default.WelcomePayload);

        Assert.NotNull(payload);
        Assert.False(payload!.Accepted);
        Assert.NotNull(payload.Reason);
        Assert.Contains("protocol version", payload.Reason!, StringComparison.OrdinalIgnoreCase);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await accept);
        Assert.Contains("protocol version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Handshake_throws_when_first_message_is_not_hello()
    {
        await using LauncherPipeHarness h = await LauncherPipeHarness.CreateConnectedAsync();

        Task accept = h.Server.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));

        await h.ClientWriter.WriteAsync(new MessageEnvelope { Type = MessageTypes.Heartbeat });

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await accept);
        Assert.Contains(MessageTypes.Hello, ex.Message, StringComparison.Ordinal);
        Assert.Contains(MessageTypes.Heartbeat, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handshake_throws_when_hello_has_no_payload()
    {
        await using LauncherPipeHarness h = await LauncherPipeHarness.CreateConnectedAsync();

        Task accept = h.Server.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));

        await h.ClientWriter.WriteAsync(new MessageEnvelope
        {
            Id = Guid.NewGuid().ToString("D"),
            Type = MessageTypes.Hello,
            // Data deliberately absent.
        });

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await accept);
        Assert.Contains("payload", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Handshake_throws_IOException_when_app_closes_pipe_before_hello()
    {
        await using LauncherPipeHarness h = await LauncherPipeHarness.CreateConnectedAsync();

        Task accept = h.Server.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));

        await h.Client.DisposeAsync();

        await Assert.ThrowsAsync<IOException>(async () => await accept);
    }

    [Fact]
    public async Task Handshake_throws_TimeoutException_when_no_app_connects_within_timeout()
    {
        await using AppPipeServer server = LauncherPipeHarness.CreateUnconnectedServer();

        await Assert.ThrowsAsync<TimeoutException>(
            async () => await server.AcceptAndHandshakeAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task Handshake_throws_TimeoutException_when_the_app_connects_but_sends_no_hello()
    {
        await using LauncherPipeHarness h = await LauncherPipeHarness.CreateConnectedAsync();

        TimeoutException ex = await Assert.ThrowsAsync<TimeoutException>(
            async () => await h.Server.AcceptAndHandshakeAsync(TimeSpan.FromMilliseconds(200)));

        Assert.Contains("no hello", ex.Message);
        Assert.Null(h.Server.ConnectedApp);
    }

    [Fact]
    public async Task Calling_AcceptAndHandshake_twice_throws()
    {
        await using LauncherPipeHarness h = await LauncherPipeHarness.CreateConnectedAsync();

        Task accept = h.Server.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));
        await SendHelloAsync(h.ClientWriter, "test-app", "1.2.3", 1);
        await ReadAsync(h.ClientReader); // welcome
        await accept;

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await h.Server.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Receiver_loop_silently_drops_heartbeats_and_unknown_messages()
    {
        await using LauncherPipeHarness h = await LauncherPipeHarness.CreateConnectedAsync();

        Task accept = h.Server.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));
        await SendHelloAsync(h.ClientWriter, "test-app", "1.2.3", 1);
        await ReadAsync(h.ClientReader); // welcome
        await accept;

        // Send a few heartbeats and an unknown type. The loop must not crash.
        await h.ClientWriter.WriteAsync(new MessageEnvelope { Type = MessageTypes.Heartbeat });
        await h.ClientWriter.WriteAsync(new MessageEnvelope { Type = "future-message" });
        await h.ClientWriter.WriteAsync(new MessageEnvelope { Type = MessageTypes.Heartbeat });

        // Brief settle, then close the pipe and confirm the loop exited cleanly.
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        await h.Client.DisposeAsync();

        Assert.NotNull(h.Server.ReceiverTask);
        await h.Server.ReceiverTask!.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(h.Server.ReceiverTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task SendPingAsync_returns_true_when_app_responds_with_pong()
    {
        await using LauncherPipeHarness h = await PerformHandshakeAsync();

        Task<bool> sendPing = h.Server.SendPingAsync(TimeSpan.FromSeconds(3));

        MessageEnvelope ping = await ReadAsync(h.ClientReader);
        Assert.Equal(MessageTypes.Ping, ping.Type);

        await SendPongAsync(h.ClientWriter, ping.Id);

        Assert.True(await sendPing.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task SendPingAsync_returns_false_when_app_does_not_respond_in_time()
    {
        await using LauncherPipeHarness h = await PerformHandshakeAsync();

        Task<bool> sendPing = h.Server.SendPingAsync(TimeSpan.FromMilliseconds(150));

        await ReadAsync(h.ClientReader); // drain the ping

        Assert.False(await sendPing.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task SendPingAsync_throws_when_called_before_handshake()
    {
        await using AppPipeServer server = LauncherPipeHarness.CreateUnconnectedServer();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await server.SendPingAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task SendPingAsync_only_resolves_for_a_pong_with_matching_replyTo()
    {
        await using LauncherPipeHarness h = await PerformHandshakeAsync();

        Task<bool> sendPing = h.Server.SendPingAsync(TimeSpan.FromMilliseconds(300));

        await ReadAsync(h.ClientReader); // drain the launcher's ping

        // Send a pong with a foreign replyTo — must be ignored by the launcher.
        await SendPongAsync(h.ClientWriter, replyTo: Guid.NewGuid().ToString("D"));

        Assert.False(await sendPing.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task OnActivity_fires_for_every_received_message()
    {
        await using LauncherPipeHarness h = await PerformHandshakeAsync();

        int activityCount = 0;
        h.Server.OnActivity = () => Interlocked.Increment(ref activityCount);

        await h.ClientWriter.WriteAsync(new MessageEnvelope { Type = MessageTypes.Heartbeat });
        await h.ClientWriter.WriteAsync(new MessageEnvelope { Type = "future-message" });
        await h.ClientWriter.WriteAsync(new MessageEnvelope { Type = MessageTypes.Heartbeat });

        await WaitForActivityAsync(() => Volatile.Read(ref activityCount) >= 3, TimeSpan.FromSeconds(2));
        Assert.Equal(3, Volatile.Read(ref activityCount));
    }

    [Fact]
    public async Task OnActivity_fires_before_pong_dispatch()
    {
        await using LauncherPipeHarness h = await PerformHandshakeAsync();

        int activityCount = 0;
        h.Server.OnActivity = () => Interlocked.Increment(ref activityCount);

        Task<bool> sendPing = h.Server.SendPingAsync(TimeSpan.FromSeconds(3));
        MessageEnvelope ping = await ReadAsync(h.ClientReader);
        await SendPongAsync(h.ClientWriter, ping.Id);

        Assert.True(await sendPing.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, Volatile.Read(ref activityCount));
    }

    [Fact]
    public async Task SendShutdownAsync_emits_a_shutdown_envelope_and_returns_when_app_acks()
    {
        await using LauncherPipeHarness h = await LauncherPipeHarness.CreateConnectedAsync();
        Task accept = h.Server.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));
        await SendHelloAsync(h.ClientWriter, "test-app", "1.2.3", 1);
        await ReadAsync(h.ClientReader); // welcome
        await accept;

        Task send = h.Server.SendShutdownAsync(
            reason: "manual",
            totalTimeout: TimeSpan.FromSeconds(30),
            ackTimeout: TimeSpan.FromSeconds(5));

        MessageEnvelope shutdown = await ReadAsync(h.ClientReader);
        Assert.Equal(MessageTypes.Shutdown, shutdown.Type);
        ShutdownPayload? payload = shutdown.Data?.Deserialize(HuskyJsonContext.Default.ShutdownPayload);
        Assert.NotNull(payload);
        Assert.Equal("manual", payload!.Reason);
        Assert.Equal(30, payload.TimeoutSeconds);

        await h.ClientWriter.WriteAsync(new MessageEnvelope
        {
            Id = Guid.NewGuid().ToString("D"),
            ReplyTo = shutdown.Id,
            Type = MessageTypes.ShutdownAck,
        });

        await send.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task SendShutdownAsync_throws_TimeoutException_when_app_does_not_ack_in_time()
    {
        await using LauncherPipeHarness h = await LauncherPipeHarness.CreateConnectedAsync();
        Task accept = h.Server.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));
        await SendHelloAsync(h.ClientWriter, "test-app", "1.2.3", 1);
        await ReadAsync(h.ClientReader); // welcome
        await accept;

        Task send = h.Server.SendShutdownAsync(
            reason: "manual",
            totalTimeout: TimeSpan.FromSeconds(30),
            ackTimeout: TimeSpan.FromMilliseconds(150));

        await ReadAsync(h.ClientReader); // drain shutdown so the pipe doesn't back up

        await Assert.ThrowsAsync<TimeoutException>(async () => await send);
    }

    [Fact]
    public async Task SendShutdownAsync_throws_when_called_before_handshake()
    {
        await using AppPipeServer server = LauncherPipeHarness.CreateUnconnectedServer();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await server.SendShutdownAsync(
                reason: "manual",
                totalTimeout: TimeSpan.FromSeconds(30),
                ackTimeout: TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task PipeName_is_exposed_after_Create()
    {
        const string name = "husky-test-explicit-name";
        await using AppPipeServer server = AppPipeServer.Create(name, "1.0.0-test");

        Assert.Equal(name, server.PipeName);
    }

    [Fact]
    public void Create_throws_when_pipe_name_is_blank()
    {
        Assert.Throws<ArgumentException>(() => AppPipeServer.Create("   ", "1.0.0-test"));
    }

    [Fact]
    public void Create_throws_when_launcher_version_is_blank()
    {
        Assert.Throws<ArgumentException>(() =>
            AppPipeServer.Create($"husky-test-{Guid.NewGuid():N}", "  "));
    }

    [Fact]
    public async Task DisposeAsync_is_idempotent()
    {
        AppPipeServer server = LauncherPipeHarness.CreateUnconnectedServer();

        await server.DisposeAsync();
        await server.DisposeAsync(); // must not throw
    }

    private static async Task<LauncherPipeHarness> PerformHandshakeAsync()
    {
        LauncherPipeHarness h = await LauncherPipeHarness.CreateConnectedAsync();
        try
        {
            Task accept = h.Server.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));
            await SendHelloAsync(h.ClientWriter, "test-app", "1.2.3", 1);
            await ReadAsync(h.ClientReader); // welcome
            await accept;
            return h;
        }
        catch
        {
            await h.DisposeAsync();
            throw;
        }
    }

    private static async Task SendPongAsync(MessageWriter writer, string? replyTo)
    {
        PongPayload payload = new(Status: "healthy", Details: null);
        JsonElement data = JsonSerializer.SerializeToElement(payload, HuskyJsonContext.Default.PongPayload);
        await writer.WriteAsync(new MessageEnvelope
        {
            Id = Guid.NewGuid().ToString("D"),
            ReplyTo = replyTo,
            Type = MessageTypes.Pong,
            Data = data,
        });
    }

    private static async Task WaitForActivityAsync(Func<bool> predicate, TimeSpan timeout)
    {
        using CancellationTokenSource cts = new(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (predicate()) return;
            try { await Task.Delay(TimeSpan.FromMilliseconds(20), cts.Token); }
            catch (OperationCanceledException) { break; }
        }
        throw new TimeoutException($"predicate did not become true within {timeout}.");
    }

    private static async Task<string> SendHelloAsync(
        MessageWriter writer,
        string appName,
        string appVersion,
        int pid,
        int? protocolVersion = null,
        IReadOnlyList<string>? capabilities = null,
        HelloPreferences? preferences = null)
    {
        HelloPayload payload = new(
            ProtocolVersion: protocolVersion ?? ProtocolVersion.Current,
            AppVersion: appVersion,
            AppName: appName,
            Pid: pid,
            Capabilities: capabilities,
            Preferences: preferences);

        JsonElement data = JsonSerializer.SerializeToElement(payload, HuskyJsonContext.Default.HelloPayload);
        string id = Guid.NewGuid().ToString("D");

        await writer.WriteAsync(new MessageEnvelope
        {
            Id = id,
            Type = MessageTypes.Hello,
            Data = data,
        });

        return id;
    }

    private static async Task<MessageEnvelope> ReadAsync(MessageReader reader)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        MessageEnvelope? envelope = await reader.ReadAsync(cts.Token);
        Assert.NotNull(envelope);
        return envelope!;
    }
}
