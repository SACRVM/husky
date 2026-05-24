using System.Globalization;
using Husky;
using Retro.Crt;

// Husky interactive-mode demo — exercises the LEASH §10.4 sticky-footer +
// native-scrollback layout without a real LauncherRuntime, so we can
// iterate visually without a config / source / hosted app. Constructs
// HuskyApp directly via InternalsVisibleTo, swaps ConsoleOutput's sink, and
// drives a scripted fixture loop on a background task.
//
// Pre-footer warm-up (plain line mode, before HuskyApp.Run starts the footer):
//   0. ascii banner with ice-blue → cyan gradient + a brief 'sniffing for
//      updates' spinner that disposes silently — matches how prod's
//      Program.cs warms up before HuskyApp.Run.
//
// The fixtures cover, in order:
//   1. boot sequence (5 quick husky lines, status row populates, [u] shows
//      because 'manual-updates' is supported).
//   2. heartbeat-style app stdout ticks.
//   3. watchdog pong updates with status words (highlighted in colour).
//   4. mixed status palette (up / down / healthy / degraded).
//   5. force-true growl escalation (the dog barks audibly).
//   6. animated InPlaceSpinner cycle ('sniffing for updates' → result).
//   7. extra-long line — wraps naturally in the terminal (no clipping now).
//   8. fake download driving the real ProgressBarDownloadSink so the
//      in-place line (LEASH §10.6) animates live in scrollback and the
//      100 % frame lands via the throttle-bypassing UpdateNow.
//   9. animated InPlaceSpinner with intermediate UpdateLabel
//      (graceful-shutdown style: 'asking app to sit' → 'no ack' → 'sat down').
//  10. fake crash + 5-second restart-pause countdown — the status row's
//      right slot reads 'down — restarting in Ns' in red while [u] hides.
//  11. 200-line burst to verify writes keep up under load.
//
// After the burst the demo idles. Press [s] for the save-logs toast,
// [u] for an update-now log line, Esc to exit.
//
// Run with: dotnet run --project samples/Husky.Demo

using var demoCts = new CancellationTokenSource();

// Self-reference for the exit callback: the lambda must call Dismiss() on
// the same HuskyApp instance to break the input loop out of Run(). The real
// launcher achieves the same handoff by dismissing from the runtime task's
// `finally` block; the demo has no runtime, so the hotkey itself does both.
HuskyApp app = null!;
app = new HuskyApp(
    "0.3.2-demo",
    Directory.GetCurrentDirectory(),
    onUpdateRequested: () =>
    {
        // The demo has no real update flow; just surface the click so the
        // hotkey/button is visibly responsive.
        ConsoleOutput.Husky(
            "[u] check & install — demo has no real update path.",
            messageColor: Color.Yellow);
    },
    onExitRequested: () =>
    {
        try { demoCts.Cancel(); }
        catch (ObjectDisposedException) { /* shutting down */ }
        // Without this the fixture loop unwinds but Run()'s input loop keeps
        // blocking the main thread, so [x]/Esc would silently 'do nothing'.
        app.Dismiss();
    });

// Match the production warm-up (LEASH §10.3): banner + a brief 'sniffing
// for updates' line-mode spinner before the sink swap. The default
// CrtConsoleSink renders these into the real terminal; Run() then starts
// the sticky footer over the same native scrollback.
Husky.Banner.Render("0.3.2-demo");
using (var bootSpinner = new InPlaceSpinner("sniffing for updates"))
{
    await Task.Delay(1200).ConfigureAwait(false);
    // Silent dispose — BootSequenceAsync owns the result line, matching how
    // LauncherRuntime announces 'up to date.' / 'new version found'.
}

ConsoleOutput.SetSink(app);

Task fixtureTask = Task.Run(() => RunFixturesAsync(demoCts.Token));

app.Run();

// User pressed Esc → demoCts already cancelled by the callback. If we got
// here some other way (Dismiss from elsewhere, future hotkey), make sure
// the fixture loop unwinds.
try { demoCts.Cancel(); } catch (ObjectDisposedException) { }
try { await fixtureTask.ConfigureAwait(false); }
catch (OperationCanceledException) { /* expected */ }

ConsoleOutput.ResetSink();
return 0;

static async Task RunFixturesAsync(CancellationToken ct)
{
    try
    {
        await BootSequenceAsync(ct).ConfigureAwait(false);
        await ActivityLoopAsync(ct).ConfigureAwait(false);
        await FakeSniffingAsync(ct).ConfigureAwait(false);
        await FakeDownloadAsync(ct).ConfigureAwait(false);
        await FakeShutdownAsync(ct).ConfigureAwait(false);
        await FakeCrashRestartAsync(ct).ConfigureAwait(false);
        await BurstAsync(ct).ConfigureAwait(false);
        await IdleAsync(ct).ConfigureAwait(false);
    }
    catch (OperationCanceledException) { /* normal */ }
}

