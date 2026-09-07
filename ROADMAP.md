# Roadmap

Work spans multiple sessions. This file is the handoff: what is done, what is next, and which
questions are already settled. `DESIGN.md` holds the why behind the settled ones.

Last updated: 2026-09-07, after Milestone 5 item 3 aligned the docs. Milestones 1-4 are
done; what remains is everything between "works on this clone" and "someone else can use it",
listed under Milestone 5 below. Output tuning held two concrete changes: `sym`
applies `--max` in the server's relevance order and sorts only what survives, so a capped
broad query keeps the best matches; and a generated document's label now names the project
that consumed the generator, which the URI never did.

63 legs pass — the 55 rows in `probes/cases.jsonl` plus 8 scripted legs (three source-generator
staleness legs, the forced non-daemon fallback, the cold-server `diag`, the packaged-tool
install, and the two first-run failures). Quote the composition, not the total, so the next
drift between the two halves shows up as a sum that no longer adds up.

Getting there took the readiness rewrite below: the suite failed a *different*
pair of cases on each of three runs, always by answering with a cross-project or generated hit
missing rather than by erroring. That window — the sentinel proving the workspace loaded but not
that every project did — is closed: `WaitReadyAsync` now takes one sentinel per discovered project and
requires each to resolve to a location under its own project directory, so an incomplete answer
at exit 0 can no longer get past readiness. The `SettleAsync` decompilation guard remains, but
it is not load-bearing for it; it watches for an *empty* answer and that failure was merely
*incomplete*. `MatchSymbolsAsync` fires a single `workspace/symbol` query with no
retry-while-empty loop, for the same reason — readiness covering every project is what makes an
empty answer mean absent. The remaining limit is two `.csproj`
in one directory, which no path scoping can separate — documented, not scheduled.

That gate gap is closed, and not the way this file used to propose. The scoping half of
`Sentinel.Nested` — that a hit under a nested project is *discarded* when deciding the parent
is ready — was exercised by nothing, since all three fixture projects are siblings and every
`Nested` list is empty in every case the suite runs. The proposal was a fourth fixture project
nested under `App/`, costing a project load on every case; it would also have been a weak
probe, because it only goes red when Tests happens to load before App, a load-order race. The
predicate is now `Sentinel.Accepts`, a pure function over a URI, and `SentinelScopingTests`
pins it directly in a temp tree — including that a generated URI can never mark a project
ready. What a unit test cannot pin is that `ResolvesAsync` passes the sentinel's `Nested` list
at all; that stays a one-line coupling at the call site.

## Status

| Milestone | Scope | State |
|---|---|---|
| 1 | `ready` + `refs`, cross-project fixture, probe gate, both workflows | **done** |
| 2 | The hard fixture cases and the read commands | **done** |
| 3 | Daemon mode, then `skill/SKILL.md` | **done** |
| 4 | Remaining commands and output tuning | **done** |
| 5 | Shippable: install path, first-run errors, metadata symbols, docs, CI | **open** |

## Milestone 1 — the loop works (done)

`cslq ready` and `cslq refs`, a fixture with a cross-project reference, six probe cases, and
`bump.yml` / `probe.yml` both green. The full pin → bump → probe → PR loop was exercised against
a deliberately stale pin and opens a PR carrying a passing `probes` status.

## Milestone 2 — the hard cases

The point of this milestone is that a probe suite which only checks go-to-definition inside one
file passes while everything real is broken. Add the fixture cases first, then the commands.

Fixture:

- [x] A **source generator** producing a symbol that gets referenced from another project.
      `fixture/Gen` emits `Fixture.Core.Generated.BuildInfo.Stamp()` into Core via
      `RegisterSourceOutput` keyed on a syntax provider that looks for `Greeter`, so the
      generated symbol genuinely depends on the compilation — rename `Greeter` and the
      generated namespace disappears (CS0234). Deliberately *not*
      `RegisterPostInitializationOutput`: that emits before any compilation analysis and can
      never go stale, so a fixture built on it would pass without testing the path that can.
      `--sourceGeneratorExecutionPreference` was not needed at the default `Automatic`, and
      `workspace/_roslyn_refreshSourceGenerators` now has a stub handler. **Still untested:**
      staleness itself, and it is *blocked*, not merely undone — see the Milestone 3 item.
      `cslq` is one-shot: every invocation starts its own server and loads the workspace cold,
      so a probe case that mutates the fixture between two invocations exercises a cold load
      of changed sources and says nothing about whether a cached generated symbol is
      refreshed. Nothing can reach that path until a single server outlives an edit.
- [x] A file with **non-ASCII characters** on a line containing a symbol. Use an **astral-plane
      character (an emoji)**, not just an accented letter: positions are UTF-16 code units, which
      .NET string indices already are, so an accent passes even on a broken implementation. Only
      a surrogate pair catches code that counts runes or UTF-8 bytes.
      `fixture/Core/Party.cs` declares `Cheer` on an emoji-bearing line; `App/Program.cs:11`
      calls it with the emoji *before* the call, putting `Cheer` at UTF-16 column 39 (rune
      counting gives 38, UTF-8 bytes 41). No code in `cslq` counts characters today —
      `LocateAsync` forwards the caller's column and `Output` renders the server's — so these
      cases pin the server staying UTF-16 behaviourally, `didOpen` text matching what Roslyn
      parses off disk, and insurance for `def` / `outline` later. **Known gap:** the position
      case catches a rune-counting error (column 38 lands on the `.` and resolves `Party`, so
      the case fails) but not a UTF-8 one (41 is still inside `Cheer`); the `'column': 39`
      assertion in the JSON case is what covers that direction.
