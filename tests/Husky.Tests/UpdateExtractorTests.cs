using Husky;
using Husky.Tests.Fixtures;

namespace Husky.Tests;

[Collection(ConsoleOutputGateCollection.Name)]
public sealed class UpdateExtractorTests
{
    [Fact]
    public void Extract_unzips_a_valid_release_and_keeps_the_executable()
    {
        using TempDirectory temp = TempDirectory.Create();
        string zipPath = FakeRelease.Valid().BuildZip(temp.Path, "package.zip");
        string extracted = Path.Combine(temp.Path, "extracted");

        UpdateExtractor.Extract(zipPath, extracted, "app/Husky.TestApp.exe");

        Assert.True(File.Exists(Path.Combine(extracted, "app", "Husky.TestApp.exe")));
        Assert.True(File.Exists(Path.Combine(extracted, "app", "release.notes")));
    }

    [Fact]
    public void Extract_makes_the_executable_runnable_when_the_zip_carries_no_unix_mode()
    {
        if (OperatingSystem.IsWindows()) return;

        // FakeReleaseBuilder writes entries without Unix attributes, the way
        // a zip made on Windows looks; they extract as 0644.
        using TempDirectory temp = TempDirectory.Create();
        string zipPath = FakeRelease.Valid().BuildZip(temp.Path, "package.zip");
        string extracted = Path.Combine(temp.Path, "extracted");

        UpdateExtractor.Extract(zipPath, extracted, "app/Husky.TestApp.exe");

        UnixFileMode exe = File.GetUnixFileMode(Path.Combine(extracted, "app", "Husky.TestApp.exe"));
        Assert.True(exe.HasFlag(UnixFileMode.UserExecute));
        Assert.Equal(exe.HasFlag(UnixFileMode.GroupRead), exe.HasFlag(UnixFileMode.GroupExecute));
        Assert.Equal(exe.HasFlag(UnixFileMode.OtherRead), exe.HasFlag(UnixFileMode.OtherExecute));

        UnixFileMode companion = File.GetUnixFileMode(Path.Combine(extracted, "app", "release.notes"));
        Assert.False(companion.HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void Extract_throws_when_the_executable_is_missing_from_the_package()
    {
        using TempDirectory temp = TempDirectory.Create();
        string zipPath = FakeRelease.MissingExecutable().BuildZip(temp.Path, "package.zip");
        string extracted = Path.Combine(temp.Path, "extracted");

        UpdateException ex = Assert.Throws<UpdateException>(() =>
            UpdateExtractor.Extract(zipPath, extracted, "app/Husky.TestApp.exe"));

        Assert.Contains("executable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Extract_throws_when_the_executable_is_at_the_wrong_path()
    {
        using TempDirectory temp = TempDirectory.Create();
        string zipPath = FakeRelease.WrongStructure().BuildZip(temp.Path, "package.zip");
        string extracted = Path.Combine(temp.Path, "extracted");

        UpdateException ex = Assert.Throws<UpdateException>(() =>
            UpdateExtractor.Extract(zipPath, extracted, "app/Husky.TestApp.exe"));

        Assert.Contains("app/Husky.TestApp.exe", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_throws_UpdateException_when_the_zip_is_corrupt()
    {
        using TempDirectory temp = TempDirectory.Create();
        string zipPath = FakeRelease.Valid().BuildZip(temp.Path, "package.zip");
        FakeReleaseBuilder.TruncateInPlace(zipPath, fraction: 0.3);

        string extracted = Path.Combine(temp.Path, "extracted");

        Assert.Throws<UpdateException>(() =>
            UpdateExtractor.Extract(zipPath, extracted, "app/Husky.TestApp.exe"));
    }

    [Fact]
    public void Extract_throws_UpdateException_when_the_zip_does_not_exist()
    {
        using TempDirectory temp = TempDirectory.Create();
        string missing = Path.Combine(temp.Path, "missing.zip");

        UpdateException ex = Assert.Throws<UpdateException>(() =>
            UpdateExtractor.Extract(missing, Path.Combine(temp.Path, "extracted"), "app/exe"));

        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Extract_clears_the_target_directory_before_unzipping()
    {
        using TempDirectory temp = TempDirectory.Create();
        string extracted = Path.Combine(temp.Path, "extracted");
        Directory.CreateDirectory(extracted);
        string staleFile = Path.Combine(extracted, "stale.txt");
        File.WriteAllText(staleFile, "should be gone");

        string zipPath = FakeRelease.Valid().BuildZip(temp.Path, "package.zip");

        UpdateExtractor.Extract(zipPath, extracted, "app/Husky.TestApp.exe");

        Assert.False(File.Exists(staleFile));
    }
}
