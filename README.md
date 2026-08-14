# Husky

> *your loyal app launcher* 🐺

Husky is a generic, configuration-driven launcher for .NET applications. It starts apps, keeps them alive, and installs updates — automatically by default, on demand if your app prefers — without your app needing to contain any update logic itself.

**Status:** v1.0 implementation underway against the spec in [LEASH.md](./LEASH.md).

---

## What it does

- Starts a .NET app and supervises it (heartbeat, ping, watchdog, crash-restart with circuit breaker).
- Watches for new releases (GitHub Releases or a custom HTTPS manifest) and installs them.
- Two update modes, chosen by the app: **auto** (apply on discovery) or **manual** (notify and wait for the app's "Update now" trigger). Both ride the same wire protocol.
- Asks the app to shut down cleanly before each update, then swaps the binaries and starts the new version.
- Renders everything to a colored console — gradient banner, in-place download progress bar, spinners for indeterminate waits (poll, extract, shutdown). No log files, no GUI. See [LEASH §10](./LEASH.md#10-console-rendering) for the full surface.
- Cross-platform: Windows + Linux, x64 and arm64.

## Three roles, three perspectives

Read the section that matches what you're trying to do.

### 1. You're an **app author** — make your app Husky-compatible

Reference the [`Husky.Client`](https://www.nuget.org/packages/Husky.Client) NuGet package (`dotnet add package Husky.Client`) and add three lines to `Program.cs`:

```csharp
using Husky.Client;

await using HuskyClient? husky = await HuskyClient.AttachIfHostedAsync();
husky?.OnShutdown(async (reason, ct) => await app.StopAsync(ct));

await app.RunAsync(husky?.ShutdownToken ?? CancellationToken.None);
```

That's it. Without Husky around (e.g. running in the debugger), `AttachIfHostedAsync` returns `null` and your app runs standalone — every client call is `?.`-safe.

If your app has a settings UI and you want users to **opt out of automatic updates**, attach with manual mode:

```csharp
HuskyClient husky = await HuskyClient.AttachAsync(
    HuskyClientOptions.Default with { UpdateMode = HuskyUpdateMode.Manual });

husky.UpdateAvailable += (_, info) =>
    SettingsViewModel.NewVersionBadge = info.NewVersion;

OnUpdateNowButtonClicked = () => await husky.RequestUpdateAsync();
OnAutoUpdatesToggled = checked =>
    await husky.SetUpdateModeAsync(checked ? HuskyUpdateMode.Auto : HuskyUpdateMode.Manual);
```

The launcher will only push `update-available` to apps that actually declared the `manual-updates` capability — apps that don't know about manual updates keep getting the existing auto behavior.

See [`src/Husky.Client/README.md`](./src/Husky.Client/README.md) for the full client surface.

### 2. You're a **distributor** — ship your Husky-ready app to users

You publish releases via GitHub or any HTTPS host. Two things happen:

- The release artefact (a ZIP of your app) gets fetched on each update.
- A `husky.config.json` ships *with* the release, telling Husky how to install your app — relative executable path, polling cadence, restart policy, display name. **Your users don't author this.** You do, once, in your repo.

For GitHub: drop `husky.config.json` at the repo root — Husky reads it from `raw.githubusercontent.com/<owner>/<repo>/HEAD/husky.config.json`. You can also attach it as a release asset to version it per-release.

For HTTP: embed a `config:` block in your manifest:

```json
{
  "version": "1.4.3",
  "url": "https://example.invalid/MyApp-1.4.3.zip",
  "sha256": "9b74...",
  "config": {
    "name": "my-app",
    "executable": "app/MyApp.exe",
    "checkMinutes": 30
  }
}
```

See [`samples/`](./samples) for full templates.

**Tell your users**: there are two equally good paths — pick the one that matches your install story. Copy the relevant version into your project's README.

> **Option A — one-liner.** No config file needed.
>
> 1. Download the launcher binary for your OS from the [Husky releases page](https://github.com/SACRVM/husky/releases). Extract `Husky.exe` (Windows) or `Husky` (Linux).
> 2. From the directory where the app should live, run:
>    ```
>    Husky --repo your-org/your-app
>    ```
>    or, for an HTTPS-manifest source:
>    ```
>    Husky --manifest https://example.com/your-app/manifest.json
>    ```
> 3. Husky downloads and starts the app on first run; updates are handled automatically thereafter.

> **Option B — drop in a config file.** Useful when users want the launcher and config to travel together.
>
> 1. Download the launcher binary for your OS from the [Husky releases page](https://github.com/SACRVM/husky/releases). Extract `Husky.exe` (Windows) or `Husky` (Linux).
> 2. Save it in a folder of your choice. Next to it, create `husky.config.json`:
>    ```json
>    { "source": { "type": "github", "repo": "your-org/your-app" } }
>    ```
> 3. Run the launcher. It downloads and starts the app on first run; updates are handled automatically thereafter.

### 3. You're a **user** — install and run someone's Husky-shipped app

Many app distributors ship a one-file installer that bundles Husky for you — if so, follow their instructions and ignore this section. The recipe below is for the bring-your-own-Husky path: full control, smallest possible artefacts, fully portable.

**Step-by-step:**

1. Download the launcher binary for your OS from the [Husky releases page](https://github.com/SACRVM/husky/releases). Pick the trim build (`husky-<rid>.zip` / `.tar.gz`); the `-aot` variant is a smaller native build with the same behaviour. Extract `Husky.exe` (Windows) or `Husky` (Linux).
2. Make a folder somewhere portable — Desktop, USB stick, `~/apps/fishbowl/`, anywhere.
3. From inside that folder, run the launcher with the source on the command line:

   ```
   Husky --repo SACRVM/the-fishbowl
   ```

   For an HTTP-manifest source:

   ```
   Husky --manifest https://example.com/fishbowl/manifest.json
   ```

   Add `--asset "Fishbowl-{version}.zip"` if a GitHub release ships multiple `.zip` files and you need to pick a specific one.

4. (Alternative) If you'd rather have the config travel with the folder, drop a `husky.config.json` next to the binary and run `Husky` with no arguments:

   ```json
   { "source": { "type": "github", "repo": "SACRVM/the-fishbowl" } }
   ```

5. First run pulls the app's deployment metadata (`name`, `executable`, timing knobs) from the source itself, downloads the latest release into `app/`, and starts it. From there: auto-updates (or manual, if the app prefers — its UI will tell you).

The folder is fully self-contained — your app's data lives in `app/data/` and moves with the folder if you copy it elsewhere. No Registry, no `%AppData%`, no admin rights.

**Working directory.** Husky operates against the current working directory by default. If the launcher lives on `PATH` and the install is somewhere else, point at it with `--dir`:

```
Husky --repo SACRVM/the-fishbowl --dir D:\Apps\Fishbowl
```

**One-shot PowerShell bootstrap (Windows).** The launcher's release assets are versionless, so a tiny script next to your install can fetch Husky on first run and start it on every run — no manual download, no `husky.config.json`. Drop this into `Run.ps1` next to where you want the app installed:

```powershell
$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot
$exe = Join-Path $dir 'Husky.exe'

if (-not (Test-Path $exe)) {
    $zip = Join-Path $env:TEMP 'husky.zip'
    Invoke-WebRequest 'https://github.com/SACRVM/husky/releases/latest/download/husky-win-x64.zip' -OutFile $zip -UseBasicParsing
    Expand-Archive $zip $dir -Force
    Remove-Item $zip
}

& $exe --manifest 'https://example.com/myapp/manifest.json' --dir $dir
```

Swap `--manifest <url>` for `--repo your-org/your-app` if you publish via GitHub Releases. Re-running the script reuses the cached `Husky.exe`; delete it (or point at a different `--dir`) to start fresh.

**Overriding settings**: anything the distributor chose can be overridden by adding the field to your local `husky.config.json`. CLI flags win over the local file, which wins over source-supplied defaults. See [`samples/`](./samples) for the full set of fields.

**Husky itself doesn't self-update.** Grab a newer launcher from the releases page when you want it; replacing the binary in place is fine — your app config and `app/` directory keep working.

---

## Running as a service (Windows)

Husky is a plain foreground executable — it doesn't implement `ServiceBase`, so `sc.exe create` against `Husky.exe` won't produce a working service (Windows expects the process to call `ServiceMain` within 30 seconds, which Husky never does). On a server, wrap it with [NSSM](https://nssm.cc/) instead — a tiny shim that turns any executable into a real Windows service with auto-restart and log redirection.

Minimal recipe (elevated PowerShell):

```powershell
nssm install Husky-MyApp "C:\Apps\MyApp\Husky.exe" "--repo your-org/your-app"
nssm set Husky-MyApp AppDirectory "C:\Apps\MyApp"
nssm set Husky-MyApp AppStdout    "C:\Apps\MyApp\logs\husky.out.log"
nssm set Husky-MyApp AppStderr    "C:\Apps\MyApp\logs\husky.err.log"
nssm start Husky-MyApp
```

- `AppDirectory` plays the role of `--dir`; you can also pass `--dir` explicitly inside the install command's quoted argument string.
- `AppStdout` / `AppStderr` are essential — without them every launcher and child-app log line is dropped, since the service has no console.
- Swap `--repo …` for `--manifest <url>` when the source is an HTTPS manifest.
- Remove with `nssm stop Husky-MyApp; nssm remove Husky-MyApp confirm`.

On **Linux**, no shim is needed: a regular systemd unit invoking `Husky` with `--repo` / `--manifest` works directly — systemd has no equivalent of the 30-second `ServiceMain` deadline that breaks raw `sc.exe` use on Windows.

Native Windows-service hosting (`UseWindowsService` + `--install` / `--uninstall`) isn't on the roadmap. The NSSM path covers the common case without baking OS-specific service plumbing into Husky's single-binary story.

## Command-line flags

Everything the launcher takes on the command line:

| Flag | Argument | What it does |
|------|----------|--------------|
| `--manifest <url>` | absolute `http(s)://` URL | Source = HTTP manifest. Mutually exclusive with `--repo`. |
| `--repo <slug>` | `owner/name` | Source = GitHub Releases. Mutually exclusive with `--manifest`. |
| `--asset <pattern>` | filename, may contain `{version}` | Picks a specific GitHub release asset. Requires `--repo`. |
| `--dir <path>` | absolute or relative path | Working directory override. Default: process cwd. |

`--manifest` / `--repo` build a synthetic `source` block at the top of the config merge, so a CLI invocation needs no local file. `--dir` redirects the directory that holds `husky.config.json`, `app/`, and `download/` — handy when Husky lives on `PATH` and the install is elsewhere.

## How config resolves

Four layers, highest priority first:

1. **CLI source flags** — `--manifest` / `--repo` / `--asset`. Ephemeral; not written to disk.
2. **Local `husky.config.json`** — what *you* author. May contain `source` and any other override.
3. **Source-supplied config** — what the *app author* ships. Pulled from a GitHub asset / repo file or embedded in the HTTP manifest.
4. **Built-in defaults** — Husky's fallbacks for the timing knobs.

Field-by-field merge: a non-null value at a higher layer fills the slot. The local file may be absent entirely when CLI flags supply a source, or shrink to `{ "source": { ... } }` whenever the source supplies `name` and `executable`.

## Runtime layout

```
<working-dir>/                ← cwd, or wherever --dir points
  husky.config.json           ← optional when --manifest/--repo is on the command line
  app/                        ← installed and updated by Husky
    YourApp.exe
    data/                     ← your app's data, never touched by Husky
  download/                   ← last update package, kept for forensics
```

`Husky.exe` itself lives wherever you put it — next to the install, or somewhere on `PATH`. The launcher's binary location does not affect runtime paths.

## Project layout

```
src/
  Husky.Protocol/    shared wire contracts (named-pipe messages)
  Husky.Client/      NuGet package consumed by hosted apps
  Husky/             the launcher binary (Husky.exe)
tests/
  Husky.Protocol.Tests/
  Husky.Client.Tests/
  Husky.Tests/       includes the end-to-end suite
  Husky.TestApp/     a real .NET app with toggleable behaviours for E2E
```

## The leash

The full specification lives in [LEASH.md](./LEASH.md). The working agreement for collaborators (and the AI agent driving most of the implementation) lives in [CLAUDE.md](./CLAUDE.md).

## License

[MIT](./LICENSE) — © 2026 Chloe Dream.

---

🐺 *woof.*
