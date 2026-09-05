# Hardening plan — post-Milestone-4 correctness and hygiene

This file is a multi-session work plan, separate from `ROADMAP.md` because it is not a
milestone: it is a list of defects found by review of the Milestone 4 working tree, with the
fix for each one already argued out. `ROADMAP.md` still owns milestone state; this owns these
items until they are done, at which point it is deleted, not archived.

Written 2026-09-05 against the then-uncommitted Milestone 4 tree, revised the same day after
two reviews of this file found four of its own probe cases and two of its fixes to be wrong,
and revised again after **PR #6 merged as `1796a31`** — which carried 1a and 1c, because the
gate could not go green without them. Read `CLAUDE.md` and `ROADMAP.md` first; both now
describe the readiness model this file used to be proposing.

**Line numbers were re-derived against `1796a31` on 2026-09-05 and will drift again.** Every
anchor also names its method, which is the durable half.

## Where these came from

Five agent reviews: two of the Milestone 4 diff, two of the first draft of this file, and one of
the revision — whose findings (the unverified misc-files premise, 1c's warm cost, 0a's late
validation, 2a's count) were verified against the source and folded in on 2026-09-05.
Everything below was verified against the source or on the wire on 2026-09-05 before being
written. What the reviews got wrong, and what this file got wrong, is recorded with each item
so a later session does not restore a suggestion that was already considered and rejected.

Rejected review suggestions, with the reason:

- Fixing `sym`'s cap by rendering in the server's order. Rejected — see 2a.
- Adding a two-answers-agree stability loop to `SettleAsync`. Rejected — see 1d.
- Rating the incomplete-result items P1. **This was wrong, and it was wrong on the facts.**
  "None has been observed failing, and the cross-project probes pass" stopped being true the
  first time the suite ran after it was written: three gate runs each failed a *different*
  pair of cases, and CI went red on `sym-query` / `sym-json` missing `Core/Shape.cs`. 1a and
  1c were pulled forward into PR #6 because the gate could not go green without them. The
  lesson worth keeping: "not observed" was doing the work of "does not happen", when what it
  really meant was that nothing had run the suite enough times yet.

## What 1s answered, 2026-09-05

Half of it, and the half that gated 1a/1c:

- **The server cannot be asked what it loaded.** `workspace/_roslyn_restorableProjects` is a
  server-to-client *request* and carries no project list. So 1a is the `.csproj` scan, and its
  limits are now a documented property of `csx`.
- **`projectInitializationComplete` cannot close the window.** Instrumented cold run: 0 hits
  for 8.1 s, then the complete set on the same poll the notification fired. Cold load never
  answers partially. The partial window belongs to a client attaching to a daemon loading a
  root it has not loaded before — where the notification already fired for the previous root.

Still open, and still gating 1b: **the misc-files question.** Nothing here established whether
`workspace/symbol` answers for a file in no project, so the first root-cause bullet and
`sentinel-skips-non-project-file` remain unverified. Note also that `fixture/` has **no
`.sln`** and loads fine, which contradicts `skill/SKILL.md`'s first troubleshooting entry —
worth re-deriving before 1b leans on it.

## Verified on the wire, 2026-09-05

Against 5.12.0-1.26426.8, so these do not need re-deriving:

- `csx refs Core/Greeter.cs:0:1 --root fixture` **crashes**: `Position(line - 1, column - 1)`
  at `Program.cs:288` sends `-1`, Roslyn throws `ArgumentOutOfRangeException` out of
  `ProtocolConversions.PositionToLinePosition`, and `csx` exits **127** with an unhandled
  `RemoteInvocationException` and a stack trace. Zero is not a lesser case of the overflow bug;
  it is the more reachable one, since `file:0:1` is what a zero-based tool emits.
- `csx diag --root fixture --json` returns **count 3**, not 1: `App/Program.cs` IDE0002 (hint),
  `App/TypeError.cs` CS0029 (error), `Gen/BuildInfoGenerator.cs` IDE0130 (info).
- `csx sym BuildInfoGenerator --root fixture` resolves to `Gen/BuildInfoGenerator.cs:9:21`,
  `project Gen (netstandard2.0)`. The analyzer project is a real workspace project, so a
  per-project sentinel exists for all three fixture projects and 1c is feasible here.
- `SourceFiles` sorts full paths `OrdinalIgnoreCase`, so `fixture/Ambient/` < `fixture/App/` <
  `fixture/Aux/`. A directory named `Aux` sorts *after* `App` and would never win inference.

## The root cause — largely closed

Four of the items — 1a through 1d — were one gap: **`csx` had no notion of which projects
exist.** 1a and 1c closed it for readiness; 1b and 1d are what is left. The six symptoms, with
what is now true of each:

- `InferSentinel` picks a type out of any `.cs` under the root, including files no project
  compiles, so readiness waits out the full 180 s on a symbol Roslyn will never index.
  **This one bullet rests on an unverified premise** — that `workspace/symbol` does not answer
  for a file in no project. Nothing in the repo establishes it; the misc-files evidence
  (`README.md:252`, `LspClient.cs:219`) is about a freshly *opened* document, which is a
  different question. 1s must settle it. If Roslyn does index such a file, this symptom is not
  real and `sentinel-skips-non-project-file` pins nothing. The five bullets below do not depend
  on it.
- Bare `diag` reports on those same non-project files.
- ~~`refs` / `impl` accept a result set that is merely *incomplete*~~ — **fixed by 1c.** This
  was the one that turned out to be live breakage rather than theory: it took CI red and three
  gate runs, each failing a different pair of cases.
- ~~`sym` returns a partial workspace search for the same reason~~ — **fixed by 1c.**
- ~~`def` resolves a genuinely ambiguous name to whichever project loaded first~~ — **fixed by
  1c**, in the sense that every project is now loaded before the query is asked, so the
  ambiguity is visible and the existing error fires.
- `diag` settles on two identical empty misc-files snapshots taken 250 ms apart.

Three compensating retry loops exist because readiness was weak — `SettleAsync`,
`QuerySymbolsAsync`'s 10 s grace, `DiagnosticsAsync`'s settle — and each has a hole. The plan
was: **give the client project knowledge, make "ready" mean every project loaded, then delete
the compensations that only existed because it did not.** The first two thirds have landed;
1d is the deletion, and it is now unblocked.

## Status

| # | Item | Phase | State |
|---|---|---|---|
| 0a | Validate numeric CLI arguments | 0 | **done** |
| 0b | Dispose the client when `initialize` fails | 0 | **done** |
| 1s | **Spike: what does Roslyn actually load?** | 1 | **half done** — see below |
| 1a | Enumerate projects | 1 | **done** (PR #6) |
| 1b | Scope `SourceFiles` to project directories | 1 | not started |
| 1c | Readiness means every project loaded | 1 | **done** (PR #6) |
| 1d | Delete the compensating retries | 1 | not started — **next**, now unblocked |
| 1e | Document what `--sentinel` now gives up | 1 | not started — debt from 1c |
| 2v | **Verify `workspace/symbol` ordering on the wire** | 2 | not started — gates 2a |
| 2a | `sym`: truncate before sorting | 2 | not started |
| 2b | README layout tree omits `App/Square.cs` | 2 | not started |
| 3 | Whole-workspace `diag` throughput | 3 | not started |

Phases 0 and 2 are small and independent; they can land together. Phase 1 is the real change
and should be its own PR. Phase 3 is gated on measurement and should be a third.

## Phase 0 — CLI hygiene — **done, uncommitted**

Independent of everything else. Do it first so the gate is green from a clean base.

Both items landed 2026-09-05 in the working tree. What the plan did not say, and is worth
keeping: the position check has to live in `Options.Parse` rather than beside `LocateAsync`, so
it applies to **every** command's argument. `PositionSpec` cannot match a bare symbol name, so
there are no false positives, and gating it per command would reintroduce exactly the
late-validation problem 0a exists to remove.

### 0a. Validate numeric CLI arguments

`Program.Options.Parse` (~`Program.cs:501`) parses `--max`, `--context` and `--timeout` with
`int.Parse`, and `Main` (~`Program.cs:37`) catches only `CsxException`, so `--max nope` prints a
`FormatException` and a stack trace. `TryParsePosition` (~`Program.cs:390`) does the same on
overflow, and `Program.cs:288` turns a zero line or column into a negative LSP position, which
crashes with exit 127 (see the wire notes above).

Add an `Int(argv, ref i, name)` helper beside the existing `Next`, wrapping `int.TryParse` and
throwing `CsxException`. Floors: `--max` >= 1, `--context` >= 0.

**Validate the position at parse time, not in `LocateAsync`.** `TryParsePosition` runs inside
`LocateAsync` (`Program.cs:284`), which `RefsAsync` reaches only after `StartAsync`
(`Program.cs:56`) and `WaitReadyAsync` (`Program.cs:114`) — so `file:0:1` starts a server and
waits out readiness before printing an argument error, where `bad-max-reports` is instant
because `Options.Parse` runs first. Both probes pass either way, so nothing catches this;
validate the shape in `Options.Parse`, or at minimum before `StartAsync`. `PositionSpec` is
`^(?<file>.+):(?<line>\d+):(?<col>\d+)$`, which a bare symbol name cannot match, so a
parse-time check has no false positives — and it also covers the second call site,
`OutlineTargetAsync` (`Program.cs:202`).

**Do not add a `--timeout >= 1` floor.** `premature-query-fails-loudly` deliberately passes
`--timeout 0` to prove a query fired before load fails loudly rather than returning empty, and
`ROADMAP.md:207-208` and `:389` both record why. Zero must stay valid. A negative timeout can be
rejected.

In `TryParsePosition`, use `int.TryParse`, and when the regex matched the `file:line:col` shape
but the numbers are unusable, throw a `CsxException` naming line or column — **rejecting zero as
well as overflow**, since positions are one-based everywhere in `csx`. Returning `false` is
wrong here: it would fall through to the symbol resolver and produce a misleading "no symbol
matched 'Core/Greeter.cs:0:1'" plus a candidate dump.

```
{"name":"bad-max-reports","args":"refs Greet --root fixture --max nope","exit":"1","expect":"csx: --max needs an integer"}
{"name":"zero-position-reports","args":"refs Core/Greeter.cs:0:1 --root fixture","exit":"1","expect":"csx: line and column are one-based"}
{"name":"overflow-position-reports","args":"refs Core/Greeter.cs:99999999999:1 --root fixture","exit":"1","expect":"csx: line out of range"}
```

`run.sh:81` captures combined stdout+stderr, so an error message on stderr is assertable — that
is what `non-daemon-fallback-reported` already relies on.

### 0b. Dispose the client when `initialize` fails

**This is hygiene, not a leak — do not oversell it.** `initialize` passes
`Environment.ProcessId`, and the positionEncoding check happens *after* `initialize` returns
(`LspClient.cs:94-103`), so that server already has the parent-process watch and exits when
`csx` does. The same holds on the cancellation path. What is actually missing is a polite
`shutdown` and a deterministic teardown.

`LspClient.StartAsync` (~`LspClient.cs:25`) wraps an initialize failure in a `CsxException`
carrying `StderrTail()` — the thing that turned "connection lost" into a real diagnostic, and it
must survive this edit — but never disposes the client it built, and the
`when (ex is not OperationCanceledException)` filter lets cancellation escape uncleaned. Capture
`StderrTail()` **before** disposing, then `await client.DisposeAsync()` on both paths.

A third path fell out of the verification: a `CsxException` from `InitializeAsync` — the encoding
assertion — was being rewrapped as "the language server closed the connection during initialize",
which is false, because that failure happens *after* `initialize` was answered by a live server.
It now disposes and rethrows unchanged, alongside cancellation.

**Done. Two things in this paragraph were wrong; both cost a run.**

- **Editing `ServerArgs.ExpectedPositionEncoding` does not force a mismatch.** The comparison is
  `result.Capabilities.PositionEncoding ?? ServerArgs.ExpectedPositionEncoding`, and this server
  does not advertise `positionEncoding` at all, so the fallback makes it vacuous whatever the
  constant says: `csx ready --no-daemon` with the constant set to `utf-32` still printed `ready`
  and exited 0. Force it at the comparison instead — temporarily `?? "utf-8"` — which is also
  the more faithful case, since it fails with the server **alive** rather than already dead.
- **An isolated `ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME` is not a substitute for `--no-daemon`.**
  The daemon still outlives the client, so there is nothing to observe; and worse, the daemon
  inherits the client's stdout, so `out=$(csx ...)` in bash **hangs forever** waiting for the
  pipe's last writer. Use `--no-daemon`, and redirect to a file rather than capturing.

Verified 2026-09-05 with `?? "utf-8"` and `--no-daemon`: exit 1, the message is still
`csx: ... Server negotiated positionEncoding 'utf-8'; csx assumes 'utf-16'.`, and no server
process survives the run. Note what this **cannot** show: `initialize` passes
`Environment.ProcessId`, so the server would have exited on the parent-process watch anyway.
The check proves the message survived the edit and nothing leaks — not that disposal is what
collected it. Not probeable.

## Phase 1 — project scoping

The real fix. 1a and 1c landed in PR #6; **1d is next and is unblocked**. 1b still waits on
the unanswered half of 1s.

### 1s. Spike: what does Roslyn actually load?

**Half answered — see "What 1s answered" above; the rest gates 1b.** The plan below identifies
projects by scanning for `.csproj`
files. That is an approximation, and the failure modes run both ways: it can wait for a project
Roslyn never loaded, and it can miss source that a project does compile. This repo already
documents an instance — `skill/SKILL.md:119-122` says a root with a `.csproj` and **no solution**
never loads at all, so a directory scan would find that project and 1c would then wait forever
for a sentinel that cannot resolve. Other cases: `.csproj` files excluded from the solution,
`<Compile Remove>`, `EnableDefaultCompileItems=false`, linked source outside the project
directory, nested projects.

~~Before writing 1a, establish whether the server can simply be **asked** what it loaded.~~
**Answered: it cannot.** `workspace/_roslyn_restorableProjects` is a server-to-client request
carrying no project list. 1a is therefore the directory scan, and its limits are recorded in
`ROADMAP.md`'s verified facts — **but not yet in `DESIGN.md`**, which this item asked for so
the approximation becomes a documented property of `csx` rather than an implementation detail.
That write-up is still owed; fold it into 1b or the close-out.

Note also that `fixture/` has **no `.sln`** and loads fine, which contradicts
`skill/SKILL.md`'s first troubleshooting entry. Re-derive that claim before 1b leans on it.

**Settle the misc-files question in the same session — it gates 1b's fixture, not just its
prose.** Drop `Ambient/Stray.cs` into a *scratch copy* of `fixture` (not the tree — see the
sequencing trap in 1b), then `csx sym Stray --root <copy>` and `csx diag --root <copy> --json`.
One run answers both halves, and they can diverge: the misc-files workspace is populated by
`didOpen`, and readiness polls `workspace/symbol` before `csx` opens anything, so the sentinel
half is plausible while the diagnostic half is the one with repo evidence behind it. Record
both answers in `ROADMAP.md`'s verified-facts section. If `sym Stray` resolves, drop
`sentinel-skips-non-project-file` and strike the first root-cause bullet; 1b's `diag` scoping
survives either way.

### 1a. Enumerate projects — **landed in `1796a31`**

`Program.ProjectDirectories` (~`Program.cs:467`): every `*.csproj` under the root minus
`bin`/`obj`, reduced to its containing directory. The `.csproj` scan, not a server query — 1s
established the server exposes no project list to ask for.

### 1b. Scope `SourceFiles` to project directories

`Program.SourceFiles` (~`Program.cs:482`) returns every `.cs` under the root minus `bin`/`obj`.
Rebuild it over `ProjectDirectories` — **which 1a already provides** (~`Program.cs:467`): the `.cs` files under each project directory, `Distinct()`
(a project nested inside another's directory would otherwise appear twice), then the existing
`Order`. If no `.csproj` is found under the root, throw a `CsxException` naming the root.

`DiagAsync` (~`Program.cs:231`) calls `SourceFiles(full)` for a directory argument. Change it to
compute the set once from `opts.Root` and filter by path prefix, so a subdirectory containing no
`.csproj` of its own still resolves through its containing project.

**The failure mode inverts, and the new one is worse.** Today `SourceFiles` is over-inclusive:
noisy, but nothing goes missing. Scoped to project directories it becomes under-inclusive, and a
`<Compile Include>` pointing outside the project directory — a linked file, a shared
`Directory.Build.props` glob — vanishes from `diag` with no signal at all. Missing diagnostics
are worse than spurious ones. Say so in `DESIGN.md`, and revisit if 1s found a way to ask the
server.

**1b does not fix `skill/SKILL.md`'s first troubleshooting entry.** A root with a `.csproj` and
no solution still never loads; 1b only makes a root with *no* `.csproj* fail immediately instead
of timing out. Update that entry to describe the new error, and keep the existing one.

#### The fixture for it — and a sequencing trap

Add a `.cs` file in **no project**, in a directory that sorts **before** `App/` so it actually
wins today's first-file-wins inference. `Aux/` does not: `OrdinalIgnoreCase` puts `App` before
`Aux` (verified above), so the obvious name makes the case vacuous — it would pass today and
prove nothing. Use **`fixture/Ambient/Stray.cs`**, and leave this paragraph in place, or the
next person renames the directory to something tidier and silently kills both cases.

Make the stray *reliably produce an error* — reference a type it cannot see, so it yields CS0246
as a misc file — otherwise the diagnostic case cannot detect anything. 1s establishes on a
scratch copy that it does.

**Sequencing trap: adding this file before 1b lands breaks the entire suite.** `InferSentinel`
would pick the stray's type, it would never resolve, and every case would time out. The fixture
file and 1b must land in the same commit.

```
{"name":"sentinel-skips-non-project-file","args":"ready --root fixture --timeout 20","exit":"0","expect":"ready"}
{"name":"workspace-diag-skips-non-project-file","args":"diag --root fixture --errors-only --json","exit":"0","expect":"'count': 1|'path': 'App/TypeError.cs'|'code': 'CS0029'"}
```

The first fails before 1b (20 s timeout against an unresolvable sentinel), which is what makes it
a real pin. The second pins `--errors-only`, **not** the unfiltered count: the raw count is 3
today and two of those are IDE analyzer results (IDE0002, IDE0130). A bump that adds or retires
one IDE hint would turn the weekly gate red for a reason unrelated to the bump, which is the
exact thing this repo exists to survive — and it is why `deliberate-error-diag-workspace`
already asserts `--errors-only`. Both cases are contingent on 1s: the first only pins something
if `workspace/symbol` really does ignore a file in no project, and the second only if the stray
really does emit CS0246 as a misc file. Write neither before 1s has answered.

### 1c. Readiness means every project loaded — **landed in `1796a31`**

`LspClient.WaitReadyAsync` (~`LspClient.cs:135`) now takes `IReadOnlyList<Sentinel>` and returns
only when every one resolves; `Program.Sentinels` (~`Program.cs:415`) and
`Program.InferSentinels` (~`Program.cs:436`) build the set. All four traps this file predicted
were real and all four are handled:

1. **Hits are matched back to their own project** by requiring the location to sit under the
   project directory (`LspClient.Under`). Not `containerName`.
2. **The round fires concurrently** across projects (`Task.WhenAll`), so warm cost is one round
   trip's wall-clock rather than N sequential ones. The predicted per-tick escalation was not
   needed: resolved projects drop out of `pending` each tick.
3. **Up to three candidates per project**, tried in order. This earned itself immediately — the
   doc comment added to `fixture/Core/Shape.cs` in the same PR contains the words
   "interface half", so Core's candidate list came out `'Greeter' / 'Party' / 'half'`, and
   `half` can never resolve.
4. **A project declaring no type is skipped**, and a root with no `.csproj` falls back to a
   single root-scoped sentinel.

`premature-query-fails-loudly` survived unchanged: the message still contains both
`did not become ready` and `returned no symbols`.

**What was decided rather than resolved:** `--sentinel` replaces the whole set with one
root-scoped probe, as proposed. That makes it the weakest mode, and **nothing tells the user
so** — see 1e.

**What did not get done:** trap 4 asked the timeout message to name projects that contributed
*no* sentinel. It names unresolved projects only. A project skipped for having no type
declaration is silently absent from readiness, which is the same class of quiet degradation
this item exists to remove. Small, and worth folding into 1e.

### 1d. Delete the compensating retries

**Unblocked:** 1c is in and the gate is green cold and warm, locally and on `ubuntu-latest`.
This is the next item in Phase 1.

- **`QuerySymbolsAsync`'s 10 s / 250 ms grace** (~`Program.cs:330-350`) — delete it. This is also
  the first review's cost complaint: `sym` pays 40 requests and 10 s on every miss, because a
  search for a name that is not there is indistinguishable from a project still loading. Fixing
  the premise removes the loop instead of tuning its constant, and `sym-no-match-fails` stops
  being a 10-second case. `QuerySymbolsAsync` collapses into a single query and
  `MatchSymbolsAsync` reabsorbs it. **Keep `Distinct` extracted** — `sym` uses it too, and
  Roslyn really does report a symbol once per project that sees it.
- **`SettleAsync`** (~`LspClient.cs:249`) — **keep**. Its decompiled-URI guard covers a case
  readiness cannot: a symbol that genuinely comes from an assembly. Do not add a
  two-answers-agree stability loop on top of it; with 1c the incompleteness it was being asked
  to catch should not arise, and such a loop would have the same false-settle hole as the
  diagnostics one.
- **`DiagnosticsAsync`'s settle** (~`LspClient.cs:279`) — keep for now. Its misc-files problem is
  a property of a freshly opened *document*, not an unloaded project, so 1c does not retire it.
  See Phase 3.

If a cross-project case flakes after 1d, that is 1c having a hole. Fix 1c. Do not restore the
grace. If `--sentinel` was left as a replacing override, note that runs using it no longer have
the grace either — that is the contradiction item 1c asks to settle.

### 1e. Document what `--sentinel` now gives up

Debt from 1c, and the only user-visible contradiction it left. `--sentinel` replaces the
per-project set with one root-scoped probe, so a run that passes it has exactly the weak
readiness that produced every incomplete answer in PR #6 — and `README.md` and
`skill/SKILL.md` still describe it as a neutral override. Say plainly in both that it is an
escape hatch for when inference cannot read the workspace layout, and that it drops the
all-projects-loaded guarantee. While there, make the readiness timeout message name projects
that contributed no sentinel at all.

Not probeable for the same reason 1c is not.

## Phase 2 — output

### 2v. Verify `workspace/symbol` ordering — gates 2a

**The premise of 2a is unverified.** The claim is that Roslyn answers a partial query in
relevance order, which is plausible (NavigateTo ranks exact over prefix over substring over
fuzzy) but was *not* measured — `Output.WriteSymbols` sorts before anything is printed, so the
raw order is not observable through `csx` today. Establish it first: log or temporarily bypass
the sort, fire a partial query with clearly different match qualities, and record the finding in
`ROADMAP.md`'s verified-facts section with the version and date.

If Roslyn does not rank, 2a is pointless and should be dropped rather than implemented.

### 2a. `sym`: truncate before sorting

`Output.WriteSymbols` (~`Output.cs:192`) sorts by name, path, line, column and container, and
*then* takes `max`. (PR #6 added the column and container keys, for determinism when two
generated documents collide on display path — that is a tie-break fix, not this one.) If 2v confirms ranking, a broad query capped at 50 keeps an alphabetical prefix rather
than the best matches. It does not show in the fixture — all three `Area` hits share a name — but
`sym Get` on a real repo would lose the ranking entirely.

Fix: `Take(max)` in the server's order, **then** sort the kept set for display. Cheap, because
`Program.Distinct` (~`Program.cs:354`) uses `DistinctBy`, which keeps first-seen order — the
server's ranking survives intact all the way into `WriteSymbols`.

**The `Take` stays inside `WriteSymbols`, and the untruncated count must survive it.** Both the
`... N more` line (`Output.cs:254`) and the JSON `count` / `truncated` fields
(`Output.cs:226`) are `hits.Count` against `shown.Count`. Truncating in `SymAsync` instead
makes those equal, the more-line never prints, and the re-pinned `sym-truncates` expect string
below is the first thing that breaks. Reorder within `WriteSymbols` — `Take` the incoming list,
sort only the kept set, keep `symbols.Count` for the totals. `WriteOutlineAsync`
(`Output.cs:284-313`) already carries `total` and `kept` separately; follow it.

Rendering in the server's order was **rejected**: it would make every `sym` case depend on an
ordering nothing pins, and `sym-query` asserts three paths in a fixed order today.
Truncate-then-sort keeps relevance for what survives the cap and keeps rendered rows
deterministic.

`sym-truncates` must be re-pinned — which two of the three `Area` hits survive becomes
server-dependent, so drop the path assertion:

```
{"name":"sym-truncates","args":"sym Area --root fixture --max 2","exit":"0","expect":"... 1 more (use --max 3 to see all)"}
```

`sym-query` is uncapped and unaffected. Add a sentence to `DESIGN.md`'s `sym` paragraph: the cap
is applied in the server's order and the display sort is cosmetic.

### 2b. README layout tree

The tree lists `Core/Shape.cs` (`README.md:292`) but not `App/Square.cs`, and the cross-project
split is the whole point of that fixture. It lists other `App/` files (`App/TypeError.cs`,
`README.md:288`), so this is an omission, not a convention. One line. **Re-checked against
`1796a31`: still true.**

## Phase 3 — whole-workspace `diag` throughput

`LspClient.DiagnosticsAsync` (~`LspClient.cs:279`) pulls, then unconditionally waits
`Task.Delay(250)` before the first comparison pull, and `DiagAsync` walks files sequentially. So
every file pays at least 250 ms: a 2,000-file repository spends 500 s in delays alone, before any
server time.

The only item whose fix cannot be specified from the code alone, and the only one the fixture is
too small to demonstrate. Two steps, in order:

1. **Measure**, with 1c already in. Check on the wire whether a second pull ever differs from the
   first once every project is loaded — separately for the *first* document opened and for
   subsequent ones. The misc-files state that motivated the loop is a property of the freshly
   opened document, so it may well survive 1c for the first document and not for the rest.
2. **Then choose**: hoist every `didOpen` ahead of the pulls and settle once; or keep the settle
   only for the first document per project; or run the per-file pulls with bounded concurrency.
   Whichever it is, the mandatory per-file 250 ms floor goes. **Concurrency is output-safe** —
   `WriteDiagnosticsAsync` sorts by path, line and column (`Output.cs:112-116`) before it takes
   `max`, so completion order cannot perturb the rendering. Do not rule it out for that reason.

Verification is a synthetic timing run against a large tree, not a probe case. `cases.jsonl` has
no way to assert throughput and should not pretend to.

## Close-out, when all phases are done

- `dotnet format --verify-no-changes` exits 0.
- `probes/run.sh` green — 44 legs at the start of Phase 0, 47 after it (0a added three),
  plus roughly two more to come, **one re-pinned**:
  `sym-truncates` (2a). `premature-query-fails-loudly` was the other candidate and survived 1c
  unchanged, because the rewritten message kept both asserted phrases.
- ~~`CLAUDE.md`: the "sentinel resolving no longer implies every project is loaded" bullet~~ —
  **done in `1796a31`.** It now describes the per-project model, keeps the `SettleAsync` /
  `MetadataAsSource` half, and carries the rule against matching a hit to a project by
  `containerName`.
- `DESIGN.md`: **still owed, and now the largest doc gap.** Readiness as an all-projects
  predicate, and the limits of the `.csproj` scan 1s settled on — neither is in `DESIGN.md`,
  though both are in `ROADMAP.md`'s verified facts. Plus the `sym` cap ordering note from 2a.
- `skill/SKILL.md`: **still owed.** 1c changed what `ready` means and what `--sentinel` costs
  (1e), and 1b changes the first troubleshooting entry — whose no-`.sln` premise 1s has already
  cast doubt on. This file is what tells the agent what to do about all of it.
- `ROADMAP.md`: Milestone 4's "output tuning" item is marked unscoped; 2a is a concrete tuning
  change and can be recorded against it. The generated-document-label limitation stays deferred.
  1s's findings are **already in** the verified-facts section; 2v's are not.
- `README.md`: the layout tree (2b), the `--sentinel` wording (1e), and the case count.
- Delete this file.
- Never push, and do not commit unless asked.

## Deliberately not doing

- **Factoring the fourth copy of the `... N more (use --max N to see all)` / no-results /
  `{ count, truncated, results }` block** out of `Output`. Four renderers sharing a shape is
  consistent; a fifth would be the moment to factor it, not this one.
- **Adding a probe for 1c, 0b or Phase 3.** A race, a process lifetime, and a throughput number:
  none is observable through `cases.jsonl`. Manual checks instead, described in each item.
  1c has since landed and this held: nothing new pins it directly, and the coverage is the
  opening cold `ready` plus the existing cross-project cases. What actually caught the race was
  running the suite repeatedly and noticing it failed a *different* pair of cases each time —
  worth remembering as a technique, since no single run looked like anything but noise.
