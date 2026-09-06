# roslyn-ls-agent

Semantic C# queries for coding agents, over Microsoft's official
[`roslyn-language-server`](https://www.nuget.org/packages/roslyn-language-server) — the same
engine behind the VS Code C# extension. `csx` is a thin LSP client that turns LSP's URIs and
zero-based ranges into `path:line` plus source context, so an agent can find every caller of a
method instead of grepping for its name.

Two constraints drive the design:

1. **Official tooling only.** The C#-specific component in the query path is Microsoft-published.
2. **Always current.** A weekly cron bumps the pin and a probe suite gates the bump.

Status: **Milestone 3 done, Milestone 4 in progress** — `csx ready`, `csx refs`, `csx def`,
`csx impl`, `csx sym`, `csx outline` and `csx diag`, cross-project fixture, probe gate, both
workflows, the source-generator, non-ASCII and deliberate-error fixture cases, the shared server
daemon on by default, source-generator staleness pinned, and `skill/SKILL.md`. Milestone 4's
remaining item is output tuning. See [ROADMAP.md](ROADMAP.md).

## Use

```
csx ready                                    # block until the workspace has loaded
csx refs <symbol | file:line:col> [--max N]  # every reference, with context
csx def <symbol | file:line:col>             # where it is declared
csx impl <symbol | file:line:col>            # what implements or overrides it
csx sym <query> [--max N]                    # search the workspace by name
csx outline <file | symbol> [--max N]        # the declarations in one document
csx diag [path] [--errors-only]              # compiler and analyzer diagnostics
```

Add `--no-daemon` to any of them to start a private server instead of sharing the background
daemon; see [Latency](#latency).

```
$ csx refs Fixture.Core.Greeter.Greet --root fixture
App/Program.cs:9:35
   8 |     {
>  9 |         Console.WriteLine(Greeter.Greet("world"));
  10 |     }

Core/Greeter.cs:5:26
  4 | {
> 5 |     public static string Greet(string name) => $"Hello, {name}!";
  6 | }
```

```
$ csx def App/Program.cs:9:35 --root fixture
Core/Greeter.cs:5:26
  4 | {
> 5 |     public static string Greet(string name) => $"Hello, {name}!";
  6 | }
```

```
$ csx outline Core/Greeter.cs --root fixture
Core/Greeter.cs
  1 | namespace Fixture.Core;
  3 |   public static class Greeter
  5 |     public static string Greet(string name) => $"Hello, {name}!";
  9 |     public static string Farewell(string name) => $"Bye, {name}!";
```

`outline` is the one command that does not print `path:line` and context per row — an outline
is already the summary, so the document path is a header and each row carries that
declaration's own source line, indented by nesting. `--context` does not apply to it. Its
target is a file path, a `file:line:col` spec (the document it names is outlined, so a position
copied out of a `def` result works), or a symbol whose declaring document is outlined — the
last being the only way to reach a source-generated document, which has no path on disk.

```
$ csx impl Fixture.Core.IShape.Area --root fixture
App/Square.cs:12:16
  11 | {
> 12 |     public int Area() => side * side;
  13 | }

Core/Shape.cs:16:16
  15 | {
> 16 |     public int Area() => 1;
  17 | }
```

`impl` renders exactly like `def`, and on a member with no implementations it *is* `def`:
Roslyn does not answer empty there, it falls through to the declaration. So an empty `impl`
result — which exits 1 — means the position resolved to no symbol at all, not that nothing
implements the symbol.

```
$ csx sym Area --root fixture
method  Area  in Square (project App (net10.0))   App/Square.cs:12:16
method  Area  in IShape (project Core (net10.0))  Core/Shape.cs:11:9
method  Area  in Unit (project Core (net10.0))    Core/Shape.cs:16:16
```

`sym` is a search, so the query goes to the server as written and every answer is a result —
no ambiguity error, no candidate dump. It is the second command that bends the output rules,
more narrowly than `outline`: it keeps `path:line:col` on every row but prints no source line
and no `>` marker, so `--context` is inert for it. The container column is Roslyn's localised
display text, not a namespace path, and is there to separate two symbols that share a name.

```
$ csx diag App/TypeError.cs --root fixture
App/TypeError.cs:18:36 error CS0029: Cannot implicitly convert type 'string' to 'int'
  17 | {
> 18 |     internal static int Wrong() => Greeter.Farewell("x");
  19 | }
```

`diag` takes a file, a directory, or nothing at all — with no argument it walks every `.cs` file
under `--root`, skipping `bin` and `obj`. It does *not* use `workspace/diagnostic`: the server
answers that endpoint but returns zero reports, which is what the `workspaceDiagnostics: false`
in its dynamic registration means.

Options: `--root <dir>` (default: cwd), `--sentinel <symbol>`, `--max N` (default 50),
`--context N` (default 1; inert for `outline` and `sym`), `--timeout N` seconds (default 180),
`--log-level L`, `--errors-only` (`diag` only), `--json`.

Paths are relative to `--root`; lines and columns are one-based.

`--sentinel` is an escape hatch, not a neutral override. By default `csx` waits for *every*
project under the root to load, one readiness probe per project the root's solution lists — or
per `.csproj` when the root holds no solution, or more than one. Passing `--sentinel` replaces
that whole set with a single probe scoped to the root, which gives up the guarantee and restores
the window in which `refs`, `impl` and `sym` can answer incompletely at exit 0. Use it when the
`.csproj` scan cannot read the workspace layout — including a root with no `.csproj`, which
otherwise fails immediately.

`refs` exits 1 with `no results` when a symbol resolves but has no references, and 1 with a
diagnostic when the symbol does not resolve or the workspace never loaded. `def`, `impl` and
`sym` follow the same rule.

`diag` exits 0 whenever the query was answered, findings or not — a clean file is a successful
`diag`, unlike an empty `refs`, which means the lookup failed. It exits 1 only when the workspace
never loaded or the path does not exist.

### Symbol names

A dotted target narrows by **enclosing type**, not by namespace: `Greeter.Greet` and
`Fixture.Core.Greeter.Greet` both work, but the namespace part is not actually checked. Roslyn
returns `containerName` as a localised display string (`in Greeter (project Core (net10.0))`),
not a namespace path, so there is nothing to match a namespace against. When a target stays
ambiguous, `csx` lists the candidates with their locations so you can switch to `file:line:col`.

`csx` pins `DOTNET_CLI_UI_LANGUAGE=en` on the server so those display strings do not change with
the developer's machine locale.

## Latency

Measured on the fixture, Windows 11 / .NET 10.0.301, debug build:

| command | cold (per invocation) |
|---|---|
| `csx ready` | ~3.9–4.1 s |
| `csx refs` | ~5.9–14.7 s |
| `csx def` | ~6.4–7.0 s |
| `csx outline` | ~5.4–6.2 s |
| `csx diag <file>` | ~11–12 s |
| `csx diag` (whole fixture) | ~16 s over the 11 files it then had |

The same suite on `ubuntu-latest` reaches ready in ~12 s and runs six cases in ~39 s.

Milestone 1 started a dedicated server per invocation, so every command paid a full solution
load. Since Milestone 3 `csx` connects to the shared daemon by default and the cost is a pipe
round-trip against an already-warm server; `--no-daemon` gets the old behaviour back. Measured
the same way on 2026-09-04:

| command | non-daemon | daemon warm |
|---|---|---|
| `csx ready` | 7.3 s | 2.3–2.6 s |
| `csx refs` | 9.6–10.4 s | 3.1–5.2 s |
| `csx def` | — | 2.6–2.8 s |
| `csx outline` | — | 2.6 s |

About 3.2x on `refs`, with little variance across repeats. The warm floor is `dotnet tool run`
plus apphost startup plus connecting the relay — not Roslyn — so it is a floor `csx` cannot
get under while it launches through `dotnet tool run`.

One daemon is shared across every workspace on the machine, keyed by user identity and the
server's versioned path rather than by the root, and it outlives the client that started it
(900 s after the last client disconnects, by default). Two consequences worth knowing:
`--log-level` is silently a no-op against a daemon someone else started, because the daemon
takes its configuration from whoever launched it; and the thin client falls back to a private
cold server without failing if it cannot reach the daemon, so `csx` watches its stderr for that
and says `csx: daemon unreachable` rather than leaving you to infer it from the latency.

`probes/run.sh` scopes itself to its own daemon with
`ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME` and a short keepalive, so the gate cannot inherit a
stale workspace and its opening `csx ready` is still a real cold load.

## The pin

`.config/dotnet-tools.json` pins the exact version; `dotnet tool restore` reproduces it.

Verified against nuget.org on 2026-09-02:

- **No stable release exists.** The RID-specific packages each show a bare `5.11.0` on the
  flat-container index, but it is *unlisted* and the non-RID `roslyn-language-server` never got
  a `5.11.0` at all — `dotnet tool install roslyn-language-server` without `--prerelease` fails
  outright. `--prerelease` is load-bearing, and a bump script must never scrape the
  flat-container index or it will pin something uninstallable. `dotnet tool update --prerelease`
  reads the registration API and skips unlisted versions.
- **The non-RID tool ID resolves per-platform.** It is a 130 KB shim whose
  `DotnetToolSettings.xml` maps all eight RIDs, so CI needs no RID selection. The payload it
  pulls is ~300 MB, hence the NuGet cache in `probe.yml`.
- Latest at time of writing: `5.12.0-1.26426.8` (2026-08-27). Minor moves between releases,
  roughly every 2–4 weeks — which is why the bump is a cron job rather than Renovate, whose
  `ignoreUnstable` default would silently never fire on this train.

`bump.yml` runs Mondays at 05:00 UTC, updates the pin, runs the probes, and opens a PR. It needs
*Settings → Actions → Allow GitHub Actions to create and approve pull requests* enabled on the
repo. When the pin is already current the update is a no-op and no PR is opened.

A PR opened with `GITHUB_TOKEN` does not get a working `probe.yml` run: GitHub creates the run
with `github-actions[bot]` as the actor and parks it at `action_required`, waiting for a human
to approve it, so it never executes. `bump.yml` therefore runs the probes itself before opening
the PR and publishes the result as a `probes` commit status — that is the check to require in
branch protection. Setting a `BUMP_TOKEN` secret (a PAT with `repo` scope) makes the PR run
`probe.yml` for real as well.

## Dependencies

`csx` depends on **StreamJsonRpc** (Microsoft, MIT) for `Content-Length` framing, request
correlation and notifications. Nothing C#-specific is third-party.

The LSP payload types in `src/Csx/Protocol.cs` are ours, which is a deliberate departure — no
maintained Microsoft package supplies them:

- `Microsoft.CodeAnalysis.LanguageServer.Protocol` is **unlisted** on nuget.org and absent from
  search. Roslyn maintainers describe these packages as "glorified .zips".
- `Microsoft.VisualStudio.LanguageServer.Protocol` last shipped **17.2.8 (May 2022)**. It
  targets netstandard2.0, depends on Newtonsoft.Json, ships under a VS SDK EULA rather than MIT,
  and predates LSP 3.17 — the assembly has no `PositionEncoding` type at all, so it cannot
  express the one capability the column-correctness story rests on.
- [dotnet/roslyn#68696](https://github.com/dotnet/roslyn/issues/68696), the tracking issue for
  making the real protocol APIs public and stable, is still open with no timeline.

Third-party alternatives are worse: `LspTypes` is LSP 3.16 and last shipped January 2021;
`OmniSharp.Extensions.*` last shipped 0.19.9 in September 2023 and drags in MediatR.

Only the payload shapes are hand-defined. Framing and correlation still come from StreamJsonRpc.

`fixture/Gen` references **Microsoft.CodeAnalysis.CSharp** (Microsoft, MIT) because a source
generator cannot be written without it. It is fixture-only and never loaded by `csx`, so the
query path stays free of C#-specific third-party code. The version is pinned **low** (4.3.0,
well past `IIncrementalGenerator`'s introduction) and deliberately never tracks the server:
the analyzer is loaded by two independently-moving compilers — the SDK's `csc` during
`dotnet build` and the language server's hosted Roslyn — and Roslyn only ever moves forward,
so a low pin is permanently compatible while a tracking pin would need re-verifying on every
weekly bump.

## Failure modes this handles

| Failure mode | How |
|---|---|
| Async project load returning empty instead of erroring | `WaitReadyAsync` polls one sentinel symbol per project until every project that has one resolves it, then fails loudly on timeout. A project the scan could infer no sentinel for — one that is only top-level statements, or only Razor or resources — is not waited on, because there is nothing to ask the server for; it is named on the failure path instead, so its absence from readiness is visible rather than silent. Never `sleep`, and never block on `workspace/projectInitializationComplete` — it never fires for a client attaching to a loaded daemon. |
| A sentinel that is itself the thing being queried | Sentinels are inferred from type declarations in each project, so "symbol absent" and "workspace not loaded" stay distinguishable. No grace poll on the target: readiness covering every project is what makes an empty answer mean absent. |
| UTF-16 position encoding | The server does not advertise `positionEncoding`, which per LSP 3.17 means utf-16 — the same unit as a .NET string index. `csx` asserts this at `initialize` and refuses to run if a future build negotiates utf-8. A fixture line carrying an astral-plane character (a surrogate pair, so utf-16 and rune counts differ) pins the reported column at 39 in three cases; an accented letter would pass even on a broken implementation. |
| A first diagnostic pull under-reporting on an unbound document | `textDocument/diagnostic` does not answer from the misc-files state and then correct itself — it **blocks until the document is bound**, so `diag` pulls once and the settle loop that used to wrap it is gone. Measured 2026-09-06: a cross-project error opened as the first document in a never-used server returns the right code on pull #1 (~4.2 s), and a second pull (~0.7 s) never once differed across six whole-fixture runs, cold and warm. (A document in **no** project is a different case: it reports nothing at all, whatever the error class. See `DESIGN.md`.) The fixture's error is deliberately *cross-project* — binding it needs Core's reference resolved — and `cold-server-diag-reports-cross-project-error` opens it as the first document of a dedicated server, which is the only state where answering early would show. |
| Roslyn ignoring unopened documents | Every query opens its document via `textDocument/didOpen` first — except source-generated ones, which the server owns and answers for without it. |
| No auto-restore | `probes/run.sh` runs `dotnet restore` on the fixture before starting the server. |
| Source-generated symbols rendering as a nonexistent path | Generated documents come back under a `roslyn-source-generated:` URI. `new Uri(u).LocalPath` does not throw for one, it returns `/BuildInfo.g.cs`, so `PathUri.Display` branches on the scheme and labels them `<generated>/<assembly>/<hintName>`. Text comes from `workspace/textDocumentContent`. |
| An unbuilt source generator contributing nothing, silently | With the analyzer assembly absent the workspace still loads and the sentinel still resolves; only the generated symbol is missing, with no error or diagnostic anywhere. `probes/run.sh` builds `fixture/Gen` before starting the server, and three cases assert the generated symbol resolves. |
| Server-to-client requests faulting the connection | `LspClient.Endpoints` answers `workspace/configuration`, `client/registerCapability`, `window/workDoneProgress/create` and friends. |
| A renamed server flag failing silently | The thin client forwards unrecognised options straight through to the server, so a rename produces no error. Flags live only in `src/Csx/ServerArgs.cs`, and the probes are the only guard. |

## Probes

```
./probes/run.sh
```

Runs `tests/Csx.Tests` first, then restores the tool and the fixture, builds `csx`, asserts
readiness, and runs every case in `probes/cases.jsonl`. Exits non-zero on any mismatch.
Fifty-seven cases today — fifty-two rows, three source-generator staleness legs, the forced
non-daemon fallback and a cold-server `diag` — including a negative one that pins a query
fired before load to a loud failure rather than an empty result.

The unit tests come first because they cost under a second and need no server: they cover the
pure logic below the transport — sentinel inference, argument parsing, path and URI rendering,
and the `sym` cap ordering — including the shapes `fixture/` cannot hold, such as prose in a
doc comment above the only declaration in a single-file project. Anything that needs a live
server belongs in a case, not a test.

`cases.jsonl` is one flat JSON object per line with four string fields so `run.sh` can parse it
with `sed` alone — no `jq`, which is absent from Git Bash on the dev machine. That keeps it
running unchanged on a GitHub runner and in Git Bash.
Inside `expect`, `'` stands for `"` and `|` separates substrings that must all appear.

## Layout

```
Csx.slnx                    src/Csx + tests/Csx.Tests; fixture/ is deliberately not in it
.config/dotnet-tools.json   the version pin
.github/workflows/          bump.yml (weekly cron), probe.yml (every PR)
src/Csx/                    the thin LSP client and CLI
  ServerArgs.cs             the only place server flags live
  Protocol.cs               hand-defined LSP payload types
  LspClient.cs              transport, initialize, readiness, didOpen
  Output.cs                 path:line + context formatting, and the outline tree
fixture/                    deliberately tricky solution
  Gen/                      incremental source generator; its output is referenced from App
  Ambient/Stray.cs          a document no project compiles, for the misc-files cases
  App/TypeError.cs          the deliberate cross-project type error for `csx diag`
  App/Square.cs             the cross-project implementer of `Core/Shape.cs`, for `impl`
  Core/Party.cs             an astral-plane character on a line carrying a symbol
  Core/Split*.cs            one type in two documents, plus an overload in one of them
  Core/Empty.cs             a compilable document that declares nothing
  Core/Shape.cs             an interface whose implementers straddle two projects, for `impl`
tests/Csx.Tests/            unit tests for the pure logic below the transport
probes/                     cases.jsonl + run.sh (runs tests/Csx.Tests first)
```
