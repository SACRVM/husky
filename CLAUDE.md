# CLAUDE.md — Working on Husky

> *Read this first. Then [LEASH.md](./LEASH.md). Then start.*

## Who you work for

**SACRVM** — developer and product owner. Communicates with you in **German**; reply in German. Code is **English-only**.

## Project vibe

Indie tool, not enterprise software. Pragmatic but sexy. Minimal by default — every knob, interface, and layer is a tax. Modern .NET 10 / C# 14 is the baseline. Windows-first, Linux must work, macOS nice-to-have. Husky has a voice — see [LEASH §10.4](./LEASH.md#104-husky-voice).

## Language — non-negotiable

- **German:** anything SACRVM reads outside source files (replies, questions, status updates, design discussions).
- **English:** all identifiers, comments, string literals, log lines, exception messages, UI text, READMEs, test names, commit messages.
- **No mixed-language code.** Not one German variable, comment, or log line.

## How to work

**When in doubt, ask SACRVM in German.** [LEASH.md](./LEASH.md) is the contract; if it is unclear, contradictory, or silent on something meaningful — ask. Do not invent design. Be specific: 2–3 options, recommend one with a one-line reason.

Trivial choices (internal names, `List<T>` vs `IEnumerable<T>`, `private` vs `internal`) — pick the simpler option, move on. Anything SACRVM sees (file layout, log-visible names, API shape, defaults) — ask.

### Code style

Write to current Microsoft .NET design guidelines and the newest stable C# (C# 14 on .NET 10). Prefer the newer idiom: primary constructors, collection expressions, `field` keyword, pattern matching, target-typed `new`, raw strings, `required`, `init`/`readonly`, file-scoped namespaces. No preview features — wait for GA.

- Records for data; classes for behavior. Sealed by default. Composition over inheritance.
- `async`/`await` for all I/O. No `.Result`, `.Wait()`, sync-over-async. `IAsyncEnumerable<T>` where streaming fits.
- Nullability on. Never `#nullable disable` to silence warnings.
- `var` when the RHS makes the type obvious; explicit otherwise.
- No Hungarian notation, no `_field` underscores — use the `field` keyword or plain auto-properties.
- One public type per file; filename matches type.
- `System.Text.Json` and `LoggerMessage` source generators over reflection.

### Testing

xUnit, FluentAssertions if it reads cleaner. Test what matters: protocol parsing, message dispatch, version comparison, source-provider URL building, watchdog state, update file ops. Skip trivial getters/setters; do not chase coverage. Mock the pipe in unit tests; real pipes only in E2E.

### Commits

English. Small and focused, one logical change each. Format `<type>: <summary>` with `feat | fix | refactor | test | docs | chore | build`. Body explains *why*, not *what*.

### Status updates (German)

After each Appendix-A step: what's done, what's next, decisions you made or need SACRVM on, anything skipped or compromised — flag it.

## Firepit-Inbox

Beim Session-Start alle `.firepit/inbox/*.md` lesen und verarbeiten. Erledigtes mit `firepit_inbox_complete` markieren (verschiebt nach `inbox/processed/`). Tool lädt erst nach Claude-Code-Neustart — fehlt es, neu starten.

## Never do

- Add features not in [LEASH.md](./LEASH.md) — ask first.
- Add config knobs "for flexibility."
- Inject DI containers, mediators, CQRS into a 3-project tool.
- Use `Microsoft.Extensions.Logging` for launcher user-facing output — that is [Retro.Crt](https://github.com/SACRVM/retro-crt) (`Crt`, `Banner`, `ProgressBar`, `Log`). MEL is fine for internal diagnostics only.
- Add NuGet packages casually — if LEASH does not name it, ask.
- Leave silent `TODO`s — surface them in the status update.
- Use AI clichés in user-visible text ("delve into", "navigate the landscape of"). Husky is curt.

## Working order

Follow [LEASH §Appendix A](./LEASH.md#appendix-a--recommended-implementation-order). Per step: implement → tests where useful → `dotnet build` and `dotnet test` both green → commit → German status update.

## Final note

Side project, no deadline. Quality over speed. If you are cutting a corner, **say so** — SACRVM decides if it is the right one.

🐺 *woof.*

## Firepit knowledge

Before researching something that may already be known, query the knowledge base with the `firepit_knowledge_search` MCP tool (scope `both` covers this project plus the global base). Save durable findings with `firepit_knowledge_add` — written in English, per the indexing convention. The created markdown files live under `.firepit/knowledge/` and are committed like any other file.

## Firepit pinned knowledge

@.firepit/knowledge-pinned.md

The import above auto-loads the knowledge docs marked `pin: true` in their frontmatter — always-on rules that apply every session without a search. Firepit regenerates the file from the pinned docs; don't edit it directly. Pin/unpin via the pinned flag on `firepit_knowledge_add` / `firepit_knowledge_update`, and keep the pinned set small — everything else stays reachable through `firepit_knowledge_search`.

## Firepit artifacts

When you produce a file the user will want to open — a report, screenshot, diagram, generated image, log excerpt, build output, or an executable you built for them to run — pin it with the `firepit_artifact_add` MCP tool so it appears in the project's paperclip pane. Do this as you produce it, not at the end of the session; a path buried in scrollback is a path the user has to hunt for. Pinning only links the file — it stays where it is, and `firepit_artifact_remove` never deletes it. Check `firepit_artifact_list` first so you update an existing entry instead of piling up near-duplicates, and unpin what has gone stale.

## Firepit conventions

<!-- claude-firepit-fragments -->

@../.firepit/projects/claude.md
@../.firepit/projects/claude-github-public.md

The two imports above are shared files in the Firepit central repo — edit them there and every project follows. They carry policy; the tools themselves are described by Firepit's MCP server at the handshake, so nothing is duplicated between the two.