- [x] One **deliberate type error** for `cslq diag`. It must live in **App**, or a new
      project — never in Core. `probes/run.sh` compiles Core to arm its `CS9057` guard (the
      analyzer-vs-compiler version mismatch that otherwise degrades to a silently absent
      generated symbol), so an uncompilable Core would disarm that guard permanently.
      `fixture/App/TypeError.cs` holds a **cross-project** CS0029 (`int Wrong() =>
      Greeter.Farewell("x")`, a member Core exposes only for it, so the refs cases keep their
      pinned reference counts) rather than a self-contained one: binding it needs Core's reference
      resolved, so the misc-files state a freshly opened document is first bound against
      cannot report it. That is what makes the re-pull-after-load requirement testable —
      a first-response-only `diag` returns nothing and the case fails. Nothing builds App,
      so an uncompilable App costs the gate nothing.

Commands:

- [x] `cslq def <file>:<line>:<col>` — also accepts `Namespace.Type.Member`. Renders through the
      same `Output.WriteLocationsAsync` as `refs`; empty exits 1. **Verified on the wire, since
      the plan turned on it:** `textDocument/definition` fired *at* a declaration returns that
      declaration (count 1), not empty, so the symbol form is safe and keeps its server round
      trip. The server also honours the absent `definition.linkSupport` and answers
      `Location[]`, not `LocationLink[]` — there is deliberately no two-shape reader, because a
      deserialization failure beats silently rendering half a response.
      `def-position-json` asserts `'count': 1` at a position where `refs` returns 2: without
      that one assertion every `def` case would also pass if `def` were secretly `refs`, since
      `run.sh` can only assert that a substring *appears*. `def-symbol` cannot be distinguished
      from printing `LocateAsync`'s own answer by any external assertion — the two are
      identical by construction — so it is regression coverage, not endpoint coverage.
- [x] `cslq diag [path] [--errors-only]`. Calls `textDocument/diagnostic` optimistically; the
      dynamic `client/registerCapability` is still accepted and discarded. `workspace/diagnostic`
      was tried and dropped: the server answers it but returns **zero reports**, matching the
      `workspaceDiagnostics: false` in that registration, and the call is specified as a long
      poll, so attempting it only bought a timeout. With no argument, `diag` walks the `.cs`
      files under `--root` instead (`Program.SourceFiles`, shared with the sentinel inference).
      Exit code is 0 whenever the query was answered — a clean file is a successful `diag`.
- [x] `cslq outline <file | symbol>` via `textDocument/documentSymbol`. Hierarchical: the client
      declares `hierarchicalDocumentSymbolSupport`, and that capability's property name has to
      serialise to `textDocument.documentSymbol` or the server quietly falls back to the flat
      `SymbolInformation[]` form. Output is the one documented exception to DESIGN.md's output
      rules — see the note there. Exits 0 for a document with no symbols (an answered query,
      like `diag`); a target that fails to resolve exits 1 from the resolver.
      Targeting: a file path, a `file:line:col` spec, or a symbol whose declaring document is
      outlined. Anything file-shaped (containing a separator or ending `.cs`) resolves as a
      file and never falls through to the symbol resolver, which would otherwise answer a
      mistyped path with "no symbol matched 'Core/Missing.cs'" and a candidate dump. Overloads
      are collapsed by document first: several matches in one file are not ambiguity for
      `outline`, only differing documents are. `fixture/Core/Split.cs` carries both halves of
      that distinction — an overloaded `Left` in one document, and a `partial class Split`
      whose second half lives in `Split.More.cs` — so `outline-overloads-collapse` and
      `outline-ambiguous-fails` pin each direction. `Core/Empty.cs` declares nothing and pins
      the exit-0-on-no-symbols path.
      `outline-generated` targets `…BuildInfo.Stamp`, not the type: every case pinning
      `Program.Matches` is member-level, and what Roslyn puts in `containerName` for a *type*
      has never been verified, so a type-level target would have bet the case on an unknown.

Done: the first document opened only reports errors that need no loaded project (a missing
semicolon, say), and waiting on readiness does not help because the document is opened after it.
`LspClient.DiagnosticsAsync` re-pulls until two consecutive reports agree, with a 5 s budget;
`deliberate-error-diag` is the case that pins it, via the cross-project error above.

- [x] Expand `cases.jsonl` to cover `def` and `outline` — 29 cases now. `outline-truncates`
      pins `--max`, the only genuinely new capping logic in this milestone;
      `def-no-definition-fails` pins the empty-result exit 1, which is the one failure path
      `def` actually added (a symbol that does not exist was already pinned by
      `unknown-symbol-fails`). Note that `|` is both the `expect` separator and `outline`'s
      gutter, so no case can quote a rendered outline row — see the header in `run.sh`.
- [x] Expand `cases.jsonl` to cover the remaining fixture case (the deliberate type error).
      `deliberate-error-diag`, `-diag-json` and `-diag-workspace`; the last one covers the
      no-argument file walk, which no path-taking case would reach. The
      symbol and position forms are kept separate on purpose — `LocateAsync`'s position branch
      never touches the server, so a single case would pass with `workspace/symbol` coverage of
      generated symbols entirely broken.

## Milestone 3 — daemon and skill

Wire behaviour was probed on 2026-09-04 before anything was designed around it; the daemon
entries under "Verified facts" are the output. Three findings moved this milestone: the daemon
is **shared**, not per-client; `LspClient` needs no protocol change at all; and source-generator
staleness is already reachable without any client work.

- [x] Switch `ServerArgs` to `--daemon-mode`. Do **not** pass `--clientProcessId`; it makes the
      server exit when the client does, which defeats the point.
