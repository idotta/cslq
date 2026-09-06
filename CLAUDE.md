# roslyn-ls-agent

A CLI (`csx`) that gives coding agents semantic C# queries over Microsoft's official
`roslyn-language-server`, with a cron-driven update loop gated by a probe suite.

**Read `ROADMAP.md` first** for milestone state and acceptance criteria, and `DESIGN.md` for the
output rules every command follows and the decisions that are already settled. The README holds
the user-facing detail and the evidence behind the dependency choices.

## Commands

```
dotnet build src/Csx/Csx.csproj          # build
dotnet test --project tests/Csx.Tests    # the unit tests alone, ~1s (never with --nologo)
./probes/run.sh                          # the gate: unit tests, restore, build, ready, every case
./src/Csx/bin/Debug/net10.0/csx refs Greet --root fixture
```

`probes/run.sh` is the gate, and it runs `tests/Csx.Tests` first. Run it before claiming
anything works: the unit tests alone prove nothing about the server's behaviour.

## Things that will cost you a session if you rediscover them

- **Positions are UTF-16 code units** and the server does not negotiate otherwise. .NET string
  indices are already UTF-16, so naive indexing is correct — counting runes or UTF-8 bytes is the
  bug. `LspClient.InitializeAsync` asserts the encoding and refuses to run if it ever changes.
- **Do not add `Console.OutputEncoding = UTF8`.** It looks required — this machine's console is
  code page 850 — but it fixes nothing and was tried and reverted. .NET writes a real console
  handle with `WriteConsoleW`, so the code page never applies, and redirected stdout is already
  UTF-8. Verified both ways against the emoji fixture line. Mojibake in a PowerShell pipeline
  (`csx refs ... | Select-String`) is PowerShell decoding our bytes with its own
  `[Console]::OutputEncoding`, which nothing `csx` sets can change.
- **The non-ASCII probe cases are the first host-dependent ones.** Both workflows run
  `ubuntu-latest`, where the encoding question does not arise; a regression here would be green
  on CI and red in Git Bash. Also note `File.ReadAllTextAsync` substitutes U+FFFD for invalid
  bytes rather than throwing, so a fixture file corrupted to a non-UTF-8 encoding would desync
  the `didOpen` text from what Roslyn parses off disk — silently, except that
  `non-ascii-refs-position` then fails.
- **Server flags live only in `src/Csx/ServerArgs.cs`.** The thin client forwards options it does
  not recognise straight through to the server, so a renamed flag produces no error at all. The
  probes are the only thing that catches it.
- **Roslyn's `containerName` is localised display text** (`in Greeter (project Core (net10.0))`),
  not a namespace path. A dotted symbol target can only narrow by enclosing type.
  `DOTNET_CLI_UI_LANGUAGE=en` is pinned on the server so it does not vary by machine locale.
- **Never gate readiness on the symbol being queried.** An absent symbol then looks identical to
  a workspace that has not loaded, and the caller waits out the whole timeout for a typo.
- **A query fired before load returns empty, not an error.** Never `sleep`; wait for
  `workspace/projectInitializationComplete` and then poll a sentinel that must resolve.
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
  **nothing** for it — but `outline` answers, off the syntax tree. So `csx outline` on such a
  file works while `csx sym` on the type it declares exits 1, and scoping `diag`'s file walk to
  project directories would suppress no noise whatsoever. The same file **linked in** with
  `<Compile Include="../Elsewhere/File.cs" />` is fully indexed and does report, so that scoping
  would silently drop real errors. Measured 2026-09-05; the reasoning is in `DESIGN.md`.