static async Task BootSequenceAsync(CancellationToken ct)
{
    ConsoleOutput.Husky("woof. starting demo-app");
    await Task.Delay(150, ct).ConfigureAwait(false);
    ConsoleOutput.Husky("config loaded from husky.config.json");
    await Task.Delay(150, ct).ConfigureAwait(false);
    ConsoleOutput.Husky("source: github://demo/demo (latest: v1.4.2)");
    await Task.Delay(200, ct).ConfigureAwait(false);
    ConsoleOutput.Husky("starting demo-app v1.4.2");
    await Task.Delay(400, ct).ConfigureAwait(false);
    // Header populates the moment the synthetic 'hello' lands.
    ConsoleOutput.SetAppInfo("demo-app", "1.4.2");
    // Demo app advertises the manual-updates capability, so [u] renders
    // active in the action bar — the hotkey forces a fresh source poll
    // on press regardless of cached state. Header's right slot stays
    // empty until the first pong.
    ConsoleOutput.SetUpdateActionState(UpdateActionState.Enabled);
    ConsoleOutput.AppOut("demo-app: bootstrap complete");
    await Task.Delay(200, ct).ConfigureAwait(false);
    ConsoleOutput.AppOut("demo-app: connected to 12 guilds");
    await Task.Delay(200, ct).ConfigureAwait(false);
    ConsoleOutput.SetHealth("healthy");
    ConsoleOutput.Husky("demo-app v1.4.2 is up.");
}

static async Task ActivityLoopAsync(CancellationToken ct)
{
    int tick = 0;
    DateTime startedAt = DateTime.UtcNow;

    while (!ct.IsCancellationRequested && (DateTime.UtcNow - startedAt).TotalSeconds < 12)
    {
        await Task.Delay(1100, ct).ConfigureAwait(false);
        tick++;

        // Most ticks are routine app stdout; sprinkle in pongs, a stderr
        // line, a growl, and an update-found scenario.
        switch (tick)
        {
            case 1:
                ConsoleOutput.AppOut("demo-app: tick — queue=12 guilds=42");
                break;
            case 2:
                ConsoleOutput.Husky("pong: status=healthy queue=12 guilds=42");
                break;
            case 3:
                ConsoleOutput.AppOut("demo-app: tick — queue=8 guilds=42");
                break;
            case 4:
                ConsoleOutput.AppErr("demo-app: WARN connection reset, retrying");
                break;
            case 5:
                ConsoleOutput.SetHealth("degraded");
                ConsoleOutput.Husky("pong: status=degraded queue=205 guilds=42");
                break;
            case 6:
                // §3.5.13 capability-warning style yellow line.
                ConsoleOutput.Husky(
                    "demo-app sent set-update-mode=manual without 'manual-updates' — ignored.",
                    messageColor: Color.Yellow);
                break;
            case 7:
                // Growl escalation — force:true rings the bell. In
                // interactive mode it appears at the tail like every other
                // line (no in-place op is open here), bell still fires.
                ConsoleOutput.SetHealth("unhealthy");
                ConsoleOutput.Husky(
                    "growling — no pong in 30s. probing.",
                    force: true);
                break;
            case 8:
                ConsoleOutput.AppOut("demo-app: tick — queue=4 guilds=42");
                break;
            case 9:
                ConsoleOutput.SetHealth("healthy");
                ConsoleOutput.Husky("pong: status=healthy queue=4 guilds=42");
                break;
            case 10:
                // Manual-mode update notification, before the FakeSniffing
                // and FakeDownload fixtures drive the in-place line.
                ConsoleOutput.Husky("manual mode — notifying app, waiting for trigger.");
                break;
            default:
                ConsoleOutput.AppOut(
                    $"demo-app: tick — queue={Math.Max(0, 12 - tick)} guilds=42");
                break;
        }
    }

    // Long-line fixture: with native scrollback the terminal wraps this
    // across rows (no in-app clipping any more, LEASH §10.4).
    ConsoleOutput.AppOut(
        "demo-app: VERY-LONG-LINE — " +
        new string('-', 220) +
        " end.");
}

static async Task FakeSniffingAsync(CancellationToken ct)
{
    using var spinner = new InPlaceSpinner("sniffing for updates");
    await Task.Delay(2200, ct).ConfigureAwait(false);
    spinner.Complete("new version found: v0.4.0", Color.LightGreen);
    await Task.Delay(600, ct).ConfigureAwait(false);
}

