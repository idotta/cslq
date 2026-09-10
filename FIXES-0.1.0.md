# Fixing what 0.1.0 testing found

Plan and control for working through `TESTING-0.1.0.md`. That file is the **frozen reference**:
it records what the released tool did and is never edited. This file is the live one — what is
being fixed, in which order, how each fix is proven, and what was decided along the way. Item
ids (`T-nn`) are the reference's; look there for the evidence and the original repro lines.

Last updated: 2026-09-10. Batch 1 is PR #21 (merged). Batch 2 is PR #22 (merged). Batch 3 is
PR #23 (merged). Batch 4 is PR #24 (merged, 69f4b6a). Batch 5 is PR #25 (merged, e283eeb).

## Rules

- `TESTING-0.1.0.md` is read-only. A finding that turns out to be wrong is recorded here as
  wrong, with the measurement; the reference keeps saying what it said.
- Nothing is marked done until `./probes/run.sh` is green and the leg named under **Proof**
  exists and passes. A unit test in `tests/Cslq.Tests` where a temp tree and a string can prove
  it; a `cases.jsonl` row or scripted leg only where a live server is needed.
- One batch is one PR. Batches are independent; the order below is the recommended one, not a
  dependency chain, except where **Blocked by** says so.
- A hypothesis is labelled as one. **Status** distinguishes `confirmed in source`, `reproduced`
  (run again after the report, on this machine), `measured by testers` and `hypothesis`.
- Every fix that changes a documented claim also fixes the claim (README, DESIGN.md, CLAUDE.md,
  `skills/csharp-semantic-queries/SKILL.md`). Doc-only items are collected in batch 7.

## Status board

