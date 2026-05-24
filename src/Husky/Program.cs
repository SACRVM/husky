using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Husky;
using Retro.Crt;

// The banner art uses Unicode block characters; force UTF-8 so Windows
// consoles on legacy code pages render them instead of '?'.
Console.OutputEncoding = Encoding.UTF8;

string launcherVersion = GetLauncherVersion();
// Banner renders once at startup. There is no alt-screen takeover any more
// (LEASH §10.4): the logo scrolls into the terminal's native scrollback
// above the sticky footer and stays in history like every other line.
Husky.Banner.Render(launcherVersion);

// Step 1 — parse CLI flags (LEASH §5.2.1). Resolves --dir and the
// synthetic source block before we touch any file.
CliArgs cliArgs;
try
{
    cliArgs = CliArgsParser.Parse(args);
}
catch (HuskyConfigException ex)
{
    ConsoleOutput.Husky($"command-line error: {ex.Message}");
    return ExitCodes.ConfigError;
}

string workingDirectory;
try
{
    workingDirectory = ResolveWorkingDirectory(cliArgs.WorkingDirectory);
}
catch (HuskyConfigException ex)
{
    ConsoleOutput.Husky($"command-line error: {ex.Message}");
    return ExitCodes.ConfigError;
}

string configPath = Path.Combine(workingDirectory, HuskyConfigLoader.DefaultFileName);

// Step 2 — load the local config when present; tolerate its absence
// when CLI source flags supply everything we need to bootstrap.
LocalHuskyConfig? localConfig;
try
{
    localConfig = HuskyConfigLoader.LoadIfPresent(configPath);
}
catch (HuskyConfigException ex)
{
    ConsoleOutput.Husky($"config error: {ex.Message}");
    return ExitCodes.ConfigError;
}

// Step 3 — merge CLI source over local source (LEASH §5.2 layer 1 > 2).
SourceConfig? mergedSource = cliArgs.CliSource ?? localConfig?.Source;
if (mergedSource is null)
{
    ConsoleOutput.Husky(
        localConfig is null
            ? $"config error: no '{HuskyConfigLoader.DefaultFileName}' in '{workingDirectory}' and no '--manifest'/'--repo' on the command line."
            : $"config error: '{configPath}' has no 'source' and no '--manifest'/'--repo' was supplied.");
    return ExitCodes.ConfigError;
}

if (cliArgs.CliSource is not null && localConfig?.Source is not null)
    ConsoleOutput.Husky("CLI source overrides local config (LEASH §5.2).");

LocalHuskyConfig effectiveLocal = (localConfig ?? new LocalHuskyConfig()) with { Source = mergedSource };

using HttpClient httpClient = BuildHttpClient(launcherVersion);

// Step 4 — initialise the source provider.
IUpdateSource source;
try
{
    source = UpdateSourceFactory.Create(mergedSource, httpClient, launcherVersion);
}
catch (HuskyConfigException ex)
{
    ConsoleOutput.Husky($"config error: {ex.Message}");
    return ExitCodes.ConfigError;
}

int sigintCount = 0;
using CancellationTokenSource gracefulTrigger = new();
using CancellationTokenSource hardKillTrigger = new();

void OnInterrupt()
{
    int n = Interlocked.Increment(ref sigintCount);
    try
    {
        if (n == 1) gracefulTrigger.Cancel();
        else hardKillTrigger.Cancel();
    }
    catch (ObjectDisposedException) { /* shutting down */ }
}

ConsoleCancelEventHandler ctrlCHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    OnInterrupt();
};
Console.CancelKeyPress += ctrlCHandler;

using PosixSignalRegistration sigtermReg = PosixSignalRegistration.Create(
    PosixSignal.SIGTERM,
    ctx =>
    {
        ctx.Cancel = true;
        OnInterrupt();
    });

