using System.Net;
using Husky;
using Husky.Tests.Fixtures;

namespace Husky.Tests;

public sealed class GitHubUpdateSourceTests
{
    [Fact]
    public async Task CheckForUpdateAsync_returns_update_when_remote_version_is_newer()
    {
        await using FakeHttpServer server = FakeHttpServer.StartEmpty();
        server.MapJson("/repos/acme/umbrella/releases/latest", """
            {
              "tag_name": "v1.4.3",
              "draft": false,
              "prerelease": false,
              "assets": [
                { "name": "UmbrellaBot-1.4.3.zip",
                  "browser_download_url": "https://example.test/UmbrellaBot-1.4.3.zip" }
              ]
            }
            """);

        using HttpClient http = new();
        GitHubUpdateSource source = new(http, server.Address, "acme/umbrella", "UmbrellaBot-{version}.zip", "0.1.0", rawBase: server.Address);

        UpdateInfo? update = await source.CheckForUpdateAsync("1.4.2", CancellationToken.None);

        Assert.NotNull(update);
        Assert.Equal("1.4.3", update!.Version);
        Assert.Equal("https://example.test/UmbrellaBot-1.4.3.zip", update.DownloadUrl.ToString());
        Assert.Null(update.Sha256);
    }

    [Fact]
    public async Task CheckForUpdateAsync_returns_null_when_remote_is_same_or_older()
    {
        await using FakeHttpServer server = FakeHttpServer.StartEmpty();
        server.MapJson("/repos/x/y/releases/latest", """
            {
              "tag_name": "1.0.0",
              "assets": [{ "name": "pkg-1.0.0.zip", "browser_download_url": "http://localhost/pkg-1.0.0.zip" }]
            }
            """);

        using HttpClient http = new();
        GitHubUpdateSource source = new(http, server.Address, "x/y", "pkg-{version}.zip", "0.1.0", rawBase: server.Address);

        Assert.Null(await source.CheckForUpdateAsync("1.0.0", CancellationToken.None));
        Assert.Null(await source.CheckForUpdateAsync("1.0.1", CancellationToken.None));
    }

    [Fact]
    public async Task CheckForUpdateAsync_strips_v_prefix_from_tag()
    {
        await using FakeHttpServer server = FakeHttpServer.StartEmpty();
        server.MapJson("/repos/x/y/releases/latest", """
            {
              "tag_name": "v2.0.0",
              "assets": [{ "name": "pkg-2.0.0.zip", "browser_download_url": "http://localhost/pkg-2.0.0.zip" }]
            }
            """);

        using HttpClient http = new();
        GitHubUpdateSource source = new(http, server.Address, "x/y", "pkg-{version}.zip", "0.1.0", rawBase: server.Address);

        UpdateInfo? update = await source.CheckForUpdateAsync("1.0.0", CancellationToken.None);
        Assert.Equal("2.0.0", update!.Version);
    }