- [x] **Decided: daemon on by default, `--no-daemon` to opt out.** ~3.2x on `refs` is the whole
      value for an agent, and the cold path stays reachable for anyone who needs it.
      `probes/run.sh` keeps its cold-load coverage by scoping itself with
      `ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME=cslq-probe-$$` plus a 60 s keepalive, so the suite
      gets a daemon of its own rather than inheriting whatever the developer's session left
      running — a stale daemon would otherwise let the gate lie. Accepted costs: `--log-level`
      against an already-running daemon is silently a no-op (the daemon takes its configuration
      from whoever launched it), and the silent non-daemon fallback is now the *default* path's
      failure mode, which is why `cslq` reports it. `no-daemon-refs` is the one case that
      still runs a dedicated server, since the whole suite would otherwise stop covering that
      path — which is also the path a fallback takes.
- [x] Verify what a second concurrent client gets. It **shares** the first client's daemon; the
      earlier "own isolated server instance" wording here was a wrong guess, and sharing is the
      documented design. One daemon served two different `--root`s with no symbol leakage in
      either direction, order-independent; three simultaneous clients all exited 0.
- [x] Verify the daemon survives killing the first client's whole process tree. It does —
      `taskkill /T /F` on the `cslq` chain left the daemon up and the next client reconnected to
      the same pid. Our own `shutdown` + `exit` does not kill it either.
- [x] `--daemonKeepAlive` / `ROSLYN_LANGUAGE_SERVER_DAEMON_KEEPALIVE` confirmed: default 900 s
      after the last client disconnects, and the env var propagates by plain inheritance through
      the whole launch chain.
- [x] Re-measure latency warm and update the README table.
- [x] **Source-generator staleness**, carried over from Milestone 2. Needed neither `didChange`
      nor a `didChangeWatchedFiles` capability — the daemon runs its own file watcher. Three
      legs at the end of `probes/run.sh`, not `cases.jsonl` rows: a row is one invocation and
      cannot restore what it changed. Baseline present, rename `Greeter` on disk (the syntax
      provider the generator keys on) and assert the generated symbol goes away, restore and
      assert it comes back. Both directions were immediate and deterministic — the poll loop
      is insurance, not a measured need. Every leg gates on `--sentinel Cheer`, which the
      rename does not touch, so absence is never concluded from an unloaded workspace; the
      legs run in order because only restore-and-present proves the daemon is still live. A
      `trap ... EXIT` restores `fixture/Core/Greeter.cs` even on failure.
