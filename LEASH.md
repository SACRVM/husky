# LEASH · Husky Specification v1.0

> *The leash that binds.*

Husky is a generic, configuration-driven launcher for .NET applications. It starts apps, keeps them alive, and installs updates automatically — without the app needing to contain any update logic itself. Husky is cross-platform (Windows, Linux), lean, and has personality.

This document is the complete specification for an initial implementation. It is written so that a coding agent (e.g. Claude Code) can derive Husky from it.

---

## ⚠️ Language Rules

This specification lives in English so it can sit comfortably in the Git repository alongside the code.

**However:** the developer (Chloe) communicates in **German**. All clarifications, design discussions, status updates, and explanations directed at the developer must be in **German**. Code remains **English-only** in every aspect: identifiers, comments, string literals, commits, READMEs.

See [CLAUDE.md](./CLAUDE.md) for the full working agreement.

---

## Table of Contents

1. [Goals and Non-Goals](#1-goals-and-non-goals)
2. [Architecture](#2-architecture)
3. [Husky.Protocol](#3-huskyprotocol)
4. [Husky.Client](#4-huskyclient)
5. [Husky (Launcher)](#5-husky-launcher)
6. [Runtime Directory Layout](#6-runtime-directory-layout)
7. [Update Flow in Detail](#7-update-flow-in-detail)
8. [Watchdog Logic](#8-watchdog-logic)
9. [Source Providers](#9-source-providers)
10. [Console Rendering](#10-console-rendering)
11. [Cross-Platform](#11-cross-platform)
12. [Error Handling](#12-error-handling)
13. [Build & Publishing](#13-build--publishing)
14. [Out of Scope](#14-out-of-scope)
15. [Glossary](#15-glossary)
16. [Appendix A — Recommended Implementation Order](#appendix-a--recommended-implementation-order)

---

## 1. Goals and Non-Goals

### 1.1 Goals

- **A generic, configurable launcher binary** for arbitrary .NET applications.
- **Self-managed updates** of the hosted app without restarting the launcher.
- **Clean shutdowns** with the app's own cleanup routine before every update or stop.
- **Minimal configuration** via a single JSON file with few fields.
- **Cross-platform** (Windows and Linux, x64 and arm64).
- **Character.** Modern, colored console with Husky branding. No enterprise look.
- **Easy app integration** — hosted apps reference a slim client library, three lines in `Program.cs`.

### 1.2 Non-Goals (for v1.0)

- No rollback. If an update is broken, it is broken — that is a release-process responsibility.
- No code signing of update packages.
- No self-update of the launcher itself.
- No file-based logging — everything goes to the console.
- No GUI.
- No multi-app management within a single launcher instance. One launcher instance = one app. Multiple apps → multiple launcher instances, each in its own directory.

---

## 2. Architecture

### 2.1 Solution Layout

```
Husky.sln
├── src/
│   ├── Husky.Protocol/      ← shared lib: messages, JSON contracts, pipe naming
│   ├── Husky.Client/        ← NuGet package for hosted apps
│   └── Husky/               ← the Husky.exe launcher itself
└── tests/
    ├── Husky.Protocol.Tests/
    ├── Husky.Client.Tests/
    └── Husky.Tests/
```

### 2.2 Communication Model

```
┌─────────────────────────┐         ┌──────────────────────────┐
│  Husky (Launcher)       │         │  Hosted App              │
│  ─────────────────      │         │  ─────────────────       │
│  - reads config         │         │  - uses Husky.Client     │
│  - polls update source  │ ◄──────►│    NuGet package         │
│  - manages app process  │  Named  │  - registers shutdown    │
│  - watches stdout       │  Pipe   │    + health callbacks    │
│  - applies updates      │         │  - normal app code       │
│  - renders console      │         │                          │
└─────────────────────────┘         └──────────────────────────┘
        │                                       │
        │ stdout/stderr piping                  │
        └───────────────────────────────────────┘
```

- **Named pipe** for control communication (shutdown requests, heartbeats, health probes, hello handshake).
- **stdout/stderr** as the log channel: whatever the app writes to the console, the launcher captures and renders with color.

### 2.3 Cross-Platform Strategy

- Named pipes work cross-platform in .NET via `NamedPipeServerStream` / `NamedPipeClientStream`.
- On Linux, pipes are realized internally as Unix domain sockets in `/tmp/CoreFxPipe_<name>`. Transparent to Husky.
- The only platform-specific piece is the **pipe ACL** (Windows: `PipeSecurity`; Linux: default filesystem permissions are sufficient).
- Service integration (Windows SCM, Linux systemd) is **not the launcher's job**. Husky runs as a regular console process. Whoever wants to run Husky as a service registers the Husky binary through the OS's mechanism (`sc.exe` / a systemd unit).

### 2.4 Target Framework

- **.NET 10.0** (LTS, GA since November 2025).
- No LTS bias for its own sake — we just happen to be on the current release.

---

## 3. Husky.Protocol

Shared library — common contracts for launcher and client.

### 3.1 Pipe Naming

- The launcher generates a GUID-based pipe name when starting the app: `husky-{guid}`.
- The pipe name is passed to the child app via environment variables:
  - `HUSKY_PIPE` → the pipe name (no path prefix, just the name).
  - `HUSKY_APP_NAME` → the name from the config (for logging).
- If `HUSKY_PIPE` is missing, the app runs without Husky (standalone mode).

### 3.2 Pipe Security

- **Windows**: `PipeSecurity` with a `PipeAccessRule` for `WindowsIdentity.GetCurrent().User`, full access. No access for other users.
- **Linux**: no extra configuration needed — the pipe socket file gets user-only permissions by default.

### 3.3 Wire Format

- **JSON Lines**: each message is exactly one line of UTF-8 JSON, terminated by `\n`.
- No BOM, no pretty-printed whitespace inside the message.
- Readers read line by line and parse each line as one message.

### 3.4 Base Message Schema

Every message has:

```json
{
  "id": "optional-correlation-guid",
  "replyTo": "optional-correlation-guid",
  "type": "message-type-string",
  "data": { ... }
}
```

- `id` is set when the sender expects a reply. GUID string, lower-case, no braces.
- `replyTo` is set when the message is a reply to a previous message; contains the `id` of the original.
- `type` is always set, identifies the message type (see below).
- `data` is optional, contains type-specific fields.

### 3.5 Message Types

#### 3.5.1 `hello` — App → Launcher

First message after pipe connect. App sends its identity, declares its capabilities, and states its initial preferences.

```json
{
  "id": "...",
  "type": "hello",
  "data": {
    "protocolVersion": 1,
    "appVersion": "1.4.2",
    "appName": "umbrella-bot",
    "pid": 4218,
    "capabilities": ["manual-updates"],
    "preferences": {
      "updateMode": "manual"
    }
  }
}
```

- `capabilities`: array of feature tokens (kebab-case strings) declaring which optional wire features the app speaks. The launcher uses this to decide which messages are safe to send to this app. **The baseline** (`hello`/`welcome`/`heartbeat`/`ping`/`pong`/`shutdown`/`shutdown-ack`) is always supported and need not be listed. Tokens defined for v1.0:
  - `manual-updates` — app speaks the `update-check` / `update-status` / `update-available` / `update-now` / `set-update-mode` family (§3.5.9–14). Required for the launcher to honour any non-default `updateMode` preference and to push `update-available`. Apps without this capability get auto-mode regardless.
  Future protocol additions add new tokens; unknown tokens are ignored by older launchers.
- `preferences`: optional block of runtime settings the app would like to start with. Ignored fields fall back to defaults. Currently:
  - `updateMode`: `"auto"` (default — launcher applies updates as soon as polling discovers them) or `"manual"` (launcher only notifies the app and waits for `update-now`). Honoured only if the app declared `manual-updates` in `capabilities`. Can be changed at runtime via `set-update-mode` (§3.5.13) — useful for apps like Fishbowl that expose an "automatic updates" toggle in their own settings UI.

#### 3.5.2 `welcome` — Launcher → App

Reply to `hello`. Confirms acceptance and echoes the launcher's own capabilities.

```json
{
  "id": "...",
  "replyTo": "...",
  "type": "welcome",
  "data": {
    "protocolVersion": 1,
    "launcherVersion": "1.0.0",
    "accepted": true,
    "reason": null,
    "capabilities": ["manual-updates"]
  }
}
```

- `capabilities`: feature tokens the launcher supports, in the same vocabulary as `hello.capabilities`. The effective feature set for the session is the intersection of the two lists. Apps use this to gate UI: if the launcher does not advertise `manual-updates`, an "Update now" button stays hidden.
- If `accepted: false`: `reason` contains plain-text explanation; the app should exit.

#### 3.5.3 `heartbeat` — App → Launcher

Periodic liveness signal. No reply expected.

```json
{ "type": "heartbeat" }
```

Send every **5 seconds**.

#### 3.5.4 `ping` — Launcher → App

Active health probe. The app must reply.

```json
{ "id": "...", "type": "ping" }
```

#### 3.5.5 `pong` — App → Launcher

Reply to `ping`. Includes health status.

```json
{
  "id": "...",
  "replyTo": "...",
  "type": "pong",
  "data": {
    "status": "healthy",
    "details": { "queue": 3, "guilds": 12 }
  }
}
```

- `status`: `"healthy"` | `"degraded"` | `"unhealthy"`
- `details`: opaque app-specific key-value pairs. The launcher only displays them in the console; it does not interpret them.

#### 3.5.6 `shutdown` — Launcher → App

Request to terminate.

```json
{
  "id": "...",
  "type": "shutdown",
  "data": {
    "reason": "update",
    "timeoutSeconds": 60
  }
}
```

- `reason`: `"update"` | `"manual"` | `"launcher-stopping"`
- `timeoutSeconds`: after this elapses, the launcher hard-kills the process.

#### 3.5.7 `shutdown-ack` — App → Launcher

Acknowledgement of the shutdown message. Send immediately, then start cleanup.

```json
{ "id": "...", "replyTo": "...", "type": "shutdown-ack" }
```

#### 3.5.9 `update-check` — App → Launcher

Asks the launcher whether an update is available right now. Reply expected. The launcher performs a fresh source poll (HTTP fetch of the manifest / GitHub release lookup) before replying, updates its cached `UpdateStatus` with the result, and answers with the freshly observed state. If the poll fails (network error, source unreachable), the launcher falls back to its last cached status so the call still resolves with the best-known truth rather than throwing on the wire.

```json
{ "id": "...", "type": "update-check" }
```

#### 3.5.10 `update-status` — Launcher → App

Reply to `update-check`.

```json
{
  "id": "...",
  "replyTo": "...",
  "type": "update-status",
  "data": {
    "available": true,
    "currentVersion": "1.4.2",
    "newVersion": "1.4.3",
    "downloadSizeBytes": 6918432
  }
}
```

- `available`: `true` if a newer version is known, `false` otherwise.
- `newVersion` and `downloadSizeBytes` are `null` when `available: false`.
- `downloadSizeBytes` is `null` when the source provider does not expose a size (e.g. some HTTP manifests).

#### 3.5.11 `update-available` — Launcher → App

Sent unsolicited, once per discovered version, when the polling loop finds a new version **and** the current update mode is `manual`. In `auto` mode the launcher proceeds straight to the update flow (§7) and never sends this push. No reply expected.

```json
{
  "type": "update-available",
  "data": {
    "currentVersion": "1.4.2",
    "newVersion": "1.4.3",
    "downloadSizeBytes": 6918432
  }
}
```

#### 3.5.12 `update-now` — App → Launcher

Requests the launcher to apply a known update immediately. Fire-and-forget; no reply. The launcher checks its cache and:

- if an update is available → starts the update flow (§7), which itself sends `shutdown` with `reason: "update"` to the app.
- if no update is available → logs a warning and ignores the request.

```json
{ "type": "update-now" }
```

Used by `manual` mode apps in response to a UI button click. `auto` mode apps may also use it (e.g. an "update now" button that bypasses the next polling tick), with the same semantics.

#### 3.5.13 `set-update-mode` — App → Launcher

Changes the current update mode at runtime. Reply expected (`update-mode-ack`) so the app can confirm the launcher accepted the change before updating its UI. The mode persists for the lifetime of the launcher process; on launcher restart, the app's next `hello` re-establishes the desired mode.

```json
{
  "id": "...",
  "type": "set-update-mode",
  "data": { "mode": "manual" }
}
```

- `mode`: `"auto"` | `"manual"`.

If the new mode is `auto` and the launcher already has a cached pending update, it proceeds to apply it on the next polling tick (or immediately, implementation choice — be consistent with the auto-mode polling behavior).

**Capability gating:** the launcher only honours mode changes from apps that declared the `manual-updates` capability in `hello`. If the capability was not declared, the launcher logs a console warning and replies with `update-mode-ack` carrying the unchanged effective mode (always `"auto"` for such apps). Same rule for the initial `preferences.updateMode` in `hello` — ignored if the capability is missing.

#### 3.5.14 `update-mode-ack` — Launcher → App

Reply to `set-update-mode`.

```json
{
  "id": "...",
  "replyTo": "...",
  "type": "update-mode-ack",
  "data": { "mode": "manual" }
}
```

`mode` echoes the now-active mode.

### 3.6 Versioning

- `protocolVersion` is an integer.
- Current version: **1**.
- Incompatibility rule: launcher and app must have the exact same `protocolVersion`. On mismatch → `welcome.accepted = false`.
- Later versions may introduce additive fields without bumping (unknown fields are ignored). Breaking changes increment the version.

### 3.7 Implementation Notes

- Use `System.Text.Json` with source generators for AOT compatibility.
- Records with `[JsonPolymorphic]` and `[JsonDerivedType]` for the message hierarchy are clean, but for v1 a dispatch switch over `type` is sufficient.
- All records should use `init` properties, not mutable setters.

---

## 4. Husky.Client

NuGet package for hosted apps.

### 4.1 Public API (Target Shape)

```csharp
namespace Husky.Client;

public sealed class HuskyClient : IAsyncDisposable
{
    // Cheap synchronous probe — checks the HUSKY_PIPE env var only.
    public static bool IsHosted { get; }

    // Returns null if not hosted; otherwise performs the full async attach
    // (pipe connect + hello/welcome handshake + background loops).
    public static Task<HuskyClient?> AttachIfHostedAsync(
        HuskyClientOptions? options = null,
        CancellationToken ct = default);

    // Throws if not hosted. For callers that require Husky to be present.
    public static Task<HuskyClient> AttachAsync(
        HuskyClientOptions? options = null,
        CancellationToken ct = default);

    public CancellationToken ShutdownToken { get; }

    public string? AppName { get; }

    public HuskyUpdateMode UpdateMode { get; }

    public void OnShutdown(Func<ShutdownReason, CancellationToken, Task> handler);

    public void SetHealth(Func<HealthStatus> provider);

    // --- Update protocol ---

    // Asks the launcher whether an update is currently known. Returns null if no
    // update is available. Cheap call; the launcher answers from its cache.
    public Task<HuskyUpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default);

    // Tells the launcher to apply the known update now. The launcher will follow
    // up with a `shutdown` (reason: Update) — the app's OnShutdown handler runs
    // exactly as for an auto-mode update. Fire-and-forget on the wire; this Task
    // completes once the message has been sent.
    public Task RequestUpdateAsync(CancellationToken ct = default);

    // Switches between auto and manual at runtime. Awaits the launcher's ack.
    public Task SetUpdateModeAsync(HuskyUpdateMode mode, CancellationToken ct = default);

    // Raised when the launcher pushes `update-available` (manual mode only).
    public event EventHandler<HuskyUpdateInfo>? UpdateAvailable;

    public ValueTask DisposeAsync();
}

public sealed record HuskyClientOptions
{
    // Initial mode sent in `hello`. Default Auto preserves pre-update-protocol
    // behavior. Apps with their own "automatic updates" toggle should pass the
    // user's last-saved choice here on attach.
    public HuskyUpdateMode UpdateMode { get; init; } = HuskyUpdateMode.Auto;
}

public enum HuskyUpdateMode { Auto, Manual }

public sealed record HuskyUpdateInfo(
    string CurrentVersion,
    string NewVersion,
    long? DownloadSizeBytes
);

public enum ShutdownReason { Update, Manual, LauncherStopping }

public enum HealthState { Healthy, Degraded, Unhealthy }

public sealed record HealthStatus(
    HealthState State,
    IReadOnlyDictionary<string, object>? Details = null
)
{
    public static HealthStatus Healthy { get; } = new(HealthState.Healthy);
    public HealthStatus With(string key, object value) => /* ... */;
}
```

### 4.2 ASP.NET Core / Generic Host Integration

```csharp
namespace Husky.Client.DependencyInjection;

public static class HuskyServiceCollectionExtensions
{
    public static IServiceCollection AddHuskyClient(
        this IServiceCollection services,
        Action<HuskyClientOptions>? configure = null);
}
```

- Internally registers an `IHostedService` that builds and owns the `HuskyClient` instance.
- On `shutdown`: calls `IHostApplicationLifetime.StopApplication()` — the host runs its `IHostedService.StopAsync` routines cleanly.
- On `ping`: by default uses `HealthCheckService` if registered via `AddHealthChecks()`, otherwise default `Healthy`.
- The app code does not need to do anything else.
- For runtime control of update mode and manual triggers, the app injects `HuskyClient` and calls `CheckForUpdateAsync` / `RequestUpdateAsync` / `SetUpdateModeAsync` directly.

### 4.3 Lifecycle

1. `AttachIfHostedAsync()` checks `Environment.GetEnvironmentVariable("HUSKY_PIPE")`. If unset → returns `null`. (`AttachAsync()` throws in this case.)
2. If set: opens `NamedPipeClientStream` with the name, connects (timeout: 5s).
3. Sends `hello` with app metadata, including the `updateMode` from `HuskyClientOptions`.
4. Awaits `welcome` (timeout: 5s). If `accepted: false` → throws; the app author decides what to do.
5. Starts two background tasks:
   - **Sender loop**: every 5s send `heartbeat`.
   - **Receiver loop**: reads incoming messages, dispatches by `type`.
6. On incoming `shutdown`:
   - Immediately sends `shutdown-ack`.
   - Calls the registered `OnShutdown` handler (with the `ShutdownReason` and a `CancellationToken` cancelled `timeoutSeconds - 5s` after start, giving cleanup time but cutting in just before the launcher's hard-kill).
   - Awaits handler completion.
   - Cancels `ShutdownToken`.
7. On incoming `ping`:
   - Calls the `SetHealth` provider (or default `Healthy` if not set).
   - Sends `pong` with the status.
8. On incoming `update-available`:
   - Raises the `UpdateAvailable` event with the parsed `HuskyUpdateInfo`.
9. `CheckForUpdateAsync` sends `update-check` with a fresh correlation `id`, awaits the matching `update-status`, returns `null` if `available: false` or the `HuskyUpdateInfo` otherwise.
10. `RequestUpdateAsync` sends `update-now`. The Task completes when the message is on the wire — the actual shutdown follows asynchronously via the normal shutdown handler.
11. `SetUpdateModeAsync` sends `set-update-mode`, awaits `update-mode-ack`, then updates the cached `UpdateMode` property.
12. On `DisposeAsync`: cleanly closes the pipe.

### 4.4 Robustness

- **Pipe disconnect detection**: if the pipe is closed by the launcher (launcher stopped/crashed), `ShutdownToken` fires with reason `LauncherStopping`. The app may react or simply exit.
- **No auto-reconnect** in v1. If the pipe is gone, it is gone.
- **Standalone mode**: if `AttachIfHosted()` returns `null`, all client methods are unreachable (`?.` is enough). The app runs normally.

### 4.5 Dependencies

- `Husky.Protocol` (project reference).
- `Microsoft.Extensions.Hosting.Abstractions` (for the DI extension).
- `Microsoft.Extensions.Diagnostics.HealthChecks` (so the DI integration in §4.2 can read `HealthCheckService` automatically when the app calls `AddHealthChecks()` — without this dep that contract is impossible to honour).
- `System.Text.Json` (built-in).
- **No** other NuGet dependencies.

---

## 5. Husky (Launcher)

The executable launcher binary.

### 5.1 Responsibilities

1. Load and validate config.
2. Poll the update source.
3. Start and supervise the child process (the app).
4. Pipe stdout/stderr from the app into its own console, color-coded.
5. Run the named-pipe server, perform the hello handshake.
6. Run the watchdog loop.
7. Apply updates when a new version is available.
8. Crash-restart logic.
9. Render the console UI.

### 5.2 Configuration

Husky operates inside a **working directory** which holds the local `husky.config.json` (if any) and the `app/` and `download/` subtrees. By default the working directory is the process's current working directory (the shell's cwd). It can be redirected with the `--dir <path>` flag — useful when Husky lives on `PATH` and the install lives elsewhere. The working directory must exist; `app/` and `download/` are created on demand. The launcher binary's own location is irrelevant beyond launching itself.

Husky operates against an **effective config** assembled at runtime from four layers, in this precedence order (higher wins):

1. **CLI source flags** — `--manifest`, `--repo`, `--asset` (see §5.2.1). Build a synthetic `source` block at the top of the merge; nothing is persisted, the flags must be passed again on every launch. In v1.0 these flags only set `source` — no overrides for other fields.
2. **Local `husky.config.json`** — file in the working directory. May contain `source` and overrides for any other field. Optional when CLI source flags supply a source.
3. **Source-supplied config** — fetched from the configured source on every successful update poll (HTTP manifest's `config` block §9.3, or a `husky.config.json` from a GitHub release asset / repo root §9.2). The app author's view: deployment metadata for their own app.
4. **Defaults** — built-in fallbacks for the timing knobs.

The merge is field-by-field: a non-null value at a higher layer fills the slot; otherwise the next layer is consulted; otherwise the default applies. The local config can be as small as `{ "source": { ... } }` when the source supplies everything else, or absent entirely when `--manifest`/`--repo` is on the command line.

**Schema** — same fields apply to both the local file and the source-supplied block (which has no `source`):

```json
{
  "source": {
    "type": "github",
    "repo": "chloe/umbrella-bot",
    "asset": "UmbrellaBot-{version}.zip"
  },

  "name": "umbrella-bot",
  "executable": "app/UmbrellaBot.exe",

  "checkMinutes": 60,
  "shutdownTimeoutSec": 60,
  "killAfterSec": 10,
  "restartAttempts": 3,
  "restartPauseSec": 30
}
```

**Field reference:**

| Field | Type | Layers that may set it | Required (effective) | Default |
|-------|------|------------------------|----------------------|---------|
| `source` | object | CLI flags or local — *never* read from source-supplied (anti-redirect) | ✓ | — |
| `name` | string | local, source-supplied | ✓ | — |
| `executable` | string | local, source-supplied | ✓ | — |
| `checkMinutes` | int (≥ 5) | local, source-supplied | – | `60` |
| `shutdownTimeoutSec` | int | local, source-supplied | – | `60` |
| `killAfterSec` | int | local, source-supplied | – | `10` |
| `restartAttempts` | int | local, source-supplied | – | `3` |
| `restartPauseSec` | int | local, source-supplied | – | `30` |

`name` is used for console display and is passed to the app via the `HUSKY_APP_NAME` environment variable. It is **not** part of the pipe name — pipe names are GUID-based (§3.1).

`executable` is a path **relative to the launcher's directory**. Forward slashes only; backslashes are normalized. Absolute paths and `..` traversal are rejected (exit code `2`).

**Refresh semantics:**

- Local config is read **once at startup**. Edits while Husky runs are not picked up — restart Husky to reload.
- Source-supplied config is **re-read on every successful poll**. Changes to launcher-internal knobs (`checkMinutes`, `restartAttempts`, …) take effect immediately. Changes to fields that affect the app process (`name`, `executable`) are applied the next time the app is started or restarted — never mid-run.
- The `source` block itself never changes mid-run; it is locked in from the local file at startup.

**Validation and failure modes:**

| Situation | Behavior |
|-----------|----------|
| Conflicting CLI flags (`--manifest` + `--repo`, repeated flag, `--asset` without `--repo`, `--dir` pointing at a non-existent directory) | exit code `2`, message naming the offending flag combination |
| No `source` from any layer (no CLI source flags **and** no local file or local file without `source`) | exit code `2`, clear console message |
| Local file present but unparseable | exit code `2`, even if CLI supplies source — fix the file or delete it |
| Local file present with a usable `source`, CLI also supplies `--manifest`/`--repo` | CLI wins (top layer); console warning so the user notices the override |
| Local file missing, CLI flags supply `source` | proceed normally; nothing is persisted |
| `name` / `executable` unresolved after merge, source poll succeeded | exit code `2` (app author omitted them — they belong in source-supplied config) |
| `name` / `executable` unresolved after merge, source poll failed | exit code `2`, message: "config incomplete and source unreachable, retry once network is back" |
| `name` / `executable` resolved from local config, source poll failed | console warning, continue, retry on the next polling tick |
| `executable` resolved but file not on disk | enter **bootstrap mode** (§7.5) — bootstrap then installs the app |
| Source-supplied block contains `source` (would be a redirect) | the field is dropped from the merge with a console warning, so the app author can spot and fix |
| Source-supplied block contains unknown fields | silently ignored — supports forward-compatible additions in future Husky versions |

App authors who ship via Husky should always put `name` and `executable` in their source-supplied config so their users can write a local config containing only `source`.

### 5.2.1 Command-Line Flags

Husky accepts a small set of flags. Anything not listed is rejected with exit code `2`.

| Flag | Argument | Purpose | Notes |
|------|----------|---------|-------|
| `--dir <path>` | absolute or relative path | Working directory override (§5.2). | Path must exist. Default: process cwd. |
| `--manifest <url>` | absolute `http(s)://` URL | Build a synthetic `{ "type": "http", "manifest": <url> }` source. | Mutually exclusive with `--repo`. |
| `--repo <slug>` | `owner/name` | Build a synthetic `{ "type": "github", "repo": <slug> }` source. | Mutually exclusive with `--manifest`. |
| `--asset <pattern>` | filename pattern, may include `{version}` | Sets `source.asset` for the GitHub provider (§9.2). | Requires `--repo`. |

Each flag may appear at most once. Source flags become the top config layer (§5.2). `--dir` is not part of the config merge — it changes which directory the merge reads from.

**Examples:**

- `husky` — read everything from `./husky.config.json`.
- `husky --manifest https://example.org/app/manifest.json` — no local file needed; bootstraps and runs the app described by the manifest, in cwd.
- `husky --repo chloe-dream/the-fishbowl --dir D:\Apps\Fishbowl` — pull from a GitHub repo, run inside the named install directory.
- `husky --repo chloe-dream/the-fishbowl --asset 'Fishbowl-{version}-win-x64.zip'` — same with a specific asset pattern.

### 5.3 Boot Sequence

1. Render the Husky ASCII logo + tagline.
2. Parse command-line flags (§5.2.1). Apply `--dir` to fix the working directory; resolve `--manifest`/`--repo`/`--asset` into a synthetic CLI `source` block. On flag conflicts or a missing `--dir` target: exit code `2`.
3. Load the local `husky.config.json` from the working directory if present. If absent and no CLI source flags were given: exit code `2`. Merge the CLI source (if any) over the local file's `source` — CLI wins, with a console warning when both supply `source`. If the resulting `source` is missing or malformed: exit code `2`.
4. Initialize the source provider based on the resolved `source.type`.
5. **Initial source poll** — fetch the latest `UpdateInfo`, which may carry a source-supplied config block. On network/parse failure: console warning; remember that the poll failed for the resolution step below.
6. **Resolve the effective config** — merge CLI + local + source-supplied + defaults per §5.2. If `name` or `executable` ends up unresolved: exit code `2` (with the appropriate message depending on whether the source poll succeeded).
7. Determine the current app version using the resolved `executable`:
   - File exists → read `FileVersionInfo.GetVersionInfo(executable).FileVersion`.
   - File does **not** exist → enter **bootstrap mode**: treat the current version as `"0.0.0"` so any source version triggers an install.
8. Decide what to do based on the source poll result and the current vs. source version comparison:
   - **Bootstrap mode** → run the bootstrap update flow (§7.5). On failure: exit code `2`.
   - New version available → run the update flow (§7), then start the app afterwards.
   - Otherwise → start the app (§5.4).
9. Start the watchdog loop.
10. Start the update-polling timer (`checkMinutes`).

### 5.4 App Start

1. Generate the pipe name: `husky-{guid}`.
2. Open `NamedPipeServerStream`, apply ACL (§3.2).
3. Build the `ProcessStartInfo`:
   - `FileName` = absolute path to the executable.
   - `WorkingDirectory` = the executable's directory.
   - `UseShellExecute = false`.
   - `RedirectStandardOutput = true`, `RedirectStandardError = true`.
   - `CreateNoWindow = true`.
   - Environment: set `HUSKY_PIPE` and `HUSKY_APP_NAME`.
4. `Process.Start()`.
5. Background task: read stdout lines and render to console (source: `app`).
6. Background task: read stderr lines and render to console (source: `app`, color red).
7. Wait for the pipe connect (timeout: 30s).
8. Receive `hello`, validate, reply with `welcome`.
9. Activate the watchdog.

### 5.5 App Stop (Graceful)

1. Send `shutdown` with the current `reason` and `shutdownTimeoutSec` as `timeoutSeconds`.
2. Await `shutdown-ack` (timeout: 5s). If absent: console warning, continue anyway.
3. Wait for process exit (timeout: `shutdownTimeoutSec`).
4. If still running: wait an additional `killAfterSec`.
5. If still running: `Process.Kill(entireProcessTree: true)`. Console warning in "growling" tone.
6. Close the pipe.

### 5.6 Console Control

- **Ctrl+C** or **SIGTERM**: Husky treats this as an app stop (`reason: "launcher-stopping"`), waits for clean exit, then exits itself.
- **Double Ctrl+C**: immediate hard-kill of the app, then exit.

---

## 6. Runtime Directory Layout

```
<working-dir>/                 ← working directory (§5.2): cwd, or --dir <path>
├── husky.config.json          ← optional when --manifest/--repo is on the command line
├── app/                       ← current app version
│   ├── UmbrellaBot.exe
│   ├── data/                  ← app user data (DB, models, etc.) — NEVER touched by launcher
│   └── ...
└── download/                  ← last update package, kept for forensics
    ├── UmbrellaBot-1.4.2.zip
    └── extracted/
        └── ...
```

**Conventions:**

- `app/` contains *everything* the app needs at runtime, including user data, DBs, caches, local models.
- `download/` is transient. It is cleared before each new download. After a successful update, it remains for forensic inspection.
- There is **no** backup directory. There is **no** state file for version info — the current version is read from the executable's metadata.
- `Husky.exe` itself can live inside the working directory (the classic "drop Husky next to its config" deployment) or anywhere on `PATH` (when Husky is installed globally). The launcher's binary location does not affect runtime paths.

---

## 7. Update Flow in Detail

### 7.1 Triggers

The launcher polls the source on its own schedule (`checkMinutes`). What happens when polling discovers a new version depends on the **current update mode**, which is initially set in `hello` and may be changed at runtime via `set-update-mode` (§3.5.13).

The flow can start in any of these situations:

1. **Polling discovery, mode = `auto`** (default). The launcher proceeds straight to Phase 1.
2. **Polling discovery, mode = `manual`**. The launcher caches the `UpdateInfo` in memory, sends `update-available` to the app, then waits. No download happens until the app says go.
3. **`update-now` from the app**. The launcher checks its cache:
   - cache populated → proceed to Phase 1.
   - cache empty → log a warning and ignore (the app should call `update-check` first, or wait for the next polling tick).
4. **Mode switched from `manual` to `auto` while a cached update exists**. The launcher proceeds to Phase 1 on the next polling tick.
5. **Bootstrap** (§7.5). The launcher starts with no app installed; mode is irrelevant — it runs Phase 1 immediately.

In cases 1–4 the app is currently running. In case 5 it is not.

### 7.2 Phase 1 — Preparation (App Keeps Running)

1. Clear `download/` (recursively delete its contents).
2. Download the ZIP from the source URL into `download/<asset-filename>`.
3. Verify SHA-256 if the source provides one. On mismatch: abort the update, console warning.
4. Extract the ZIP into `download/extracted/`.
5. Sanity check: does the executable exist at the expected path inside `extracted/`?
   - Expected path: identical to the configured `executable` path. So if `executable: "app/UmbrellaBot.exe"`, then `download/extracted/app/UmbrellaBot.exe` must exist.
6. **If Phase 1 fails**: abort the update, the app continues unaffected, retry on next check cycle.

### 7.3 Phase 2 — Cutover (App Downtime ≈ Seconds)

1. If the app is running: stop it gracefully (§5.5). On a boot-time update or a bootstrap install (§7.5) there is nothing to stop — skip this step.
2. Recursively copy files from `download/extracted/` into `<install-dir>/`:
   - Existing files are overwritten.
   - Existing files that are *not* in the update remain unchanged (critical: `app/data/` etc.).
   - New files from the update are created.
   - **Never delete** anything (not even files that existed in the old version but not in the new — the app cleans those up itself if it cares).
3. Start the app (§5.4).
4. Await `hello` (timeout: 30s).
5. On successful hello: log update success.
6. On missing hello: loud console warning. The watchdog continues normally; the crash-restart logic kicks in if needed.

### 7.4 Concurrent Updates

- While an update is in progress (Phase 1 or Phase 2), further polling is paused and any incoming `update-now` messages are ignored with a console log.
- The launcher caches at most one pending `UpdateInfo` at a time. If polling discovers a newer version while a previous one is still cached (manual mode, app hasn't pulled the trigger yet), the cache is overwritten with the newer version and a new `update-available` push is sent.

### 7.5 Bootstrap Update

When the launcher starts and finds no app installed at the configured `executable` path, it tries to install one from the configured source:

1. Run Phase 1 (§7.2) against the source.
2. Run Phase 2 (§7.3) — the stop step (Phase 2 step 1) is skipped because no app is running.
3. After a successful copy, the app is started normally (§5.4).
4. If the source returns no version, or Phase 1 fails for any reason: console error and exit code `2`.

Bootstrap is the **only** way to install an app from an empty `app/` directory in v1; there is no manual install command. Shipping just `Husky.exe` + `husky.config.json` and letting the launcher pull the app on first run is therefore a supported deployment story.

---

## 8. Watchdog Logic

### 8.1 Activity Tracking

The watchdog maintains a `lastActivity` timestamp for the app. It is reset to "now" on:

- Receipt of a `heartbeat` message.
- Receipt of any other message from the app.
- Any stdout or stderr output from the app.

### 8.2 Probing

- **If 10 minutes pass without any activity**: send an active `ping` to the app.
- Reply expected within **30 seconds**.
- On reply: `lastActivity` is reset, all is well.
- On timeout: increment the strike counter.

### 8.3 Escalation

- After **3 failed probes in a row**: the app is declared dead.
- Hard-kill (`Process.Kill`).
- Restart the app (§5.4) — this counts as one crash restart.

### 8.4 Crash-Restart Logic

- On process exit with a non-zero code (or a watchdog-triggered kill): restart.
- Maximum `restartAttempts` per **rolling one-hour window**.
- Pause between restarts: `restartPauseSec` seconds.
- If the limit is reached: Husky stops attempting restarts, remains in the "broken" state, visible in the console. Husky itself keeps running (in case a later update fixes the problem — but only passively, no further app starts without an update).
- On a successful new update: the restart counter resets and a start is attempted.

---

## 9. Source Providers

### 9.1 Provider Interface (Internal)

```csharp
internal interface IUpdateSource
{
    Task<UpdateInfo?> CheckForUpdateAsync(string currentVersion, CancellationToken ct);
}

internal sealed record UpdateInfo(
    string Version,
    Uri DownloadUrl,
    string? Sha256,
    SourceSuppliedConfig? Config
);

internal sealed record SourceSuppliedConfig(
    string? Name,
    string? Executable,
    int? CheckMinutes,
    int? ShutdownTimeoutSec,
    int? KillAfterSec,
    int? RestartAttempts,
    int? RestartPauseSec
);
```

`SourceSuppliedConfig` carries optional config fields that the source can supply on the app author's behalf — see §5.2 Config resolution.

### 9.2 GitHub Provider

**Config:**

```json
"source": {
  "type": "github",
  "repo": "chloe/umbrella-bot",
  "asset": "UmbrellaBot-{version}.zip"
}
```

`asset` is optional; if omitted, the provider picks the first asset whose name ends with `.zip`.

**Behavior:**

- Call: `GET https://api.github.com/repos/{repo}/releases/latest`.
- User-Agent header: `Husky/{version}`.
- Response includes `tag_name` (the version, possibly with a `v` prefix to be stripped) and `assets[]`.
- Asset match: the first asset whose name matches the `asset` pattern (placeholder `{version}` substituted).
- `DownloadUrl` is `assets[].browser_download_url` of the matched asset.
- `Sha256` is `null` (GitHub does not provide a hash by default — a future provider could fetch the digest via the assets API; skip in v1).
- Version comparison: SemVer-based.
- If the new version > current version: return `UpdateInfo`, otherwise `null`.

**Source-supplied config:**

The provider also looks for a `husky.config.json` to populate `UpdateInfo.Config`:

1. First, check if the release has an asset literally named `husky.config.json` — if so, fetch and parse.
2. Otherwise, fetch `husky.config.json` from the repo's default branch root: `GET https://raw.githubusercontent.com/{repo}/HEAD/husky.config.json`. 404 is fine — just means no source-supplied config.

The `source` block of any source-supplied config is dropped with a console warning (the user's local `source` always wins; otherwise apps could redirect themselves elsewhere). Only the deployment-metadata fields are accepted.

### 9.3 HTTP Provider

**Config:**

```json
"source": {
  "type": "http",
  "manifest": "https://chloe.neocities.org/x7k3p2-9f4q/umbrella/manifest.json"
}
```

**Manifest Format:**

```json
{
  "version": "1.4.3",
  "url": "https://chloe.neocities.org/x7k3p2-9f4q/umbrella/UmbrellaBot-1.4.3.zip",
  "sha256": "9b74c9897bac770ffc029102a200c5de",
  "config": {
    "name": "umbrella-bot",
    "executable": "app/UmbrellaBot.exe",
    "checkMinutes": 30,
    "shutdownTimeoutSec": 60
  }
}
```

`version`, `url` are required. `sha256` is optional but strongly recommended. `config` is optional and carries the deployment-metadata fields (§5.2) that the launcher should use unless the local config overrides them. The `source` block is intentionally not allowed in `config` — see §9.2.

**Behavior:**

- Call: `GET <manifest-url>`.
- User-Agent header: `Husky/{version}`.
- Parse the JSON, map `version`/`url`/`sha256` to `UpdateInfo` and the `config` block to `UpdateInfo.Config`.
- Version comparison: identical to GitHub.
- Auth: none. Security via non-public, hard-to-guess URLs ("security through obscurity" — explicitly accepted for non-public use).

### 9.4 Extensibility

- v1 has exactly these two providers, hardcoded with `if`/`switch` over `source.type`.
- Future versions may switch to a plugin architecture if needed.

---

## 10. Console Rendering

### 10.1 Two Modes

Husky renders in one of two modes, picked at startup based on whether
stdout is attached to an interactive terminal:

- **Interactive mode** (default on a real terminal): log lines stream
  into the terminal's **native scrollback** (no alt-screen takeover),
  with a 3-row **sticky footer** pinned to the bottom carrying the
  status line, a separator, and the action bar. The terminal's own
  selection, mouse wheel, and scrollback history all keep working;
  long lines wrap the way the terminal wraps them. Hotkeys arrive
  through a raw-mode key loop.
- **Line mode** (when stdout is redirected, piped, or
  `NO_COLOR`/non-TTY environments): the line-oriented stream with
  banner + log lines, no footer. `husky | grep`, `husky > file.log`,
  CI-style log scraping, and journald all stay first-class. Bars and
  spinners auto-degrade to a single final-frame line per operation.

Detection is `Console.IsOutputRedirected == false` and not `NO_COLOR`.
The footer (`StickyFooter`) is a no-op on non-interactive or too-small
terminals, and if raw mode can't be entered Husky degrades to plain
line output at run time without complaining.

### 10.2 Libraries

- **[Retro.Crt](https://github.com/chloe-dream/retro-crt)** (NuGet,
  **0.8.0+**) for everything: color, screen buffer, banner, the
  `StickyFooter` bottom region, and `RawMode` + `TerminalInput` for
  the key loop. No `Retro.Crt.Tui` — the alt-screen `Application` /
  `LogViewer` layer was dropped when Husky moved to native scrollback
  (it only repainted on input, so a launcher fed from background
  threads showed nothing until a keypress).
- Pascal CRT-Unit-style API. Tiny, no third-party dependencies,
  trim- and AOT-clean.
- Cross-platform, ANSI-capable on modern terminals; `NO_COLOR` and
  Windows legacy console are handled by the library. Truecolor is
  quantized down to 256-color or Standard16 if the terminal cannot
  render the full 24 bits.

### 10.3 Line Mode

The fallback used when stdout is redirected.

#### Greeting Banner

On startup: Husky ASCII logo + tagline. Stored as a `string[]` in
the launcher and rendered with `Retro.Crt.Banner.Gradient` — colour
interpolated per line from a cool ice-blue at the top to a brighter
cyan at the bottom. The banner renders once at startup in **both**
modes; in interactive mode it scrolls up into the terminal's native
scrollback above the footer and stays in history like any other line.

```
  <husky-ascii-art (rendered with ice-blue→cyan vertical gradient)>

  Husky v0.3.0
  your loyal app launcher
```

#### Log Line Format

```
HH:mm:ss  <source>  <message>
```

- `HH:mm:ss`: time, dimmed (gray).
- `<source>`: fixed width 8 chars (right-padded), color-coded by source:
  - `husky` → cyan
  - `app` → green (stdout) / red (stderr)
  - `pipe` → dim (low-level transport diagnostics)
- `<message>`: default foreground, with status-word highlights
  (e.g. `up` green, `down` red, `degraded` yellow, `growling` yellow).

#### Bars and Spinners

Auto-degraded to a single start line + a single summary line. No
in-place redraw, no cursor magic — `husky > file.log` produces clean,
parseable output. The summary line carries the authoritative result
(byte count, duration, outcome).

```
13:47:03  husky     fetching v0.0.20…
13:47:08  husky     fetched 6.8 MB in 4.2 s.
```

### 10.4 Interactive Mode

The default rendering on an interactive terminal: native scrollback
above, a 3-row sticky footer pinned to the bottom.

#### Layout

Log lines stream into the terminal's normal scrollback exactly like
line mode — same `HH:mm:ss  <source>  <message>` format, same
status-word highlights (§10.3). The only chrome is the footer, which
the terminal keeps pinned to the bottom three rows while everything
above scrolls in native history:

```
 13:47:03  husky    sniffing for updates… up to date.
 13:47:08  app      worker started, 12 guilds connected     ← native scrollback
 13:47:15  husky    new version found: v1.4.3                 (real terminal history,
 13:47:15  husky    manual mode — waiting for trigger.        wheel / PgUp / selection)
 husky v0.4.4          umbrella-bot v1.4.2          healthy  ┐ status   (footer row 1)
───────────────────────────────────────────────────────────┤ separator (footer row 2)
 s save logs · u check & install · x exit                   ┘ actions  (footer row 3)
```

- **Status row** (footer row 1): `husky vX.Y.Z` left-aligned in the
  launcher accent colour (cyan, bold); `<appName> v<appVersion>`
  centered in white; current health (`healthy`/`degraded`/`unhealthy`)
  right-aligned in its semantic colour. Pre-handshake the center is
  `(starting…)` in dim grey; while crash-restart-paused, the right
  side reads `down — restarting in 3s` in red.
- **Separator** (footer row 2): full-width `─` (U+2500) in dim grey on
  black, dividing the live log from the footer chrome.
- **Action bar** (footer row 3): three commands rendered as
  dot-separated hotkey hints (`s save logs · u check & install · x exit`).
  The hotkey letter is in the launcher's accent colour and bold; the
  label is light grey on black; the middle-dot separator picks up the
  label colour. A transient toast (e.g. the save-logs confirmation,
  §10.4 actions) takes this row over for ~3 s.

The footer is painted via `StickyFooter` (DECSTBM scroll region). It
repaints on demand (`Refresh`) when status/health/action state change,
and reflows itself on terminal resize.

#### Hotkeys

A raw-mode key loop (`RawMode` + `TerminalInput.WaitForEvent`) on the
launcher's main thread dispatches:

- `s` — save logs (see §10.4 actions).
- `u` — check & install (force a fresh source poll, apply if newer).
- `x`, `Esc` — exit.

Raw mode keeps `keepSignals: true`, so `Ctrl+C` still raises a real
`SIGINT` handled by the launcher's signal path (§5.5) — a hung Husky
can't swallow the user's `Ctrl+C`.

Scrolling, selection, and history are the **terminal's own** — mouse
wheel, `PgUp`/`PgDn`, and click-drag selection all operate on native
scrollback. Husky no longer ships an in-app scrollbar or focus model;
there is nothing to Tab between.

#### Actions

- **Save logs** — writes the current in-memory log buffer to
  `husky-logs-<UTC-timestamp>.txt` in the working directory and
  shows a 3-second toast (or status-bar replacement) reading
  `wrote N lines → husky-logs-…`. No clipboard integration — file
  export is portable across Windows/Linux without OS-specific
  plumbing.
- **Check & install** — forces a fresh source poll right now, bypassing
  the next scheduled polling tick. If the source reports a newer
  version than what's currently installed, the launcher follows up
  with the normal apply flow (graceful shutdown → install → restart).
  Hidden when the connected app does not advertise `manual-updates`;
  always actionable when shown — no greyed state. A second press
  while a check (or its follow-up apply) is still in flight is
  ignored with a console log. Distinct from the inbound `update-now`
  message (§3.5.12), which trusts the app's prior `update-check` and
  applies the cached update without re-polling.
- **Exit** — graceful shutdown via §5.5 (sends `shutdown` with
  `reason: "manual"`, awaits ack and timeout, then exits `0`).

### 10.5 Husky Voice

Husky speaks tersely, like a dog — short, punchy, with the occasional
`woof.`. But never in the way. Examples:

- Start: `woof. starting umbrella-bot`
- Update check: `sniffing for updates` → `up to date.` or `new version found: v1.4.3` or `poll failed.`
- Download: `fetching v0.0.20…` → in-place updates while running → `fetched 6.6 MB in 4.2 s.`
- Extract: `extracting` → `extracted.` or `extract failed.`
- Shutdown: `asking app to sit` → `app sat down.` or `app didn't respond. growling.` or `double interrupt — taking it down.`. Intermediate states: `no shutdown-ack — waiting anyway`, `pipe is gone — waiting for exit`, `grace period (+10s)`.
- Restart: `back online.` or `woof. <appname> v<version> is up.`
- Crash limit reached: `enough. lying down.`

These are *suggestions* — the implementer is free to stay in the
Husky voice as they see fit. No AI clichés (`delve`, `navigate`,
`seamless`, …) anywhere in user-visible text.

### 10.6 In-Place Log Lines

Long-running operations (downloads, sniffing, extracting, shutdown
waits) re-render their own log line in place rather than spawning a
separate widget:

```
13:47:03  husky     fetching ███████░░░░░░░░░░░░░░░░░░░░░░░  18%
                 ↓
13:47:03  husky     fetching ████████████████████████████░░  92%
                 ↓
13:47:03  husky     fetched 6.8 MB in 4.2 s.   (timestamp updates on completion)
```

Behaviour:

- One operation = one log line, one frozen start-timestamp during
  updates. The completion line gets a fresh timestamp.
- Frame rate capped at 10 Hz; the 0% / 100% frames are guaranteed.
- **Interactive mode**: rewrites the current scrollback row via a
  carriage return (`\r`). Regular log lines (app stdout, watchdog
  warnings) that arrive while the in-place line is open are buffered
  so they can't scroll it out of reach, then flushed below it when it
  completes.
- **Line mode**: degraded to start line + summary line, no in-place
  redraw — pipe-friendly (see §10.3).
- Two in-place operations at once are a contract error and throw —
  Husky never has more than one running by design (download or
  shutdown-wait or extract or sniff — never overlapping).

The `force: true` channel from the previous live-widget gate remains
for line-mode error escalations that bypass the in-place line. In
interactive mode a forced line can't safely overwrite the animating
row, so it is buffered like any other line — but Husky rings the bell
(`Crt.Bell`) so the escalation isn't silently delayed.

### 10.7 Logs Are Not Files

- Husky writes nothing to disk on its own. The log buffer is in-memory
  only, capped at **5 000 lines** (oldest dropped).
- Persistence is the operator's choice: in line mode, redirect output
  (`> husky.log` / journald). In interactive mode, the user presses
  `s` to export the current buffer.
- This satisfies §1.2 ("no file-based logging") — the `s` action is
  an opt-in snapshot, not a streaming log file.

---

## 11. Cross-Platform

### 11.1 Platform Differences

| Aspect | Windows | Linux |
|--------|---------|-------|
| Pipe backend | real named pipe | Unix domain socket (`/tmp/CoreFxPipe_<name>`) |
| Pipe ACL | `PipeSecurity` with the current user | default filesystem permissions are sufficient |
| Process tree kill | `Process.Kill(entireProcessTree: true)` | same (.NET supports it from .NET 5+) |
| Service integration | not Husky's concern | not Husky's concern |

### 11.2 Platform Conditionals

- `OperatingSystem.IsWindows()` for Windows-specific branches.
- `[SupportedOSPlatform("windows")]` attributes on Windows-only methods.

### 11.3 Test Matrix

- At least: Win11 x64, Linux x64 (Ubuntu 24.04), Linux arm64 (Raspberry Pi OS).
- macOS is not an official target but should not be intentionally broken.

---

## 12. Error Handling

### 12.1 Launcher Errors

| Scenario | Behavior |
|----------|----------|
| CLI flags invalid (unknown flag, conflicting source flags, missing `--dir` target) | banner + error message in console, exit code 2 |
| Config missing/broken (no `source` resolvable from CLI or local file, or local file unparseable) | banner + error message in console, exit code 2 |
| Executable not found | banner + error, exit code 2 |
| Update source unreachable | console warning, update check skipped, app keeps running |
| ZIP download failed | console warning, update aborted, app keeps running |
| ZIP hash mismatch | console warning, update aborted |
| ZIP extract error | console warning, update aborted |
| App start failed | crash-restart logic (§8.4) |
| Pipe connect timeout after app start | console warning; app keeps running without Husky link, watchdog runs on stdout activity only |
| App not responding to shutdown | hard-kill after timeout, console warning |

### 12.2 Client Library Errors

| Scenario | Behavior |
|----------|----------|
| `HUSKY_PIPE` not set | standalone mode, all API calls are no-ops |
| Pipe connect error | exception thrown; the app author may catch or ignore |
| Pipe disconnected at runtime | `ShutdownToken` fires with `LauncherStopping` |
| Hello handshake fails | exception from `AttachAsync()` |
| Inbound message cannot be parsed | library logs to stdout, ignores the message, continues |

---

## 13. Build & Publishing

### 13.1 Husky.exe (Launcher)

- `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`
- `dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true`
- `dotnet publish -c Release -r linux-arm64 --self-contained -p:PublishSingleFile=true`

Output: a single binary `Husky.exe` (Windows) or `Husky` (Linux).

### 13.2 Husky.Client (NuGet)

- Standard `dotnet pack -c Release`.
- Single-target `net10.0` for v1. Add older targets only if a hosted app explicitly needs them — there is no preemptive multi-targeting.
- `Husky.Protocol` as a project reference, packed into the NuGet (or shipped as a separate package — decide at actual release time).

### 13.3 Versioning

- SemVer throughout.
- Husky.Protocol-version and wire-protocol-version are independent concerns:
  - The wire-protocol version (§3.6) is the integer in hello/welcome.
  - NuGet package versions follow standard SemVer.

---

## 14. Out of Scope

Explicitly *not* in v1.0 — possible candidates for later:

- **Update channels** (stable / beta / nightly).
- **Code signing** and Authenticode verification.
- **Self-update** of the launcher itself (via a bootstrap binary).
- **Multi-app management** in a single Husky instance.
- **MCP server integration** (Claude Code → Husky → apps). Will be a separate project that talks to Husky's pipes.
- **Rollback** to a previous version.
- **GUI / tray icon**.
- **Structured `log` message type** over the pipe — stdout is enough.
- **Plugin architecture** for source providers.
- **Husky packaging helpers** — see Future Ideas below.

---

## Future Ideas

Not specified, not committed — sketches for later, when v1.0 is in the wild and we know what's worth building.

### Distribution helpers — getting Husky to users without friction

v1.0 covers the protocol, the capability handshake, and config layering (local + source-supplied + defaults — see §5.2). What v1.0 does **not** cover is the user-facing distribution story: how does someone without prior Husky knowledge actually run an app like Fishbowl? Sketches for later, all additive on top of the v1.0 layering:

- **More CLI overrides.** v1.0 ships `--manifest` / `--repo` / `--asset` / `--dir` (§5.2.1). Future flags could override individual config fields too: `--exec Z.exe`, `--name foo`, `--check 30`. Same merge layer, just more knobs.
- **Slug invocation.** `husky chloe-dream/the-fishbowl`. The slug is the source — Husky points at GitHub, pulls the latest release's `husky.config.json` (already a v1.0 mechanism via §9.2), runs. No local file, no flags. Most natural once Husky is on PATH.
- **Global install story.** winget / brew / install.sh / scoop. Makes slug invocation natural — install Husky once, then any Husky-ready app is one command. Always parallel to portable bundled-binary deployments, never mandatory.
- **`husky init` subcommand.** Guided Claude-Code-style prompt sequence (4-5 questions) that writes a `husky.config.json`. For app authors committing one to their repo, or users authoring a local override. Sequential prompts only — no TUI forms (Retro.Crt forms layer is explicitly *not* a prerequisite).
- **`husky-package` GitHub Action.** Reusable Action that drops into a release workflow, produces a ZIP with `Husky.exe` (correct RID) + generated `husky.config.json` as a release asset. The natural deployment artifact for app authors who want a one-file download for their users.

A hosted registry service (`husky register name --repo ...`) and a web-form config generator were both considered and explicitly rejected: the registry is a forever-hosting commitment unfit for an indie tool; the web form is marketing without an audience.

Status: parking lot. Pick whichever matches the deployment shape we actually need once v1.0 is being used.

---

## 15. Glossary

| Term | Definition |
|------|------------|
| **Launcher** | `Husky.exe` — the host process. |
| **Hosted app** | The application Husky starts and supervises. |
| **Pipe** | Named pipe / Unix domain socket for IPC between launcher and hosted app. |
| **Source / update source** | Where new versions are discovered (GitHub Releases / HTTP manifest). |
| **Manifest** | JSON document describing version, URL, hash (HTTP source). |
| **Strike** | A failed health probe. |
| **Cutover** | The brief moment during an update when the app is down. |
| **Standalone mode** | App runs without Husky (e.g. in the debugger). The library detects this and no-ops. |
| **Update mode** | Per-launcher-process setting: `auto` (apply on discovery) or `manual` (notify and wait for `update-now`). Initial value comes from `hello.preferences`; can be changed at runtime. |
| **Capability** | A feature token declared in `hello.capabilities` / `welcome.capabilities`. Replaces static config knobs for "does this app speak feature X?". |
| **Source-supplied config** | Deployment metadata (`name`, `executable`, timing knobs) provided by the source (HTTP manifest's `config` block, or a `husky.config.json` in a GitHub release/repo). Lets the local config shrink to just `{ "source": ... }`. |
| **Working directory** | The directory holding `husky.config.json`, `app/`, and `download/`. Defaults to the process cwd; overridden by `--dir`. Independent of where `Husky.exe` lives. |
| **CLI source flags** | `--manifest` / `--repo` / `--asset` — top-priority config layer (§5.2). Ephemeral; nothing is written to disk. |

---

## Appendix A — Recommended Implementation Order

A suggested order for the initial implementation:

1. Create the solution and project structure.
2. **Husky.Protocol**: records, JSON serialization, pipe-naming constants, tests.
3. **Husky.Client**: connect/hello/heartbeat, shutdown handler, `IsHosted` / `AttachIfHosted`. Tests with a mock pipe server.
4. **Husky** skeleton: local-only config loading (defer the source poll and merge to step 12), process start/stop, stdout piping, pipe server, hello handler.
5. **Husky** watchdog: activity tracking, probes, escalation.
6. **Husky** update flow: phase 1 (download/extract), phase 2 (stop/copy/start) — auto-mode trigger only.
7. **Husky** source providers: GitHub, then HTTP.
8. **Husky** console rendering: Retro.Crt, banner, log format, Husky voice.
9. **Husky** crash-restart logic.
10. **Capabilities & preferences** in `hello`/`welcome`: emit and consume `capabilities` arrays on both sides; thread the intersection through dispatch so unsupported messages are never sent and unsolicited optional pushes are gated.
11. **Update protocol** end-to-end: `update-check` / `update-status` / `update-available` / `update-now` / `set-update-mode` on both sides, gated by the `manual-updates` capability; `updateMode` preference in `hello`; manual-mode trigger path in §7.1; client API surface (`CheckForUpdateAsync`, `RequestUpdateAsync`, `SetUpdateModeAsync`, `UpdateAvailable` event).
12. **Source-supplied config**: extend `UpdateInfo` with a `Config` block; populate it in the GitHub provider (release-asset and repo-root lookup) and the HTTP provider (`config` field in manifest); switch the boot sequence (§5.3) to do the initial source poll *before* config resolution; implement the merge per §5.2 precedence rules; verify the case where the local file contains only `{ "source": ... }`.
13. **CLI source flags & working directory**: parse `--dir`, `--manifest`, `--repo`, `--asset` per §5.2.1; switch all path resolution from "launcher's directory" to "working directory"; insert the synthetic CLI `source` as the top layer of the §5.2 merge; verify (a) `husky --manifest <url>` in an empty directory triggers bootstrap and runs, (b) CLI `source` overrides a local file's `source` with a console warning, (c) flag conflicts exit `2` cleanly.
14. End-to-end test: example app + Husky + simulated GitHub release, exercising both auto and manual modes, a runtime mode switch, a release whose deployment metadata comes entirely from a `husky.config.json` asset (local config is just `{ "source": ... }`), and a fully CLI-driven launch (no local file, just `husky --manifest <url>`).

---

*Husky — your loyal app launcher.* 🐺