    [Fact]
    public async Task CheckForUpdateAsync_throws_when_asset_pattern_does_not_match()
    {
        await using FakeHttpServer server = FakeHttpServer.StartEmpty();
        server.MapJson("/repos/x/y/releases/latest", """
            {
              "tag_name": "1.1.0",
              "assets": [{ "name": "OtherName.zip", "browser_download_url": "http://localhost/Other.zip" }]
            }
            """);

        using HttpClient http = new();
        GitHubUpdateSource source = new(http, server.Address, "x/y", "Bot-{version}.zip", "0.1.0");

        UpdateException ex = await Assert.ThrowsAsync<UpdateException>(() =>
            source.CheckForUpdateAsync("1.0.0", CancellationToken.None));
        Assert.Contains("Bot-1.1.0.zip", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckForUpdateAsync_skips_prereleases_by_default()
    {
        await using FakeHttpServer server = FakeHttpServer.StartEmpty();
        server.MapJson("/repos/x/y/releases/latest", """
            {
              "tag_name": "v2.0.0",
              "prerelease": true,
              "assets": [{ "name": "pkg-2.0.0.zip", "browser_download_url": "http://localhost/pkg.zip" }]
            }
            """);

        using HttpClient http = new();
        GitHubUpdateSource source = new(http, server.Address, "x/y", "pkg-{version}.zip", "0.1.0", rawBase: server.Address);

        Assert.Null(await source.CheckForUpdateAsync("1.0.0", CancellationToken.None));
    }

    [Fact]
    public async Task CheckForUpdateAsync_skips_semver_prerelease_tags_by_default()
    {
        await using FakeHttpServer server = FakeHttpServer.StartEmpty();
        server.MapJson("/repos/x/y/releases/latest", """
            {
              "tag_name": "v2.0.0-rc.1",
              "prerelease": false,
              "assets": [{ "name": "pkg-2.0.0-rc.1.zip", "browser_download_url": "http://localhost/pkg.zip" }]
            }
            """);

        using HttpClient http = new();
        GitHubUpdateSource source = new(http, server.Address, "x/y", "pkg-{version}.zip", "0.1.0", rawBase: server.Address);

        Assert.Null(await source.CheckForUpdateAsync("1.0.0", CancellationToken.None));
    }

    [Fact]
    public async Task CheckForUpdateAsync_returns_prerelease_when_explicitly_allowed()
    {
        await using FakeHttpServer server = FakeHttpServer.StartEmpty();
        server.MapJson("/repos/x/y/releases/latest", """
            {
              "tag_name": "v2.0.0-beta",
              "prerelease": true,
              "assets": [{ "name": "pkg-2.0.0-beta.zip", "browser_download_url": "http://localhost/pkg.zip" }]
            }
            """);

        using HttpClient http = new();
        GitHubUpdateSource source = new(
            http, server.Address, "x/y", "pkg-{version}.zip", "0.1.0", allowPreRelease: true, rawBase: server.Address);

        UpdateInfo? update = await source.CheckForUpdateAsync("1.0.0", CancellationToken.None);
        Assert.NotNull(update);
        Assert.Equal("2.0.0-beta", update!.Version);
    }

    [Fact]
    public async Task CheckForUpdateAsync_skips_drafts()
    {
        await using FakeHttpServer server = FakeHttpServer.StartEmpty();
        server.MapJson("/repos/x/y/releases/latest", """
            {
              "tag_name": "v9.9.9",
              "draft": true,
              "assets": [{ "name": "pkg-9.9.9.zip", "browser_download_url": "http://localhost/pkg.zip" }]
            }
            """);

        using HttpClient http = new();
        GitHubUpdateSource source = new(http, server.Address, "x/y", "pkg-{version}.zip", "0.1.0", rawBase: server.Address);

        Assert.Null(await source.CheckForUpdateAsync("1.0.0", CancellationToken.None));
    }

    [Fact]
    public async Task CheckForUpdateAsync_throws_on_http_error()
    {
        await using FakeHttpServer server = FakeHttpServer.StartEmpty();
        server.MapStatus("/repos/x/y/releases/latest", (int)HttpStatusCode.NotFound);

        using HttpClient http = new();
        GitHubUpdateSource source = new(http, server.Address, "x/y", "pkg-{version}.zip", "0.1.0", rawBase: server.Address);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            source.CheckForUpdateAsync("1.0.0", CancellationToken.None));
    }

    [Fact]
    public async Task CheckForUpdateAsync_sends_user_agent_with_launcher_version()
    {
        string? capturedUserAgent = null;
        await using FakeHttpServer server = FakeHttpServer.StartEmpty();
        server.Map("/repos/x/y/releases/latest", req =>
        {
            capturedUserAgent = req.Headers["User-Agent"];
            return new RouteResponse(200, "application/json",
                System.Text.Encoding.UTF8.GetBytes("""
                    { "tag_name": "1.0.0", "assets": [] }
                    """));
        });

        using HttpClient http = new();
        GitHubUpdateSource source = new(http, server.Address, "x/y", "pkg-{version}.zip", "0.1.0", rawBase: server.Address);

        await source.CheckForUpdateAsync("1.0.0", CancellationToken.None);

        Assert.Contains("Husky/0.1.0", capturedUserAgent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckForUpdateAsync_populates_config_from_husky_config_release_asset()
    {
        // LEASH §9.2 — first lookup path: a release asset literally named
        // husky.config.json next to the actual release ZIP.
        await using FakeHttpServer server = FakeHttpServer.StartEmpty();
        server.MapJson("/repos/x/y/releases/latest", $$"""
            {
              "tag_name": "v2.0.0",
              "assets": [
                { "name": "pkg-2.0.0.zip", "browser_download_url": "{{server.Address}}pkg.zip" },
                { "name": "husky.config.json", "browser_download_url": "{{server.Address}}config-asset.json" }
              ]
            }
            """);
        server.MapJson("/config-asset.json", """
            {
              "name": "fishbowl",
              "executable": "Fishbowl.exe",
              "checkMinutes": 15
            }
            """);

        using HttpClient http = new();
        GitHubUpdateSource source = new(http, server.Address, "x/y", "pkg-{version}.zip", "0.1.0", rawBase: server.Address);

        UpdateInfo? update = await source.CheckForUpdateAsync("1.0.0", CancellationToken.None);

        Assert.NotNull(update?.Config);
        Assert.Equal("fishbowl", update!.Config!.Name);
        Assert.Equal("Fishbowl.exe", update.Config.Executable);
        Assert.Equal(15, update.Config.CheckMinutes);
    }

    [Fact]
    public async Task CheckForUpdateAsync_falls_back_to_repo_root_for_husky_config()
    {
        // LEASH §9.2 — second lookup path: raw.githubusercontent.com/<repo>/HEAD/husky.config.json
        // when no asset by that name exists.
        await using FakeHttpServer server = FakeHttpServer.StartEmpty();
        server.MapJson("/repos/x/y/releases/latest", $$"""
            {
              "tag_name": "v2.0.0",
              "assets": [
                { "name": "pkg-2.0.0.zip", "browser_download_url": "{{server.Address}}pkg.zip" }
              ]
            }
            """);
        server.MapJson("/x/y/HEAD/husky.config.json", """
            {
              "name": "fallback-app",
              "executable": "FallbackApp.exe"
            }
            """);

        using HttpClient http = new();
        GitHubUpdateSource source = new(http, server.Address, "x/y", "pkg-{version}.zip", "0.1.0", rawBase: server.Address);

        UpdateInfo? update = await source.CheckForUpdateAsync("1.0.0", CancellationToken.None);

        Assert.NotNull(update?.Config);
        Assert.Equal("fallback-app", update!.Config!.Name);
    }

    [Fact]
    public async Task CheckForUpdateAsync_returns_null_config_when_neither_lookup_finds_anything()
    {
        // Both the asset path and the repo-root path return 404 → no config,
        // no exception, update still proceeds.
        await using FakeHttpServer server = FakeHttpServer.StartEmpty();
        server.MapJson("/repos/x/y/releases/latest", $$"""
            {
              "tag_name": "v2.0.0",
              "assets": [
                { "name": "pkg-2.0.0.zip", "browser_download_url": "{{server.Address}}pkg.zip" }
              ]
            }
            """);

        using HttpClient http = new();
        GitHubUpdateSource source = new(http, server.Address, "x/y", "pkg-{version}.zip", "0.1.0", rawBase: server.Address);

        UpdateInfo? update = await source.CheckForUpdateAsync("1.0.0", CancellationToken.None);

        Assert.NotNull(update);
        Assert.Null(update!.Config);
        Assert.False(update.SourceFieldDropped);
    }

    [Fact]
    public async Task CheckForUpdateAsync_picks_first_zip_asset_when_pattern_is_omitted()
    {
        // LEASH §9.2 — null/empty assetPattern means "first .zip on the release".
        // husky.config.json must be excluded so it never gets mistaken for the app.
        await using FakeHttpServer server = FakeHttpServer.StartEmpty();
        server.MapJson("/repos/x/y/releases/latest", $$"""
            {
              "tag_name": "v2.0.0",
              "assets": [
                { "name": "husky.config.json", "browser_download_url": "{{server.Address}}config.json" },
                { "name": "checksums.txt", "browser_download_url": "{{server.Address}}sums.txt" },
                { "name": "MyApp-2.0.0.zip", "browser_download_url": "{{server.Address}}myapp.zip" }
              ]
            }
            """);

        using HttpClient http = new();
        GitHubUpdateSource source = new(
            http, server.Address, "x/y", assetPattern: null, "0.1.0", rawBase: server.Address);

        UpdateInfo? update = await source.CheckForUpdateAsync("1.0.0", CancellationToken.None);

        Assert.NotNull(update);
        Assert.EndsWith("myapp.zip", update!.DownloadUrl.ToString());
    }

    [Fact]
    public async Task CheckForUpdateAsync_throws_when_pattern_is_omitted_and_no_zip_assets_exist()
    {
        await using FakeHttpServer server = FakeHttpServer.StartEmpty();
        server.MapJson("/repos/x/y/releases/latest", $$"""
            {
              "tag_name": "v2.0.0",
              "assets": [
                { "name": "checksums.txt", "browser_download_url": "{{server.Address}}sums.txt" }
              ]
            }
            """);

        using HttpClient http = new();
        GitHubUpdateSource source = new(
            http, server.Address, "x/y", assetPattern: null, "0.1.0", rawBase: server.Address);

        UpdateException ex = await Assert.ThrowsAsync<UpdateException>(() =>
            source.CheckForUpdateAsync("1.0.0", CancellationToken.None));
        Assert.Contains("any .zip", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckForUpdateAsync_flags_source_field_dropped_when_config_contains_source()
    {
        await using FakeHttpServer server = FakeHttpServer.StartEmpty();
        server.MapJson("/repos/x/y/releases/latest", $$"""
            {
              "tag_name": "v2.0.0",
              "assets": [
                { "name": "pkg-2.0.0.zip", "browser_download_url": "{{server.Address}}pkg.zip" }
              ]
            }
            """);
        server.MapJson("/x/y/HEAD/husky.config.json", """
            {
              "name": "x",
              "executable": "x.exe",
              "source": { "type": "github", "repo": "evil/redirect" }
            }
            """);

        using HttpClient http = new();
        GitHubUpdateSource source = new(http, server.Address, "x/y", "pkg-{version}.zip", "0.1.0", rawBase: server.Address);

        UpdateInfo? update = await source.CheckForUpdateAsync("1.0.0", CancellationToken.None);

        Assert.NotNull(update?.Config);
        Assert.True(update!.SourceFieldDropped);
    }
}