- **Sentinel inference reads prose, and neither taking every match nor capping at three saves
  it.** The candidate regex matches `class|struct|record|interface|enum` followed by a word, so
  the doc comment "identifying the class and assembly context" yields the candidate `and` — and
  one sentence can yield `and` / `of` / `for` and fill all three slots, leaving a project probed
  only by words nothing can resolve while readiness burns its entire timeout. `csx ready` on
  OrchardCore failed this way after 900 s on fifteen projects. `Program.NonCode` therefore
  strips comments and string literals before the declaration regex runs. Keep the fallback
  chain anyway: the regex still reads types out of `#if` branches and uncompiled files. The
  fixture cannot reproduce the original failure — it needs prose in a doc comment above the
  only declaration in a single-file project.
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
- **`csx` never sends `didClose`, so daemon document state outlives the client.** `_open` is
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
- **Never pipe or command-substitute `csx` output in bash while the daemon is in play.** The
  daemon inherits the client's stdout, so `csx ... | tail` and `out=$(csx ...)` block forever
  waiting for the pipe's last writer — it looks exactly like a hung cold load. Redirect to a
  file and `cat` it, or pass `--no-daemon`. Only the run that *launches* the daemon can hang,
  which is why `probes/run.sh` captures every case with `$(...)` and never blocks: its cold
  `csx ready` — the one leg that starts the daemon — is deliberately uncaptured. Keep it that
  way.
- **Nothing in the suite covers Ctrl+C, and MSYS `kill -INT` does not test it.** From Git Bash
  it terminates the process without ever raising a console control event, so the handler never
  runs and the 130 you see is bash's own signal status. To exercise the real path, launch `csx`
  with `CREATE_NEW_PROCESS_GROUP` and send it `CTRL_BREAK_EVENT` with
  `GenerateConsoleCtrlEvent` — a throwaway file-based app does it in 40 lines. Measured
  2026-09-06: `csx: interrupted.` and exit 130 within 62 ms, with the fallback run's own server
  tree gone.
- **`dotnet test --nologo` runs zero tests and exits 5.** `global.json` opts into the MTP mode
  of `dotnet test` (`"test": {"runner": "Microsoft.Testing.Platform"}`), where `--nologo` is no
  longer a build-only flag. It fails loudly, but it reads as a broken test project rather than
  a bad flag, and every other `dotnet` call in `run.sh` passes `--nologo`, so it is the obvious
  thing to add. Do not.
- **Project discovery reads the root's solution, and only the root's.** `ProjectDirectories`
  takes the project list from a single `.sln`/`.slnx` sitting at the top of `--root`, and falls
  back to the recursive `.csproj` scan when there are none or more than one. A solution one
  directory down does not count — which is what keeps `fixture/Fixture.slnx` from narrowing a
  root above it, and what makes `--root fixture` and `--root .` two different workspaces rather
  than one. `.slnf` is not read. Two `.csproj` in one directory are still indistinguishable, and
  still a documented limit. This is the fix for the OrchardCore template failure above; scoping
  `--root` below the templates was only the workaround.
- **`csx` can now be pointed at its own repo, and `Csx.slnx` is why.** The root solution lists
  `src/Csx` and `tests/Csx.Tests` and deliberately excludes `fixture/`, whose `App` does not
  compile on purpose. Put a fixture project in it and `dotnet build` at the root fails by
  design; leave it out and `csx ready --root .` resolves in ~6 s — the `self-hosted-*` cases.
  `fixture/` keeps its own `Fixture.slnx`, which `run.sh` restores separately.
- **The server does not restore your projects.** `dotnet restore` before starting it.
- **The daemon is the default, and it changes what "ready" means.** `csx` connects to the
  shared multi-client daemon unless `--no-daemon` is passed. One daemon serves every
  workspace on the machine, keyed by user and server path rather than by root, and it outlives
  the client that started it. Two consequences bit already:
  - **`workspace/projectInitializationComplete` never fires for a client that attaches to a
    loaded daemon** — it fired before the process existed. `WaitReadyAsync` must poll the
    sentinel from the start and treat the notification as diagnostic only. Blocking on it
    first made every warm run burn its entire timeout, 300 s under `run.sh`, and looked
    exactly like a slow cold load.
  - **One sentinel no longer proved the workspace was loaded — now there is one per
    project.** Cold load used to close that window by accident, costing a minute; warm attach
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
- **`probes/run.sh` must scope its own daemon.** It exports
  `ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME=csx-probe-$$` and a 60 s keepalive. Without it the
  gate inherits whatever daemon the developer's session left running — a stale workspace can
  make the suite lie — and the opening `csx ready` stops being a cold load.