- [x] Assert on the silent non-daemon fallback. `cslq` **detects** it — `LspClient` scans the
      thin client's stderr for `Falling back to non-daemon mode` /
      `Running language server in non-daemon fallback mode` and prints
      `cslq: daemon unreachable; this run used its own cold server`, read after the command
      rather than right after connecting because the marker races the initialize response.
      `non-daemon-fallback-reported` at the end of `run.sh` **forces** one: the thin client
      falls back when it times out waiting for a mutex named `Global\<pipeName>.client`
      (~20 s), so `probes/hold-mutex.cs` holds it while one client starts. The case needs its
      own pipe name — the mutex only guards check-server-then-launch, so a client that finds a
      daemon already listening never contends for it — and asserts both exit 0 (a fallback run
      still answers, which is what makes it silent) and the warning (that `cslq` noticed).
      **No new project was needed**, which was the reason this was briefly deferred: .NET 10
      runs a bare `.cs` file, and `dotnet run probes/hold-mutex.cs` compiles in under a second.
      Two traps in that holder, both of which look like the mechanism not working rather than
      like a mistake: the mutex must be created with `CurrentUserOnly = true` to match the
      server, and with `CurrentSessionOnly = false` or the `Global\` prefix is rejected. Either
      one wrong throws instead of contending, and so does passing the whole mutex name from
      the shell: a backslash immediately before `$` in a double-quoted string escapes the
      dollar, so `"Global\${pipe}.client"` yields a literal `${pipe}` and the holder guards a
      name nothing contends for — the case then fails silently.
      `run.sh` passes only the pipe name and the holder builds the rest. This is the second
      host-dependent case in the suite after the non-ASCII ones, since .NET implements named
      mutexes over files on Linux — **verified there on 2026-09-04**, in the `probe` run on
      PR #5, which passed all 34 cases on `ubuntu-latest`.
- [x] Write `skill/SKILL.md`.

Three client bugs surfaced while measuring, all fixed here:

- **`WaitReadyAsync` waited on `projectInitializationComplete` before polling the sentinel.**
  A client attaching to an already-loaded daemon never sees that notification — it fired
  before the process existed — so every warm run burned its entire timeout (300 s under
  `run.sh`) on a workspace that was ready before it connected. It now polls the sentinel from
  the start and keeps the notification only as diagnostic detail on the failure path. The
  docstring had already noticed the warm case; the code had not acted on it. `--timeout 0`
  still issues no query at all, so `premature-query-fails-loudly` still pins the timeout guard.
- **An initialize-time connection loss escaped as an unhandled `ConnectionLostException`** and
  discarded the server's stderr — the only thing that says why. It is now a `CslqException`
  carrying `StderrTail()`, after a bounded wait for the process to finish exiting so stderr is
  flushed. This is what turned the fallback experiment above from "connection lost" into the
  exact mutex name and file:line.
- **The sentinel resolving stopped implying every project was loaded.** Cold load closed that
  window by accident; warm attach reaches it in seconds. Roslyn binds a `ProjectReference` to
  the referenced project's built assembly until that project loads, so `def App/Program.cs:11:39`
  came back as a decompiled temp file under `MetadataAsSource` — exit 0, no context lines, no
  relation to the repo. It showed up as `def-non-ascii-json` failing once in a run where all 30
  other cases passed. `PathUri.IsDecompiled` recognises the shape and `LspClient.SettleAsync`
  re-asks for up to 10 s, on both `references` and `definition`. Residual risk: a `refs` answer
  that is merely *incomplete* in that window carries no decompiled URI to detect, so nothing
  catches it — the position form of `refs` and `def` never round-trips the symbol resolver,
  which is where `MatchSymbolsAsync` already has its own grace period.

`SKILL.md` conventions: YAML frontmatter with `name` and `description`, where the description is
the entire triggering mechanism and should lean pushy, since skills under-trigger. Body under
~500 lines, command reference inline. Include an explicit "task → use this command → do NOT use
grep/read for this" table, which is the part that actually changes agent behaviour. Tell the
agent to run `cslq ready` once at session start.

## Milestone 4 — the rest

- [x] `cslq impl <symbol | file:line:col>` via `textDocument/implementation`. Structurally
      `DefAsync`: same `LocateAsync`, same `SettleAsync` decompilation guard, same
      `Output.WriteLocationsAsync`. No `Protocol.cs` edit — `TextDocumentPositionParams`
      already existed — and no two-shape reader, for a stronger reason than `def` has: the
      client declares no `textDocument.implementation` capability node at all, so
      `linkSupport` is absent by construction and the server owes us `Location[]`.
      **Probed on the wire before it was designed**, which changed a case: fired at a member
      with no implementations, Roslyn does *not* answer empty — it returns the declaration
      itself, exactly as `definition` does at a declaration. So `impl` on an ordinary method
      is `def`, the planned `impl-none-fails` case was impossible, and
      `impl-falls-through-to-declaration` pins that behaviour instead. Empty comes back only
      for a position that resolves to no symbol, and `impl-no-symbol-fails` pins the exit 1.
      Exit 1 on empty rather than the `diag` / `outline` exit-0-is-an-answered-query rule,
      for consistency with `refs` and `def`: the empty case here is a failed lookup, not an
      empty answer.
      Fixture: `Core/Shape.cs` declares `IShape` and `Unit`, `App/Square.cs` the second
      implementer. The split across projects is the point — a pair inside Core resolves in
      one compilation and passes even with cross-project binding broken. Neither file touches
      `Greet`, `Farewell` or `Cheer`, so every pinned reference count is undisturbed, and
      `Square.cs` sorts after `Program.cs` so `InferSentinel` still picks `Program`.
- [x] `cslq sym <query>` — `workspace/symbol` was already wired up in `LspClient.SymbolsAsync`.
      A search, not a resolution: the query goes to the server as written, with no dotted
      narrowing, no ambiguity error and no candidate dump, since several matches are the
      point. Renders through a new `Output.WriteSymbols` — see the DESIGN.md note on why it
      is a second, narrower exception to the output rules. It fetches no source text at all,
      so a broad query costs no per-hit round trips; that is also why its JSON has no `text`
      field where `refs` and `outline` have one.
      `refs` and `sym` share one `Program.MatchSymbolsAsync`, which issues a single
      `workspace/symbol` query and selects from its candidates. The retry-while-empty loop it
      once needed is gone: it existed because the sentinel proved the workspace loaded and not
      that every project did, so a query fired in that window answered nothing —
      indistinguishable from a typo. Sentinel-per-project readiness closed that window, and an
      empty answer now means absent.
- [x] Output tuning — the one concrete item under it, DESIGN.md's generated-document label,
      is done. The label now leads with the consuming project's directory
      (`<generated>/Core/Gen/BuildInfo.g.cs`), which comes from
      `textDocument/_vs_getProjectContexts` rather than from the URI: the URI's stable fields
      name the *generator*, so one generator serving several projects rendered several
      distinct documents identically, and `Output` sorts on that label. The `_vs_id` it
      answers with is `<projectId guid>|<absolute .csproj> ($<tfm>)`; only the path half is
      read, the guid being regenerated per load like the URI's own authority. `_vs_label` is
      display text and is not parsed, for the same reason `containerName` never was. The
      lookup is made only for a generated URI, cached per document, and falls back to the old
      generator-only label if the server will not answer. The two ambiguity listings in
      `Program` were rendering the same label twice — `outline Stamp` on two consumers said
      "pick one" and then printed one string twice — and now name the projects.
      Fixture: `fixture2/`, where `Alpha` and `Beta` both consume `Gen2`. It is a second
      fixture rather than an extension of the first because `App` references `Core`, so a
      second copy of the generated type collides at the use site (CS0433); `Alpha` and `Beta`
      reference nothing of each other's. Three cases, and `run.sh` restores and builds it the
      way it does `fixture/Core`.
      Nothing else was ever written down under this item, so it closes with it.

## Milestone 5 — shippable

Opened 2026-09-06 from a review of what a user who is not this repository would hit. Every
finding below was reproduced on a scratch two-project solution outside the repo, driven by the
Debug binary from `src/Cslq/bin`, unless it says otherwise. The order is the order to do them in:
nothing after item 1 matters to a user who cannot start `cslq`.

- [x] **Resolve the server pin from somewhere an installed binary can reach.**
      `ServerArgs.ToolManifestRoot` walks up from `AppContext.BaseDirectory` — the *binary's*
      directory, not the cwd or `--root` — for `.config/dotnet-tools.json`, and `LspClient`
      uses that as the server's working directory. So `cslq` works from any cwd today, but only
      while the binary still sits under this repository; copied to `~/.dotnet/tools` or
      anywhere else it throws at startup, and no packaging is meaningful until this is settled.
      **Decided: ship the manifest inside the tool package.** A global tool's binary lands at
      `~/.dotnet/tools/.store/<id>/<v>/<id>/<v>/tools/net10.0/any/`, and a local install has
      the same `tools/net10.0/any/` shape in the NuGet cache; pack `.config/dotnet-tools.json`
      into that directory (`<None Include="../../.config/dotnet-tools.json" Pack="true"
      PackagePath="tools/net10.0/any/.config/" />`) and the existing `ToolManifestRoot` walk
      finds it on its first step, with `LspClient` already using that directory as the
      server's working directory. The pin then travels with the `cslq` version — a bump PR
      bumps both and a release ships both — and the user never types `--prerelease` or learns
      the server exists. On the not-restored failure, run `dotnet tool restore` in that
      directory once and retry; it is idempotent, and the message should name the one-time
      ~300 MB download. Rejected: resolving the manifest from `--root` (every target repo
      would have to carry the server pin), a separately installed global
      `roslyn-language-server` (loses the pin, so the probe gate guards nothing), a
      self-contained single-file binary (the server needs `dotnet` regardless), and
      referencing the server package directly (`DotnetTool` packages cannot be referenced).
      The tool was `csx` until 2026-09-06 and was renamed for this: `.csx` is the C# script
      extension, `dotnet-script` owns the word, and the nuget ID was already taken (2.0.3).
      `cslq` was free on nuget.org that day, so `PackageId` and `ToolCommandName` are both
      `cslq`; check again before the first push.
      Then `PackAsTool` / `Version` / licence metadata in `Cslq.csproj`, a `--version` flag
      (the only version string today is the hard-coded `ClientInfo("cslq", "0.1.0")` in
      `initialize`, never printed), and a tag-triggered `dotnet pack` + `dotnet nuget push`
      workflow behind a nuget.org API key secret, with bump PRs also bumping `Version` so a
      new pin is a new release. `--help` should also work in any position; `cslq refs Foo
      --help` is currently `unknown option`.
- [x] **First-run failures must be `cslq:` messages, not stack traces.** `Main` catches only
      `CslqException` and cancellation. With `dotnet` off `PATH`, `Process.Start` escaped as an
      unhandled `Win32Exception` with a stack trace and exit 127 — the first-time-user case
      exactly. An unrestored tool surfaced the server's stderr tail but never said to run
      `dotnet tool restore`. And a root with no solution burned the whole timeout before
      exiting 1 — measured with the scratch solution's `.slnx` removed: two bare `.csproj`
      never loaded, matching the "Verified facts" entry that `--autoLoadProjects` does not
      discover a bare project.
      **Done.** Every `dotnet` launch now goes through one `LspClient.StartProcess`, which
      wraps any start failure in a `CslqException` naming `dotnet` and the .NET 10 SDK, so the
      server launch and item 1's restore share the wrapping rather than each catching for
      itself; the restore-failed message now names the directory to run `dotnet tool restore`
      in, on top of the feed's own first line. And `InferSentinels` rejects a root with no
      `.sln`/`.slnx` at its top before `LspClient.StartAsync` is reached — 1 s instead of the
      whole timeout — saying that `cslq` loads the projects the root's solution lists, so
      `--root` must be the directory holding it. The `.csproj` scan is now reachable only for
      a root holding more than one solution, which `ProjectDirectories` says in as many words;
      `Workspace` in the test suite grew an auto-written solution because a solutionless temp
      tree is no longer a valid workspace, and the two tests that still need the scan write
      two solutions on purpose. Probe legs `no-solution-root-fails-fast` (which asserts the
      elapsed time, not just the message) and `dotnet-off-path-reports`; the case
      `no-project-root-reports` became `no-solution-root-reports`.
- [x] **README install section, and stop the docs disagreeing.** README had no
      prerequisites, no route from a clone to a binary on `PATH`, and no note that the first
      restore is ~300 MB; it opened with `cslq ready` as if `cslq` were already installed, and
      `skill/SKILL.md` assumed the same without saying how it got there.
      **Done.** README opens with an **Install** section before any example: prerequisites, the
      route that works today (clone → `dotnet pack` → `dotnet tool install -g cslq --source`),
      that `dotnet tool install -g cslq` from nuget.org is the intended route and **is not
      published yet**, the automatic first-run `dotnet tool restore` in the tool's own manifest
      directory with its one-time ~300 MB download, and that `--root` must be the directory
      holding the `.sln`/`.slnx`. A sub-section says how `skill/SKILL.md` reaches an agent —
      a copy into a skills directory, no plugin or marketplace — and that other agents take the
      same file. The disagreements are closed: the status line matches this table; the case
      count is stated as its composition (55 rows + 8 scripted legs = 63) in both files so the
      next drift stops adding up rather than going stale; the two references to a
      `Program.Query*` symbol-retry helper that never existed are gone, since
      `MatchSymbolsAsync` issues one query and no longer retries; and
      the latency tables were re-measured against the Release binary on 2026-09-07 and relabelled
      with the build configuration. `SKILL.md` also surfaces the two readiness limits from
      DESIGN.md — a project with no type to probe, and two `.csproj` in one directory — so an
      empty answer is not attributed to user setup by default, and its solutionless-root entry
      now says that case errors in about a second instead of hanging. `TestResults/` is in
      `.gitignore`.
- [ ] **Metadata symbols answer instead of being suppressed, and there is a way to ask what
      something is.** `def` at `Console.WriteLine` waited ~12 s in `SettleAsync`'s
      decompilation guard, then once returned `no results` at exit 1 and once returned a
      machine-absolute `MetadataAsSource` temp path; `outline System.Console` exits 1.
      `PathUri.IsDecompiled` treats every metadata answer as the not-yet-loaded fingerprint,
      which was right for a `ProjectReference` still bound to a built assembly and wrong for a
      framework or NuGet type, where decompiled *is* the answer. The two need telling apart —
      the sentinel-per-project readiness makes the first case rare enough that the guard may
      now be doing more harm than good, but re-measure before removing it. Separately, no
      command answers "what type is this, what are the parameters": no `textDocument/hover`
      or `signatureHelp` is wired, and it is the most common question an agent has. Also in
      this bucket, because they are contract holes an agent trips on: `ready` prints the
      literal `ready` under `--json`, breaking the `{count, truncated, results}` envelope on
      the one command every session runs first; the two ambiguity candidate dumps in `Program`
      ignore `--max`, so a broad ambiguous target floods the context the output rules exist to
      protect; and `ProjectOfAsync` already resolves which project compiles a file and its
      TFM but is only called for generated URIs — a `cslq project <file>` is nearly free.
- [ ] **A Windows CI leg, a format gate, and a `permissions:` block.** Both workflows run
      `ubuntu-latest` only, while the non-ASCII and mutex cases are the two host-dependent
      ones and the Git Bash console is where they would go red. `dotnet format
      --verify-no-changes` runs nowhere in CI. `probe.yml` declares no `permissions:`. And
      `IsUnder` plus the URI dictionaries compare paths `OrdinalIgnoreCase` on Linux too — a
      latent bug on the platform CI actually runs, unflagged anywhere. Not worth doing: a
      separate unit-test job; the tests run in under a second inside `run.sh`, and a second
      workflow would only duplicate the restore.

Considered and left out: `cslq daemon status/stop` (the daemon is unmanaged by design — see
README and `SKILL.md`), and any change to what `--log-level` does against a daemon someone else
started, which stays an accepted cost.

## Acceptance criteria

- [x] `.config/dotnet-tools.json` pins `roslyn-language-server`; `dotnet tool restore` reproduces it
- [x] Zero non-Microsoft C#-specific dependencies in the query path
- [x] `cslq refs` on a cross-project symbol returns correct `file:line` plus context
- [x] `cslq refs` on a source-generated symbol resolves
- [x] Column positions correct on the non-ASCII fixture line
- [x] `cslq diag` finds the deliberate error and does *not* report it before load completes
- [x] `cslq def` resolves from a use, a symbol and a source-generated symbol
- [x] `cslq outline` renders a nested document outline, including a generated document
- [x] Probe suite fails loudly when the server returns empty due to premature querying
- [x] `bump.yml` opens a PR that is gated (see the `probes` commit status caveat in the README)
- [x] Two concurrent clients work (sharing one daemon); daemon survives killing client 1's
      process tree
- [x] Warm command latency measured and recorded in the README
- [x] The daemon is the default path, with `--no-daemon` as the opt-out, and the probe suite
      still exercises a cold load
- [x] A source-generated symbol disappears when what the generator keys on is renamed on
      disk, and comes back when it is restored, against a daemon that outlives both queries
- [x] A run that silently fell back to a non-daemon server says so, and a probe forces one
- [x] `skill/SKILL.md` exists and tells an agent not to grep for what `cslq` answers
- [x] `cslq impl` resolves an interface member to implementers in two different projects
- [x] `cslq sym` searches the workspace by name, including a source-generated declaration
- [x] Readiness means every project loaded, not just one, so no command can answer with a
      cross-project hit missing at exit 0
- [x] The pure logic below the transport is unit-tested, and `probes/run.sh` runs those tests
      before it starts a server
- [x] Readiness waits for the projects the root's solution lists, so a repository carrying
      `.csproj` files the solution excludes does not time out
- [x] `cslq` answers about its own repository: `cslq ready --root .` and a `refs` that crosses
      from `src/Cslq` into `tests/`
- [x] A generated document's label names the project that consumed the generator, so one
      generator emitting into two projects renders two distinct labels rather than one
- [x] The nested-project half of readiness scoping is pinned by a test: a hit under
      `Web/Tests/` does not mark `Web/` ready
- [x] `cslq` installed outside this repository — a global tool or a copied binary — starts and
      answers; `cslq --version` prints the version a release is tagged with
- [x] `dotnet` missing, the tool not restored, and a root with no solution each produce a
      one-line `cslq:` message naming the fix, with no stack trace and no timeout
- [x] README tells a new user how to install `cslq` and the skill, and README, this file and
      `cases.jsonl` agree on the case count
- [ ] `cslq def` on a framework member returns its decompiled declaration without a 10 s
      stall, and a project-reference-still-bound-to-metadata answer is still told apart
- [ ] An agent can ask what a symbol is — type and signature — with one command
- [ ] `cslq ready --json` honours the envelope; ambiguity listings honour `--max`
- [ ] The probe suite runs green on a Windows runner as well as `ubuntu-latest`, and
      `dotnet format --verify-no-changes` gates every PR

## Verified facts, and when

Re-verify before relying on these; the server is a fast-moving prerelease train. All confirmed
against nuget.org and the shipped binary on 2026-09-02/03, and the daemon entries on 2026-09-04
against 5.12.0-1.26426.8 / win-x64.

- No stable release exists. Every RID package lists a bare `5.11.0`, but it is **unlisted**, and
  the non-RID tool ID never had one. `--prerelease` is load-bearing. Never pick a version by
  scraping the flat-container index — it includes unlisted versions.
- The non-RID tool ID resolves per-platform through `RuntimeIdentifierPackages`, so CI needs no
  RID selection. Payload is ~300 MB.
- Server flags come from the bundled `Microsoft.CodeAnalysis.LanguageServer.exe --help`; the thin
  client itself has no `--help`. `--autoLoadProjects` takes an optional integer. `--daemon-mode`
  is the thin client's flag; the server's internal equivalent is `--daemon`.
- The server does **not** advertise `positionEncoding`, which per LSP 3.17 means utf-16.
- `workspace/projectInitializationComplete` is a real server→client notification and is the
  readiness signal.
- Roslyn's `containerName` is localised display text, not a namespace path.
- Pull diagnostics: `textDocument/diagnostic` answers unadvertised, returning
  `kind: "full"` reports. `workspace/diagnostic` also answers but returns zero reports —
  `workspaceDiagnostics: false` in its dynamic registration is honest. Roslyn leaves
  `source` null on compiler diagnostics and sends `code` as a string (`"CS0029"`).
- Against a **cold** server `workspace/symbol` answers nothing at all — not a partial list —
  until `workspace/projectInitializationComplete` fires, and then answers completely: measured
  0 hits for 8.1 s, then the full 3 on the same poll the notification arrived, with the
  inferred sentinel resolving 0.33 s later. So the partial-answer window is not a property of
  cold load. It belongs to a client attaching to a **daemon loading a root it has not loaded
  before**, where the notification already fired for the previous root and never fires again —
  which is exactly the case the notification cannot be used to close. Verified 2026-09-05.
- A `.cs` file that **no project compiles** is answered for asymmetrically: `workspace/symbol`
  does not index it (`cslq sym` on a type declared only there exits 1 with `no results`) and
  `textDocument/diagnostic` reports **nothing** for it, but `textDocument/documentSymbol` still
  answers off the syntax tree, so `cslq outline` works. The same file **linked into** a project
  from outside its directory (`<Compile Include="../Ambient/Stray.cs" />`) is fully indexed and
  reports its errors. This is why readiness inference is scoped to project directories while
  `diag`'s file walk is not: scoping the walk would suppress no noise and would silently drop a
  linked file's real diagnostics. Verified 2026-09-05 against 5.12.0-1.26426.8 on a scratch copy
  of the fixture.
- `fixture/` **does** have a solution — `Fixture.slnx` — so it is not a counterexample to
  `skill/SKILL.md`'s entry on solutionless roots, which since PR #12 reads that such a root
  errors in about a second rather than hanging. `.slnx` counts as the solution there exactly as
  `.sln` does. Verified 2026-09-05.
- The server exposes **no project list to ask for**. `workspace/_roslyn_restorableProjects` is
  a server-to-client request and carries none, so `cslq` enumerates `.csproj` files instead and
  accepts that this is an approximation. Verified 2026-09-05.
- **`textDocument/diagnostic` does not answer from the misc-files state and then correct
  itself -- it blocks until the document is bound.** A cross-project error opened as the first
  and only document in a never-used daemon returns the correct `CS0029` on the *first* pull;
  that pull costs ~4.2 s and a second, redundant one ~0.7 s. Across six whole-fixture runs,
  three against a fresh daemon and three warm, 22 pulls each, the second pull never once
  differed from the first. So `DiagnosticsAsync`'s settle loop was buying a mandatory 250 ms
  delay plus a duplicate round trip per file -- about 45% of warm per-file cost -- and nothing
  else. It is gone. Verified 2026-09-06 against 5.12.0-1.26426.8.
- **`diag` cost splits into a fixed term and a per-file term, and the fixed term is not small.**
  Fixture (11 files, 3 projects): ~2.5 s fixed per invocation -- process start, sentinel
  inference, readiness -- then ~560 ms per file warm and ~1080 ms cold. The first `diag` of a
  document in a fresh daemon costs ~4 s more than any later one. Any per-file number taken from
  an undecomposed wall clock is wrong; `cslq` never sends `didClose`, so daemon document state
  outlives the client that opened it. Verified 2026-09-06.
- **Sentinel inference matched English prose, and one bad line could sink a project.**
  Candidates were the *first* regex match per file, and the regex matches
  `class|struct|record|interface|enum` followed by a word -- so "identifying the class and
  assembly context" in a doc comment yielded the candidate `and` and masked the real type below
  it. `cslq ready` on OrchardCore v3.0.1 failed after 900 s on fifteen projects, six with
  `'and' / 'and' / 'and'`. Taking every match per file instead cut that to two, both of which
  are `dotnet new` template content excluded from the solution -- Roslyn never loads them, so
  the `.csproj` scan's over-inclusion is fatal there, not merely wasteful. Scoped below the
  templates, `cslq ready` on `src/OrchardCore` resolves all 101 projects in ~83 s.
  Verified 2026-09-06 against 5.12.0-1.26426.8. **Superseded as the workaround:**
  `ProjectDirectories` now reads the root's solution when there is exactly one, and
  `OrchardCore.slnx` excludes precisely those template projects, so `--root` no longer has to
  be pointed below them.
- **Project discovery reads the root's solution; the `.csproj` scan is now the fallback.**
  Exactly one `.sln`/`.slnx` at the top of `--root` supplies the project list; more than one
  falls back to the recursive scan, and **none is now an error** rather than a third route into
  the scan — `--autoLoadProjects` does not discover a bare project, so scanning one up only
  bought a full timeout. A solution one directory down does not count, which is
  what keeps `fixture/Fixture.slnx` from narrowing a root above it. `.slnf` is not read, a listed
  project that is not on disk is dropped, and a root whose solution lists no C# project is named
  in the error rather than reported as "no .csproj under <root>". This also made the repository
  self-hosting: `Cslq.slnx` lists `src/Cslq` and `tests/Cslq.Tests` and excludes `fixture/`, so
  `cslq ready --root .` resolves in ~6 s where it previously waited out the whole timeout on the
  three fixture projects Roslyn had not loaded. Cases `self-hosted-ready` and `self-hosted-refs`.
  Verified 2026-09-06 against 5.12.0-1.26426.8.
- **`workspace/symbol` ranks its answer by relevance, globally.** Measured on a scratch copy of
  `fixture` carrying `Core/AbcZed.cs`, `Core/ZedHelper.cs` and `App/Zed.cs`, chosen so that
  relevance order, alphabetical order, document order and per-project-then-relevance order are
  four distinct strings. The query `Zed` answered `Zed` (App), `ZedHelper` (Core), `AbcZed`
  (Core) -- exact, prefix, substring, with the exact match's project coming second in document
  order, so neither project order nor declaration order explains it. Identical across two runs.
  This is what makes `sym`'s truncate-before-sort meaningful. Verified 2026-09-05 against
  5.12.0-1.26426.8.
- `textDocument/implementation` answers `Location[]`, with zero-width ranges at the
  implementer's name. Fired at an interface member it returns the implementing members, at an
  interface type the implementing types, and at a base-list mention of the interface the same
  as at the type. Fired at a member with **no** implementations it returns that member's own
  declaration rather than nothing; only a position resolving to no symbol answers `[]`.
  Verified 2026-09-05 against 5.12.0-1.26426.8.
- `textDocument/definition` answers `Location[]` when the client omits
  `definition.linkSupport`, and returns the declaration itself when fired at a declaration
  rather than falling through to implementations. `textDocument/documentSymbol` returns the
  nested `DocumentSymbol[]` form when `hierarchicalDocumentSymbolSupport` is declared, with
  the namespace as the root node and `detail` duplicating `name`.
- Source-generated documents use the `roslyn-source-generated:` scheme. `workspace/symbol`,
  `textDocument/definition`, `textDocument/references` and `textDocument/documentSymbol` all
  cover them. `workspace/textDocumentContent` (LSP 3.18) returns the text and needs no declared
  client capability; `sourceGeneratedDocument/_roslyn_getText` is gone. The URI's authority guid
  and `documentId` are regenerated on every workspace load.
- `DOTNET_CLI_UI_LANGUAGE=en` pins Roslyn's own display strings, but StreamJsonRpc's error text
  still came back localised (Portuguese on this machine). Do not assert on transport error text.

- **A generated document's consuming project can be asked for, and only one request answers.**
  `textDocument/_vs_getProjectContexts` is a VS protocol extension the server implements
  without advertising it and without requiring a matching client capability. For a
  `roslyn-source-generated:` URI it answers one context per TFM, whose `_vs_id` is
  `<projectId guid>|<absolute .csproj> ($<tfm>)` — the guid matching the URI's own authority,
  and regenerated with it on every load. The path half is the only stable identification of
  the consuming project available anywhere: `assemblyName`, `typeName` and `assemblyPath` in
  the URI all name the *generator*, and `containerName` is localised display text absent from
  reference locations entirely. Verified 2026-09-06 against 5.12.0-1.26426.8.

### The daemon

Two sources beyond experiment, both shipped in the package and both worth re-reading before
trusting any of this: `roslyn-language-server.xml` next to the thin client documents the whole
daemon design (`DaemonBootstrap`, `DaemonPipeName`, `DaemonServerMutex`, `ChildServerHost`,
`ExitCodes`) including *why* the bootstrap exists, and the dll's UTF-16 string table carries the
flag and env-var names that appear in no `--help`.

- **The launch chain is four processes deep**, and the middle one is deliberate:

  ```
  cslq
   └─ dotnet tool run
       └─ roslyn-language-server.exe --daemon-mode --stdio --autoLoadProjects --logLevel L
           └─ roslyn-language-server.exe --daemon-launch   (bootstrap, exits immediately)
               └─ Microsoft.CodeAnalysis.LanguageServer.exe --daemon --pipe <name> ...
  ```

  The thin client relaunches *itself* as a short-lived bootstrap purely so the daemon is
  orphaned rather than a descendant — the XML doc says process-tree teardowns walk parent/child
  links, which "neither Windows job-object breakaway nor Unix `setsid` change". The bootstrap
  waits on the server mutex, then exits. The thin client then relays our stdio to the daemon's
  named pipe.
- **`LspClient` needs no protocol change**: same `initialize`, same stdio
  `HeaderDelimitedMessageHandler`. Daemon mode is an argv change and nothing else.
- **One daemon is shared across workspaces, not one per client.** The pipe name is a hash of
  user identity plus the server exe's versioned path — *not* the workspace. `--daemon` on the
  server is documented as "run as a multi-client daemon".
- **The daemon takes its configuration from whoever launched it.** It inherits the *first*
  client's `--autoLoadProjects` and `--logLevel`, both visible in its cmdline; later clients only
  connect. So `cslq --log-level Debug` is silently a no-op against an already-running daemon.
  Inferred from the observed cmdline, not separately tested.
- **`ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME=<literal>`** yields a fully isolated daemon under
  that exact name; a client without it starts a separate daemon alongside. This is the per-run
  scoping a probe suite wants. Keepalive defaults to 900 s after the last client disconnects
  (`-1` for indefinite) and `ROSLYN_LANGUAGE_SERVER_DAEMON_KEEPALIVE` propagates by plain env
  inheritance down the whole chain.
- **There is a silent non-daemon fallback.** The dll carries "Falling back to non-daemon mode"
  and "Running language server in non-daemon fallback mode" (a daemon startup-mutex timeout, for
  one). A fallback run still answers correctly, just cold — nothing but latency or that stderr
  line distinguishes it, which is why `cslq` watches for it. Forced deliberately by
  `non-daemon-fallback-reported`: the client mutex is named `Global\<pipeName>.client`, created
  with .NET 10's `NamedWaitHandleOptions { CurrentUserOnly = true }` — a same-named mutex
  without that option makes the thin client throw `WaitHandleCannotBeOpenedException` rather
  than contend, which is how the name was pinned down.
- **A root with only a `.csproj` and no solution never becomes ready**, daemon or not.
  `projectInitializationComplete` never fires and `workspace/symbol` stays empty for the full
  timeout; `--autoLoadProjects` does not discover a bare project. Adding a `.slnx` fixes it
  immediately. **Superseded as a symptom:** since Milestone 5 item 2 that root is rejected
  before the server starts, so the timeout is no longer reachable — the underlying fact about
  `--autoLoadProjects` is what the rejection rests on. `SKILL.md` carries the error, not the
  hang.
- `premature-query-fails-loudly` still exits 1 against a warm daemon, because `--timeout 0` means
  `WaitReadyAsync` never issues a query at all. That case pins the timeout guard, not cold load.
