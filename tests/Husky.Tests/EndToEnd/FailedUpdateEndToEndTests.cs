using System.Collections.Concurrent;
using System.Text;
using Husky.Tests.Fixtures;

namespace Husky.Tests.EndToEnd;

/// <summary>
/// A failed update must never take the launcher down. The old session is
/// stopped on purpose during cutover, so its clean exit code says nothing
/// about the app being "done" — the launcher has to route the failure into
/// the crash-restart policy and, once the cap trips, park alive
/// (LEASH §7.3.6, §8.4).
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class FailedUpdateEndToEndTests
{
    [Fact]
    public async Task Launcher_survives_an_update_whose_new_build_cannot_start()
    {
        await using StagedLauncher staged = StagedLauncher.Create();

        // v2.0.0 "release" whose executable is garbage — installs fine,
        // can never start. Reproduces the failed-cutover outage.
        using TempDirectory releases = TempDirectory.Create("husky-broken-release");
        string zipPath = new FakeReleaseBuilder()
            .WithExecutable(staged.AppRelativeExecutable, body: "not a real binary")
            .BuildZip(releases.Path, "TestApp-2.0.0.zip");
        string sha256 = FakeReleaseBuilder.Sha256(zipPath);

        await using FakeHttpServer server = FakeHttpServer.Start(releases.Path);
        Uri zipUrl = server.Url("TestApp-2.0.0.zip");

        // Boot-time polls must see "up to date" so the broken build is only
        // offered to the running session; the test flips the switch once the
        // app is attached.
        bool offerUpdate = false;
        server.Map("/manifest.json", _ =>
        {
            string json = Volatile.Read(ref offerUpdate)
                ? $$"""{ "version": "2.0.0", "url": "{{zipUrl}}", "sha256": "{{sha256}}" }"""
                : """{ "version": "1.0.0", "url": "http://localhost/unused.zip" }""";
            return new RouteResponse(200, "application/json", Encoding.UTF8.GetBytes(json));
        });

        staged.WriteConfig($$"""
            {
              "name": "update-app",
              "executable": "{{staged.AppRelativeExecutable}}",
              "source": {
                "type": "http",
                "manifest": "{{server.Url("manifest.json")}}"
              },
              "checkMinutes": 60,
              "shutdownTimeoutSec": 5,
              "killAfterSec": 1,
              "restartAttempts": 2,
              "restartPauseSec": 1
            }
            """);

        ConcurrentQueue<string> stdoutLines = new();
        System.Diagnostics.Process launcher = staged.Start(
            onStandardOutput: stdoutLines.Enqueue,
            extraEnv: new Dictionary<string, string?> { ["HUSKY_TESTAPP_MODE"] = "self-update" });

        await WaitForFragmentAsync(stdoutLines, "awaiting shutdown", TimeSpan.FromSeconds(30));
        Volatile.Write(ref offerUpdate, true);

        // The TestApp's self-update loop finds v2.0.0 and pulls the trigger.
        // The broken build cannot start, so: update aborts, the restart
        // policy burns its attempts on the same broken binary, and the
        // launcher parks — alive, waiting for a fixed release.
        await WaitForFragmentAsync(stdoutLines, "update aborted", TimeSpan.FromSeconds(60));
        await WaitForFragmentAsync(stdoutLines, "enough. lying", TimeSpan.FromSeconds(60));

        Assert.Contains(stdoutLines, l => l.Contains("considering restart", StringComparison.Ordinal));
        Assert.Contains(stdoutLines, l => l.Contains("restart failed", StringComparison.Ordinal));
        Assert.DoesNotContain(stdoutLines, l => l.Contains("exited cleanly", StringComparison.Ordinal));

        // Give a wrongly-exiting launcher a moment to actually die before we
        // assert it didn't.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(launcher.HasExited, "a failed update must not take the launcher down.");
    }

    private static async Task WaitForFragmentAsync(
        ConcurrentQueue<string> lines, string fragment, TimeSpan timeout)
    {
        using CancellationTokenSource cts = new(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (lines.Any(line => line.Contains(fragment, StringComparison.Ordinal))) return;
            try { await Task.Delay(TimeSpan.FromMilliseconds(100), cts.Token); }
            catch (OperationCanceledException) { break; }
        }

        throw new TimeoutException(
            $"Did not see a line containing '{fragment}' within {timeout}.\nLines so far:\n{string.Join("\n", lines)}");
    }
}
