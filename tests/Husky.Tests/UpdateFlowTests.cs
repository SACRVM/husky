using Husky;
using Husky.Tests.Fixtures;

namespace Husky.Tests;

[Collection(ConsoleOutputGateCollection.Name)]
public sealed class UpdateFlowTests
{
    [Fact]
    public async Task RunAsync_executes_stop_install_and_start_for_a_valid_update()
    {
        await using TestRig rig = await TestRig.CreateAsync();
        rig.PublishRelease(version: "2.0.0", body: "v2-binary");

        int stopCalls = 0;
        int startCalls = 0;

        await rig.Flow.RunAsync(
            rig.UpdateInfo("2.0.0", sha256: rig.LastReleaseSha256),
            stopAppAsync: _ => { stopCalls++; return Task.CompletedTask; },
            startAppAndAwaitHelloAsync: _ => { startCalls++; return Task.CompletedTask; });

        Assert.Equal(1, stopCalls);
        Assert.Equal(1, startCalls);

        string installedExe = Path.Combine(rig.InstallDir.Path, "app", "Husky.TestApp.exe");
        Assert.Equal("v2-binary", File.ReadAllText(installedExe));
    }

    [Fact]
    public async Task RunAsync_remembers_the_installed_version()
    {
        await using TestRig rig = await TestRig.CreateAsync();
        rig.PublishRelease(version: "2.0.0", body: "v2-binary");

        await rig.Flow.RunAsync(
            rig.UpdateInfo("2.0.0"),
            stopAppAsync: _ => Task.CompletedTask,
            startAppAndAwaitHelloAsync: _ => Task.CompletedTask);

        Assert.Equal("2.0.0", rig.Flow.ReadRememberedVersion());
        Assert.True(File.Exists(Path.Combine(rig.InstallDir.Path, InstallStateStore.FileName)));
    }

    [Fact]
    public async Task RunAsync_keeps_the_remembered_version_when_phase_1_fails()
    {
        await using TestRig rig = await TestRig.CreateAsync();
        rig.PublishRelease(version: "2.0.0", body: "v2-binary");
        await rig.Flow.RunAsync(
            rig.UpdateInfo("2.0.0"),
            stopAppAsync: _ => Task.CompletedTask,
            startAppAndAwaitHelloAsync: _ => Task.CompletedTask);

        rig.PublishRelease(version: "3.0.0", body: "v3-binary");
        await Assert.ThrowsAsync<UpdateException>(async () =>
            await rig.Flow.RunAsync(
                rig.UpdateInfo("3.0.0", sha256: "deadbeef"),
                stopAppAsync: _ => Task.CompletedTask,
                startAppAndAwaitHelloAsync: _ => Task.CompletedTask));

        Assert.Equal("2.0.0", rig.Flow.ReadRememberedVersion());
    }

    [Fact]
    public async Task RunAsync_skips_install_when_phase_1_fails_with_hash_mismatch()
    {
        await using TestRig rig = await TestRig.CreateAsync();
        rig.PublishRelease(version: "2.0.0", body: "v2-binary");

        int stopCalls = 0;
        int startCalls = 0;

        UpdateException ex = await Assert.ThrowsAsync<UpdateException>(async () =>
            await rig.Flow.RunAsync(
                rig.UpdateInfo("2.0.0", sha256: "deadbeef"),
                stopAppAsync: _ => { stopCalls++; return Task.CompletedTask; },
                startAppAndAwaitHelloAsync: _ => { startCalls++; return Task.CompletedTask; }));

        Assert.Contains("SHA-256", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, stopCalls);
        Assert.Equal(0, startCalls);

        // Install dir untouched.
        Assert.False(File.Exists(Path.Combine(rig.InstallDir.Path, "app", "Husky.TestApp.exe")));
    }

    [Fact]
    public async Task RunAsync_skips_install_when_package_is_missing_the_executable()
    {
        await using TestRig rig = await TestRig.CreateAsync();
        rig.PublishRelease(
            version: "2.0.0",
            spec: FakeRelease.MissingExecutable());

        int stopCalls = 0;

        await Assert.ThrowsAsync<UpdateException>(async () =>
            await rig.Flow.RunAsync(
                rig.UpdateInfo("2.0.0"),
                stopAppAsync: _ => { stopCalls++; return Task.CompletedTask; },
                startAppAndAwaitHelloAsync: _ => Task.CompletedTask));

        Assert.Equal(0, stopCalls);
    }

