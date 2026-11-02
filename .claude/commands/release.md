---
description: Bump Husky launcher version, tag, push — release.yml then publishes binaries + Husky.Client to GitHub Release / nuget.org.
argument-hint: [test|patch|minor|major|<x.y.z>]
---

# /release — cut a Husky launcher release

You are cutting a Husky **launcher** release (the `Husky.exe` artifact).
Follow these steps **exactly**, in order. **Never skip the confirmation
step.** Speak to SACRVM in German; commit messages and tag names stay
English.

`Husky.Client` (the NuGet package consumed by hosted apps) versions
**independently** — it is *not* in scope for this command. The release
workflow re-packs and re-pushes whatever `<Version>` is currently set
in `src/Husky.Client/Husky.Client.csproj` with `--skip-duplicate`, so a
launcher release is a no-op for the client unless that version has
been bumped separately. If the user wants both bumped, ask which
versions and bump `Husky.Client.csproj` first in a separate commit
before invoking this command.

## Argument

`$ARGUMENTS` is one of:
- (empty) — auto-detect bump from commits since the last tag
- `test` — dry-run: print the plan, do nothing
- `patch` / `minor` / `major` — force that bump
- `1.0.0` (or any `X.Y.Z`) — set this exact version

## Step 1 — Sanity checks

Run all four. If any fails, stop and report to SACRVM — do not proceed.

```bash
git rev-parse --abbrev-ref HEAD                                    # must be 'main'
test -z "$(git status --porcelain)" && echo clean || echo dirty    # must be 'clean'
git fetch origin main --quiet
test "$(git rev-parse HEAD)" = "$(git rev-parse origin/main)" && echo synced || echo behind  # must be 'synced'
test -f src/Husky/Husky.csproj && echo found || echo missing       # must be 'found'
```

## Step 2 — Determine current state

```bash
git describe --tags --abbrev=0          # last tag, e.g. v0.2.1
grep -oP '(?<=<Version>)[^<]+' src/Husky/Husky.csproj   # csproj version
git log $(git describe --tags --abbrev=0)..HEAD --format='%s'    # commits since last tag
```

If `git describe` finds no tag, treat last tag as `v0.0.0` and analyze all commits.

Also read `src/Husky.Client/Husky.Client.csproj`'s `<Version>` so the
plan in Step 4 can show whether the Client will piggy-back (same
version → `--skip-duplicate` no-op) or ship a fresh package.

## Step 3 — Decide the bump

Parse `$ARGUMENTS`:

| Arg | Action |
|---|---|
| empty | classify commits: any `feat:` → minor; only `fix:` → patch; only `refactor:`/`test:`/`docs:`/`chore:`/`build:` → **nothing to release**, stop here |
| `test` | same classification as empty, but stop after the plan in Step 4 |
| `patch` | force Z+1 |
| `minor` | force Y+1, Z=0 |
| `major` | force X+1, Y=0, Z=0 (unusual pre-1.0 — confirm twice) |
| `X.Y.Z` | use literally — must be greater than current |

Compute `new_version` from the current csproj version (NOT from the tag, in case they differ).

## Step 4 — Show the plan, ask for confirmation

Print to SACRVM in German, exactly like:

```
Letzter Tag:           v0.2.1
Husky.csproj <Version>: 0.2.1
Husky.Client <Version>: 0.1.3 (bleibt — pushed mit --skip-duplicate)
Commits seit Tag:      19 (8 feat, 5 fix, 6 chore/docs/etc.)
Vorgeschlagener Bump:  minor (weil 8 feats drin)
Neue Launcher-Version: 0.3.0
Neuer Tag:             v0.3.0
```

Then list the commits since the last tag, grouped by prefix (feat / fix
/ chore / docs / refactor / test / build / other), as a sanity check.

If the Husky.Client `<Version>` *is* greater than the version on the
last tag (i.e. the client was bumped in a prior commit since), say so
explicitly — the tag push will publish a new client package to
nuget.org as a side effect:

```
Husky.Client <Version>: 0.2.0 (NEU — wird zu nuget.org gepusht)
```

**If `$ARGUMENTS` is `test`: stop here. Do not change any files. Tell SACRVM „Trockenlauf — nichts geändert."**

Otherwise: ask **„OK so? [j/n]"** and wait for the answer.
- `j` / `ja` / `y` / `yes` → continue to Step 5
- anything else → abort, change nothing

## Step 5 — Bump csproj version

Edit `src/Husky/Husky.csproj`: change `<Version>OLD</Version>` to
`<Version>NEW</Version>`. Use the Edit tool with the full surrounding
`<Version>…</Version>` string for uniqueness.

Do **not** touch `src/Husky.Client/Husky.Client.csproj` — its version
is its own concern.

## Step 6 — Sanity build

```bash
dotnet build Husky.sln --configuration Release --nologo
```

Catches compile errors locally before the tag goes out. If it fails,
**stop**, show the error to SACRVM, leave the working tree as-is so
SACRVM can inspect — do not commit, do not tag, do not push.

(The release workflow runs `dotnet test` on the matrix runners, so
we skip the test pass here for speed. If the user wants the full gate
locally, they can run it manually before invoking `/release`.)

## Step 7 — Commit, tag, push

```bash
git add src/Husky/Husky.csproj
git commit -m "release: vNEW"
git tag vNEW
git push origin main
git push origin vNEW
```

Use the standard commit-message HEREDOC pattern; respect the repo's
commit attribution settings (do not add a Co-Authored-By trailer if
the repo's `.claude` config disables it).

## Step 8 — Hand off

Tell SACRVM in German:

```
Release vNEW ist raus.
- Tag gepusht → release.yml läuft
- Per-OS Binärartefakte (win-x64 + linux-x64, trim + AOT) ~5-8 min
- Husky.Client NuGet wird gepackt und gepusht (skip-duplicate falls Version unverändert)
- GitHub Release wird automatisch erstellt mit generated release notes + Asset-Anhängen
- CI-Status: https://github.com/SACRVM/husky/actions
```

Do **not** poll or wait for the CI run — just hand off.

## Hard rules

- Never push tags without Step 4 confirmation.
- Never use `--no-verify` or `--force` on any git command.
- Never touch `.csproj` fields other than `<Version>` (and never
  Husky.Client.csproj at all from this command).
- If anything is unclear or smells wrong (e.g., no commits since last
  tag, csproj/tag version mismatch, weird state), stop and ask SACRVM
  instead of guessing.
