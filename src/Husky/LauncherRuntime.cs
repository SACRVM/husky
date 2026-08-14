using Husky.Protocol;
using Retro.Crt;

namespace Husky;

/// <summary>
/// Top-level launcher loop. Owns the conversation between the source
/// provider, the update flow, the app sessions, and the crash-restart
/// policy. Extracted from Program.cs so the wiring is testable.
/// </summary>
internal sealed class LauncherRuntime(
    HuskyConfig config,
    IUpdateSource source,
    UpdateFlow updateFlow,
    AppSessionLauncher sessionLauncher,
    RestartPolicy restartPolicy,
    string executablePath,
    UpdateInfo? seedUpdateInfo = null)
{
    private readonly object sessionGate = new();
    private AppSession? currentSession;
    private TaskCompletionSource<bool>? updateInFlight;
    private TaskCompletionSource declaredDeadTrigger = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource sessionStartedTrigger = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private UpdateInfo? cachedUpdate;
    private CancellationToken pollingToken;
    // Tracks which version we've already pushed an unsolicited update-available
    // for in manual mode. LEASH §3.5.11: "once per discovered version" — so
    // the polling loop must not re-push the same version on every tick.
    // Cleared when an update applies, when a new session starts, or when the
    // discovered version changes.
    private string? lastPushedManualVersion;
    // Re-entrancy gate for the TUI [u] check-and-install hotkey. 0 = idle,
    // 1 = a user-triggered poll is currently in flight (or its follow-up
    // apply). Repeated [u] presses while the flag is set get a console
    // log and are otherwise ignored. The pipe-driven update-now path
    // (OnUpdateNowFromApp) does not share this gate — that side has its
    // own once-per-version dedup via lastPushedManualVersion.
    private int userCheckInFlight;

    public async Task<int> RunAsync(CancellationToken graceful, CancellationToken hardKill)
    {
        // Boot-time update check (LEASH §5.3). The seed comes from a pre-poll
        // performed during config resolution (Program.cs); using it here saves
        // a second HTTP round-trip and lets us decide bootstrap-vs-update
        // against the actual installed version.
        bool installed = File.Exists(executablePath);
        string currentVersion = AppVersionReader.ReadCurrent(executablePath);

        if (!installed)
        {
            ConsoleOutput.Husky("no app installed yet — bootstrapping.");
            if (seedUpdateInfo is null)
            {
                ConsoleOutput.Husky("bootstrap failed: source had no version available.");
                return ExitCodes.ConfigError;
            }
            string installedVersion;
            try
            {
                installedVersion = await BootstrapAsync(seedUpdateInfo, graceful).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ConsoleOutput.Husky($"bootstrap failed: {ex.Message}");
                return ExitCodes.ConfigError;
            }
            ConsoleOutput.Husky($"update succeeded — now on v{installedVersion}");
            currentVersion = AppVersionReader.ReadCurrent(executablePath);
        }
        else
        {
            UpdateInfo? bootCheck = SelectBootUpdate(seedUpdateInfo, currentVersion);
            if (bootCheck is not null)
            {
                ConsoleOutput.Husky($"new version found: v{bootCheck.Version}");
                try
                {
                    await ApplyUpdateAtBootAsync(bootCheck, graceful).ConfigureAwait(false);
                    ConsoleOutput.Husky($"update succeeded — now on v{bootCheck.Version}");
                    currentVersion = AppVersionReader.ReadCurrent(executablePath);
                }
                catch (OperationCanceledException) when (graceful.IsCancellationRequested)
                {
                    return ExitCodes.Ok;
                }
                catch (Exception ex)
                {
                    ConsoleOutput.Husky($"update aborted: {ex.Message}");
                    // Continue with the existing install — polling will retry.
                }
            }
            else if (seedUpdateInfo is null)
            {
                ConsoleOutput.Husky("source unreachable — running with last installed version.");
            }
            else
            {
                ConsoleOutput.Husky("up to date.");
            }
        }

        // Now start the app for the first time.
        AppSession? session;
        try
        {
            ConsoleOutput.Husky($"starting {config.Name}");
            session = await StartSessionAsync(graceful).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ConsoleOutput.Husky($"failed to start {config.Name}: {ex.Message}");
            return ExitCodes.Generic;
        }

        AnnounceUp(session);

        // Polling loop (LEASH §5.3.7).
        pollingToken = graceful;
        await using UpdateScheduler scheduler = new(
            interval: TimeSpan.FromMinutes(config.CheckMinutes),
            tickAsync: ct => PollOnceAsync(ct));
        scheduler.Start();

        try
        {
            return await SuperviseAsync(graceful, hardKill).ConfigureAwait(false);
        }
        finally
        {
            await DisposeCurrentSessionAsync().ConfigureAwait(false);
        }
    }

    private async Task<int> SuperviseAsync(CancellationToken graceful, CancellationToken hardKill)
    {
        while (true)
        {
            AppSession? session = CurrentSession;
            if (session is null)
            {
                ConsoleOutput.Husky("enough. lying down.");
                return ExitCodes.Generic;
            }

            Task gracefulWait = AwaitCancellationAsync(graceful);
            Task deadWait;
            lock (sessionGate) deadWait = declaredDeadTrigger.Task;
            Task winner = await Task.WhenAny(session.ExitTask, gracefulWait, deadWait).ConfigureAwait(false);

            if (winner == gracefulWait)
            {
                // If a polling-tick update is mid-flight, let it finish (or
                // tear it down via the hard-kill token on a double Ctrl+C)
                // before we drive our own shutdown — otherwise we'd race
                // StopCurrentSessionAsync with a duplicate stop.
                Task<bool>? pending = PeekUpdateInFlight();
                if (pending is not null)
                {
                    ConsoleOutput.Husky("update in flight — letting it finish before we sit.");
                    try { await pending.WaitAsync(hardKill).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (hardKill.IsCancellationRequested) { /* fall through */ }
                }

                ConsoleOutput.Husky("asking app to sit.");
                AppSession? alive = CurrentSession;
                if (alive is not null && !alive.HasExited)
                {
                    await GracefulShutdown.StopAsync(
                        alive,
                        reason: "launcher-stopping",
                        shutdownTimeout: TimeSpan.FromSeconds(config.ShutdownTimeoutSec),
                        killAfter: TimeSpan.FromSeconds(config.KillAfterSec),
                        hardKill: hardKill).ConfigureAwait(false);
                }
                return ExitCodes.Ok;
            }

            if (winner == deadWait)
            {
                try { await session.ExitTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }

            // Session ended. If an update is driving this exit, hand control
            // back to the polling tick — it owns the next session.
            bool updateFailed = false;
            Task<bool>? updateAwait = TakeUpdateInFlight();
            if (updateAwait is not null)
            {
                bool updateOk;
                try { updateOk = await updateAwait.ConfigureAwait(false); }
                catch { updateOk = false; }

                if (updateOk && CurrentSession is not null) continue;

                // Update aborted with no fresh session. The old session was
                // stopped on purpose, so its clean exit code means nothing —
                // never interpret it as "app is done" (that would end the
                // launcher). Route into the restart policy instead (§7.3.6);
                // the new build is already installed, so a retried start
                // usually recovers.
                updateFailed = true;
            }

            int exitCode = session.ExitCode;
            await DisposeCurrentSessionAsync().ConfigureAwait(false);

            if (exitCode == 0 && !updateFailed)
            {
                ConsoleOutput.Husky($"{config.Name} exited cleanly.");
                return ExitCodes.Ok;
            }

            ConsoleOutput.Husky(updateFailed
                ? $"update failed to bring {config.Name} back — considering restart."
                : $"{config.Name} exited with code {exitCode} — considering restart.");

            // Retry under the restart policy until a start sticks or the cap
            // trips and we park (§8.4). A failed start attempt must never end
            // the launcher — only a graceful interrupt exits from here.
            while (true)
            {
                if (!restartPolicy.CanRestart())
                {
                    ConsoleOutput.Husky("enough. lying down.");
                    bool revived = await ParkUntilUpdateOrInterruptAsync(graceful).ConfigureAwait(false);
                    if (!revived) return ExitCodes.Ok;

                    ConsoleOutput.Husky("update brought a fresh build — back online.");
                    break;
                }

                ConsoleOutput.Husky($"pausing {config.RestartPauseSec}s before restart.");
                try
                {
                    // §10.4: while we're between crash and restart, the
                    // header's right slot reads 'down — restarting in Ns'
                    // in red. Tickle once a second so the countdown is live.
                    for (int s = config.RestartPauseSec; s > 0; s--)
                    {
                        ConsoleOutput.SetCrashRestart($"down — restarting in {s}s");
                        await Task.Delay(TimeSpan.FromSeconds(1), graceful).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    ConsoleOutput.SetCrashRestart(null);
                    return ExitCodes.Ok;
                }
                ConsoleOutput.SetCrashRestart(null);

                restartPolicy.RecordAttempt();
                try
                {
                    AppSession revived = await StartSessionAsync(graceful).ConfigureAwait(false);
                    AnnounceUp(revived);
                    break;
                }
                catch (OperationCanceledException) when (graceful.IsCancellationRequested)
                {
                    return ExitCodes.Ok;
                }
                catch (Exception ex)
                {
                    ConsoleOutput.Husky($"restart failed: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Wait for either a graceful interrupt or a successful update that brings
    /// up a fresh session (LEASH §8.4: a successful update resets the cap and
    /// triggers a start). Returns true when a session is now available.
    /// </summary>
    private async Task<bool> ParkUntilUpdateOrInterruptAsync(CancellationToken graceful)
    {
        Task gracefulWait = AwaitCancellationAsync(graceful);
        Task sessionWait;
        lock (sessionGate) sessionWait = sessionStartedTrigger.Task;

        Task winner = await Task.WhenAny(gracefulWait, sessionWait).ConfigureAwait(false);
        return winner == sessionWait && CurrentSession is not null;
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        AppSession? session = CurrentSession;
        string version = session is not null
            ? session.ConnectedApp.Version
            : AppVersionReader.ReadCurrent(executablePath);

        UpdateInfo? update = null;
        Exception? pollError = null;
        bool cancelled = false;
        using (var spinner = new InPlaceSpinner("sniffing for updates"))
        {
            try
            {
                update = await source.CheckForUpdateAsync(version, ct).ConfigureAwait(false);
                spinner.Complete(
                    update is null ? "up to date." : $"new version found: v{update.Version}",
                    Color.LightGreen);
            }
            catch (OperationCanceledException)
            {
                spinner.Complete("interrupted.", Color.DarkGray);
                cancelled = true;
            }
            catch (Exception ex)
            {
                spinner.Complete("poll failed.", Color.Yellow);
                pollError = ex;
            }
        }

        if (cancelled) return;
        if (pollError is not null)
        {
            ConsoleOutput.Husky($"update check failed: {pollError.Message}");
            return;
        }

        // Refresh the per-session cache regardless of whether a new version
        // was found — apps in manual mode may call update-check at any time.
        cachedUpdate = update;
        UpdateAppSessionCache(session, version, update);
        RefreshUpdateActionState();

        if (update is null) return;

        // Manual mode: notify the app and wait for update-now (LEASH §3.5.11).
        if (IsSessionInManualMode(session))
        {
            // §3.5.11 says "once per discovered version" — skip the push if
            // we've already announced this exact version on this session.
            if (lastPushedManualVersion == update.Version) return;

            ConsoleOutput.Husky("manual mode — notifying app, waiting for trigger.");
            try
            {
                await session!.PipeServer.PushUpdateAvailableAsync(
                    new UpdateAvailablePayload(
                        CurrentVersion: version,
                        NewVersion: update.Version,
                        DownloadSizeBytes: null),
                    ct).ConfigureAwait(false);
                lastPushedManualVersion = update.Version;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                ConsoleOutput.Husky($"could not push update-available: {ex.Message}");
            }
            return;
        }

        await TriggerUpdateAsync(update, ct).ConfigureAwait(false);
    }

    private async Task TriggerUpdateAsync(UpdateInfo update, CancellationToken ct)
    {
        TaskCompletionSource<bool> mark = StartUpdate();
        try
        {
            await updateFlow.RunAsync(
                update,
                stopAppAsync: c => StopCurrentSessionAsync(c),
                startAppAndAwaitHelloAsync: c => RestartAfterUpdateAsync(c),
                ct: ct).ConfigureAwait(false);
            restartPolicy.Reset();
            cachedUpdate = null;
            lastPushedManualVersion = null;
            RefreshUpdateActionState();
            ConsoleOutput.Husky($"update succeeded — now on v{update.Version}");
            mark.TrySetResult(true);
        }
        catch (UpdateException ex)
        {
            ConsoleOutput.Husky($"update aborted: {ex.Message}");
            mark.TrySetResult(false);
        }
        catch (OperationCanceledException) { mark.TrySetResult(false); }
        catch (Exception ex)
        {
            ConsoleOutput.Husky($"update aborted: {ex.Message}");
            mark.TrySetResult(false);
        }
    }

    private static bool IsSessionInManualMode(AppSession? session) =>
        session is not null
        && session.ConnectedApp.SupportsManualUpdates
        && session.ConnectedApp.UpdateMode == UpdateModes.Manual;

    private static void UpdateAppSessionCache(AppSession? session, string currentVersion, UpdateInfo? update)
    {
        if (session is null) return;
        if (update is null)
        {
            session.PipeServer.SetCurrentUpdateStatus(
                new UpdateStatusPayload(Available: false, CurrentVersion: currentVersion));
        }
        else
        {
            session.PipeServer.SetCurrentUpdateStatus(
                new UpdateStatusPayload(
                    Available: true,
                    CurrentVersion: currentVersion,
                    NewVersion: update.Version,
                    DownloadSizeBytes: null));
        }
    }

    /// <summary>
    /// Handles an inbound <c>update-check</c> RPC by polling the source
    /// synchronously, refreshing both the per-runtime cached
    /// <see cref="UpdateInfo"/> and the per-session
    /// <see cref="UpdateStatusPayload"/>, and returning the fresh payload
    /// for <see cref="AppPipeServer"/> to send as the reply (LEASH §3.5.9).
    /// On poll failure the exception bubbles so the pipe handler can fall
    /// back to the last cached status; the launcher logs the failure on its
    /// own console.
    /// </summary>
    private async Task<UpdateStatusPayload> RefreshUpdateStatusFromAppAsync(CancellationToken ct)
    {
        AppSession? session = CurrentSession;
        string version = session is not null
            ? session.ConnectedApp.Version
            : AppVersionReader.ReadCurrent(executablePath);

        UpdateInfo? update;
        try
        {
            update = await source.CheckForUpdateAsync(version, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ConsoleOutput.Husky(
                $"app asked for an update check — source unreachable: {ex.Message}",
                messageColor: Color.Yellow);
            throw;
        }

        cachedUpdate = update;
        UpdateAppSessionCache(session, version, update);
        RefreshUpdateActionState();

        ConsoleOutput.Husky(
            update is null
                ? "app asked for an update check — up to date."
                : $"app asked for an update check — new version found: v{update.Version}",
            messageColor: Color.LightGreen);

        return update is null
            ? new UpdateStatusPayload(Available: false, CurrentVersion: version)
            : new UpdateStatusPayload(
                Available: true,
                CurrentVersion: version,
                NewVersion: update.Version,
                DownloadSizeBytes: null);
    }

    private void OnUpdateNowFromApp()
    {
        UpdateInfo? snapshot = cachedUpdate;
        if (snapshot is null)
        {
            ConsoleOutput.Husky("update-now received but no update is cached.");
            return;
        }
        ConsoleOutput.Husky($"user triggered update — applying v{snapshot.Version}.");
        _ = Task.Run(() => TriggerUpdateAsync(snapshot, pollingToken));
    }

    /// <summary>
    /// TUI <c>[u]</c> hotkey: force a fresh source poll and, if the source
    /// reports a newer version than what's currently installed, run the
    /// normal apply flow (graceful shutdown → install → restart). Lets the
    /// human bypass the 60-minute polling tick when they want an immediate
    /// check + install. Distinct from the pipe-driven <c>update-now</c>
    /// path (<see cref="OnUpdateNowFromApp"/>) — that one trusts the
    /// app's prior <c>update-check</c> and applies the cached update.
    /// </summary>
    public void RequestUpdateNow()
    {
        if (Interlocked.CompareExchange(ref userCheckInFlight, 1, 0) != 0)
        {
            ConsoleOutput.Husky("check already in flight — ignoring [u].");
            return;
        }
        _ = Task.Run(async () =>
        {
            try { await CheckAndApplyAsync(pollingToken).ConfigureAwait(false); }
            finally { Interlocked.Exchange(ref userCheckInFlight, 0); }
        });
    }

    private async Task CheckAndApplyAsync(CancellationToken ct)
    {
        AppSession? session = CurrentSession;
        if (session is null || !session.ConnectedApp.SupportsManualUpdates) return;
        if (PeekUpdateInFlight() is not null)
        {
            ConsoleOutput.Husky("update already in flight — ignoring [u].");
            return;
        }

        string version = session.ConnectedApp.Version;
        UpdateInfo? update;
        using (var spinner = new InPlaceSpinner("user check — sniffing for updates"))
        {
            try
            {
                update = await source.CheckForUpdateAsync(version, ct).ConfigureAwait(false);
                spinner.Complete(
                    update is null ? "up to date." : $"new version found: v{update.Version}",
                    Color.LightGreen);
            }
            catch (OperationCanceledException)
            {
                spinner.Complete("interrupted.", Color.DarkGray);
                return;
            }
            catch (Exception ex)
            {
                spinner.Complete("poll failed.", Color.Yellow);
                ConsoleOutput.Husky($"update check failed: {ex.Message}");
                return;
            }
        }

        cachedUpdate = update;
        UpdateAppSessionCache(session, version, update);
        RefreshUpdateActionState();

        if (update is null) return;

        ConsoleOutput.Husky($"applying v{update.Version}.");
        await TriggerUpdateAsync(update, ct).ConfigureAwait(false);
    }

    private async Task<string> BootstrapAsync(UpdateInfo seed, CancellationToken ct)
    {
        ConsoleOutput.Husky($"new version found: v{seed.Version}");
        await updateFlow.RunAsync(
            seed,
            stopAppAsync: _ => Task.CompletedTask,
            startAppAndAwaitHelloAsync: _ => Task.CompletedTask,
            ct: ct).ConfigureAwait(false);
        return seed.Version;
    }

    /// <summary>
    /// Decide whether the seeded UpdateInfo represents a release newer than
    /// what's currently installed. The seed was polled with currentVersion
    /// "0.0.0" (during config resolution) so we re-compare against the
    /// installed version here.
    /// </summary>
    private static UpdateInfo? SelectBootUpdate(UpdateInfo? seed, string currentVersion)
    {
        if (seed is null) return null;
        if (!SemanticVersion.TryParse(seed.Version, out SemanticVersion remote)) return null;
        if (!SemanticVersion.TryParse(currentVersion, out SemanticVersion current)) return null;
        return remote > current ? seed : null;
    }

    private async Task ApplyUpdateAtBootAsync(UpdateInfo update, CancellationToken ct)
    {
        await updateFlow.RunAsync(
            update,
            stopAppAsync: _ => Task.CompletedTask,
            startAppAndAwaitHelloAsync: _ => Task.CompletedTask,
            ct: ct).ConfigureAwait(false);
    }

    private async Task<AppSession> StartSessionAsync(CancellationToken ct)
    {
        // Reset the dead trigger so the supervisor only reacts to *this*
        // session's watchdog escalation.
        lock (sessionGate)
            declaredDeadTrigger = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        AppSession session = await sessionLauncher.StartAsync(OnSessionDeclaredDead, ct).ConfigureAwait(false);
        session.PipeServer.OnUpdateNowRequested = OnUpdateNowFromApp;
        session.PipeServer.OnUpdateCheckRequested = RefreshUpdateStatusFromAppAsync;
        session.PipeServer.OnHealthChanged = ConsoleOutput.SetHealth;
        // Each new session re-announces its update state, so a fresh hello
        // resets the §3.5.11 "once per version" guard.
        lastPushedManualVersion = null;
        // §10.4 header: surface the new app's identity immediately; clear
        // the health slot until the first pong arrives.
        ConsoleOutput.SetAppInfo(session.ConnectedApp.Name, session.ConnectedApp.Version);
        ConsoleOutput.SetHealth(null);
        SetCurrentSession(session);
        RefreshUpdateActionState();
        return session;
    }

    /// <summary>
    /// Push the current <see cref="UpdateActionState"/> at the TUI's
    /// action bar (LEASH §10.4): hidden when no app is attached or the
    /// app does not advertise <c>manual-updates</c>; otherwise enabled.
    /// No longer cache-gated — pressing <c>[u]</c> always forces a fresh
    /// source poll, so there's no "nothing cached" disabled state.
    /// Called whenever the session changes.
    /// </summary>
    private void RefreshUpdateActionState()
    {
        AppSession? session = CurrentSession;
        UpdateActionState state = session is null || !session.ConnectedApp.SupportsManualUpdates
            ? UpdateActionState.Hidden
            : UpdateActionState.Enabled;
        ConsoleOutput.SetUpdateActionState(state);
    }

    private async Task StopCurrentSessionAsync(CancellationToken ct)
    {
        AppSession? session = CurrentSession;
        if (session is null) return;

        ConsoleOutput.Husky("asking app to sit.");
        await GracefulShutdown.StopAsync(
            session,
            reason: "update",
            shutdownTimeout: TimeSpan.FromSeconds(config.ShutdownTimeoutSec),
            killAfter: TimeSpan.FromSeconds(config.KillAfterSec),
            hardKill: ct).ConfigureAwait(false);

        await DisposeCurrentSessionAsync().ConfigureAwait(false);
    }

    private async Task RestartAfterUpdateAsync(CancellationToken ct)
    {
        AppSession session = await StartSessionAsync(ct).ConfigureAwait(false);
        AnnounceUp(session);
    }

    private void OnSessionDeclaredDead(AppSession session)
    {
        ConsoleOutput.Husky("no answer. growling.");
        session.Kill();
        TaskCompletionSource trigger;
        lock (sessionGate) trigger = declaredDeadTrigger;
        trigger.TrySetResult();
    }

    private static void AnnounceUp(AppSession session) =>
        ConsoleOutput.Husky($"{session.ConnectedApp.Name} v{session.ConnectedApp.Version} is up.");

    private AppSession? CurrentSession
    {
        get { lock (sessionGate) return currentSession; }
    }

    private void SetCurrentSession(AppSession session)
    {
        TaskCompletionSource? previousTrigger;
        lock (sessionGate)
        {
            currentSession = session;
            previousTrigger = sessionStartedTrigger;
            sessionStartedTrigger = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        previousTrigger.TrySetResult();
    }

    private TaskCompletionSource<bool> StartUpdate()
    {
        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sessionGate) updateInFlight = tcs;
        return tcs;
    }

    private Task<bool>? TakeUpdateInFlight()
    {
        TaskCompletionSource<bool>? tcs;
        lock (sessionGate)
        {
            tcs = updateInFlight;
            updateInFlight = null;
        }
        return tcs?.Task;
    }

    private Task<bool>? PeekUpdateInFlight()
    {
        lock (sessionGate) return updateInFlight?.Task;
    }

    private async Task DisposeCurrentSessionAsync()
    {
        AppSession? toDispose;
        lock (sessionGate)
        {
            toDispose = currentSession;
            currentSession = null;
        }
        // Header reverts to its pre-attach state so the centre reads
        // '(starting…)' until the next session's hello lands; the
        // action bar's [u] hint disappears with no app to target.
        ConsoleOutput.SetAppInfo(null, null);
        ConsoleOutput.SetHealth(null);
        ConsoleOutput.SetUpdateActionState(UpdateActionState.Hidden);
        if (toDispose is not null)
            await toDispose.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task AwaitCancellationAsync(CancellationToken token)
    {
        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using CancellationTokenRegistration _ = token.Register(() => tcs.TrySetResult());
        await tcs.Task.ConfigureAwait(false);
    }
}
