using System.IO.Compression;
using System.Runtime.Versioning;
using Retro.Crt;

namespace Husky;

internal static class UpdateExtractor
{
    /// <summary>
    /// Extracts <paramref name="zipPath"/> into <paramref name="targetDirectory"/>
    /// (which is wiped first), then verifies the package contains the expected
    /// executable. Implements LEASH §7.2 steps 4-5.
    /// </summary>
    public static void Extract(
        string zipPath,
        string targetDirectory,
        string expectedExecutableRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedExecutableRelativePath);

        if (!File.Exists(zipPath))
            throw new UpdateException($"Update package not found: '{zipPath}'.");

        if (Directory.Exists(targetDirectory))
            Directory.Delete(targetDirectory, recursive: true);
        Directory.CreateDirectory(targetDirectory);

        // ZipFile.ExtractToDirectory is synchronous with no progress hook. The
        // in-place spinner gives the user a "still working" cue and stays in
        // the LEASH §10.5 voice on its way out.
        using (var spinner = new InPlaceSpinner("extracting"))
        {
            try
            {
                ZipFile.ExtractToDirectory(zipPath, targetDirectory);
                spinner.Complete("extracted.", Color.LightGreen);
            }
            catch (InvalidDataException ex)
            {
                spinner.Complete("extract failed.", Color.LightRed);
                throw new UpdateException($"Update package is corrupt: {ex.Message}", ex);
            }
        }

        string expectedAbsolute = Path.Combine(
            targetDirectory,
            expectedExecutableRelativePath.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(expectedAbsolute))
            throw new UpdateException(
                $"Update package is missing the expected executable at '{expectedExecutableRelativePath}'.");

        if (!OperatingSystem.IsWindows())
            EnsureExecutable(expectedAbsolute);
    }

    /// <summary>
    /// A zip made on Windows (or by PowerShell's Compress-Archive anywhere)
    /// stores no Unix mode, so the entry extracts as 0644 and the app fails
    /// to start with "Permission denied". Add execute wherever read is
    /// granted, like <c>chmod +x</c>. Phase 2's copy carries the mode over
    /// into the install directory.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private static void EnsureExecutable(string path)
    {
        UnixFileMode mode = File.GetUnixFileMode(path);
        if (mode.HasFlag(UnixFileMode.UserExecute)) return;

        mode |= UnixFileMode.UserExecute;
        if (mode.HasFlag(UnixFileMode.GroupRead)) mode |= UnixFileMode.GroupExecute;
        if (mode.HasFlag(UnixFileMode.OtherRead)) mode |= UnixFileMode.OtherExecute;
        File.SetUnixFileMode(path, mode);
    }
}