// Step 5 — initial source poll. Currentversion "0.0.0" makes the providers
// always return the latest available release (so we get the source-supplied
// config block whether or not we'd update). A network failure here is OK if
// the local config can stand on its own.
UpdateInfo? bootPoll = null;
Exception? bootPollError = null;
bool bootPollCancelled = false;
{
    // Silent dispose: LauncherRuntime owns the boot announcement (it knows
    // the installed version and decides whether to apply, log "up to date.",
    // or "source unreachable.") so we don't pre-empt it with our own line.
    using var spinner = new InPlaceSpinner("sniffing for updates");
    try
    {
        bootPoll = await source.CheckForUpdateAsync("0.0.0", gracefulTrigger.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (gracefulTrigger.IsCancellationRequested)
    {
        bootPollCancelled = true;
    }
    catch (Exception ex)
    {
        bootPollError = ex;
    }
}

if (bootPollCancelled) return ExitCodes.Ok;
if (bootPollError is not null)
    ConsoleOutput.Husky($"initial source poll failed: {bootPollError.Message}");
if (bootPoll?.SourceFieldDropped == true)
    ConsoleOutput.Husky(
        "source-supplied config contained a 'source' block — dropped (anti-redirect, LEASH §9.2).");

// Step 6 — resolve the effective config (CLI source + local + source-supplied + defaults).
HuskyConfig config;
try
{
    config = HuskyConfigResolver.Resolve(effectiveLocal, bootPoll?.Config);
}
catch (HuskyConfigException ex)
{
    ConsoleOutput.Husky(bootPoll is null
        ? $"config incomplete and source unreachable: {ex.Message}"
        : $"config error: {ex.Message}");
    return ExitCodes.ConfigError;
}

string executablePath = Path.GetFullPath(Path.Combine(workingDirectory, config.Executable));
string installDirectory = workingDirectory;

UpdateDownloader downloader = new(httpClient);
using ProgressBarDownloadSink progressSink = new();
downloader.Progress = progressSink;
using UpdateFlow updateFlow = new(downloader, installDirectory, config.Executable);

AppSessionLauncher sessionLauncher = new(
    executablePath: executablePath,
    appName: config.Name,
    launcherVersion: launcherVersion,
    watchdogOptions: WatchdogOptions.Default,
    onStandardOutput: ConsoleOutput.AppOut,
    onStandardError: ConsoleOutput.AppErr);

RestartPolicy restartPolicy = new(
    maxAttemptsPerHour: config.RestartAttempts,
    pauseBetweenAttempts: TimeSpan.FromSeconds(config.RestartPauseSec));

LauncherRuntime runtime = new(
    config: config,
    source: source,
    updateFlow: updateFlow,
    sessionLauncher: sessionLauncher,
    restartPolicy: restartPolicy,
    executablePath: executablePath,
    seedUpdateInfo: bootPoll);

// Step 7 — pick the rendering mode. Interactive mode (LEASH §10.4) layers a
// sticky footer + hotkeys over native scrollback and needs a real terminal;
// piping or redirection forces plain line mode (LEASH §10.3) so `husky | grep`
// and `husky > log.txt` keep working. `NO_COLOR` (https://no-color.org/) also
// forces line mode — the user asked for no styling, so we skip the footer
// chrome. If raw mode or the footer can't start at run time, HuskyApp.Run
// degrades to plain line output on its own without complaining.
HuskyApp? tuiApp = null;
if (!Console.IsOutputRedirected && !IsNoColorRequested())
{
    try
    {
        tuiApp = new HuskyApp(
            launcherVersion,
            workingDirectory,
            onUpdateRequested: runtime.RequestUpdateNow,
            onExitRequested: () =>
            {
                try { gracefulTrigger.Cancel(); }
                catch (ObjectDisposedException) { /* shutting down */ }
            });
        ConsoleOutput.SetSink(tuiApp);
    }
    catch
    {
        tuiApp = null;
    }
}

try
{
    if (tuiApp is not null)
    {
        Task<int> runtimeTask = Task.Run(async () =>
        {
            try
            {
                return await runtime.RunAsync(gracefulTrigger.Token, hardKillTrigger.Token).ConfigureAwait(false);
            }
            finally
            {
                // Always dismiss the TUI when the runtime exits, no matter
                // how — natural shutdown, exception, or user-triggered Esc.
                tuiApp.Dismiss();
            }
        });

        tuiApp.Run();
        return await runtimeTask.ConfigureAwait(false);
    }

    return await runtime.RunAsync(gracefulTrigger.Token, hardKillTrigger.Token).ConfigureAwait(false);
}
finally
{
    Console.CancelKeyPress -= ctrlCHandler;
    ConsoleOutput.ResetSink();
}

static HttpClient BuildHttpClient(string launcherVersion)
{
    HttpClient client = new() { Timeout = TimeSpan.FromMinutes(5) };
    client.DefaultRequestHeaders.UserAgent.ParseAdd($"Husky/{launcherVersion}");
    return client;
}

static string GetLauncherVersion()
{
    Assembly asm = Assembly.GetExecutingAssembly();
    string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    string raw = string.IsNullOrWhiteSpace(info)
        ? asm.GetName().Version?.ToString() ?? "0.0.0"
        : info;
    return VersionFormat.ToDisplay(raw);
}

static bool IsNoColorRequested() =>
    !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

static string ResolveWorkingDirectory(string? overrideDir)
{
    if (overrideDir is null)
        return Path.GetFullPath(Directory.GetCurrentDirectory());

    string resolved = Path.GetFullPath(overrideDir);
    if (!Directory.Exists(resolved))
        throw new HuskyConfigException(
            $"Working directory from '--dir' does not exist: '{resolved}'.");
    return resolved;
}

internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Generic = 1;
    public const int ConfigError = 2;
}
