using System.Diagnostics;
using System.Text;

namespace Husky;

internal sealed class AppProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly TaskCompletionSource exitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int exitCode;
    private volatile bool hasExited;
    private bool disposed;

    public int ProcessId { get; }
    public bool HasExited => hasExited;
    public int ExitCode => exitCode;
    public Task ExitTask => exitTcs.Task;

    private AppProcess(Process process)
    {
        this.process = process;
        ProcessId = process.Id;

        // WaitForExitAsync (unlike the raw Exited event) waits for the
        // process to exit *and* drains the async stdout/stderr readers'
        // EOF tasks before returning. Without this, ExitTask can complete
        // while ErrorDataReceived / OutputDataReceived callbacks for the
        // final stderr/stdout bytes are still queued on the ThreadPool,
        // so a consumer reading the captured lines right after the await
        // sees an empty list. Observed under CI load on AppProcessTests
        // (.NET docs flag the same race explicitly for the event path).
        _ = MonitorExitAsync();
    }

    private async Task MonitorExitAsync()
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch
        {
            // Whatever happened, we still need to complete the TCS so
            // callers don't hang forever — fall through to the recording.
        }

        try { exitCode = process.ExitCode; }
        catch (InvalidOperationException) { exitCode = -1; }
        hasExited = true;
        exitTcs.TrySetResult();
    }

    public static AppProcess Start(
        AppProcessOptions options,
        Action<string>? onStandardOutput = null,
        Action<string>? onStandardError = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!File.Exists(options.ExecutablePath))
            throw new FileNotFoundException(
                $"Executable not found: '{options.ExecutablePath}'.", options.ExecutablePath);

        ProcessStartInfo psi = new()
        {
            FileName = options.ExecutablePath,
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // Decode the child's stdout/stderr as UTF-8 explicitly instead
            // of inheriting whatever Console.OutputEncoding happens to be.
            // The launcher sets that to UTF-8 today, but the redirected
            // readers shouldn't depend on global console state.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (options.Environment is { } env)
        {
            foreach (KeyValuePair<string, string?> entry in env)
            {
                psi.Environment[entry.Key] = entry.Value;
            }
        }

        // EnableRaisingEvents keeps the internal wait-state primed even
        // though we don't subscribe to Process.Exited any more (we wait
        // via WaitForExitAsync instead, which drains stdout/stderr).
        Process proc = new() { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) onStandardOutput?.Invoke(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) onStandardError?.Invoke(e.Data);
        };

        try
        {
            if (!proc.Start())
                throw new InvalidOperationException(
                    $"Failed to start '{options.ExecutablePath}'.");
        }
        catch
        {
            proc.Dispose();
            throw;
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        return new AppProcess(proc);
    }

    public void Kill()
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* already exited or already disposed */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;

        Kill();

        try
        {
            await ExitTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException) { /* exit task did not complete — proceed with cleanup */ }
        catch (OperationCanceledException) { /* normal */ }

        process.Dispose();
    }
}
