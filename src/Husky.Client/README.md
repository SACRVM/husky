# Husky.Client

Slim client library for apps hosted by [Husky](https://github.com/SACRVM/husky), the generic .NET app launcher.

## Install

```bash
dotnet add package Husky.Client
```

Targets `net10.0`. Pair it with the matching `Husky.exe` launcher from the [releases page](https://github.com/SACRVM/husky/releases).

## Quick start — auto updates (the default)

`Program.cs`:

```csharp
using Husky.Client;

await using HuskyClient? husky = await HuskyClient.AttachIfHostedAsync();

husky?.OnShutdown(async (reason, ct) =>
{
    // Drain queues, flush state, close sockets — whatever your app needs.
    await myApp.StopAsync(ct);
});

await myApp.RunAsync(husky?.ShutdownToken ?? CancellationToken.None);
```

That is it. If Husky is not present (e.g. running in the debugger), `AttachIfHostedAsync` returns `null` and the app keeps going standalone — every call to the client is `?.`-safe.

## Manual update mode — let users decide when to update

For UI apps that want an "Update now" button or an "automatic updates" toggle, attach with manual mode. The launcher then pushes `update-available` instead of triggering the apply, and waits for the app's signal.

```csharp
HuskyClient husky = await HuskyClient.AttachAsync(
    HuskyClientOptions.Default with { UpdateMode = HuskyUpdateMode.Manual });

// React to discoveries: light up a UI badge, prompt the user, …
husky.UpdateAvailable += (_, info) =>
{
    Console.WriteLine($"v{info.NewVersion} is available (current {info.CurrentVersion})");
    // myUi.ShowUpdateBadge();
};

// User clicked "Update now":
await husky.RequestUpdateAsync();
// Husky will follow up with a `shutdown` (reason: Update); your OnShutdown
// handler runs exactly as for an auto-mode update.

// User toggled auto-updates back on:
await husky.SetUpdateModeAsync(HuskyUpdateMode.Auto);

// User opens a "Check for updates" menu without a known cached version:
HuskyUpdateInfo? cached = await husky.CheckForUpdateAsync();
```

Manual mode is opt-in per session. The launcher only honours it if the client declared the `manual-updates` capability, which `Husky.Client` does automatically. Older launchers that don't speak the capability will silently keep behaving as auto — your manual-update calls then throw `NotSupportedException`, so you can hide the UI:

```csharp
if (husky.SupportsManualUpdates)
{
    settingsView.AutoUpdateToggle.IsVisible = true;
}
```

## ASP.NET Core / Generic Host

```csharp
using Husky.Client.DependencyInjection;

builder.Services.AddHuskyClient();
// or pre-set manual mode for an app with its own update UI:
builder.Services.AddHuskyClient(o => o.UpdateMode = HuskyUpdateMode.Manual);
```

The hosted service:

- Owns the `HuskyClient` instance and registers it as a singleton (so other components can inject it for the manual-update API).
- Calls `IHostApplicationLifetime.StopApplication()` on `shutdown` so your `IHostedService.StopAsync` chain runs cleanly.
- Replies to `ping` probes with `HealthCheckService` (if you registered `AddHealthChecks()`), otherwise `Healthy`.

## What you get

- `ShutdownToken` — cancelled when Husky asks the app to stop or the launcher disappears.
- `OnShutdown(handler)` — single shutdown hook with a `ShutdownReason` (`Update`, `Manual`, `LauncherStopping`).
- `SetHealth(provider)` — return `Healthy` / `Degraded` / `Unhealthy` plus arbitrary detail key/values.
- `CheckForUpdateAsync` / `RequestUpdateAsync` / `SetUpdateModeAsync` — manual-update surface.
- `UpdateAvailable` event — fired when the launcher pushes a discovery (manual mode only).
- `SupportsManualUpdates` / `LauncherCapabilities` — runtime introspection of what the launcher supports.
- Standalone-mode safety — without Husky, `IsHosted` is `false` and `AttachIfHostedAsync` returns `null`. All client API methods are unreachable via `?.`.

## Wire-protocol version

Husky and Husky.Client must share the same wire-protocol version. The handshake refuses connections on mismatch. Major NuGet bumps map to wire-protocol bumps. Capability tokens grow additively without bumping the wire version — apps and launchers ignore tokens they don't recognise.

## License

MIT — © 2026 Chloe Dream. Same as the launcher.

🐺 *woof.*