static async Task FakeShutdownAsync(CancellationToken ct)
{
    ConsoleOutput.Husky("update complete — bouncing demo-app.");
    await Task.Delay(300, ct).ConfigureAwait(false);

    using var spinner = new InPlaceSpinner("asking app to sit");
    await Task.Delay(1500, ct).ConfigureAwait(false);
    // Intermediate label change keeps the animation ticking.
    spinner.UpdateLabel("no shutdown-ack — waiting anyway");
    await Task.Delay(1500, ct).ConfigureAwait(false);
    spinner.UpdateLabel("grace period (+10s)");
    await Task.Delay(1500, ct).ConfigureAwait(false);
    spinner.Complete("app sat down.", Color.LightGreen);
    // Session ended — header reverts to the pre-attach state, [u] hides
    // (no app to target) until the fresh hello lands.
    ConsoleOutput.SetAppInfo(null, null);
    ConsoleOutput.SetHealth(null);
    ConsoleOutput.SetUpdateActionState(UpdateActionState.Hidden);
    await Task.Delay(800, ct).ConfigureAwait(false);
    ConsoleOutput.SetAppInfo("demo-app", "0.4.0");
    // Fresh session re-attaches — [u] re-enables (manual-updates capability
    // still advertised; no cache gate any more).
    ConsoleOutput.SetUpdateActionState(UpdateActionState.Enabled);
    ConsoleOutput.SetHealth("healthy");
    ConsoleOutput.Husky("demo-app v0.4.0 is up.");
    await Task.Delay(400, ct).ConfigureAwait(false);
}

static async Task FakeDownloadAsync(CancellationToken ct)
{
    ConsoleOutput.Husky("user triggered update — applying v0.4.0.");
    await Task.Delay(400, ct).ConfigureAwait(false);

    using var sink = new ProgressBarDownloadSink();
    const long Total = 6_800_000;
    sink.OnStarted(Total);

    System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
    var rng = new Random(42);
    long received = 0;

    while (received < Total && !ct.IsCancellationRequested)
    {
        await Task.Delay(60, ct).ConfigureAwait(false);
        // Realistic chunk-size jitter so the 10 Hz throttle in TuiInPlaceLine
        // gets a workout. The throttle drops most updates and renders the
        // 0% / 100% frames plus whatever lands on the 100 ms boundary.
        long chunk = rng.Next(80_000, 280_000);
        received = Math.Min(Total, received + chunk);
        sink.OnAdvanced(received);
    }
    sw.Stop();
    sink.OnFinished(received, sw.Elapsed);
}

static async Task FakeCrashRestartAsync(CancellationToken ct)
{
    // Synthetic crash: clear the per-session header state, then drive the
    // 'down — restarting in Ns' override once a second so the right slot
    // turns red and counts down (LEASH §10.4).
    ConsoleOutput.AppErr("demo-app: FATAL unhandled exception");
    await Task.Delay(200, ct).ConfigureAwait(false);
    ConsoleOutput.Husky("demo-app exited with code 1 — considering restart.");
    ConsoleOutput.SetAppInfo(null, null);
    ConsoleOutput.SetHealth(null);
    ConsoleOutput.SetUpdateActionState(UpdateActionState.Hidden);
    await Task.Delay(300, ct).ConfigureAwait(false);

    ConsoleOutput.Husky("pausing 5s before restart.");
    for (int s = 5; s > 0 && !ct.IsCancellationRequested; s--)
    {
        ConsoleOutput.SetCrashRestart($"down — restarting in {s}s");
        await Task.Delay(1000, ct).ConfigureAwait(false);
    }
    ConsoleOutput.SetCrashRestart(null);

    // Fresh session lands. Header repopulates and the action bar's [u]
    // re-enables (capability is back, no cache gate any more).
    ConsoleOutput.SetAppInfo("demo-app", "0.4.0");
    ConsoleOutput.SetUpdateActionState(UpdateActionState.Enabled);
    ConsoleOutput.SetHealth("healthy");
    ConsoleOutput.Husky("back online. demo-app v0.4.0 is up.");
    await Task.Delay(400, ct).ConfigureAwait(false);
}

static async Task BurstAsync(CancellationToken ct)
{
    ConsoleOutput.Husky("burst: 200 lines incoming.");
    await Task.Delay(200, ct).ConfigureAwait(false);

    for (var i = 0; i < 200; i++)
    {
        if (ct.IsCancellationRequested) break;
        switch (i % 4)
        {
            case 0: ConsoleOutput.AppOut($"burst {i:000}: stdout payload"); break;
            case 1: ConsoleOutput.AppOut($"burst {i:000}: another stdout line"); break;
            case 2: ConsoleOutput.AppErr($"burst {i:000}: stderr noise"); break;
            default: ConsoleOutput.Pipe($"burst {i:000}: pipe trace 0x{i:X4}"); break;
        }
        if (i % 20 == 0)
            await Task.Delay(50, ct).ConfigureAwait(false);
    }

    ConsoleOutput.Husky(
        $"burst complete — drained 200 lines.",
        messageColor: Color.LightGreen);
}

static async Task IdleAsync(CancellationToken ct)
{
    ConsoleOutput.Husky("idle. press [s] for save-logs toast, [u] for update demo, Esc to exit.");
    int n = 0;
    while (!ct.IsCancellationRequested)
    {
        await Task.Delay(7000, ct).ConfigureAwait(false);
        n++;
        ConsoleOutput.AppOut(
            $"demo-app: idle heartbeat #{n.ToString(CultureInfo.InvariantCulture)} at {DateTime.Now:HH:mm:ss}");
    }
}