- **The staleness legs write to the fixture.** They rename `Greeter` in
  `fixture/Core/Greeter.cs` and rely on a `trap ... EXIT` to put it back. If `run.sh` is
  interrupted between the rename and the trap, check `git diff fixture/` before believing
  anything else the suite says.
- **A failure during `initialize` must never escape as a StreamJsonRpc exception.** The thin
  client can die before it answers, and StreamJsonRpc then reports nothing but
  `ConnectionLostException` — the server's stderr is the only thing that says why, and it is
  discarded unless the failure is wrapped in a `CsxException` carrying `StderrTail()`. That
  wrapping is what turned "connection lost" into the exact mutex name and `file:line`
  below.
- **To force the silent non-daemon fallback**, hold a mutex named `Global\<pipeName>.client`
  while a client starts — the thin client falls back after about 20 s of waiting for it.
  `probes/hold-mutex.cs` does this and `non-daemon-fallback-reported` is the case. Two traps,
  both of which look like the mechanism not working rather than like a mistake: the mutex must
  be created with `CurrentUserOnly = true` to match the server, and with
  `CurrentSessionOnly = false` or .NET rejects the `Global\` prefix. Either one wrong throws
  `WaitHandleCannotBeOpenedException` / `ArgumentException` instead of contending, so the
  client connects normally and the case fails for a reason that has nothing to do with `csx`.
  It also needs its own pipe name: the mutex only guards check-server-then-launch, so a client
  that finds a daemon already listening never contends for it. It is the second
  host-dependent case in the suite after the non-ASCII ones — .NET implements named mutexes
  over files on Linux — but it **passed on `ubuntu-latest`** in PR #5, so the file-backed
  implementation contends the same way.
- **`probes/hold-mutex.cs` is a .NET 10 file-based app, not a project, and that is deliberate.**
  `dotnet run probes/hold-mutex.cs` compiles a bare `.cs` in under a second with no `.csproj`.
  Reach for that before adding a project to the tree for a probe.
## C# and .NET rules

This repo is .NET 10 / C# 14: a CLI and a thin LSP client, no UI, no web host, no DI container.

- **`dotnet format` must pass clean.** `dotnet format --verify-no-changes` exits 0 today; keep it
  that way and run `dotnet format` before calling a change done. There is deliberately **no
  `.editorconfig` yet**, so `dotnet format` enforces its own defaults rather than house style —
  match the surrounding code instead of reformatting a file you touched.
- **`DateTime.UtcNow`, never `DateTime.Now`.** Every deadline in `LspClient` is UTC.
- **No `async void`** outside an event handler, and never `.Result` or `.Wait()` — `csx` is async
  from `Main` down, and a sync-over-async wait here deadlocks against the JSON-RPC read loop.
- **Reach for C# 14 first:** `extension` blocks rather than `this` extension methods, the `field`
  keyword rather than a hand-written backing field, `x?.P = v` rather than an `if` guard. C# 14
  comes free with `net10.0` and `LangVersion` is deliberately unset. `fixture/Gen` is the one
  exception: analyzers must target netstandard2.0, whose default is C# 7.3, so it pins
  `<LangVersion>latest</LangVersion>` explicitly.
- **`[LibraryImport]`, not `[DllImport]`**, if native interop ever appears.
- **Fix root causes and delete what is dead.** Don't preserve a shape for backwards
  compatibility — nothing depends on `csx`'s internals yet. Simplify rather than layering.
- **Never push to a remote, and never commit unless asked.** `bump.yml` is the only thing that
  opens PRs here.

## Conventions

- **`probes/run.sh` is still the gate, but it is no longer the only suite.**
  `tests/Csx.Tests` (xunit v3 over MTP) covers the pure logic below the transport -- sentinel
  inference, `Options.Parse`, `PathUri`, `Output.WriteSymbols` -- and `run.sh` runs it first,
  before the fixture restore, because it costs under a second. Anything that needs a live
  server stays in `probes/`; put nothing there that a temp directory and a string could prove.
  The tested members are `internal`, reached through `<InternalsVisibleTo Include="Csx.Tests" />`
  in `Csx.csproj`.
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
  never executes. The `probes` commit status published by `bump.yml` is the real gate.
