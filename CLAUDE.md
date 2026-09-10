# cslq

A CLI (`cslq`) that gives coding agents semantic C# queries over Microsoft's official
`roslyn-language-server`, with a cron-driven update loop gated by a probe suite.

**Read `ROADMAP.md` first** for milestone state and acceptance criteria, and `DESIGN.md` for the
output rules every command follows and the decisions that are already settled. The README holds
the user-facing detail and the evidence behind the dependency choices.

## Commands

```
dotnet build src/Cslq/Cslq.csproj          # build
dotnet test --project tests/Cslq.Tests    # the unit tests alone, ~1s (never with --nologo)
./probes/run.sh                          # the gate: unit tests, restore, build, ready, every case
./src/Cslq/bin/Debug/net10.0/cslq refs Greet --root fixture
```

`probes/run.sh` is the gate, and it runs `tests/Cslq.Tests` first. Run it before claiming
anything works: the unit tests alone prove nothing about the server's behaviour.

## Things that will cost you a session if you rediscover them

- **Positions are UTF-16 code units** and the server does not negotiate otherwise. .NET string
  indices are already UTF-16, so naive indexing is correct — counting runes or UTF-8 bytes is the
  bug. `LspClient.InitializeAsync` asserts the encoding and refuses to run if it ever changes.
- **Do not add `Console.OutputEncoding = UTF8`.** It looks required — this machine's console is
  code page 850 — but it fixes nothing and was tried and reverted. .NET writes a real console
  handle with `WriteConsoleW`, so the code page never applies, and redirected stdout is already
  UTF-8. Verified both ways against the emoji fixture line. Mojibake in a PowerShell pipeline
  (`cslq refs ... | Select-String`) is PowerShell decoding our bytes with its own
  `[Console]::OutputEncoding`, which nothing `cslq` sets can change.
- **The non-ASCII probe cases are the first host-dependent ones.** They no longer go green on
  CI and red in Git Bash: since Milestone 5 item 5 `probe.yml` is a `fail-fast: false` matrix
  over `ubuntu-latest`, `windows-latest` **and** `macos-latest`, where the job runs
  `./probes/run.sh` under the runner's `bash` — which on Windows is Git Bash, the shell that
  raised the encoding question in the first place. `bump.yml` and `release.yml` stay
  `ubuntu-latest` alone; they gate a publish, and the platform coverage lives on every PR.
  Also note `File.ReadAllTextAsync` substitutes U+FFFD for invalid
  bytes rather than throwing, so a fixture file corrupted to a non-UTF-8 encoding would desync
  the `didOpen` text from what Roslyn parses off disk — silently, except that
  `non-ascii-refs-position` then fails.
- **Server flags live only in `src/Cslq/ServerArgs.cs`.** The thin client forwards options it does
  not recognise straight through to the server, so a renamed flag produces no error at all. The
  probes are the only thing that catches it.
- **Roslyn's `containerName` is localised display text** (`in Greeter (project Core (net10.0))`),
  not a namespace path, and nothing may match against it.
  `DOTNET_CLI_UI_LANGUAGE=en` is pinned on the server so it does not vary by machine locale.
- **`hover` cannot verify a dotted target, and the reason is invisible on a type.** Its first
  line is fully qualified for types only — `class Fixture.Core.Greeter` — while a member prints
  the minimal form, `string Greeter.Greet(string name)` and `int Volume.Litres { get; }`.
  Measured on the fixture 2026-09-09. So the obvious cheap check (one `hover` per candidate,
  compare its first line) passes every type test you would write and silently cannot see a
  member's namespace, which is the case that was wrong. `Targets` reads the chain off
  `textDocument/documentSymbol` instead, where the namespace node's name is already dotted.
- **Never gate readiness on the symbol being queried.** An absent symbol then looks identical to
  a workspace that has not loaded, and the caller waits out the whole timeout for a typo.
- **A query fired before load returns empty, not an error.** Never `sleep`; poll a sentinel
  that must resolve, from the first round. Once `workspace/projectInitializationComplete` has
  fired **in this process**, the wait is bounded to a further 20 s (`LspClient.PostLoadGrace`)
  and then fails — a project whose only type sits in an `#if false` branch is the shape it
  exists for, ordinary `.csproj` and all, so no skip rule sees it. That notification ends
  *this client's own* reload on every attach, cold and warm alike (see the daemon bullet), so
  the bound is meaningful on both. It is not "everything has loaded": measured 2026-09-10,
  projects kept resolving for 6-8 s after it on CommunityToolkit, which is the length the
  grace has to cover and the reason it is 20 s rather than zero. **Never bound it when the
  notification has not fired**: that state means the load this client asked for has not
  finished, and the incomplete-answer window is inside it.
  `exhausted-candidate-fails-after-load` is the leg.
- **A `%XX` in the root path is MSBuild's problem, not the URI layer's.** A root like
  `.../pct%20x` never becomes ready, and the obvious suspect is wrong: `new Uri(path)` escapes
  the literal `%` to `%25`, so `PathUri.FromPath` / `ToPath` round-trip it exactly (pinned by
  `PathUriTests`). What fails is MSBuild, which unescapes `%XX` in the paths it reads — plain
  `dotnet restore` on such a tree already errors with `pct x` — so nothing `cslq` does can fix
  it. A `%` not followed by two hex digits is fine end to end: `cslq ready` on `pct%zzx`
  resolves in ~4 s.