| Batch | Items | State |
|---|---|---|
| 1. Exit 127 class and non-C# targets | T-01 T-02 T-03 T-04 T-05 T-47 T-48 T-51 | merged (PR #21) |
| 2. Rendering correctness | T-17 T-23 T-30 T-32 | merged (PR #22) |
| 3. Daemon under captured stdout | T-55 | merged (PR #23) |
| 4. Symbol targeting | T-06 T-07 T-11 T-16 | merged (PR #24) |
| 5. Readiness on real repositories | T-82 T-84 T-39 T-40 T-36 T-41 | merged (PR #25) |
| 6. Per-attach reload (investigation first) | T-83 T-56 T-61 T-87 T-88 T-85 | investigated; cause is upstream, no `src/` fix, docs corrected |
| 7. Multi-targeting | T-26 T-27 T-28 T-29 | not started |
| 8. Output, CLI and docs | T-65–T-81, section (k) wording | not started |

Deferred, with the reason, at the end.

---

## Batch 1 — the exit 127 class and non-C# targets

Every entry here is confirmed in source: `Program.Main` catches `CslqException` and
`OperationCanceledException` and nothing else, so any other exception is a stack trace and exit
127.

- [x] **T-04 / T-05 — wrap every post-`initialize` failure.**
      Status: confirmed in source (`Program.Main`; `LspClient.StartCoreAsync` wraps only
      `initialize`).
      Fix: `RemoteInvocationException` and `ConnectionLostException` become `CslqException`
      with a message naming the request and, for the lost connection, the pipe and "rerun to
      relaunch". Decide where: one `catch` in `Main` is smallest; a wrapper around `_rpc` calls
      in `LspClient` can name the request. Prefer the wrapper — the message is the point.
      Proof: `cases.jsonl` row `outline` on a `.vb` file under a fixture project is not possible
      (no VB project in the fixture); a `.vb` file dropped in `fixture/App/` is not compiled and
      answers differently. So: unit test for the wrapping shape, and the T-47 guard below makes
      the `.vb` path unreachable anyway. The daemon-killed-mid-request case stays manual.
- [x] **T-01 — line past EOF.**
      Status: confirmed in source (`LspClient.OpenAsync` reads the text; nothing range-checks).
      Fix: after the read, `line > lineCount` throws `CslqException`
      `line out of range: 99 (App/Program.cs has 13 lines)`. Column stays server-side
      (`no results` today, acceptable).
      Proof: `cases.jsonl` row `refs App/Program.cs:99:1 --root fixture` expects that message.
- [x] **T-02 — `--root ""`.**
      Status: confirmed in source (`Options.Parse`: `Path.GetFullPath(Next(...))`).
      Fix: empty value → `option '--root' needs a value`.
      Proof: `OptionsTests`.
- [x] **T-03 — `\\?\` root crashes `XDocument.Load(string)`.**
      Status: confirmed in source (`Program.SolutionXml`; catches `XmlException` only).
      Fix: load from a `FileStream`, not a path string. Then check whether the rest of the
      pipeline survives a `\\?\` root (`PathUri.FromPath`, `Under`); if not, normalise the
      prefix away in `Options.Parse`.
      Proof: unit test loading a `.slnx` through a `\\?\`-prefixed path (Windows-only fact;
      skip on other OSes).
- [x] **T-47 / T-48 / T-51 — non-C# file targets.**
      Status: confirmed in source (`ProjectAsync` checks `File.Exists` only; `OpenAsync` sends
      `languageId: csharp` unconditionally; `diag`/`outline`/position parsing never look at
      the extension).
      Fix: one guard before any `didOpen`, on every file-taking command: the file must be
      `.cs`, `.razor` or `.cshtml` (Razor works in file mode — T-50, section (l)) and must lie
      under `--root`. Message: `App/App.csproj is not a C# document` /
      `../x.cs is outside --root`. A directory given to `project` is `not a file`, not
      `no such file`.
      Proof: `cases.jsonl` rows `diag App/App.csproj --root fixture` and
      `project App/App.csproj --root fixture`; unit test for the predicate.

## Batch 2 — rendering correctness

- [x] **T-17 — `refs` lists the declaration twice and counts it.**
      Status: confirmed in source (`RefsAsync` → `Output.WriteLocationsAsync` with raw
      `locations`; `Program.Distinct` runs only on `workspace/symbol` results).
      Fix: `DistinctBy((uri, range))` on every `Location[]` before rendering — in
      `WriteLocationsAsync` so `def` and `impl` get it too (T-30's per-TFM twins fold here as
      well, for source rows).
      Proof: `cases.jsonl` row `refs Square --root fixture --context 0 --json` expects
      `"count":1`; `Output` unit test with two identical locations.
- [x] **T-23 — `sym --max` truncates in arrival order.**
      Status: confirmed in source (`Output.WriteSymbolsAsync`: `symbols.Take(max)` before the
      sort). ROADMAP.md's "applies `--max` in the server's relevance order" rests on an
      ordering the testers measured absent on three real corpora.
      Fix: rank client-side before the cut — exact name, then prefix, then substring
      (case-insensitive), source before generated, `(path, line)` as the stable tiebreak — then
      the existing display sort.
      Proof: `Output.WriteSymbols` unit test with a substring hit ahead of an exact one in
      arrival order and `max` = 1. Update ROADMAP.md's sentence and DESIGN.md's premise.
- [x] **T-30 — generated hits repeated once per TFM push source rows past the cap.**
      Status: measured by testers (CommunityToolkit). Folding in T-17 handles the identical
      rows; ranking source before generated is the second half.
      Fix: in `WriteLocationsAsync`, order source rows before generated/metadata rows before
      `Take(max)`.
      Proof: `Output` unit test; no fixture can produce 4-TFM generated twins.
- [x] **T-32 — generated labels collide (two generators, one hintName).**
      Status: measured by testers; the URI's `typeName` is a stable field (CLAUDE.md).
      Fix: append the generator `typeName` to the label only when two labels would otherwise
      collide, or always — decide on readability; always is simpler and honest.
      Proof: `PathUriTests`; the two-generators shape has no fixture (would need a second
      generator in `fixture/Gen`; only add it if the fix is uncertain).

## Batch 3 — the daemon under captured stdout

- [x] **T-55 — the daemon-launching call blocks for KEEPALIVE seconds and the daemon is dead
      when it returns.**
      Status: **reproduced 2026-09-08** in PowerShell 7 with a scoped pipe and
      `KEEPALIVE=10`: cold `ready` 17.6 s, the very next call 14.1 s, both ≈ keepalive + a
      cold load. Mechanism (hypothesis, consistent with every observation): .NET
      `Process.Start` on Windows passes `bInheritHandles=TRUE`, so cslq's own stdout handle —
      the pipe PowerShell reads — leaks into the thin client and from it into the daemon, and
      PowerShell waits for EOF on that pipe until the daemon exits. Git Bash `> file` hands
      cslq a file handle, and bash waits for exit, not EOF, which is why the whole report could
      be measured there. Unix is not affected: the child's fds 0–2 are `dup2`'d to the pipes
      and .NET opens its own fds `O_CLOEXEC`.
      Fix: before `StartProcess`, on Windows, clear `HANDLE_FLAG_INHERIT` on the process's
      std input, output and error handles (`GetStdHandle` + `SetHandleInformation`, via
      `[LibraryImport]` — CLAUDE.md's interop rule). Confirm first that this is the leak: run
      the repro with the flags cleared and watch the second call attach in ~2.5 s.
      Proof: no bash leg can pin it. A `probes/stdout-capture.cs` file-based app (same
      pattern as `hold-mutex.cs`) that starts `cslq ready` with `RedirectStandardOutput` and a
      10 s keepalive on its own pipe, asserts it returns in under 10 s, and asserts the daemon
      is alive afterwards. `run.sh` runs it on Windows only (`probe.yml`'s `windows-latest`
      leg); the elapsed check is the assertion, like `no-solution-root-fails-fast`.
      Docs: README **Latency** — "only that one invocation is exposed" is false under a
      capturing harness; rewrite once fixed.
      Done: the hypothesis held exactly. `LspClient.DisableStdioInheritance`, called from
      `StartProcess`, clears the flag once per process via `Native.GetStdHandle` /
      `SetHandleInformation`.

## Batch 4 — targeting a symbol by name

`Program.Matches` is confirmed in source to test only `segments[^2]`, as an identifier token of
Roslyn's localised `containerName`, and to ignore every earlier segment. Two decisions come
first; record them in DESIGN.md.

- [x] **Decision A — bare name vs its own constructors (T-06).**
      Decided: when the distinct candidates are one type-kind symbol plus same-named
      `Method`-kind symbols whose declaration *chain* is that type's chain plus one, the bare
      name selects the type. `Type.Type` keeps selecting the constructors. The kind is not
      re-rendered: `sym` would need a `documentSymbol` request per hit to know a constructor
      from a method, and the ambiguity listing has no kind column until T-16.
- [x] **Decision B — namespace segments (T-07, T-11).**
      `containerName` cannot verify a namespace. Neither can `hover`, which was the proposal:
      measured on the fixture 2026-09-09, its first line is fully qualified for *types* only
      (`class Fixture.Core.Greeter`) while a member prints the minimal form
      (`string Greeter.Greet(string name)`), and a member is the repro. Decided: read the
      declaration chain off `textDocument/documentSymbol` for the candidate's document, whose
      namespace node is already dotted, and require the target's segments to be a contiguous
      suffix of it. One request per distinct candidate document, only for a dotted target or an
      ambiguous bare name.
- [x] **T-06 / T-07 / T-11 — implement the two decisions in `Targets` /
      `Program.SelectAsync`.**
      Status: confirmed in source.
      Proof: `cases.jsonl` rows: `refs Wrong.Namespace.Greeter.Greet --root fixture` exits 1;
      `def Fixture.Core.Greeter` still works; a type with an explicit constructor resolves by
      bare name — add one to the fixture (`ImplicitCtor` has none; give `Square` a sibling with
      a constructor, or use `fixture2`). Unit tests for the candidate-set predicate.
- [x] **T-16 — the ambiguity listing: `path:line:col`, sorted, capped, exact first.**
      Status: measured by testers (three orders for one target).
      Done: `Output.SymbolListingAsync` renders the ambiguity listing and the `candidates:` dump
      through the same `ShownAsync`/`Rows` path `sym` uses (relevance, rank, label, line, column;
      `--max` cap and footer). The ambiguity listing prints `constructor` off round A's chains;
      `candidates:` prints the server's kind; `outline`'s per-document listing is ordered by
      `Output.Rank` then label. Proof: three `OutputTests`, `candidate-listing-honours-max`, two
      rows extended to pin `:line:col` and `constructor  Widget`.

## Batch 5 — readiness on real repositories

- [x] **T-82 — OrchardCore never becomes ready: the `ProjectTemplates` wrapper.**
      Status: measured by testers, 900 s; cause confirmed against the solution file and
      `Program.Candidates` (nested exclusion uses *discovered* projects only).
      Fix, two halves, both knowable before the server starts: (1) in `Candidates`, treat any
      `.csproj` found on disk under the project directory as a nesting boundary, listed in the
      solution or not; (2) skip a project whose `.csproj` sets `EnableDefaultItems=false` and
      declares no `<Compile Include>`, naming it in the "not probed" list.
      Proof: `SentinelScopingTests`-style temp tree for (1) and a csproj-text test for (2); one
      live `cslq ready --root C:/dev/corpus/OrchardCore --timeout 600` on a fresh pipe, ~130 s
      cold, recorded here with the elapsed time.
      Docs: CLAUDE.md and ROADMAP.md say the solution "excludes precisely those template
      projects" — it excludes the five content projects and lists their wrapper. Fix both.
- [x] **T-84 — `--sentinel` replaces the inferred set and answers incompletely at exit 0.**
      Status: measured by testers (`impl StartupBase` 321 vs 331).
      Fix: `--sentinel` is additive — the inferred per-project set still has to resolve, and
      the explicit one is added to it. Where inference finds nothing (the T-38 shape),
      `--sentinel` alone is what runs, as today.
      Proof: `premature-query-fails-loudly` and the other `--sentinel` rows keep passing;
      `ready --json` `projects` reports the full count.
      Blocked by: T-82, or every OrchardCore run stays at 900 s.
- [x] **T-39 — a project whose candidates are all exhausted holds readiness to the deadline
      although `projectInitializationComplete` fired.**
      Status: measured by testers (`#if false`).
      Decide: bounding the wait after the notification risks reopening the incomplete-answer
      window on a daemon attach, where the notification never fires. Only bound it when it
      *did* fire in this process (a cold load) — that is a true signal there. Otherwise leave
      it and let T-82's skip rules cover the known shapes.
- [x] **T-40 — `.projitems` / linked-only projects are never waited on; `ready --json` counts
      them.**
      Status: measured by testers (14 of 26 on CommunityToolkit).
      Fix: read `<Compile Include>` and `<Import Project="*.projitems">` for a candidate;
      report `skipped` in `ready --json` and the not-probed list at `--log-level Information`
      on success.
      Proof: unit test on csproj text; a CommunityToolkit `ready --json` recorded here.
- [x] **T-36 — two solution files at the root burn the whole timeout.**
      Status: measured by testers, 3 of 3.
      Fix: fail before the server starts, naming both files. DESIGN.md's "fall back to the
      scan" becomes "error".
      Proof: `cases.jsonl` row against a temp root is not possible from `sed`-parsed rows; a
      scripted leg that copies `fixture/` and adds a second `.sln` (like the staleness legs,
      with the trap).
- [x] **T-41 — a root containing `%XX` never becomes ready.**
      Status: measured by testers and re-run by the lead; **cause is a hypothesis**
      (`PathUri` round-trip decoding once too often, so `LspClient.Under` never matches).
      Verify first: copy `fixture/` to `paths/pct%20x`, run `ready --no-daemon --sentinel Greeter
      --timeout 20`, and log the hit URI against the project directory. Then fix in `PathUri`.
      Proof: `PathUriTests` with a `%20` path.

## Batch 6 — the per-attach reload (investigation first)

- [x] **T-83 / T-56 / T-61 / T-87 / T-88 — every client attach re-runs the workspace load.**
      Status: measured by testers (CPU-seconds on one server pid, OrchardCore; the same shape
      at 26 projects). **Cause is in the server and is not known**; the report only infers
      what `initialize` triggers. Nothing in cslq is changed until the mechanism is read.
      Investigation, in order:
      1. CommunityToolkit (`C:/dev/corpus`), not OrchardCore — 40–118 s per attach is enough
         to see and a fifth of the wait. Fresh pipe, daemon started at
         `--log-level Information` (the flag cannot change a running daemon — T-58), then a
         second client; read what the server logs on the second `initialize`.
      2. Test the `rootUri` / `workspaceFolders` hypothesis: does the reload happen when the
         second client sends the same root? A different root? No `workspaceFolders`?
      3. Only then decide: a cslq-side change to `InitializeAsync`, an upstream issue against
         `roslyn-language-server`, or documenting the scale at which `--no-daemon` is the
         right default.
      Record the measurements here. Until fixed, the README's warm-attach claim gets a scale
      qualifier.
      **Outcome (rounds 1 and 2 below):** the mechanism is read and named — the daemon shares
      a process, not a loaded workspace, and each client's own `initialized` re-runs the whole
      solution load under `--autoLoadProjects`. Option 1 of step 3 is measured dead: the only
      lever cslq holds at `InitializeAsync` is omitting `workspaceFolders`, which yields an
      empty workspace. Options 2 and 3 taken — the issue is drafted below (not filed) and the
      scale qualifier is in README, SKILL.md and CLAUDE.md. No `src/` behaviour change.
- [x] **T-85 — `diag` reports false CS0234/CS0246/CS0103 while references are still binding.**
      Status: measured by testers on OrchardCore, with `--sentinel`. Rides on T-83 (the pull
      runs during the per-attach reload) and T-82 (which forced `--sentinel`).
      Do not fix independently. Re-measure after batch 5 and the T-83 outcome; if it persists,
      withhold those three codes while any referenced project is unresolved, or gate `diag` on
      the full readiness set.
      **Outcome: not reproduced, on both corpora.** Five `diag` runs on CommunityToolkit and
      nine on OrchardCore — the reported corpus, restored and not built, using the reference's
      own repro lines including the two files it named individually — report zero
      CS0234/CS0246/CS0103 and zero `error CS` of any code (2026-09-10, below). Batch 5 made
      `--sentinel` additive, so readiness waits for every project before `diag` opens
      anything. Closed as fixed by batch 5.
- [x] **T-61 — warm floor scales with project count (the poll itself).**
      Status: measured; two mechanisms, and the second (T-83) dominates. Revisit only after
      T-83: cache readiness per (daemon, root) for a short window, or log one line per
      sentinel at `--log-level Information`.
      **Outcome: closed without a fix.** The wall clock is the reload, so a cached readiness
      would return during the next attach's reload while `workspace/symbol` is still partial
      — the incomplete-answer-at-exit-0 bug batch 5 closed. Reasoning under step 4 below.

### Measurements 2026-09-09 — Batch 6 round 1 (investigation only, no `src/` change)

Corpus `C:/dev/corpus/CommunityToolkit` (`dotnet.slnx`, restored, not built), command
`cslq ready --root <corpus> --timeout 600`, one fresh daemon pipe per experiment, VS Code's own
`Microsoft.CodeAnalysis.LanguageServer` (pid 35128) excluded from every process reading.

**How the server log was obtained.** Three routes were tried; only the third says anything.
1. `--extensionLogDirectory <dir>` appended to `ServerArgs.Daemon` through a temporary env
   passthrough. The flag *is* forwarded — the daemon's command line reads
   `... --daemon --pipe cslq-b6-a --autoLoadProjects --logLevel Information --extensionLogDirectory <dir>`
   — and the directory stayed empty. No file is written.
2. The daemon started by hand rather than by cslq —
   `Microsoft.CodeAnalysis.LanguageServer.exe --daemon --pipe cslq-b6-b --daemonKeepAlive -1 --autoLoadProjects --logLevel Information`
   with stdout+stderr redirected to a file, cslq pointed at it with
   `ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME` — which works and costs nothing, but the daemon's
   own log is 16 lines for three attaches: the server banner, `Language server initialized`, and
   `Daemon accepted a new client connection.` once per client. Nothing about the workspace. At
   `--logLevel Trace` two attaches added 162 lines, all `MEF Assembly Loader` and none of them
   about loading projects.
3. **The route that worked:** the workspace-load log is sent to the *client* as
   `window/logMessage`, and `LspClient.Endpoints.OnLogMessage` discards it. A temporary `Dump`
   in `Endpoints` appending every `window/logMessage` to a file (reverted before this commit)
   makes it readable.

**Cold is warm.** The three attaches on the hand-started daemon of route 2 (pid 38312), in
order: 32.7 s / +76.0 CPU-s (cold, first client), 40.7 s / +114.2 (warm), 27.2 s / +70.0 (warm).
The warm attaches cost the cold one, which is T-83 reproduced at 26 projects.

**Step 1 — the reload, and what names it.** Daemon at `--logLevel Information`, pid 15980,
CPU-seconds read with `Get-Process -Id <pid>` before and after each call:

| attach | wall | server CPU delta | `Completed (re)load of all projects in` |
|---|---|---|---|
| warm | 32.7 s | +63.8 CPU-s | 00:00:29.84 |
| warm | 28.0 s | +74.8 CPU-s | 00:00:24.66 |

The reload accounts for essentially the whole call. Every attach logs the same sequence under
the `[initialized]` handler, and it names the mechanism:

```
[initialized] [AutoLoadProjectsInitializer] Searching for VS Code settings to load in workspace folder: file:///C:/dev/corpus/CommunityToolkit
[initialized] [AutoLoadProjectsInitializer] Found single solution file C:\dev\corpus\CommunityToolkit\dotnet.slnx to auto load
[initialized] [LanguageServerProjectSystem] Loading C:\dev\corpus\CommunityToolkit\dotnet.slnx...
[initialized] [BuildHostProcessManager] .NET BuildHost started from ...\BuildHost-netcore\...
[initialized] [BuildHost PID 38820] Message on stderr: info: Registered MSBuild 10.0.301 instance at C:\Program Files\dotnet\sdk.0.301
[initialized] [BuildHost PID 38820] Message on stderr: info: Loading <each .csproj>          # all of them
[initialized] [LanguageServerProjectSystem] Successfully completed load of <each .csproj>     # all of them
[initialized] [LanguageServerProjectSystem] Completed (re)load of all projects in 00:00:24.6632995
```

So yes: the second `initialize`/`initialized` re-runs the full design-time build. A **new
`BuildHost` process per attach** (pid 27392 on one, 38820 on the next), MSBuild re-registered,
every `.csproj` re-loaded. It is not an index refresh.

Two corrections to what the repo currently assumes:
- **`workspace/projectInitializationComplete` fires once per attach**, warm daemon included —
  it is the end of *that client's* reload. `LspClient.WaitReadyAsync`'s comment and CLAUDE.md
  say it "never fires for a client that attaches to a loaded daemon". Measured: it fired on
  every attach that sent workspace folders, and only on those.
- `workspace/symbol` is answered throughout the reload (`Starting request handler` /
  `Request handler completed successfully.`, hundreds of them) — it returns empty rather than
  waiting, which is the readiness poll spinning against a workspace that is still loading.

**Step 2 — the `rootUri` / `workspaceFolders` hypothesis.** One daemon (pid 33744), four
attaches in order, then a fifth with a temporary `InitializeAsync` edit sending
`rootUri: null` and `workspaceFolders: null` (reverted before this commit):

| # | what the client sent | wall | CPU delta | reload |
|---|---|---|---|---|
| 1 | CommunityToolkit (cold daemon) | 32.2 s | +71.0 | 00:00:22.24 |
| 2 | CommunityToolkit again (same root) | 33.3 s | +89.5 | 00:00:24.31 |
| 3 | `C:/dev/cs-lspls` (different root, 2 projects) | 2.6 s | +2.7 | 00:00:01.47 |
| 4 | CommunityToolkit again | 24.5 s | +60.8 | 00:00:19.37 |
| 5 | CommunityToolkit, **no `rootUri`, no `workspaceFolders`** | 600.8 s, exit 1 | +25.3 | none — no `AutoLoadProjectsInitializer` search, no `Loading`, no `projectInitializationComplete` |
| 6 | CommunityToolkit again (folders restored) | 26.9 s | +61.4 | 00:00:25.65 |

(a) Same root reloads. (b) A different root reloads *that* root — the cost tracks the root's own
project count, not the daemon's history, so nothing is being reused across roots either.
(c) Sending no workspace folders does suppress the reload completely — and leaves the client
with an **empty workspace**: `workspace/symbol` returned no symbols for the full 600 s timeout
and `ready` exited 1, on a daemon that had loaded that exact solution 30 seconds earlier. The
loaded solution is not visible to a client that did not ask for it.

**Conclusion.** The reload is triggered by the attaching client's own `initialized` under
`--autoLoadProjects`, scoped to the `workspaceFolders` that client sent, and its cost is the
entire warm floor. The only lever cslq holds is *not sending* the folders, and that is measured
to produce an empty workspace rather than a shared one — so there is no cslq-side fix at the
`InitializeAsync` layer. The daemon shares a process, not a loaded workspace.

### Measurements 2026-09-10 — Batch 6 round 2

**Step 1 — the notification, re-verified on the fixture.** `--root fixture`, fresh
`ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME`, three sequential attaches, temporary
`window/logMessage` dump (route 3 above; reverted). It reproduces locally in seconds — the
per-attach reload is not a large-repository effect:

| attach | wall | `Completed (re)load of all projects in` | `projectInitializationComplete` |
|---|---|---|---|
| 1 (cold) | 4.6 s | 00:00:01.71 | fired, 03:48:21.465 |
| 2 (warm) | 2.7 s | 00:00:01.56 | fired, 03:48:25.455 |
| 3 (warm) | 2.6 s | 00:00:01.67 | fired, 03:48:28.418 |

The actual per-attach lines, at `--log-level Information`:

```
03:48:23.771 [initialized] [AutoLoadProjectsInitializer] Found single solution file C:\dev\cs-lspls\fixture\Fixture.slnx to auto load
03:48:23.794 [initialized] [LanguageServerProjectSystem] Loading C:\dev\cs-lspls\fixture\Fixture.slnx...
03:48:25.182 [initialized] [LanguageServerProjectSystem] Successfully completed load of ...\fixture\Gen\Gen.csproj
03:48:25.399 [initialized] [LanguageServerProjectSystem] Successfully completed load of ...\fixture\App\App.csproj
03:48:25.426 [initialized] [LanguageServerProjectSystem] Successfully completed load of ...\fixture\Core\Core.csproj
03:48:25.454 [initialized] [LanguageServerProjectSystem] Completed (re)load of all projects in 00:00:01.5622899
03:48:25.455 NOTIFICATION workspace/projectInitializationComplete
```

Attaches 1 and 3 are identical in shape. At cslq's default `--log-level Warning` the
`LanguageServerProjectSystem` lines are absent but the notification still arrives once per
attach (3 of 3 attaches on a separate pipe). So round 1's finding holds on the smallest
workspace in the repository.

**The incomplete-answer window, measured.** A second temporary dump, logging each sentinel the
moment it first resolves, run against CommunityToolkit:

| attach | sentinels resolved before the notification | after it | last resolution after the notification |
|---|---|---|---|
| 1 | 5 of 12, over 8 s | 7 | +7.9 s |
| 2 | 8 of 12, over 21 s | 4 | +6.4 s |

This corrects the wire claim `WaitReadyAsync` carried (`answers nothing at all until
projectInitializationComplete and then jumps straight to complete`). `workspace/symbol` answers
**partially** throughout a load, and it is still incomplete for several seconds *after* the
notification. The window straddles it in both directions, which is exactly what
`PostLoadGrace` covers and why it is 20 s rather than 0.

**Step 2 — what changed.** Prose only; `WaitReadyAsync`'s logic is unchanged and
`exhausted-candidate-fails-after-load` stays green.

- `LspClient.WaitReadyAsync` doc comment: the poll-from-the-start rationale (the notification
  ends the load rather than preceding it, so blocking on it costs the load), the wire
  behaviour paragraph (partial, straddling), and the `PostLoadGrace` paragraph (the
  notification fires on every attach, so the bound applies to cold and warm alike; the tail
  after it is what the grace is for).
- CLAUDE.md: the daemon bullet is rewritten around "shares a process, not a loaded workspace",
  with the instrumentation route and the numbers; the readiness bullet no longer claims a cold
  load answers nothing until the notification.
- README: the daemon latency table gains a paragraph naming it as the fixture's three
  projects, with CommunityToolkit's 26 projects / 28-33 s per attach beside it; the design
  table's "never fires for a client attaching to a loaded daemon" is replaced.
- `skills/csharp-semantic-queries/SKILL.md`: the same scale qualifier, phrased for an agent
  budgeting calls.

**Why the logic was left alone.** The rule it rests on — never bound the wait when the
notification has not fired — is still right, and is now right for a better reason. The
notification cannot arrive early on a warm attach, because it terminates *this client's* own
reload; so `loaded` is never set against a workspace that is still loading, and the feared
failure mode (bounding to 20 s at second zero of a live load, then failing or answering short)
cannot occur. The `loaded is null` branch is not dead either: a client that sends no workspace
folders never sees the notification, and round 1's fifth row is what that costs.

**Step 3 — T-85 re-measured, on CommunityToolkit.** Corpus restored, not built — the same shape
T-85 blamed for the missing fallback metadata reference.
`tests/CommunityToolkit.Common.UnitTests/Test_Converters.cs` has a direct `ProjectReference` on
`src/CommunityToolkit.Common`. Warm daemon throughout:

| call | wall | result |
|---|---|---|
| `diag <file>` pull 1 | 31.1 s | `no diagnostics` |
| `diag <file>` pull 2 | 27.6 s | `no diagnostics` |
| `diag <file> --sentinel Guard` | 20.7 s | `no diagnostics` |
| `diag <dir>` (CommunityToolkit.Common.UnitTests) | 24.1 s | 139 lines, every one an `IDE****` suggestion |
| `diag <dir> --errors-only` (HighPerformance.UnitTests) | 33.5 s | `no diagnostics` |

Zero CS0234, CS0246 or CS0103 in any of them, including the `--sentinel` shape that produced
them on OrchardCore. That is the expected consequence of batch 5: `--sentinel` is now additive,
so readiness still waits for **every** project before `diag` opens anything, and the pull can no
longer run against half-bound references. T-85 was reported against the pre-batch-5 behaviour,
where one explicit sentinel replaced the whole readiness set.

**OrchardCore, the reported corpus, re-run 2026-09-10.** Restored, not built — `bin/Debug`
exists and holds zero `OrchardCore.*.dll`, so the fallback metadata reference T-85 named is
genuinely absent. Fresh daemon pipe, `S=OrchardCore.ContentManagement.ContentItem`, the
reference's own repro lines:

| call | wall | result |
|---|---|---|
| `ready --sentinel $S` (cold attach) | 85.3 s | `ready` |
| `diag .../DefaultContentManager.cs --sentinel $S` pull 1 | 42.6 s | `no diagnostics` |
| `diag .../DefaultContentManager.cs --sentinel $S` pull 2 | 57.6 s | `no diagnostics` |
| `diag .../DefaultContentManager.cs` (no `--sentinel`) | 40.2 s | `no diagnostics` |
| `diag src/OrchardCore/OrchardCore.ContentManagement --sentinel $S` | 52.5 s | `no diagnostics` |
| `diag src/OrchardCore.Modules/OrchardCore.Contents --errors-only --sentinel $S` | 90.7 s | `no diagnostics` |
| `diag src/OrchardCore.Modules/OrchardCore.Contents/Controllers --max 5 --sentinel $S` | 114.5 s | one `hint IDE0047`, correct |
| `diag .../ContentManagement/Cache/ContentDefinitionCacheContextProvider.cs` | 92.6 s | `no diagnostics` |
| `diag src/OrchardCore.Modules/OrchardCore.Contents/AdminMenu.cs` | 110.4 s | `no diagnostics` |

Zero `error CS` of any code across all nine, and zero CS0234/CS0246/CS0103. The last two rows
are the two files the reference named individually — `ContentDefinitionCacheContextProvider.cs`
(3 x CS0246 then) and `AdminMenu.cs` (6 errors then). The walk is doing real work: the
`Controllers --max 5` row returns a genuine `IDE0047` with its context lines, so a silent
no-op is not what "no diagnostics" means here. T-85 is fixed by batch 5 on the corpus that
reported it.

Per-call cost fell too, though that was not what was being measured: 40-115 s against the
reference's 75-160 s, now with the full per-project readiness set instead of one explicit
sentinel.

**Step 4 — T-61, closed without a fix.** The proposal was to cache readiness per (daemon, root)
for a short window. The measurements above make that unsafe: the wall clock *is* the reload, and
a cached "ready" would return during the next attach's reload, when `workspace/symbol` is
measurably partial (5 of 12 projects, 8 of 12 projects, above). That hands back exactly the
incomplete-answer-at-exit-0 bug batch 5 closed — a cross-project `refs`, or a whole project's
`sym` hits, simply missing at exit 0. The poll is not the cost; it is the only thing making the
cost visible. Probing the sentinels concurrently is already what `WaitReadyAsync` does (one
round for all projects at once). Closed as won't-fix here; it stops being interesting at all if
the upstream issue below is fixed.

### Draft upstream issue against `roslyn-language-server` (not filed)

> **Title:** `--daemon`: every client attach re-runs the full solution load, so the daemon shares
> a process but not a workspace
>
> **Version:** `roslyn-language-server` 5.12.0-1.26426.8
> (`Microsoft.CodeAnalysis.LanguageServer` 5.12.0.0), .NET 10.0.11, Windows 11.
>
> **What happens.** A daemon started with `--daemon --pipe <name> --autoLoadProjects` re-runs the
> entire MSBuild design-time load of the solution for *every* client that connects, not just the
> first. On each attach the server logs, under that client's `initialized` handler:
>
> ```
> [AutoLoadProjectsInitializer] Found single solution file <path>.slnx to auto load
> [LanguageServerProjectSystem] Loading <path>.slnx...
> [BuildHostProcessManager] .NET BuildHost started from ...\BuildHost-netcore\...
> [BuildHost PID nnnnn] info: Registered MSBuild 10.0.301 instance at ...
> [BuildHost PID nnnnn] info: Loading <every .csproj>
> [LanguageServerProjectSystem] Successfully completed load of <every .csproj>
> [LanguageServerProjectSystem] Completed (re)load of all projects in 00:00:24.66
> ```
>
> A fresh `BuildHost` process is spawned per attach and MSBuild is re-registered in it. Nothing
> loaded by an earlier client is reused.
>
> **Measured.** CommunityToolkit/dotnet (26 projects, restored, not built), one daemon, three
> sequential clients, each polling `workspace/symbol` until it resolves: cold attach 32.7 s and
> +76 CPU-seconds on the server process; warm attach 40.7 s / +114; warm attach 27.2 s / +70. On
> a 233-project solution the same shape costs 100-160 s and 200-300 CPU-seconds per attach, and
> three concurrent clients take ~350 s each against 76-92 s alone. A three-project solution is
> ~1.6 s per attach, so the cost scales with project count rather than being fixed overhead.
>
> **Expected.** A daemon that has already loaded a workspace should serve a second client asking
> for the same `workspaceFolders` out of the loaded state, at roughly the cost of the pipe round
> trip.
>
> **The obvious workaround is not one.** Omitting `rootUri`/`workspaceFolders` from `initialize`
> does suppress the reload — and leaves that client with an empty workspace: `workspace/symbol`
> returns nothing indefinitely, on a daemon that had loaded that exact solution seconds earlier.
> A client cannot opt into the already-loaded state, so the reload is the only way to get a
> workspace at all, which is what makes the daemon a process cache rather than a workspace cache.
>
> **Two smaller things found alongside**, either of which would have made this much cheaper to
> diagnose:
>
> - `--extensionLogDirectory <dir>` is accepted by the daemon (it appears on its command line)
>   and no file is ever written there. The only way to read the load log is to be a connected
>   client and keep `window/logMessage` — so the log is unavailable exactly when the server has
>   no client — and the daemon's own stdout/stderr carries nothing but the startup banner and
>   `Daemon accepted a new client connection.`, even at `--logLevel Trace`.
> - `--logLevel` passed by a later client is silently ignored, since the first client configures
>   the daemon, and there is no way to ask a running daemon what it was started with.

## Batch 7 — multi-targeting

- [ ] **T-26 — `project <file>` prints one arbitrary context.**
      Status: confirmed in source (`ProjectContextAsync` keeps `_vs_defaultIndex`; the whole
      contexts array is in hand).
      Fix: `project` lists every context, one row per `.csproj` + TFM, `count` = contexts.
      `ProjectOfAsync` (the generated-label path) keeps the default.
      Proof: no multi-TFM project in the fixture; `Protocol` deserialisation unit test with two
      contexts and a rendering test. Consider a two-TFM `fixture2` project only if a later item
      needs it live.
- [ ] **T-27 — `hover`/`def`/`refs` inside `#if` answer `no results` intermittently.**
      Status: measured by testers and re-run by the lead (4 of 6 misses, not tied to what
      `project` reports). Cause unknown — a server context choice.
      Verify first: whether Roslyn honours a project context on positional requests
      (VS sends `_vs_projectContext` in the `TextDocumentIdentifier`). If yes, retry an empty
      positional answer in the other contexts; if no, name the context in the miss
      (`no results in net10.0; the document has N other contexts`).
- [ ] **T-28 / T-29 — `diag` and `outline` answer from one unlabelled context.**
      Status: measured by testers. Same lever as T-27; at minimum print the context.
      Blocked by: T-27's verification.

## Batch 8 — output, CLI and docs

Small, independent, all measured by testers. Take them in one PR after the batches above so the
docs pass covers everything at once.

- [ ] T-65 — `no results` / `no project` go to stdout without `cslq:`; `--json` empty envelope
      vs no body. One rule; write it in DESIGN.md.
- [ ] T-79 — `--json` ignored on error paths. Same rule as T-65.
- [ ] T-66 / T-67 / T-68 — options before the command; exit 2 undocumented; `ready` accepts a
      stray positional.
- [ ] T-58 — validate `--log-level` names client-side; `--help` lists them.
- [ ] T-73 — `outline` text repeats a source line per declaration on that line.
- [ ] T-74 — kinds: `constructor` for constructors (also serves T-06's listing).
- [ ] T-75 — invalid UTF-8: throwing decoder, one stderr line naming the file. CLAUDE.md
      already predicts the desync; make it loud.
- [ ] T-76 — elide around the hit column on very long lines.
- [ ] T-77 — `source` is null on every diagnostic row: drop or populate.
- [ ] T-81 — a linked file outside the root prints a machine path in `sym`; label it.
- [ ] T-35 — interpret `projectInitializationComplete fired` + every project empty as a failed
      design-time build; pre-flight `dotnet --version` in the root (exit 155 = `global.json`
      mismatch).
- [ ] T-42 / T-43 — readiness failure text: one item per line; short-timeout wording.
- [ ] Docs, section (k): README line 327 ("the namespace part is not actually checked"),
      "the server does not restore your projects" (T-45: it restores; a *failed* restore is
      the boundary), daemon vs `--no-daemon` discovery (T-37), `sym` matching semantics
      (T-80), `<param>` omission (T-72), the two-`.csproj` `sym` effect, CJK argv from Git
      Bash (T-12).

## Deferred, with the reason

- **T-08 / T-09 / T-10** (overloads, partials, same type in two projects): real, but each
  needs a selector design (`--project`, signature rows) — after batch 4 settles the matching
  rules, not before.
- **T-12** CJK by name: not reproduced here; needs a PowerShell repro to separate a
  `workspace/symbol` limit from an argv problem. Cyrillic/Greek untried.
- **T-18 – T-22, T-24, T-25**: engine behaviours of this server build or accepted semantics;
  recorded, no cslq change planned.
- **T-37** daemon vs `--no-daemon` discovery differ: document (batch 8); the daemon's
  behaviour is the one the fail-fast is written for.
- **T-38** top-level-statements-only workspace: message improvement in batch 8; skipping
  readiness for file commands is a design change to weigh after batch 6.
- **T-44** spurious restore line: not reproduced on demand; likely two concurrent restores in
  the shared packages folder — the documented two-versions limit.
- **T-49 / T-50** `diag` walk scope (misc-file syntax errors; Razor files not walked): label
  rows from files no project compiles (`project: null`) and walk `*.razor`/`*.cshtml` for
  Razor-SDK projects — after batch 1's extension guard, since they share the file-kind logic.
- **T-53** VB/F# projects ignored: a stderr count line; batch 8 if cheap.
- **T-62 / T-63 / T-64** `diag` and positional latency: after batch 6, which may move them.

## Log

- 2026-09-08 — file created. T-55 reproduced in PowerShell (17.6 s cold, 14.1 s next call,
  `KEEPALIVE=10`, scoped pipe). Source claims for T-01–T-05, T-06/T-07/T-11, T-17, T-23,
  T-26, T-47/T-48 confirmed by reading `Program.cs`, `LspClient.cs`, `Output.cs`.
- 2026-09-08 — T-01, T-02, T-03 implemented on `fix/batch-1-exit-127` (uncommitted). T-03 needed
  two halves: `SolutionXml` loads from a stream, and `PathUri.Plain` strips the `\?\` prefix off
  `--root`, because `new Uri(@"\?\C:\...")` throws the same `UriFormatException`. T-01 reports
  the `cat -n` count, so line 14 of a 13-line file is rejected although Roslyn counts 14; an empty
  file accepts 1:1 only (`fixture/Ambient/Empty.cs`, row `empty-file-line-out-of-range`). Gate 84/84.
- 2026-09-08 — T-47/T-48/T-51 done. One guard (`Program.CheckDocument` / `CheckUnderRoot` /
  `CheckFile`, `PathUri.IsUnder`) runs on every file-taking command *before* `WaitReadyAsync`, so
  the argument error costs milliseconds, not a cold load. `outline`'s target resolution split into
  `OutlineFile` (pure, pre-readiness) and `OutlineSymbolAsync`; `diag` resolves its file list in
  `DiagFiles` the same way. `DocumentGuardTests` (12 cases), four `cases.jsonl` rows, one line
  each in README and SKILL.md. Gate 88/88, 139 unit tests.
- 2026-09-08 — T-04/T-05 done; batch 1 complete. `LspClient.RequestAsync`/`NotifyAsync` wrap every
  post-initialize call; `LspClient.Describe` is the pure message function (`RequestFailureTests`).
  Two deliberate bypasses: `initialize` (own wrapping in `StartCoreAsync`) and the
  `_vs_getProjectContexts` call inside `ProjectContextAsync`, whose `catch (RemoteRpcException)`
  softens the label on purpose. Manual check with the T-01 guard short-circuited:
  `cslq: textDocument/references failed: The requested line number 98 must be less than the
  number of lines 14. (Parameter 'Line')`, exit 1. Gate 88/88, 144 unit tests, format clean.
  The daemon-killed-mid-request case remains manual.
- 2026-09-09 — T-17/T-30 done on `fix/batch-2-rendering`. The fold key is the **rendered label**
  plus range, not the raw URI as planned above: T-30's per-TFM twins differ in authority guid and
  `documentId` and render as one label, so a URI key would have left them. `Output.Rank` orders
  file, generated, decompiled before the cap. Three `OutputTests`, row `refs-declaration-not-doubled`
  (`refs Square ... --json` → `count: 1`), one DESIGN.md bullet. Gate 89/89, 147 unit tests,
  format clean.
- 2026-09-09 — T-23/T-32 done; batch 2 complete. `Output.WriteSymbolsAsync` takes the query and
  ranks before the cut: `Relevance` (exact 0, prefix 1, substring 2, else 3, case-insensitive),
  then `Rank` (file, generated, decompiled), then raw URI and position as the tiebreak — the
  label projection stays *after* the cut so a broad query spends no requests on dropped
  generated hits. A dotted `sym` target matches no name and lands in class 3 for every hit,
  which is the old arrival cut made deterministic; matching the last segment is batch 4's call.
  T-32: the label is `<generated>/<project dir>/<assembly>/<generator full type name>/<hintName>`,
  always (a collision-only label would change shape with the result set), mirroring
  `EmitCompilerGeneratedFiles`' on-disk layout; `PathUriTests` had been carrying
  `typeName=BuildInfo`, the generated type, where Roslyn puts the generator — fixed. Ten probe
  rows, DESIGN/README/SKILL/CLAUDE/ROADMAP updated. Gate 89/89, 151 unit tests, format clean.
- 2026-09-09 — T-55 done on `fix/batch-3-daemon-stdout`; batch 3 complete. Confirmed before
  implementing, PowerShell 7, scoped pipe, `KEEPALIVE=10`: `ready --root fixture > out.txt 2>&1`
  took 14.6 s then 15.1 s, both ≈ keepalive + a cold load, with no pipe left behind. With the
  inherit flag cleared, on a fresh pipe: 4.5 s then 2.5 s, and `\.\pipe\cslq-t55-b1` still
  listening after the first. Git Bash `$(...)` moved the same way — 25 s to 4 s at
  `KEEPALIVE=20`. The fix is `LspClient.DisableStdioInheritance`, once per process before the
  first `StartProcess`, `[LibraryImport]` in the new `Native.cs`, which needed
  `<AllowUnsafeBlocks>` — the generated marshalling stubs are unsafe. Proof is
  `probes/stdout-capture.cs` + the Windows-only `daemon-survives-captured-stdout` leg; it was
  checked both ways (34.4 s and a dead daemon with the call commented out). Keepalive 30 and a
  25 s bound rather than 10 and 10: the pass is a cold load and the failure is keepalive plus a
  cold load, so the gap has to swallow a slow CI load. README/SKILL/CLAUDE rewritten — capturing
  `cslq` is now safe, and what survives is the trap one level up, where an *intermediary*
  process holds the capture pipe. Gate 90/90, 151 unit tests, format clean.
- 2026-09-09 — Batch 4 done on `fix/batch-4-symbol-targeting` (uncommitted). Decision B went to
  `textDocument/documentSymbol` rather than `hover`: measured first, hover's first line is FQN
  for types only (`class Fixture.Core.Greeter`) and minimal for members
  (`string Greeter.Greet(string name)`), so it could not see a member's namespace. `Targets`
  (pure: `Chain`, `ChainMatches` contiguous-suffix, `IsConstructor`, `TypeOverConstructors`) +
  `Program.SelectAsync`, which requests chains only for a dotted target or an ambiguous bare name.
  `fixture/Core/Widget.cs` adds `Widget` (two ctors), `Gadget` (one), `Outer.Inner.Depth` /
  `Other.Inner.Depth`. T-16 via `Output.SymbolListingAsync` shared with `sym`; `sym`'s display
  order is now relevance → rank → label → line → column (exact match first), no longer
  alphabetical. `candidates:` dump is deduplicated and capped. `T-nn` ids scrubbed from every
  tracked file (an old `T-50` in `DocumentGuardTests` included). Ten probe rows, twelve unit
  tests. Gate 100/100, 163 unit tests, format clean.
- 2026-09-09 — Batch 5 done on `fix/batch-5-readiness` (approved, uncommitted), three coder
  rounds. **T-82/T-40**: nesting boundaries are now every `.csproj` on disk (`ScannedProjects`,
  unioned with the discovered list), not the solution's list; new pure `ProjectSources.Read`
  classifies a csproj from text — `EnableDefaultItems`/`EnableDefaultCompileItems=false` means
  it compiles nothing of its own (and `None` is true whenever default items are off and no
  include points inside), a `*.projitems` import or outside-only `<Compile Include>` plus no
  own `.cs` means *skipped*. `ready --json` now carries `projects` (probed), `skipped` and
  `unprobed` (own sources, no type declaration the regex reads); the three sum to the discovered
  count. `--log-level Information` prints one `cslq: not probed — …` stderr line. Measured:
  OrchardCore `ready --timeout 600 --json` 165 s (2m45s; first run 195 s), `projects: 214`,
  `skipped: 1` (`src/Templates/OrchardCore.ProjectTemplates`), `unprobed: 12`, 227 total —
  was a 900 s failure. CommunityToolkit 18 s, `projects: 12`, fourteen skipped. **T-84**:
  `--sentinel` is additive (`Sentinel.Explicit`), stands alone only when inference throws;
  the explicit probe is excluded from the `ready --json` counts; row
  `ready-with-explicit-sentinel-still-probes-every-project`. **T-36**: two solutions at the
  root throw `Program.TwoSolutions` before the server starts, naming both; the scan fallback in
  `ProjectDirectories` is gone (the scan survives only as the boundary source); leg
  `two-solutions-root-fails-fast`. **T-39**: `WaitReadyAsync` bounds the wait to 20 s
  (`PostLoadGrace`) after `projectInitializationComplete` *fired in this process*
  — written here as "never on a daemon attach", which batch 6 measured wrong: the notification
  ends *this client's own* reload and fires on every attach, so the bound applies warm as well
  as cold (2026-09-10, batch 6 round 2). The logic is unchanged and still correct; only the
  reason was. Leg `exhausted-candidate-fails-after-load` (temp two-project tree, `#if
  false` type, `--no-daemon`) fails in 24 s against a 150 s timeout. **T-41: the hypothesis
  was wrong and nothing in cslq changes.** `new Uri(path)` escapes a literal `%` to `%25`, so
  `PathUri` round-trips `pct%20x` exactly (pinned by `PathUriTests`); the failure is MSBuild
  unescaping `%XX` in project paths — `dotnet restore` alone fails with MSB3202 on
  `<tmp>/pct%20x`, and the control `pct%zzx` is ready in 3.8 s. Recorded as a documented limit
  (README, CLAUDE.md, DESIGN.md). Gate 103/103, 182 unit tests, format clean.
- 2026-09-09 — Batch 5 merged as PR #25 (squash e283eeb). CodeRabbit raised three findings: two
  withdrawn after reply (outside-root paths render absolute by the DESIGN.md rule; `--sentinel`
  falling back on every inference failure is the documented escape hatch), one fixed —
  `ProjectSources.DefaultItemsDisabled` counts only an *unconditional* `false`, a conditioned
  value is unknown and leaves the project probed (two tests, 184 unit tests). Batch 6 next.
- 2026-09-10 — Batch 6 done on `fix/batch-6-per-attach-reload` (four commits, no PR). Investigation
  first, then prose only: **no `src/` behaviour change**. **T-83/T-56/T-87/T-88**: the mechanism
  is read rather than inferred. The daemon shares a server *process*, not a loaded workspace —
  every client's own `initialized` re-runs the whole solution load under `--autoLoadProjects`
  (`AutoLoadProjectsInitializer` → `LanguageServerProjectSystem] Loading <solution>` → a fresh
  `BuildHost` process → every `.csproj` → `Completed (re)load of all projects in ...`).
  CommunityToolkit 25-30 s per attach, cold and warm alike (32.7 / 40.7 / 27.2 s wall, +76 /
  +114 / +70 server CPU-seconds on one pid); the fixture reproduces it at ~1.6 s per attach, so
  it is not a large-repository effect. Reading it needed instrumentation, because the server's
  own stderr says nothing but `Daemon accepted a new client connection.` even at `Trace` and
  `--extensionLogDirectory` is accepted and never written: the load log arrives at the *client*
  as `window/logMessage`, which `Endpoints.OnLogMessage` discards. **The cslq-side fix does not
  exist**: the only lever at `InitializeAsync` is omitting `workspaceFolders`, and that is
  measured to suppress the reload *and* leave the client with an empty workspace —
  `workspace/symbol` returned nothing for a full 600 s on a daemon that had loaded that exact
  solution seconds earlier. So the upstream issue is drafted in the batch 6 section and
  **not filed**, and the scale qualifier is the deliverable. Two claims were measured wrong and
  corrected in four files (`LspClient.WaitReadyAsync`'s doc comment, CLAUDE.md, README,
  `skills/csharp-semantic-queries/SKILL.md`): `projectInitializationComplete` fires **once per
  attach**, warm daemon included, because it ends that client's reload rather than the daemon's
  history; and `workspace/symbol` answers **partially** throughout a load rather than nothing —
  8 of 12 projects resolved before the notification over 21 s, and four more kept resolving for
  6-8 s after it, so the incomplete-answer window straddles it. `WaitReadyAsync`'s logic was
  left alone deliberately: the notification cannot arrive early on a warm attach, so
  `PostLoadGrace` is never armed against a still-loading workspace, and the tail after it is
  what the grace is for. **T-85: not reproduced, on either corpus.** Five `diag` runs on
  CommunityToolkit and nine on OrchardCore — the reported corpus, restored and not built
  (`bin/Debug` holds zero `OrchardCore.*.dll`), using the reference's own repro lines including
  the two files it named individually — report zero CS0234/CS0246/CS0103 and zero `error CS` of
  any code; the `Controllers --max 5` walk returns a real `IDE0047` with context, so "no
  diagnostics" is not a silent no-op. Closed as fixed by batch 5, whose additive `--sentinel`
  makes readiness wait for every project before `diag` opens anything. **T-61: closed
  won't-fix.** The wall clock *is* the reload, so caching readiness per (daemon, root) would
  return during the next attach's reload while `workspace/symbol` is measurably partial, handing
  back the incomplete-answer-at-exit-0 bug batch 5 closed; the poll is not the cost, it is what
  makes the cost visible, and probing concurrently is already what `WaitReadyAsync` does.
  Gate 103/103, 184 unit tests, format clean.
