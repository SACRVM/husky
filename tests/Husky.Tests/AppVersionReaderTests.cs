using Husky;
using Husky.Tests.Fixtures;

namespace Husky.Tests;

public sealed class AppVersionReaderTests
{
    [Fact]
    public void ReadCurrent_returns_bootstrap_version_when_file_does_not_exist()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe");

        string version = AppVersionReader.ReadCurrent(missing);

        Assert.Equal(AppVersionReader.BootstrapVersion, version);
    }

    [Fact]
    public void ReadCurrent_returns_FileVersion_for_an_existing_executable()
    {
        // The TestApp binary always carries a FileVersion (default 1.0.0.0).
        string version = AppVersionReader.ReadCurrent(TestAppLocator.ResolvePath());

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.NotEqual(AppVersionReader.BootstrapVersion, version);
    }

    [Fact]
    public void ReadCurrent_falls_back_to_the_installed_version_when_the_executable_carries_none()
    {
        // Any non-PE file stands in for a native ELF binary or a Linux
        // single-file bundle: FileVersionInfo finds nothing to read.
        using TempDirectory temp = TempDirectory.Create();
        string native = temp.Combine("matchmaker");
        File.WriteAllText(native, "#!/bin/sh\necho native\n");

        Assert.Equal("0.3.1", AppVersionReader.ReadCurrent(native, installedVersion: "0.3.1"));
        Assert.Equal(AppVersionReader.BootstrapVersion, AppVersionReader.ReadCurrent(native));
    }

    [Fact]
    public void ReadCurrent_prefers_the_executable_FileVersion_over_the_installed_version()
    {
        string version = AppVersionReader.ReadCurrent(TestAppLocator.ResolvePath(), installedVersion: "9.9.9");

        Assert.NotEqual("9.9.9", version);
        Assert.Equal(AppVersionReader.ReadCurrent(TestAppLocator.ResolvePath()), version);
    }

    [Fact]
    public void ReadCurrent_ignores_the_installed_version_when_the_executable_is_missing()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}");

        Assert.Equal(AppVersionReader.BootstrapVersion, AppVersionReader.ReadCurrent(missing, installedVersion: "0.3.1"));
    }

    [Fact]
    public void ReadCurrent_throws_when_path_is_blank()
    {
        Assert.Throws<ArgumentException>(() => AppVersionReader.ReadCurrent("  "));
    }
}