- **Roslyn will not answer for documents it does not consider open** — `didOpen` first. The
  exception is a source-generated document: the server owns it, answers without a `didOpen`,
  and there is no file to read the text from. `OpenAsync` skips them.
- **Source-generated locations arrive under `roslyn-source-generated:`, and every path
  helper lies about them.** `new Uri(u).LocalPath` does not throw — it returns
  `/BuildInfo.g.cs`, which then renders as a confident wrong answer with no context lines and
  exit 0. Route every location through `PathUri.Display`. The authority guid, the
  `documentId` and `assemblyPath` in that URI all change between runs and machines, so
  **never assert on the raw URI** — only `hintName`, `assemblyName`, `assemblyVersion` and
  `typeName` are stable. Text comes from `workspace/textDocumentContent`, which the server
  implements without advertising a `textDocumentContentProvider` and answers whether or not
  the client declares the matching capability (verified both ways). The older
  `sourceGeneratedDocument/_roslyn_getText` no longer exists.
- **A decompiled metadata location is a *file* URI, so every path helper answers it happily
  with a machine-absolute temp path.** `<temp>/MetadataAsSource/<guid>/DecompilationMetadataAsSourceFileProvider/<guid>/Console.cs`
  is a real file that really exists, which is why this is worse than the generated-URI trap:
  nothing throws, the context lines render correctly off disk, and the answer is a path no other
  machine has. `PathUri.Display` therefore checks `IsDecompiled` **before** the file branch, not
  after — put the checks the other way round and `Relative` hands the caller the temp path. The
  assembly is not in the URI at all: it comes off the `#region Assembly <name>, Version=...`
  header Roslyn writes at the top of the document, behind a byte-order mark, so the
  `<metadata>/<assembly>/<TypeName>.cs` label needs a document read the way a generated
  document's label needs a request. Only the file name is stable — both guids are per server
  instance — so **never assert on the raw path, and never assert the line and column of a
  framework declaration**: those move with the reference assembly.
- **`PathUri.AnyUnder` has to exclude decompiled and generated URIs explicitly, and the
  decompiled one is the trap.** It is the discriminator that keeps `SettleAsync`'s guard: a
  decompiled answer is stale only if the workspace also declares that type. But a decompiled URI
  is a path under the *temp directory*, so a plain "is this under the root" prefix test would
  call every framework answer stale for any workspace living under temp — which is every
  workspace the unit tests build. `PathUriTests` pins it.
- **The framework `def` cannot be pinned by a `cases.jsonl` row, because both its failure modes
  are invisible to a substring.** What went wrong was an absence (the temp path must *not*
  appear) and a duration (the guard burned its whole 10 s budget and then returned the answer it
  already had). `expect` can only require a substring, and the right message passes just as well
  after twelve seconds — so `framework-def-is-labelled-and-does-not-stall` is a scripted leg
  with an elapsed check, like `no-solution-root-fails-fast`. It is warm by construction: the
  `def-framework-member` row above it is what makes Roslyn write the decompiled document, which
  cold costs about six seconds on its own. Keep it after the rows.
- **`textDocument/hover`'s output shape is chosen by the client capability, and plaintext is
  what makes it printable.** With `contentFormat: ["markdown"]` — or the node missing, which is
  what the deprecated `MarkedString` forms are for — Roslyn answers fenced code blocks and
  `&nbsp;` runs that an agent then has to undo. `HoverCapabilities` declares
  `["plaintext"]` alone, and the answer is then the signature on line one and the doc summary
  after it. It carries the parameter types, so `signatureHelp` is deliberately not wired.
- **A generated document's URI names the generator, never the project consuming it.**
  `assemblyName`, `typeName` and `assemblyPath` are all the generator's; the only thing
  separating two projects' copies of one generated document is the authority guid, which is
  regenerated on every workspace load. `textDocument/_vs_getProjectContexts` is what answers
  it — a VS protocol extension, not LSP, which the server neither advertises nor gates on a
  client capability. Its `_vs_id` is `<projectId guid>|<absolute .csproj> ($<tfm>)`: read the
  path half only. `_vs_label` (`"Core (net10.0)"`) is display text, same class of thing as
  `containerName`, and is not parsed. The label is
  `<generated>/<project dir>/<assembly>/<generator full type name>/<hintName>` — the generator
  type is the URI's `typeName`, and it is in the label always because two generators in one
  assembly may emit the same `hintName` — and every rendering path has to go
  through it — the two ambiguity listings in `Program` did not, and printed the same string
  twice under "pick one".
