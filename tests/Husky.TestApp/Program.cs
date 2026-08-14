using Husky.Client;

const string ModeEnvVar = "HUSKY_TESTAPP_MODE";
const string ModeNormal = "normal";
const string ModeSlowShutdown = "slow-shutdown";
const string ModeWait = "wait";
const string ModeCrash = "crash";
const string ModeManualUpdate = "manual-update";
const string ModeSelfUpdate = "self-update";

string mode = Environment.GetEnvironmentVariable(ModeEnvVar) ?? ModeNormal;

// Distinct stdout + stderr markers — the launcher's stdout/stderr forwarding tests
// pin on these strings.
Console.Out.WriteLine($"testapp: ready (mode={mode})");
Console.Out.Flush();
Console.Error.WriteLine("testapp: hello stderr");
Console.Error.Flush();

// 'wait' mode: do not attach, sleep forever. Used to exercise the launcher's
// hard-kill path without needing a pipe server in scope.
if (mode == ModeWait)
{
    await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
    return 0;
}

if (!HuskyClient.IsHosted)
{
    Console.Out.WriteLine("testapp: standalone — exiting cleanly.");
    return 0;
}

HuskyClientOptions clientOptions = mode == ModeManualUpdate
    ? HuskyClientOptions.Default with { UpdateMode = HuskyUpdateMode.Manual }
    : HuskyClientOptions.Default;

await using HuskyClient client = await HuskyClient.AttachAsync(clientOptions);

if (mode == ModeCrash)
{
    Console.Out.WriteLine("testapp: crash mode — exiting with code 7.");
    Console.Out.Flush();
    await Task.Delay(TimeSpan.FromMilliseconds(50));
    return 7;
}

switch (mode)
{
    case ModeSlowShutdown:
        // Register a handler that never returns within any reasonable launcher
        // shutdown window — exercises the launcher's hard-kill path.
        client.OnShutdown(async (_, _) =>
        {
            Console.Out.WriteLine("testapp: slow-shutdown handler entered — sleeping.");
            Console.Out.Flush();
            await Task.Delay(TimeSpan.FromMinutes(10), CancellationToken.None);
        });
        break;

    case ModeManualUpdate:
        // Subscribe to update-available; on the first push, log a marker and
        // immediately call RequestUpdateAsync to trigger the launcher's update
        // flow. This exercises the LEASH §3.5.11 / §3.5.12 round-trip.
        client.UpdateAvailable += (_, info) =>
        {
            Console.Out.WriteLine(
                $"testapp: update-available received (current={info.CurrentVersion} new={info.NewVersion}) — triggering update.");
            Console.Out.Flush();
            _ = client.RequestUpdateAsync();
        };
        client.OnShutdown((reason, _) =>
        {
            Console.Out.WriteLine($"testapp: shutdown received (reason={reason}).");
            Console.Out.Flush();
            return Task.CompletedTask;
        });
        break;

    case ModeSelfUpdate:
        // Poll the launcher over the pipe until the source advertises an
        // update, then pull the trigger. Lets E2E tests drive the
        // mid-session update path (update-check → update-now) without
        // waiting for the launcher's polling cadence.
        client.OnShutdown((reason, _) =>
        {
            Console.Out.WriteLine($"testapp: shutdown received (reason={reason}).");
            Console.Out.Flush();
            return Task.CompletedTask;
        });
        _ = Task.Run(async () =>
        {
            while (!client.ShutdownToken.IsCancellationRequested)
            {
                try
                {
                    HuskyUpdateInfo? update = await client.CheckForUpdateAsync(client.ShutdownToken);
                    if (update is not null)
                    {
                        Console.Out.WriteLine($"testapp: update found (new={update.NewVersion}) — requesting update.");
                        Console.Out.Flush();
                        await client.RequestUpdateAsync(client.ShutdownToken);
                        return;
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Console.Out.WriteLine($"testapp: update check failed ({ex.Message}) — retrying.");
                    Console.Out.Flush();
                }

                try { await Task.Delay(TimeSpan.FromMilliseconds(250), client.ShutdownToken); }
                catch (OperationCanceledException) { return; }
            }
        });
        break;

    case ModeNormal:
    default:
        client.OnShutdown((reason, _) =>
        {
            Console.Out.WriteLine($"testapp: shutdown received (reason={reason}).");
            Console.Out.Flush();
            return Task.CompletedTask;
        });
        break;
}

Console.Out.WriteLine("testapp: attached, awaiting shutdown.");
Console.Out.Flush();

try
{
    await Task.Delay(Timeout.InfiniteTimeSpan, client.ShutdownToken);
}
catch (OperationCanceledException) { /* normal */ }

Console.Out.WriteLine("testapp: exiting.");
return 0;
