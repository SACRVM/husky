using Husky;
using Husky.Tests.Fixtures;

namespace Husky.Tests;

public sealed class InstallStateStoreTests
{
    [Fact]
    public void ReadVersion_returns_what_Write_stored_for_the_same_executable()
    {
        using TempDirectory temp = TempDirectory.Create();

        InstallStateStore.Write(temp.Path, "0.3.1", "app/matchmaker");

        Assert.Equal("0.3.1", InstallStateStore.ReadVersion(temp.Path, "app/matchmaker"));
    }

    [Fact]
    public void ReadVersion_treats_backslashes_like_forward_slashes()
    {
        using TempDirectory temp = TempDirectory.Create();

        InstallStateStore.Write(temp.Path, "0.3.1", "app\\matchmaker");

        Assert.Equal("0.3.1", InstallStateStore.ReadVersion(temp.Path, "app/matchmaker"));
    }

    [Fact]
    public void ReadVersion_returns_null_when_nothing_was_written()
    {
        using TempDirectory temp = TempDirectory.Create();

        Assert.Null(InstallStateStore.ReadVersion(temp.Path, "app/matchmaker"));
    }

    [Fact]
    public void ReadVersion_returns_null_when_the_state_belongs_to_another_executable()
    {
        using TempDirectory temp = TempDirectory.Create();

        InstallStateStore.Write(temp.Path, "0.3.1", "app/matchmaker");

        Assert.Null(InstallStateStore.ReadVersion(temp.Path, "app/other"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "executable": "app/matchmaker" }""")]
    public void ReadVersion_returns_null_for_a_broken_state_file(string content)
    {
        using TempDirectory temp = TempDirectory.Create();
        File.WriteAllText(temp.Combine(InstallStateStore.FileName), content);

        Assert.Null(InstallStateStore.ReadVersion(temp.Path, "app/matchmaker"));
    }
}