- **A positional request on a multi-targeted document is answered in one context, and
  `_vs_defaultIndex` is not the one to blame.** Measured 2026-09-10 on `fixture2/Multi`
  (`net10.0;net9.0`): `_vs_defaultIndex` was `0` in 6 of 6 runs while the *order* of the
  `_vs_getProjectContexts` array varied per attach, and the unqualified answer followed
  `contexts[0]` in 6 of 6. So `hover Only9` answered 4 times in 8 and `project` alternated
  `net10.0`/`net9.0` — one bug, not two. The fix is `_vs_projectContext` in the
  `TextDocumentIdentifier` (36 of 36 forced right, 12 of 12 forced wrong answered empty), and
  it comes with three traps:
  - **The `_vs_id` must go back verbatim and in the same attach.** Roslyn matches on it alone,
    and the projectId guid inside it is regenerated on every attach, so a dump-then-use across
    two processes sends a stale id. `LspClient.ContextsAsync` caches per document per attach
    for exactly that reason.
  - **A fixed context alone is a worse bug than the coin flip.** A type inside `#if NET9_0`
    does not exist in the `net10.0` context, so pinning the first context turns 4 misses in 8
    into 8 in 8. `AskEachAsync` asks the contexts in `Contexts.Order`'s order and stops at the
    first that answers; deleting the retry to "make it deterministic" is the regression this
    bullet exists to prevent.
  - **A server that stops honouring the field fails silently.** An unrecognised member of a
    request payload is ignored — unlike `_vs_getProjectContexts`, which answers or fails — so
    the symptom is T-27's intermittency returning, an answer that is right most of the time.
    `tfm-excludes-the-other-branch` is the only guard: it asserts `hover Only10 --tfm net9.0`
    finds **nothing**, which can only hold if the context was honoured. It is an absence, so
    do not "fix" it into an assertion about output.
  `refs`, `impl`, `diag` and `outline` answer with a union instead — see the next bullet.
  `fixture2/Multi` is the fixture: `Both` in both contexts, `Only10`/`Only9`
  one branch each, and a CS0029 in `TfmError.cs` that exists only in `net9.0`. Nothing builds
  it, so `dotnet build fixture2/Fixture2.slnx` fails by design — `run.sh` builds `Alpha` and
  `Beta` alone, and readiness is unaffected because `workspace/symbol` is context-independent
  (it listed both conditional declarations in 8 of 8 runs).
- **A set-valued answer must ask every context, and the merge has one trap that is invisible
  until a dotted target fails.** `hover` and `def` answer with one thing and stop at the first
  context that answers; `refs`, `impl`, `outline` and `diag` answer with a *set*, so they ask
  all of them (or the `--tfm` subset) and union — a reference inside an `#if NET9_0` consumer
  exists only in that context, and stopping early drops it at exit 0. Three things to keep:
  - **`Outline.Merge` keys on name, kind and identifier position but must take the *widest*
    range.** Every context parses the same text, so those three agree — but a declaration's
    `range` does not: on `fixture2/Multi/Conditional.cs` the namespace ends at line 6 in
    `net10.0` and line 13 in `net9.0`, each context seeing only its own branch. Keeping the
    first view's range put `Only9` outside its own merged parent, and `Targets.Chain` walks
    down by full-range containment, so `def Fixture2.Multi.Only9` went from failing 2 runs in 6
    to failing **4 of 4** — a "fix" that made it deterministically wrong. `Outline.Widen` is
    the union of the extents; `OutlineMergeTests` pins it.
  - **The dotted-target chain reads the union and ignores `--tfm`.** `SelectAsync` used one
    context's `documentSymbol` tree, which is why `def Fixture2.Multi.Only9` missed while the
    bare `def Only9` was already deterministic after batch 7 round 2. Constraining the *lookup*
    by `--tfm` would hand that bug back.
  - **The folds are what keep the union from duplicating.** `refs`/`impl` rely on
    `Output.WriteLocationsAsync`'s existing fold on rendered label plus range, before `--max` —
    the same fold that closed T-30 — and `Program.Reports` folds diagnostics on position,
    severity, code **and message**, the message included because serilog answers
    `Substring can be simplified` in one context and `Slice can be simplified` in another at
    one position, and those are two findings rather than one.
  The cost was measured before the rule was adopted, not after: a whole-tree `diag` walk on
  `fixture2` (6 documents, 3 two-context) went 2694-2956 ms to 2827-3030 ms warm, and on
  `fixture` (4 single-context documents) 2690-2925 ms to 2534-2732 ms, i.e. nothing. A
  single-context document pays only the one `_vs_getProjectContexts` request every
  context-bound command already makes.
- **`fixture2/` is the two-consumers-of-one-generator shape, and it cannot live in
  `fixture/`.** `App` references `Core`, so a second copy of the generated type collides at
  the use site with CS0433. `fixture2/Alpha` and `fixture2/Beta` reference nothing of each
  other's, so both compilations hold `Fixture2.Generated.Stamp` happily. `run.sh` restores and
  builds both consumers for the same reason it builds `fixture/Core`: an unbuilt analyzer
  contributes nothing, silently. It is excluded from `Cslq.slnx`, so `--root .` never loads it.
- **An unbuilt source generator produces nothing, silently.** With `fixture/Gen/bin` absent
  the workspace still loads, the sentinel still resolves, and only the generated symbol is
  missing — no error, no diagnostic, no CS9057 on the wire. `run.sh` builds `fixture/Gen`
  with `-c Debug` pinned, because the design-time build resolves the analyzer from
  `Gen/bin/Debug`; building it Release leaves that path stale.
