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

### The misc-files question — answered 2026-09-05, and it killed half of 1b

Measured on a scratch copy of `fixture` with `Ambient/Stray.cs` added, against
5.12.0-1.26426.8:

- **`workspace/symbol` does not index a file in no project.** `csx sym Stray` exits 1 with
  `no results`. The first root-cause bullet's premise was right.
- **Neither does `textDocument/diagnostic`.** `csx diag Ambient/Stray.cs --json` returns
  **count 0**, and the whole-workspace count stays 3. So the second root-cause bullet —
  "bare `diag` reports on those same non-project files" — **is not real**, and
  `workspace-diag-skips-non-project-file` would have asserted the same thing before and after
  1b. It was never written.
- **`outline` still answers**, off the syntax tree. The asymmetry is indexing, not parsing.
- **A linked file is fully indexed.** Adding `<Compile Include="../Ambient/Stray.cs" />` to
  `App.csproj` makes `sym Stray` resolve (`project App`) and `diag` report both CS0246 errors.
  So the inverted failure mode 1b predicted is **measured, not theoretical**: full 1b would
  have silently dropped two real errors to suppress zero noise.
- **`fixture/` has `Fixture.slnx`.** The "no `.sln` and loads fine" contradiction with
  `skill/SKILL.md` was a false alarm; that entry already says `.sln`/`.slnx` and stands.

**1b was therefore cut down to what the measurements support**: readiness inference stays
project-scoped (it already was, since 1a), a root with no `.csproj` now fails immediately
instead of timing out, and `diag`'s file walk is left alone — deliberately, with the reason
written into `DESIGN.md` so it is not "fixed" later. `fixture/Ambient/Stray.cs` landed anyway, and review corrected what it
pins. The first attempt, `sentinel-skips-non-project-file`, was vacuous — per-project inference
has been unable to pick that file since 1a, so the case passed identically with the fixture
file deleted, which is precisely the `Aux/` mistake this file warned about. It was replaced by
three cases that go red if the measurement itself drifts: `non-project-file-not-indexed`,
`non-project-file-outlines` and `non-project-file-no-diagnostics`. The last is the load-bearing
one — it asserts the workspace `--errors-only` **count**, where
`deliberate-error-diag-workspace` only greps for a line, so a bump that started reporting
diagnostics for files in no project would take the count to 2 and go red.

~~Still open, and still gating 1b: **the misc-files question.** Nothing here established whether
`workspace/symbol` answers for a file in no project, so the first root-cause bullet and
`sentinel-skips-non-project-file` remain unverified. Note also that `fixture/` has **no
`.sln`** and loads fine, which contradicts `skill/SKILL.md`'s first troubleshooting entry —
worth re-deriving before 1b leans on it.~~

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

- ~~`InferSentinel` picks a type out of any `.cs` under the root~~ — **fixed by 1a/1c**, and
  its premise confirmed by 1s: `workspace/symbol` really does ignore a file in no project.
  Inference has been per-project since 1a, so the stray is never picked. Pinned by
  `sentinel-skips-non-project-file`.
- ~~Bare `diag` reports on those same non-project files~~ — **not real.** 1s measured count 0
  for such a file. Nothing to fix; see the misc-files section above for why scoping the walk
  would be a net loss.
- ~~`refs` / `impl` accept a result set that is merely *incomplete*~~ — **fixed by 1c.** This
  was the one that turned out to be live breakage rather than theory: it took CI red and three
  gate runs, each failing a different pair of cases.
- ~~`sym` returns a partial workspace search for the same reason~~ — **fixed by 1c.**
- ~~`def` resolves a genuinely ambiguous name to whichever project loaded first~~ — **fixed by
  1c**, in the sense that every project is now loaded before the query is asked, so the
  ambiguity is visible and the existing error fires.
