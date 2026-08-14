using Husky;

namespace Husky.Tests;

public sealed class CliArgsParserTests
{
    // Thin wrapper so individual tests can pass argv as `params` —
    // dodges CA1861 for short literal arrays at every call site.
    private static CliArgs Parse(params string[] argv) => CliArgsParser.Parse(argv);

    [Fact]
    public void Parse_returns_no_overrides_when_args_are_empty()
    {
        CliArgs result = Parse();

        Assert.Null(result.WorkingDirectory);
        Assert.Null(result.CliSource);
    }

    [Fact]
    public void Parse_picks_up_working_directory_override()
    {
        CliArgs result = Parse("--dir", "/some/path");

        Assert.Equal("/some/path", result.WorkingDirectory);
        Assert.Null(result.CliSource);
    }

    [Fact]
    public void Parse_builds_an_http_source_from_manifest_flag()
    {
        CliArgs result = Parse("--manifest", "https://example.org/manifest.json");

        Assert.NotNull(result.CliSource);
        Assert.Equal(SourceConfig.HttpType, result.CliSource.Type);
        Assert.Equal("https://example.org/manifest.json", result.CliSource.Manifest);
    }

    [Fact]
    public void Parse_builds_a_github_source_from_repo_flag()
    {
        CliArgs result = Parse("--repo", "SACRVM/husky");

        Assert.NotNull(result.CliSource);
        Assert.Equal(SourceConfig.GitHubType, result.CliSource.Type);
        Assert.Equal("SACRVM/husky", result.CliSource.Repo);
        Assert.Null(result.CliSource.Asset);
    }

    [Fact]
    public void Parse_combines_repo_and_asset_flags()
    {
        CliArgs result = Parse("--repo", "SACRVM/husky", "--asset", "Husky-{version}-win-x64.zip");

        Assert.NotNull(result.CliSource);
        Assert.Equal("SACRVM/husky", result.CliSource.Repo);
        Assert.Equal("Husky-{version}-win-x64.zip", result.CliSource.Asset);
    }

    [Fact]
    public void Parse_combines_manifest_and_dir_flags()
    {
        CliArgs result = Parse("--manifest", "https://example.org/manifest.json", "--dir", "D:\\Apps\\Umbrella");

        Assert.Equal("D:\\Apps\\Umbrella", result.WorkingDirectory);
        Assert.NotNull(result.CliSource);
        Assert.Equal(SourceConfig.HttpType, result.CliSource.Type);
    }

    [Fact]
    public void Parse_throws_when_manifest_and_repo_are_both_set()
    {
        HuskyConfigException ex = Assert.Throws<HuskyConfigException>(() =>
            Parse("--manifest", "https://example.org/manifest.json", "--repo", "SACRVM/husky"));

        Assert.Contains("mutually exclusive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_throws_when_asset_is_supplied_without_repo()
    {
        HuskyConfigException ex = Assert.Throws<HuskyConfigException>(() =>
            Parse("--asset", "x-{version}.zip"));

        Assert.Contains("--asset", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--repo", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_throws_when_a_flag_is_repeated()
    {
        HuskyConfigException ex = Assert.Throws<HuskyConfigException>(() =>
            Parse("--manifest", "https://example.org/a.json", "--manifest", "https://example.org/b.json"));

        Assert.Contains("once", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_throws_when_a_flag_is_missing_its_value()
    {
        HuskyConfigException ex = Assert.Throws<HuskyConfigException>(() =>
            Parse("--manifest"));

        Assert.Contains("requires a value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_throws_when_a_flag_value_looks_like_another_flag()
    {
        HuskyConfigException ex = Assert.Throws<HuskyConfigException>(() =>
            Parse("--manifest", "--repo"));

        Assert.Contains("requires a value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_throws_on_an_unknown_flag()
    {
        HuskyConfigException ex = Assert.Throws<HuskyConfigException>(() =>
            Parse("--whatever"));

        Assert.Contains("Unknown", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.org/manifest.json")]
    [InlineData("/relative/path")]
    public void Parse_throws_when_manifest_is_not_an_absolute_http_url(string value)
    {
        HuskyConfigException ex = Assert.Throws<HuskyConfigException>(() =>
            Parse("--manifest", value));

        Assert.Contains("--manifest", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-slug")]
    [InlineData("/leading-slash")]
    [InlineData("trailing/")]
    [InlineData("too/many/parts")]
    public void Parse_throws_when_repo_is_not_owner_slash_name(string value)
    {
        HuskyConfigException ex = Assert.Throws<HuskyConfigException>(() =>
            Parse("--repo", value));

        Assert.Contains("owner/name", ex.Message, StringComparison.Ordinal);
    }
}