- **A malformed request payload takes the server's whole queue down.** Sending
  `workspace/textDocumentContent` with `{textDocument:{uri}}` instead of `{uri}` returned
  `TaskCanceled`, and every later request then failed with `-32000: Server was requested to
  shut down`. Payloads are hand-rolled in `Protocol.cs`, so a shape mistake is silent and
  then fatal rather than a clean error.
- **A `.cs` file no project compiles is half-invisible, and the halves are not the ones you
  would guess.** `workspace/symbol` does not index it and `textDocument/diagnostic` reports
  **nothing** for it — but `outline` answers, off the syntax tree. So `cslq outline` on such a
  file works while `cslq sym` on the type it declares exits 1, and scoping `diag`'s file walk to
  project directories would suppress no noise whatsoever. The same file **linked in** with
  `<Compile Include="../Elsewhere/File.cs" />` is fully indexed and does report, so that scoping
  would silently drop real errors. Measured 2026-09-05; the reasoning is in `DESIGN.md`.
- **Sentinel inference reads prose, and neither taking every match nor capping at three saves
  it.** The candidate regex matches `class|struct|record|interface|enum` followed by a word, so
  the doc comment "identifying the class and assembly context" yields the candidate `and` — and
  one sentence can yield `and` / `of` / `for` and fill all three slots, leaving a project probed
  only by words nothing can resolve while readiness burns its entire timeout. `cslq ready` on
  OrchardCore failed this way after 900 s on fifteen projects. `Program.NonCode` therefore
  strips comments and string literals before the declaration regex runs. Keep the fallback
  chain anyway: the regex still reads types out of `#if` branches and uncompiled files. The
  fixture cannot reproduce the original failure — it needs prose in a doc comment above the
  only declaration in a single-file project.
- **The nested-project scoping is `Sentinel.Accepts`, and a probe case cannot pin it.**
  A fixture project nested under another only goes red without the scoping when the nested one
  happens to load *first* — a load-order race, so the case would pass on a broken build most
  runs. It is a pure predicate over a URI instead, pinned by `SentinelScopingTests`. Both
  fixtures are flat, so every `Nested` list is empty in every probe case.
- **A candidate must resolve inside the project's *own* directory, nested projects excluded.**
  `LspClient.Under` counts any hit below a directory, so with `Web/` and `Web/Tests/` both
  declaring `Program` — the ordinary shape — Tests loading marks Web ready and the
  incomplete-answer-at-exit-0 bug is back. Both halves are needed: `Program.Candidates` skips
  files under a nested project, and `Sentinel.Nested` scopes the hit. Two `.csproj` in **one**
  directory cannot be separated by any path scoping and are a documented limit, not a bug to
  fix here.
- **`diag` pulls once, and the settle loop that used to wrap it is gone on evidence.**
  `textDocument/diagnostic` does not answer from the misc-files state and then correct itself —
  it **blocks until the document is bound**. A cross-project error opened as the first document
  in a never-used daemon returns the right code on pull #1 (~4.2 s), and the second pull (~0.7 s)
  never once differed across six whole-fixture runs, cold and warm. The leg that pins this names
  one file: a whole-fixture `diag` opens three other documents before `App/TypeError.cs`, so it
  never observes a first document at all. If a bump starts answering
  early, `diag` is where it shows up. Do not restore the loop without re-measuring: the old one
  could not have caught that case anyway, since two equally-wrong pulls agree.
- **`cslq` never sends `didClose`, so daemon document state outlives the client.** `_open` is
  per-process and says nothing about what the shared daemon still has open. Any measurement of
  first-open behaviour must use a fresh `ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME` or
  `--no-daemon`; a warm daemon shows "no divergence" for the wrong reason. The same effect is
  worth ~4 s per document: the first `diag` of a file in a fresh daemon cost 7.6 s against 3.3 s
  warm.
- **`cases.jsonl` order is load-bearing for the `diag` cases, invisibly.** `run.sh` scopes one
  daemon for the whole suite, and cases 1-12 never open `App/TypeError.cs`. So
  `deliberate-error-diag` is the first `didOpen` of that document and the only leg that observes
  a cold document at all; by the time `deliberate-error-diag-workspace` and
  `non-project-file-no-diagnostics` run it is already open and warm. Reordering the file, or
  running one case against an ambient daemon, disarms that coverage with nothing going red.
