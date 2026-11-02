using Retro.Crt;

namespace Husky;

internal sealed class UpdateFlow(
    UpdateDownloader downloader,
    string installDirectory,
    string executableRelativePath) : IDisposable
{
    private readonly SemaphoreSlim updateLock = new(initialCount: 1, maxCount: 1);

    public void Dispose() => updateLock.Dispose();

    /// <summary>
    /// Phase 1 (LEASH §7.2) followed by phase 2 (§7.3). The launcher provides
    /// <paramref name="stopAppAsync"/> (graceful stop) and
    /// <paramref name="startAppAndAwaitHelloAsync"/> (start the new app and
    /// wait for its hello). For bootstrap mode, <paramref name="stopAppAsync"/>
    /// should be a no-op.
    /// </summary>
    public async Task RunAsync(
        UpdateInfo update,
        Func<CancellationToken, Task> stopAppAsync,
        Func<CancellationToken, Task> startAppAndAwaitHelloAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(stopAppAsync);
        ArgumentNullException.ThrowIfNull(startAppAndAwaitHelloAsync);

        if (!await updateLock.WaitAsync(0, ct).ConfigureAwait(false))
            throw new UpdateException("Another update is already running.");

        try
        {
            string downloadDir = Path.Combine(installDirectory, "download");
            string extractedDir = Path.Combine(downloadDir, "extracted");

            // Phase 1 — preparation; app keeps running.
            ClearDirectory(downloadDir);

            string assetFileName = DeriveAssetFileName(update);
            string zipPath = Path.Combine(downloadDir, assetFileName);

            await downloader.DownloadAsync(update.DownloadUrl, update.Sha256, zipPath, ct).ConfigureAwait(false);
            UpdateExtractor.Extract(zipPath, extractedDir, executableRelativePath);

            // Phase 2 — cutover.
            await stopAppAsync(ct).ConfigureAwait(false);
            UpdateInstaller.Install(extractedDir, installDirectory);
            RememberInstalledVersion(update.Version);
            await startAppAndAwaitHelloAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            updateLock.Release();
        }
    }

    /// <summary>
    /// The version this flow last installed for the configured executable,
    /// or null when none is remembered (LEASH §6).
    /// </summary>
    public string? ReadRememberedVersion() =>
        InstallStateStore.ReadVersion(installDirectory, executableRelativePath);

    private void RememberInstalledVersion(string version)
    {
        // The new files are already in place. Failing the update over the
        // state file would leave the app stopped; without it, a binary that
        // carries no version just reads as 0.0.0 again and gets reinstalled.
        try
        {
            InstallStateStore.Write(installDirectory, version, executableRelativePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ConsoleOutput.Husky(
                $"could not write {InstallStateStore.FileName}: {ex.Message}",
                messageColor: Color.Yellow);
        }
    }

    private static string DeriveAssetFileName(UpdateInfo update)
    {
        string fromUrl = Path.GetFileName(update.DownloadUrl.LocalPath);
        if (!string.IsNullOrEmpty(fromUrl)) return fromUrl;
        return $"package-{update.Version}.zip";
    }

    private static void ClearDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            return;
        }

        foreach (string dir in Directory.EnumerateDirectories(path))
            Directory.Delete(dir, recursive: true);
        foreach (string file in Directory.EnumerateFiles(path))
            File.Delete(file);
    }
}