    [Fact]
    public async Task RunAsync_runs_with_a_no_op_stop_for_bootstrap_mode()
    {
        await using TestRig rig = await TestRig.CreateAsync();
        rig.PublishRelease(version: "1.0.0", body: "boot-binary");

        int startCalls = 0;
        await rig.Flow.RunAsync(
            rig.UpdateInfo("1.0.0"),
            stopAppAsync: _ => Task.CompletedTask, // no-op for bootstrap
            startAppAndAwaitHelloAsync: _ => { startCalls++; return Task.CompletedTask; });

        Assert.Equal(1, startCalls);
        string installedExe = Path.Combine(rig.InstallDir.Path, "app", "Husky.TestApp.exe");
        Assert.Equal("boot-binary", File.ReadAllText(installedExe));
    }

    [Fact]
    public async Task RunAsync_throws_when_a_concurrent_update_is_in_flight()
    {
        await using TestRig rig = await TestRig.CreateAsync();
        rig.PublishRelease(version: "2.0.0", body: "v2");

        TaskCompletionSource secondCallStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task first = rig.Flow.RunAsync(
            rig.UpdateInfo("2.0.0"),
            stopAppAsync: async _ => { secondCallStarted.TrySetResult(); await releaseFirstCall.Task; },
            startAppAndAwaitHelloAsync: _ => Task.CompletedTask);

        await secondCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        UpdateException ex = await Assert.ThrowsAsync<UpdateException>(async () =>
            await rig.Flow.RunAsync(
                rig.UpdateInfo("2.0.0"),
                stopAppAsync: _ => Task.CompletedTask,
                startAppAndAwaitHelloAsync: _ => Task.CompletedTask));

        Assert.Contains("Another update", ex.Message, StringComparison.OrdinalIgnoreCase);

        releaseFirstCall.TrySetResult();
        await first;
    }

    [Fact]
    public async Task RunAsync_clears_download_directory_between_runs()
    {
        await using TestRig rig = await TestRig.CreateAsync();

        // First run leaves download/ populated.
        rig.PublishRelease(version: "2.0.0", body: "v2");
        await rig.Flow.RunAsync(
            rig.UpdateInfo("2.0.0"),
            stopAppAsync: _ => Task.CompletedTask,
            startAppAndAwaitHelloAsync: _ => Task.CompletedTask);

        string staleFile = Path.Combine(rig.InstallDir.Path, "download", "leftover.zip");
        File.WriteAllText(staleFile, "should be cleared");

        rig.PublishRelease(version: "3.0.0", body: "v3");
        await rig.Flow.RunAsync(
            rig.UpdateInfo("3.0.0"),
            stopAppAsync: _ => Task.CompletedTask,
            startAppAndAwaitHelloAsync: _ => Task.CompletedTask);

        Assert.False(File.Exists(staleFile));
    }

    private sealed class TestRig : IAsyncDisposable
    {
        public TempDirectory InstallDir { get; }
        public TempDirectory ReleasesDir { get; }
        public FakeHttpServer Server { get; }
        public UpdateFlow Flow { get; }
        public HttpClient Http { get; }
        public string LastReleaseSha256 { get; private set; } = string.Empty;
        public string LastAssetFileName { get; private set; } = string.Empty;

        private TestRig(
            TempDirectory installDir,
            TempDirectory releasesDir,
            FakeHttpServer server,
            HttpClient http)
        {
            InstallDir = installDir;
            ReleasesDir = releasesDir;
            Server = server;
            Http = http;
            UpdateDownloader downloader = new(http);
            Flow = new UpdateFlow(downloader, InstallDir.Path, "app/Husky.TestApp.exe");
        }

        public static async Task<TestRig> CreateAsync()
        {
            TempDirectory install = TempDirectory.Create("husky-installdir");
            TempDirectory releases = TempDirectory.Create("husky-releases");
            FakeHttpServer server = FakeHttpServer.Start(releases.Path);
            HttpClient http = new();
            await Task.CompletedTask;
            return new TestRig(install, releases, server, http);
        }

        public void PublishRelease(string version, string body = "v-binary", FakeReleaseBuilder? spec = null)
        {
            FakeReleaseBuilder builder = spec ?? FakeRelease.Valid().WithExecutable(
                "app/Husky.TestApp.exe", body);
            string fileName = $"Husky.TestApp-{version}.zip";
            string zipPath = builder.BuildZip(ReleasesDir.Path, fileName);
            LastReleaseSha256 = FakeReleaseBuilder.Sha256(zipPath);
            LastAssetFileName = fileName;
        }

        public UpdateInfo UpdateInfo(string version, string? sha256 = null) =>
            new(Version: version,
                DownloadUrl: Server.Url(LastAssetFileName),
                Sha256: sha256 ?? LastReleaseSha256);

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await Server.DisposeAsync();
            ReleasesDir.Dispose();
            InstallDir.Dispose();
        }
    }
}