- **`LspClient.StartProcess` clears `HANDLE_FLAG_INHERIT` on our own std handles before every
  `dotnet` launch, and that is what makes captured output safe.** Windows `CreateProcess` is
  called with `bInheritHandles=TRUE`, so cslq's stdout reached the thin client and, through it,
  the *daemon* — which outlives the call. A capturing harness then waited for EOF on a pipe the
  daemon still held: the launching call blocked for the whole keepalive and returned with the
  daemon dead, so every call was a launching call. Measured on the fixture, keepalive 20 s:
  `out=$(cslq ready --root fixture)` 25 s before, 4 s after. Best effort by design — a process
  with no console has invalid std handles and must not be broken by this — so a regression is
  silent, and `daemon-survives-captured-stdout` (Windows-only, `probes/stdout-capture.cs`) is
  the only thing that catches it. **The trap survives one level up:** launch `cslq` from a
  process whose own stdout is an inheritable pipe — `dotnet run probes/stdout-capture.cs`
  under `$(...)` — and *that* pipe is inherited into cslq as an ordinary handle and travels on
  into the daemon. Redirect an intermediary to a file and `cat` it; never capture it. The
  instance that reaches users is a **PowerShell-hosted harness** (Claude Code on Windows,
  Actions `shell: pwsh`): keepalive 10, bash `out=$(cslq ready)` 4 s against bash
  `out=$(pwsh -c '$x = & cslq ready; $x')` 18 s. So the launch must be an unredirected
  `cslq ready`, or `--no-daemon`, and SKILL.md carries that qualifier because it ships alone.
- **Nothing in the suite covers Ctrl+C, and MSYS `kill -INT` does not test it.** From Git Bash
  it terminates the process without ever raising a console control event, so the handler never
  runs and the 130 you see is bash's own signal status. To exercise the real path, launch `cslq`
  with `CREATE_NEW_PROCESS_GROUP` and send it `CTRL_BREAK_EVENT` with
  `GenerateConsoleCtrlEvent` — a throwaway file-based app does it in 40 lines. Measured
  2026-09-06: `cslq: interrupted.` and exit 130 within 62 ms, with the fallback run's own server
  tree gone.
- **`dotnet test --nologo` runs zero tests and exits 5.** `global.json` opts into the MTP mode
  of `dotnet test` (`"test": {"runner": "Microsoft.Testing.Platform"}`), where `--nologo` is no
  longer a build-only flag. It fails loudly, but it reads as a broken test project rather than
  a bad flag, and every other `dotnet` call in `run.sh` passes `--nologo`, so it is the obvious
  thing to add. Do not.
- **Project discovery reads the root's solution, and only the root's — there is no `.csproj`
  scan fallback left.** `ProjectDirectories` takes the project list from the single
  `.sln`/`.slnx` sitting at the top of `--root`. **No solution at all is an error, and so is
  more than one**, both thrown before the server starts: `--autoLoadProjects` never discovers a
  bare `.csproj`, and the scan that used to answer a two-solution root is over-inclusive, so a
  project neither solution loads gets a sentinel that can never resolve and readiness burns the
  whole timeout (3 of 3 tester runs). So every temp tree a unit test builds needs a solution,
  which `Workspace` writes for it, and the tests that write two on purpose are now asserting
  the error. `Solutions` still caps at two, which is all the message needs. A solution one
  directory down does not count — which is what keeps `fixture/Fixture.slnx` from narrowing a
  root above it, and what makes `--root fixture` and `--root .` two different workspaces rather
  than one. `.slnf` is not read. Two `.csproj` in one directory are still indistinguishable, and
  still a documented limit. It is *half* the fix for the OrchardCore template failure above —
  see the next bullet for the other half — and scoping `--root` below the templates was only
  the workaround.
  Parse failures go through `CslqException`: `Main` catches that and nothing else, so a
  hand-edited `.slnx` that no longer parses would otherwise exit 127 with a stack trace.
- **A nesting boundary is any `.csproj` on disk, listed in the solution or not, and reading the
  solution alone did not fix OrchardCore.** `OrchardCore.slnx` **lists** the wrapper
  `src/Templates/OrchardCore.ProjectTemplates` and excludes only the five `content/*/*.csproj`
  under it, so the wrapper's own candidate scan still read types out of `content/**/*.cs` —
  `dotnet new` template text Roslyn never binds — and `cslq ready` on the full root still burned
  900 s. `Program.Probe` therefore computes both the `Candidates` file filter and
  `Sentinel.Nested` from the *on-disk* `.csproj` set (`ScannedProjects`, unioned with the
  discovered list so a project the solution places outside the root is still a boundary), and
  reads the project's own `.csproj` through `ProjectSources`, which is pure text and unit-tested
  as such. Two things come off it, and neither is visible to a source scan:
  - `EnableDefaultItems=false` (or `EnableDefaultCompileItems=false`) with no
    `<Compile Include>` means the project compiles **nothing** — the wrapper is exactly this —
    so it gets no candidates however many `.cs` files sit under it.
  - Sources linked in from outside the directory — a `*.projitems` import, or every
    `<Compile Include>` pointing outside — plus no `.cs` of its own on disk means the project
    is **skipped**: a hit for a linked document sits under the *source* directory, so
    `Sentinel.Accepts` can never accept it and no candidate could prove it loaded even in
    principle. Fourteen of CommunityToolkit's twenty-six projects are that shape, and
    `ready --json` used to report `projects: 26` as if all had been probed. Measured 2026-09-09:
    `projects: 12`, fourteen skipped, ~18 s.
  A malformed `.csproj` must read as an *ordinary* project rather than throw — one unparseable
  file somewhere in a 226-project tree would otherwise take readiness down — so
  `ProjectSources.Read` swallows `XmlException` and answers "ordinary", which costs only the
  candidates a scan would have found anyway.