- `diag` settles on two identical empty misc-files snapshots taken 250 ms apart. Still true,
  and now the whole of the remaining cost — Phase 3.

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
| 0c | Reject a malformed position at parse time | 0 | **done** |
| 1s | **Spike: what does Roslyn actually load?** | 1 | **done** — see below |
| 1a | Enumerate projects | 1 | **done** (PR #6) |
| 1b | Scope `SourceFiles` to project directories | 1 | **done, redefined** — 1s killed the `diag` half |
| 1c | Readiness means every project loaded | 1 | **done** (PR #6) |
| 1d | Delete the compensating retries | 1 | **done** |
| 1e | Document what `--sentinel` now gives up | 1 | **done** |
| 2v | **Verify `workspace/symbol` ordering on the wire** | 2 | **done** — Roslyn ranks |
| 2a | `sym`: truncate before sorting | 2 | **done** |
| 2b | README layout tree omits `App/Square.cs` | 2 | **done** |
| 3 | Whole-workspace `diag` throughput | 3 | not started |

Phases 0 and 2 are small and independent; they can land together. Phase 1 is the real change
and should be its own PR. Phase 3 is gated on measurement and should be a third.

## Phase 0 — CLI hygiene — **done**

Independent of everything else. Do it first so the gate is green from a clean base.

All three items landed 2026-09-05; 0a and 0b as `1e36173`, 0c from the review of it. What the
plan did not say, and is worth keeping: the position check has to live in `Options.Parse`
rather than beside `LocateAsync`, so it applies to **every** command's argument. `PositionSpec`
cannot match a bare symbol name, so there are no false positives, and gating it per command
would reintroduce exactly the late-validation problem 0a exists to remove. 0c is the same
argument applied to the shapes `PositionSpec` does not match either.

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

### 0c. Reject a malformed position at parse time

Found reviewing 0a. `TryParsePosition` now throws once `PositionSpec` **matched**, but a
file-shaped argument it could not match at all still fell through to the symbol resolver —
the exact outcome 0a exists to remove, and the slow one:

```
$ csx refs Core/Greeter.cs:+1:2 --root fixture
csx: no symbol matched 'Core/Greeter.cs:+1:2'          # after ~40 s of start and readiness
```

`\d+` rejects `+1`, `-1`, `1.5` and an empty coordinate, so all of those took the fallthrough.
`OutlineTargetAsync` was already guarded by `LooksLikePath` (`Program.cs:207`); `LocateAsync`
(`Program.cs:284`) never was.

Fixed in `Options.Parse`, beside the 0a check and for the same reason — before `StartAsync`,
so it costs nothing. `ValidatePosition` (`Program.cs:~421`) takes the colon in the argument's
**last path segment** as the signal, which is what keeps `C:\dev\x.cs` out of it: a drive
letter puts its colon in the first segment. A bare symbol name has no colon at all.

The 0a call site was `TryParsePosition(argument, out _, out _, out _)` — a `Try*` method
invoked for its throw, with three discards and a dropped `bool`. It is now
`ValidatePosition(argument)`, which is where the new check lives.

Also folded in: `Int` reported `--max 99999999999` as "needs an integer", which is a lie about
the input and inconsistent with the position path's "out of range". It now distinguishes the
two, matching `Coordinate`'s wording.

```
{"name":"malformed-position-reports","args":"refs Core/Greeter.cs:+1:2 --root fixture","exit":"1","expect":"is not a position: expected file:line:col"}
```

**The `expect` field cannot contain a single quote.** `run.sh:95` rewrites `'` to `"` before
matching, so the existing cases can assert JSON keys — which means the quotes this message puts
around the argument are unassertable, and the case pins the tail of the message only.

**Not fixed, and out of scope:** `csx refs Core/Greeter.cs` — a bare path, no colon — still
reaches the symbol resolver and answers "no symbol matched". `refs` has no `LooksLikePath`
guard, unlike `outline`. It is the same shape of complaint but a different fix, since `refs` on
a file with no position has no sensible answer to give.

## Phase 1 — project scoping

The real fix, and **complete**. 1a and 1c landed in PR #6; 1s, 1b, 1d and 1e landed after it —
1b cut down to what 1s's measurements support, which is the one place this plan was wrong about
its own diagnosis rather than about a detail.

### 1s. Spike: what does Roslyn actually load?

**Answered in full — see "What 1s answered" and the misc-files section above.** The plan below identifies
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
`ROADMAP.md`'s verified facts ~~but not yet in `DESIGN.md`; that write-up is still owed, fold it
into 1b or the close-out~~ — **and in `DESIGN.md`, which carries the whole of it**: the server
cannot be asked, the scan is an approximation erring deliberately towards over-inclusion, a root
with no `.csproj` fails immediately, and `--sentinel` bypasses the scan. So the approximation is
a documented property of `csx`, which is what this item asked for.

~~Note also that `fixture/` has **no `.sln`** and loads fine, which contradicts
`skill/SKILL.md`'s first troubleshooting entry. Re-derive that claim before 1b leans on it.~~
**Re-derived and false:** `fixture/Fixture.slnx` exists, so it was never a counterexample. That
entry already covers `.slnx` and stands.

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

### 1b. Scope `SourceFiles` to project directories — **landed, cut down**

**What shipped, and what did not.** 1s measured the `diag` half away (see above), so
`SourceFiles` is unchanged and `DiagAsync` still walks every `.cs` under the root. What landed:
`InferSentinels` throws `no .csproj under <root>; point --root at a workspace or pass
--sentinel` instead of falling back to a root-scoped sentinel that could never resolve, and the
scan runs in `RunAsync` **before `StartAsync`**, so that error costs 53 ms and no server — the
same late-validation lesson as 0a. Pinned by `no-project-root-reports`.

Two follow-ups from review of that commit, both landed:

- **The command name is validated in `Options.Parse` too.** Moving the scan ahead of
  `StartAsync` meant `csx bogus --root <dir with no .csproj>` reported the missing project
  instead of the typo — whichever check ran first answered. `DispatchAsync`'s default branch is
  now `UnreachableException`. Pinned by `unknown-command-reports`, which asserts an unquoted
  substring because `run.sh` rewrites `'` to `"` inside `expect`.
- **A nested project can no longer satisfy its parent's sentinel.** `Candidates` took the
  recursive `SourceFiles(d)` and `LspClient.Under` counts any hit below the directory, so with
  `A/B/B.csproj` inside `A/`, B loading could mark A ready — the every-project-loaded guarantee
  failing in exactly the quiet way it exists to prevent. Candidates now exclude files under a
  nested project. Not reachable in the fixture, so not pinned; it was carried in from 1a.

The original plan follows, struck, because its reasoning is what the measurements overturned.

<details><summary>Original 1b plan — superseded</summary>

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

</details>

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

### 1d. Delete the compensating retries — **landed**

`QuerySymbolsAsync` is gone: `MatchSymbolsAsync` and `SymAsync` each fire one
`workspace/symbol` query, `Distinct` stays extracted, and `sym-no-match-fails` stops costing
40 requests and 10 s. `SettleAsync` and the diagnostics settle were kept, as planned. Three
consecutive `probes/run.sh` runs after the deletion: 50/50 each, no case differing between
runs.

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

### 1e. Document what `--sentinel` now gives up — **landed**

`README.md` and `skill/SKILL.md` now say plainly that `--sentinel` is an escape hatch that
drops the all-projects-loaded guarantee, `DESIGN.md` gained a `Readiness` section covering the
all-projects predicate and the `.csproj` scan's limits, and the timeout message names projects
that contributed no sentinel at all (`Not probed at all, for want of a type declaration: ...`).
`README.md`'s failure-mode table also lost its two stale rows: the `projectInitializationComplete`
ordering and the deleted 10 s grace.

<details><summary>Original 1e note</summary>

Debt from 1c, and the only user-visible contradiction it left. `--sentinel` replaces the
per-project set with one root-scoped probe, so a run that passes it has exactly the weak
readiness that produced every incomplete answer in PR #6 — and `README.md` and
`skill/SKILL.md` still describe it as a neutral override. Say plainly in both that it is an
escape hatch for when inference cannot read the workspace layout, and that it drops the
all-projects-loaded guarantee. While there, make the readiness timeout message name projects
that contributed no sentinel at all.

Not probeable for the same reason 1c is not.

</details>

## Phase 2 — output

### 2v. Verify `workspace/symbol` ordering — **done, and 2a's premise holds**

Measured 2026-09-05 against 5.12.0-1.26426.8 on a scratch copy of `fixture`, with the five
`OrderBy`/`ThenBy` lines in `WriteSymbols` temporarily commented out so the raw order reached
stdout. Recorded in `ROADMAP.md`'s verified-facts section.

**The first design of this experiment was confounded and review caught it.** Three names in one
file cannot separate relevance ranking from declaration order, and three names in one project
cannot separate global ranking from per-project ranking concatenated in project order. What was
actually run puts the *exact* match in a different project from the two inexact ones:

- `Core/AbcZed.cs` → `AbcZed` (substring), `Core/ZedHelper.cs` → `ZedHelper` (prefix),
  `App/Zed.cs` → `Zed` (exact).
- Query `Zed`. Global relevance → `Zed, ZedHelper, AbcZed`; per-project then relevance →
  `ZedHelper, AbcZed, Zed`; document order → `AbcZed, ZedHelper, Zed`; alphabetical →
  `AbcZed, Zed, ZedHelper`. Four hypotheses, four distinct strings.
- Observed: **`Zed, ZedHelper, AbcZed`**, identical across two runs. Global relevance ranking.

Two preconditions were checked before reading anything into the order, and both matter. The run
is void unless all three names come back — an incomplete result set is the silent exit-0 failure
`CLAUDE.md` describes, and here the measurement *is* the membership and order, so a missing
`AbcZed` would leave `Zed, ZedHelper`, where relevance and alphabetical agree, and 2a would have
been dropped on a measurement failure rather than on evidence. And the query was run twice and
required to answer identically. The structural reason only the substring match discriminates:
the display sort's first key is `Symbol.Name`, and an exact match is always
`OrdinalIgnoreCase`-≤ every prefix match of itself.

<details><summary>Original 2v note</summary>

**The premise of 2a is unverified.** The claim is that Roslyn answers a partial query in
relevance order, which is plausible (NavigateTo ranks exact over prefix over substring over
fuzzy) but was *not* measured — `Output.WriteSymbols` sorts before anything is printed, so the
raw order is not observable through `csx` today. Establish it first: log or temporarily bypass
the sort, fire a partial query with clearly different match qualities, and record the finding in
`ROADMAP.md`'s verified-facts section with the version and date.

If Roslyn does not rank, 2a is pointless and should be dropped rather than implemented.

</details>

### 2a. `sym`: truncate before sorting — **done**

`WriteSymbols` now projects into `hits`, `Take(max)` in the server's order, and sorts only the
kept set. `hits.Count` still carries the untruncated total into both the `... N more` line and
the JSON `count` / `truncated`, so the `Take` stayed inside `WriteSymbols` exactly as planned.

Two things the plan for this item did not say:

- **The comment above the sort keys had to be rewritten, not kept.** It justified the
  containerName key by saying that without it "the winner of `--max` truncation would be
  whichever order the server happened to answer in" — which after 2a is the behaviour by
  design. The key still earns its place, but for display determinism between two colliding
  generated documents, and it now says so.
- **The re-pinned `sym-truncates` did not have to lose all its row coverage.** Only the *path*
  is server-dependent. Both survivors are named `Area` and both are methods whichever pair the
  server keeps, so with two rows shown `kindWidth` is 6 and `nameWidth` is 4 and the row always
  begins `method  Area`. `run.sh` splits `expect` on `|` and requires every part, so that
  assertion is free.

<details><summary>Original 2a plan</summary>

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

</details>

### 2b. README layout tree — **done**

The tree listed `Core/Shape.cs` but not `App/Square.cs`, and the cross-project split is the
whole point of that fixture. It listed other `App/` files, so this was an omission, not a
convention.

**`Ambient/Stray.cs` was missing from the same tree**, and the close-out checklist below claimed
it was already there. Both lines were added. The other half of that same checklist claim — that
the case count is still owed — was stale too: `README.md` already says fifty-three cases,
forty-nine rows, which is correct, and Phase 2 re-pins one row without adding any.

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
- `probes/run.sh` green — 44 legs at the start of Phase 0, 48 after it (0a added three,
  0c a fourth), **53 after Phase 1** (three non-project-file cases,
  `no-project-root-reports`, `unknown-command-reports`), and **still 53 after Phase 2**:
  `sym-truncates` was re-pinned, not added to. `premature-query-fails-loudly` was the other candidate and survived 1c
  unchanged, because the rewritten message kept both asserted phrases.
- ~~`CLAUDE.md`: the "sentinel resolving no longer implies every project is loaded" bullet~~ —
  **done in `1796a31`.** It now describes the per-project model, keeps the `SettleAsync` /
  `MetadataAsSource` half, and carries the rule against matching a hit to a project by
  `containerName`.
- `DESIGN.md`: **done** — the Phase 1 `Readiness` section carries the all-projects predicate,
  the `.csproj` scan's limits, what `--sentinel` gives up, and why `diag`'s walk is deliberately
  not scoped; the `sym` paragraph now carries 2a's cap-ordering note.
- `skill/SKILL.md`: **done** — `--sentinel` is described as an escape hatch, and the first
  troubleshooting entry now also covers a root with no `.csproj` at all. Its no-`.sln` premise
  was **re-derived and stands**: `fixture/` has `Fixture.slnx`, so it was never a
  counterexample.
- `ROADMAP.md`: **done** — 2v's finding is in the verified-facts section and Milestone 4's
  "output tuning" item records 2a. The generated-document-label limitation stays deferred.
- `README.md`: **done.** The `--sentinel` wording and the two stale failure-mode rows landed
  with 1e; `Ambient/Stray.cs` and `App/Square.cs` landed with 2b. The case count needed no
  change — `README.md`'s fifty-three / forty-nine was already right, and 2a re-pins a row
  rather than adding one. This entry previously claimed the opposite of both facts.
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