- **`ready --json` carries three numbers that must add up, and a project missing from all three
  reads as loaded.** `projects` is the probed count, `skipped` names the linked-only projects of
  the bullet above, and `unprobed` names the ones that own sources but declare no type the
  candidate regex reads — top-level statements only, or only Razor or resources. Together they
  are every project the solution yielded: OrchardCore is `projects: 214`, one skipped and twelve
  unprobed, which is its 227 discovered project directories exactly (measured 2026-09-09,
  ~165 s). Keep the
  two arrays separate and keep both populated. Folding them would put "no sources of their own"
  against projects that have them; dropping either — which is what the first cut of this did
  with `unprobed` — leaves a dozen projects that nothing checked looking exactly like a dozen
  that loaded. `Output.WriteReady` renders all three and `Program.Ready` is the only caller.
- **`cslq` can now be pointed at its own repo, and `Cslq.slnx` is why.** The root solution lists
  `src/Cslq` and `tests/Cslq.Tests` and deliberately excludes `fixture/`, whose `App` does not
  compile on purpose. Put a fixture project in it and `dotnet build` at the root fails by
  design; leave it out and `cslq ready --root .` resolves in ~6 s — the `self-hosted-*` cases.
  `fixture/` keeps its own `Fixture.slnx`, which `run.sh` restores separately.
- **To test `dotnet` off `PATH`, empty `PATH` and set `DOTNET_ROOT` — do not filter it.**
  Stripping "dotnet-shaped" entries out of `PATH` is host-dependent nonsense: on this machine
  `dotnet` lives in `C:\Program Files\dotnet`, on `ubuntu-latest` it is `/usr/bin/dotnet`, and
  dropping `/usr/bin` there breaks everything else. `PATH=""` plus `DOTNET_ROOT` pointed at the
  SDK's real directory (`dirname` of `readlink -f "$(command -v dotnet)"`, through `pwd -W` on
  Git Bash) leaves the apphost able to find its own runtime while `Process.Start("dotnet")`
  fails — which is the failure under test. `dotnet-off-path-reports` is the leg.
- **The server does not restore your projects.** `dotnet restore` before starting it.
- **The daemon is the default, and it changes what "ready" means.** `cslq` connects to the
  shared multi-client daemon unless `--no-daemon` is passed. One daemon serves every
  workspace on the machine, keyed by user and server path rather than by root, and it outlives
  the client that started it. Two consequences bit already:
  - **The daemon shares a process, not a loaded workspace: every attach re-runs the whole
    solution load.** Measured 2026-09-10 on the fixture and on CommunityToolkit (26 projects),
    with the server's log read the only way it can be — Roslyn sends it to the *client* as
    `window/logMessage`, which `Endpoints.OnLogMessage` discards, so a temporary dump there is
    the instrument; the daemon's own stderr says nothing but `Daemon accepted a new client
    connection.`, and `--extensionLogDirectory` is forwarded and writes no file. Each attach
    logs `AutoLoadProjectsInitializer` → `LanguageServerProjectSystem] Loading <solution>` →
    **a fresh `BuildHost` process** → every `.csproj` reloaded →
    `Completed (re)load of all projects in ...`. Fixture ~1.6 s per attach; CommunityToolkit
    ~25-30 s per attach, which is the entire warm wall clock. The only lever cslq holds is not
    sending `workspaceFolders`, and that is measured to leave the client with an **empty**
    workspace rather than the daemon's loaded one — no cslq-side fix exists at the
    `initialize` layer. Consequences, all measured rather than inferred:
    - **`workspace/projectInitializationComplete` fires once per attach**, warm daemon
      included: it marks the end of *this client's* reload, not the daemon's history. (This
      bullet used to say it never fires on a warm attach. That was wrong, but the fix it
      justified was not: `WaitReadyAsync` polls the sentinels from the start rather than
      blocking on the notification, and that is right for a reason the bullet got to by
      accident — the notification comes *after* the load it terminates, so blocking on it
      first would still cost the whole reload.) It fires only for a client that sends
      workspace folders, because that is what triggers the reload it terminates.
    - **The notification does not mean every project is queryable.** On CommunityToolkit
      sentinels resolved progressively *through* the reload — 8 of 12 projects before the
      notification, spread over 20 s — and four kept resolving for a further 6-8 s *after*
      it. So `workspace/symbol` answers partially during a load, in both directions: this is
      the incomplete-answer window, and it straddles the notification.
      `WaitReadyAsync`'s `PostLoadGrace` exists for the tail.
  - **One sentinel no longer proved the workspace was loaded — now there is one per
    project, and `--sentinel` adds to that set rather than replacing it.** Cold load used to close that window by accident, costing a minute; warm attach
    reaches it in seconds. Two symptoms came out of it. Until a project is loaded, Roslyn
    binds a `ProjectReference` to the referenced project's *built assembly*, so `definition`
    answers with a decompiled temp file under `MetadataAsSource` — exit 0, no context lines,
    no relation to the repo; `PathUri.IsDecompiled` spots it and `LspClient.SettleAsync`
    re-asks for up to 10 s. Worse, `refs`, `impl` and `sym` answered *incompletely* — a
    cross-project hit or a whole project's hits simply missing, at exit 0, which no guard
    caught because every one of them watches for an **empty** answer. `WaitReadyAsync` now
    takes one sentinel per discovered project and requires each to resolve to a location under its own
    project directory. **Do not match a hit to a project by `containerName`** — it is
    localised display text. It cost a red CI run and two red gate runs that each failed a
    *different* pair of cases, so treat a lone flake of this shape as this, not as noise.
    An explicit `--sentinel` used to *replace* the whole set with one root-scoped probe, which
    handed exactly this bug back through the escape hatch — testers on OrchardCore got 321 hits
    against 331 from `impl StartupBase`, at exit 0. `Program.Sentinels` now appends the explicit
    probe (`Explicit: true`) to the inferred set and only falls back to it alone when
    `InferSentinels` throws. That probe is not a project: `ready --json`'s three numbers must
    exclude it, and `LspClient.Names` prints it as `explicit sentinel 'X'`.
- **`probes/run.sh` must scope its own daemon.** It exports
  `ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME=cslq-probe-$$` and a 60 s keepalive. Without it the
  gate inherits whatever daemon the developer's session left running — a stale workspace can
  make the suite lie — and the opening `cslq ready` stops being a cold load.
- **The staleness legs write to the fixture.** They rename `Greeter` in
  `fixture/Core/Greeter.cs` and rely on a `trap ... EXIT` to put it back. If `run.sh` is
  interrupted between the rename and the trap, check `git diff fixture/` before believing
  anything else the suite says.
- **A failure during `initialize` must never escape as a StreamJsonRpc exception.** The thin
  client can die before it answers, and StreamJsonRpc then reports nothing but
  `ConnectionLostException` — the server's stderr is the only thing that says why, and it is
  discarded unless the failure is wrapped in a `CslqException` carrying `StderrTail()`. That
  wrapping is what turned "connection lost" into the exact mutex name and `file:line`
  below.
- **To force the silent non-daemon fallback**, hold a mutex named `Global\<pipeName>.client`
  while a client starts — the thin client falls back after about 20 s of waiting for it.
  `probes/hold-mutex.cs` does this and `non-daemon-fallback-reported` is the case. Two traps,
  both of which look like the mechanism not working rather than like a mistake: the mutex must
  be created with `CurrentUserOnly = true` to match the server, and with
  `CurrentSessionOnly = false` or .NET rejects the `Global\` prefix. Either one wrong throws
  `WaitHandleCannotBeOpenedException` / `ArgumentException` instead of contending, so the
  client connects normally and the case fails for a reason that has nothing to do with `cslq`.
  It also needs its own pipe name: the mutex only guards check-server-then-launch, so a client
  that finds a daemon already listening never contends for it. It is the second
  host-dependent case in the suite after the non-ASCII ones — .NET implements named mutexes
  over files on Linux — but it **passed on `ubuntu-latest`** in PR #5, so the file-backed
  implementation contends the same way. Both halves are now watched: the `windows-latest` leg
  of `probe.yml` runs it against the real Win32 named mutex the code was written for.
- **`probes/hold-mutex.cs` is a .NET 10 file-based app, not a project, and that is deliberate.**
  `dotnet run probes/hold-mutex.cs` compiles a bare `.cs` in under a second with no `.csproj`.
  Reach for that before adding a project to the tree for a probe.
- **The unrestored-tool message is localised; the command inside it is not.** `dotnet tool run`
  against a manifest whose tool is missing exits 1 with `Run "dotnet tool restore" to make the
  "<tool>" command available.` — in Portuguese on this machine, since
  `DOTNET_CLI_UI_LANGUAGE=en` is set on the *server* process and not on the one that prints
  this. So `LspClient.NotRestored` matches the quoted `dotnet tool restore` alone, which every
  localisation carries verbatim. To exercise the restore-and-retry path without a 300 MB
  download, move `~/.dotnet/toolResolverCache/1/roslyn-language-server` aside: the package
  stays in `~/.nuget/packages`, so the tool reads as unrestored and the retry costs a second.
- **A successful restore prunes every other server version from the global packages folder,
  and that folder is shared.** `Prune.Run` deletes `roslyn-language-server*/<version>` for every
  version but the pin, in the folder `dotnet nuget locals global-packages --list` names — never
  an assumed `~/.nuget/packages`, and asked **from the manifest root**, where the restore ran:
  NuGet.Config resolves from the working directory, so asked from a repository with its own
  `globalPackagesFolder` it would name a folder the restore never wrote to. Two different
  `cslq` versions in regular use on one machine delete each other's pin in turn; that is a
  documented limit, not a locking bug. It is safe for two reasons that are both measured, not
  assumed: a deleted version reads as unrestored again (`dotnet tool run` answers the same
  `Run "dotnet tool restore"` line with the directory moved aside, so an older `cslq` self-heals),
  and the `.nupkg.sha512` marker is deleted *first*, so a directory a still-running old daemon
  holds half-locked on Windows is absent to NuGet rather than a trusted half-package. Keep the
  prune off the ordinary start path: it costs a `dotnet` launch, and nothing but a restore
  changes what the pin is. To test it live, `mkdir` a fake version beside the real one and run
  `cslq restore`; the unit tests cover the selection.
- **`skills/csharp-semantic-queries/SKILL.md`'s path is load-bearing twice, and both failures
  are silent.** `npx skills add idotta/cslq` — the install the docs now lead with — scans a
  fixed list of container directories (the repository root, `skills/`,
  `skills/.curated|.experimental|.system/`, and each agent's own `.<agent>/skills/`), walking
  three levels into each, and installs the skill under the name of the **containing directory**,
  not the frontmatter `name`. `skill/` singular — what this was until 2026-09-08 — is on none of
  those lists, so `npx skills add` found nothing at all, and even `--full-depth` would have
  landed it at `~/.claude/skills/skill/`. Separately, `probes/cases.jsonl` uses `--root skills`
  as the solutionless-root fixture for `no-solution-root-reports` and `unknown-command-reports`:
  the directory is load-bearing there precisely for having no `.sln`, so renaming it turns two
  cases red for a reason unrelated to the code under test. The file must also stay
  **self-contained** — `npx skills` puts that one file on a machine holding no clone of this
  repository, so a cross-reference to the README from inside it points at nothing.

## C# and .NET rules

This repo is .NET 10 / C# 14: a CLI and a thin LSP client, no UI, no web host, no DI container.

- **`dotnet format` must pass clean.** `dotnet format --verify-no-changes` exits 0 today; keep it
  that way and run `dotnet format` before calling a change done. There is deliberately **no
  `.editorconfig` yet**, so `dotnet format` enforces its own defaults rather than house style —
  match the surrounding code instead of reformatting a file you touched.
- **`DateTime.UtcNow`, never `DateTime.Now`.** Every deadline in `LspClient` is UTC.
- **No `async void`** outside an event handler, and never `.Result` or `.Wait()` — `cslq` is async
  from `Main` down, and a sync-over-async wait here deadlocks against the JSON-RPC read loop.
- **Reach for C# 14 first:** `extension` blocks rather than `this` extension methods, the `field`
  keyword rather than a hand-written backing field, `x?.P = v` rather than an `if` guard. C# 14
  comes free with `net10.0` and `LangVersion` is deliberately unset. `fixture/Gen` is the one
  exception: analyzers must target netstandard2.0, whose default is C# 7.3, so it pins
  `<LangVersion>latest</LangVersion>` explicitly.
- **`[LibraryImport]`, not `[DllImport]`**, if native interop ever appears.
- **Fix root causes and delete what is dead.** Don't preserve a shape for backwards
  compatibility — nothing depends on `cslq`'s internals yet. Simplify rather than layering.
- **Never push to a remote, and never commit unless asked.** `bump.yml` is the only thing that
  opens PRs here.

## Conventions

- **`probes/run.sh` is still the gate, but it is no longer the only suite.**
  `tests/Cslq.Tests` (xunit v3 over MTP) covers the pure logic below the transport -- sentinel
  inference, `Options.Parse`, `PathUri`, `Output.WriteSymbols` -- and `run.sh` runs it first,
  before the fixture restore, because it costs under a second. Anything that needs a live
  server stays in `probes/`; put nothing there that a temp directory and a string could prove.
  The tested members are `internal`, reached through `<InternalsVisibleTo Include="Cslq.Tests" />`
  in `Cslq.csproj`.
- `probes/run.sh` parses `cases.jsonl` with `sed` alone. **No `jq`** — it does not exist in Git
  Bash on the dev machine. (`python` does, 3.14.6, despite what this file used to claim; the
  `sed`-only rule still stands for the GitHub runner.) Keep `cases.jsonl` to four flat string fields.
- **A backslash immediately before `$` in a double-quoted bash string escapes the dollar.**
  `"Global\${pipe}.client"` yields a literal `${pipe}`, not the expansion; `\\` is what
  produces the intended `Global\<pipe>.client`. It cost a probe run:
  `probes/hold-mutex.cs` held a mutex nothing contended for and the fallback case failed with
  no hint of why. The mutex name is now built in C#, where a backslash needs no escape at all,
  and `run.sh` passes only the pipe name.
- `probes/run.sh` must stay mode `100755` in the index. Windows Git has `core.filemode=false`, so
  `chmod +x` does not register; use `git update-index --chmod=+x` if it ever reverts.
- Adding a NuGet package for LSP types is a regression, not a cleanup. See the README.
- A bump PR opened with `GITHUB_TOKEN` gets a `probe.yml` run parked at `action_required` that
  never executes unless a human approves it. `bump.yml` dispatches `probe.yml` on the PR branch
  instead — `workflow_dispatch` and `repository_dispatch` are the two events exempt from the
  `GITHUB_TOKEN` suppression — and the ruleset on `main` requires the three `probe (<os>)`
  checks. Dependabot is scoped away from `.config/dotnet-tools.json`: the server
  pin moves only through `bump.yml`.
