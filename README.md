# C# Language Query

Semantic C# queries for coding agents, over Microsoft's official
[`roslyn-language-server`](https://www.nuget.org/packages/roslyn-language-server) — the same
engine behind the VS Code C# extension. `cslq` is a thin LSP client that turns LSP's URIs and
zero-based ranges into `path:line` plus source context, so an agent can find every caller of a
method instead of grepping for its name.

Two constraints drive the design:

1. **Official tooling only.** The C#-specific component in the query path is Microsoft-published.
2. **Always current.** A weekly cron bumps the pin and a probe suite gates the bump.

Status: **all five milestones done, `0.1.0` is the first release.** Ten commands — `ready`,
`refs`, `def`, `impl`, `hover`, `sym`, `outline`, `diag`, `project` and `restore` — over a
cross-project fixture with source-generated, non-ASCII, metadata and deliberate-error cases;
the shared server daemon on by default; `skills/csharp-semantic-queries/SKILL.md` for the
agent; a probe gate that runs on Linux, Windows and macOS for every PR; a weekly bump PR gated
by that suite; and a tag-triggered release that publishes to nuget.org and binds each version
to a GitHub Release.
The history and the evidence behind each decision are in [ROADMAP.md](ROADMAP.md).

## Install

`cslq` is built for coding agents, so installing it is two things: the tool on `PATH`, and the
skill that makes an agent reach for it instead of grep. The tool on its own is inert — nothing
will invoke it.

Prerequisites: the **.NET 10 SDK** and **git**. Nothing else — `cslq` fetches the language
server itself on first run. Linux, Windows and macOS are the supported platforms, and every PR
runs the gate on all three.

### 1. The tool

```
dotnet tool install -g cslq
cslq --version
```

`dotnet tool update -g cslq` moves to the latest release, and every release is a [GitHub
Release](https://github.com/idotta/cslq/releases) of the same tag with the `.nupkg` attached
and notes saying which language server it pins. To run what is on `main` instead, install from
a package you build — the `--source` pointing at your own `dotnet pack` output is what takes
the place of nuget.org:

```
git clone https://github.com/idotta/cslq.git
cd cslq
dotnet pack src/Cslq/Cslq.csproj -c Release -o ./artifacts
dotnet tool install -g cslq --source ./artifacts
```

### 2. The skill

`skills/csharp-semantic-queries/SKILL.md` is what makes an agent reach for `cslq` instead of
grep. It is a plain markdown file with YAML frontmatter, and installing it means putting it
where the agent looks:

```
npx skills add idotta/cslq -g
```

That is [`npx skills`](https://github.com/vercel-labs/skills), which reads GitHub as its
registry rather than a package feed: it detects the coding agents on the machine and installs
to each one's skills directory — `~/.claude/skills/csharp-semantic-queries/` for Claude Code.
Drop `-g` to install into the current repository (`.claude/skills/...`) instead, which is what
to do when only one project you work on is C#. It is the one step that wants Node; the
prerequisites above do not.

Without `npx`, copy the file — that is the whole of the installation, and the skill needs
nothing else on disk. For Claude Code it goes to
`.claude/skills/csharp-semantic-queries/SKILL.md` in the repository you want to query, or the
same path under `~` to have it everywhere; the `name` and `description` in its frontmatter are
what Claude Code matches against. Other agents read the same file — its body names no tool but
`cslq` — so paste it into whatever that agent takes as standing instructions.

### Or hand both steps to the agent

Every step above is something an agent can run itself. Paste this at it:

> Install `cslq` and its skill so you can answer C# questions semantically instead of grepping:
> run `dotnet tool install -g cslq`, then `npx skills add idotta/cslq -g`, then `cslq restore`.
> Then run `cslq ready --root .` here and tell me what it printed.

### The first run

The first command that needs the language server restores it for you and says so:

```
cslq: the pinned language server is not restored; restoring it in <dir>. This is a one-time ~300 MB download.
```

`<dir>` is the tool's *own* manifest directory — the `.config/dotnet-tools.json` packed
alongside the binary, which for a global install is under
`~/.dotnet/tools/.store/cslq/<version>/cslq/<version>/tools/net10.0/any/`. It is never your
repository: the pin travels with the `cslq` version, so nothing you query has to carry it. The
restore is idempotent and later runs skip it.

Each pin is its own ~300 MB under `~/.nuget/packages/roslyn-language-server.<rid>/<version>`,
and `dotnet tool` never removes one, so updating `cslq` with every weekly bump would otherwise
stack them up. `cslq` does the housekeeping itself: the restore that brings in a new pin then
deletes every other version of the server packages and says which —
`removed 1 other version(s) of the language server from <packages>: ...`. The folder is the
one `dotnet nuget locals global-packages --list` names, so `NUGET_PACKAGES` is honoured. If
another tool manifest on the machine still pins a deleted version, that version simply reads
as unrestored again — `dotnet tool run` answers with the same *Run "dotnet tool restore"* line
`cslq` itself recognises — so an older `cslq` still installed elsewhere restores it back rather
than breaking. A version a still-running old daemon holds open cannot be deleted on Windows;
`cslq` reports it and a later `cslq restore` removes it. The one shape this does not serve is
two different `cslq` versions in regular use on one machine — say a `-g` install and a
`--tool-path` one: each restore deletes the other's pin, and they take turns re-downloading it.
One `cslq` per machine is the supported shape; there is deliberately no lock, because a lock
would serialise that fight rather than end it.

What it writes is `~/.nuget/packages` and `~/.dotnet/toolResolverCache`, never that install
directory, so a `--tool-path` install owned by root and used by another account is fine. What
breaks is the home those two live in — the minimal-container case. With `HOME` unset the CLI
computes no path at all and refuses up front, naming the variable that fixes it: *The user's
home directory could not be determined. Set the 'DOTNET_CLI_HOME' environment variable to
specify the directory to use.* With `HOME` set but not writable, the restore fails as any
write to it would. Point `DOTNET_CLI_HOME` at a writable directory and it has somewhere to go:

```
DOTNET_CLI_HOME=/var/cache/dotnet cslq ready --root <dir>
```

The other first-run edge is a race: two `cslq` processes started together in a fresh home both
reach the restore, and `dotnet tool restore` is not built to be raced — one of them can fail
rather than wait. `cslq` does not lock around it, deliberately; the window is the first run
only and a rerun once the winner finishes succeeds. Pre-warm instead, which is the whole of
`cslq restore`:

```
cslq restore                   # fetch the pinned server now, then exit
```

It takes no `--root` and needs no workspace — it restores the manifest packed beside the
binary, prints where it restored to and what the prune above removed, and exits — so it is
what a Dockerfile layer or a CI step runs to keep the download out of the first query. `--json`
gives it the same envelope every other command uses, with `removed` and `kept` arrays.

### Querying a repository

In the repository you want to query:

```
dotnet restore                 # the server does not restore your projects
cslq ready --root <dir>
```

`--root` must be **the directory holding the `.sln` or `.slnx`** — `cslq` loads the projects
that solution lists. A root with no solution at its top is an error, reported in about a second
rather than after the timeout, and a solution one directory down does not count. A checkout
whose path contains a `%XX` sequence (`.../pct%20x`) never loads at all — MSBuild unescapes it,
so `dotnet restore` on such a tree fails too — and nothing in `cslq` can fix that: rename or
move the checkout. Two `.csproj` in one directory are indistinguishable to readiness, since a
hit under that directory cannot be attributed to one of them; the second is covered only
incidentally.

## Use

```
cslq ready                                    # block until the workspace has loaded
cslq refs <symbol | file:line:col> [--max N]  # every reference, with context
cslq def <symbol | file:line:col> [--tfm T]   # where it is declared
cslq impl <symbol | file:line:col>            # what implements or overrides it
cslq hover <symbol | file:line:col> [--tfm T] # what it is: type, signature, docs
cslq sym <query> [--max N]                    # search the workspace by name
cslq outline <file | symbol> [--max N]        # the declarations in one document
cslq diag [path] [--errors-only]              # compiler and analyzer diagnostics
cslq project <file> [--tfm T]                 # every .csproj + TFM that compiles it
cslq restore                                  # fetch the pinned language server, then exit
```

Add `--no-daemon` to any of them to start a private server instead of sharing the background
daemon; see [Latency](#latency).

```
$ cslq refs Fixture.Core.Greeter.Greet --root fixture
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
$ cslq def App/Program.cs:9:35 --root fixture
Core/Greeter.cs:5:26
  4 | {
> 5 |     public static string Greet(string name) => $"Hello, {name}!";
  6 | }
```

```
$ cslq outline Core/Greeter.cs --root fixture
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
$ cslq impl Fixture.Core.IShape.Area --root fixture
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
$ cslq hover App/Program.cs:9:17 --root fixture
App/Program.cs:9:17
void Console.WriteLine(string? value) (+ 19 overloads)
Writes the specified string value, followed by the current line terminator, to the standard output stream.

Exceptions:
  IOException
```

`hover` is the answer to "what is this" — the type, the full signature including parameter
types, and the doc-comment summary, from a single request. It is the third command to bend the
output rules and the narrowest bender of the three: the position the hover applies to is still
a root-relative one-based `path:line:col` header, but the body is prose rather than source, so
there is no `>` marker, `--context` is inert, and `--max` caps the documentation's *lines*.
There is no `signatureHelp` command; hover already carries the parameters, and
`textDocument/signatureHelp` only answers inside an argument list.

```
$ cslq def App/Program.cs:9:17 --root fixture
<metadata>/System.Console/Console.cs:825:24
  824 |     [MethodImpl(MethodImplOptions.NoInlining)]
> 825 |     public static void WriteLine(string? value)
  826 |     {
```

A symbol whose source is not in the workspace — a framework or NuGet type — is answered from
the document Roslyn decompiles for it, labelled `<metadata>/<assembly>/<TypeName>.cs`. The real
file is a machine-absolute temp path made of two per-server-instance guids, which is never
printed; the assembly comes from the `#region Assembly` header Roslyn writes into the document,
and the context lines come off that file, which does exist on disk. `outline` on a metadata
document works if you point it at that absolute path, but there is no way to reach one from a
type name — `outline System.Console` exits 1, because `workspace/symbol` indexes source only
and nothing but a `def` at a use site makes Roslyn write the document. Ask `hover` instead.

```
$ cslq project App/Program.cs --root fixture
App/App.csproj  net10.0
```

`project` names the `.csproj` that compiles a file and the target framework it compiles it for.
A file no project compiles answers `no project` at exit 1 — which is also why `sym` cannot see
the types declared in it and `diag` reports nothing for it.

```
$ cslq sym Area --root fixture
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
$ cslq diag App/TypeError.cs --root fixture
App/TypeError.cs:18:36 error CS0029: Cannot implicitly convert type 'string' to 'int'
  17 | {
> 18 |     internal static int Wrong() => Greeter.Farewell("x");
  19 | }
```

`diag` takes a C# file (`.cs`, `.razor`, `.cshtml`), a directory, or nothing at all — with no
argument it walks every `.cs` file under `--root`, skipping `bin` and `obj`. Anything else, a
`.csproj` or a `.json` or a path above `--root`, is an argument error rather than a document
parsed as C#. It does *not* use `workspace/diagnostic`: the server answers that endpoint but
returns zero reports, which is what the `workspaceDiagnostics: false` in its dynamic
registration means.

Options: `--root <dir>` (default: cwd), `--sentinel <symbol>`, `--max N` (default 50),
`--context N` (default 1; inert for `outline`, `sym` and `hover`), `--timeout N` seconds
(default 180), `--log-level L`, `--errors-only` (`diag` only), `--tfm T`, `--json`.

`--tfm T` answers in one target framework's context. A file in a `net10.0;net9.0` project is
compiled twice, so a type inside `#if NET9_0` exists in one context and not the other, and
`hover`, `def` and `project` would otherwise answer for whichever context Roslyn happened to
bind the document to. They now ask every context in a fixed order and report which one
answered; `--tfm` narrows that to one, which is what "does net9.0 build" needs. A framework the
document has no context for is an error naming the ones it has.

`--json` wraps every command in the same `{ count, truncated, results }` envelope, `ready`
included — one result carrying `ready`, `projects` (how many projects were actually probed) and
two arrays naming the ones that were not, as directories relative to `--root` with forward
slashes, both empty in the ordinary case. `projects + skipped.length + unprobed.length` is every
project the root's solution yielded, so a partial readiness is visible rather than silent.

- `skipped` — the project's `.csproj` says it compiles nothing of its own, or draws its sources
  from outside its directory (a `*.projitems` import, `<Compile Include="../Shared/**">`) and
  owns no `.cs` under it. A hit for such a document sits under the source directory, so nothing
  can prove that project loaded. Fourteen of CommunityToolkit's twenty-six projects are that
  shape.
- `unprobed` — the project owns sources but declares no type the sentinel scan can read: only
  top-level statements, or only Razor or resources. There is nothing to ask the server for, and
  failing on it would break those projects outright, so readiness says nothing about them
  either way. About a dozen of OrchardCore's projects are this.

In text mode `ready` still prints the single word `ready`, so a shell test stays a string
comparison; at `--log-level Information` it names both classes on stderr.

Paths are relative to `--root`; lines and columns are one-based.

`--sentinel` *adds* a probe, it does not replace the inferred set. By default `cslq` waits for
*every* project under the root to load, one readiness probe per project the root's solution
lists; a root holding no solution, or two of them, is an error rather than a scan. Passing
`--sentinel` keeps all of those and adds one more probe, scoped to the root, carrying the symbol
you named. Replacing the set was the earlier behaviour, and it reopened the bug the per-project
set exists to close: on OrchardCore a `--sentinel` run answered `impl StartupBase` with 321 hits
against 331, at exit 0. The explicit probe stands alone only where inference finds nothing at
all — no solution, two solutions, no C# project, no candidate anywhere — which is the layout it
is the escape hatch for. It is not a project: `ready --json` neither counts nor lists it.

`refs` exits 1 with `no results` when a symbol resolves but has no references, and 1 with a
diagnostic when the symbol does not resolve or the workspace never loaded. `def`, `impl`, `sym`
and `hover` follow the same rule.

`diag` exits 0 whenever the query was answered, findings or not — a clean file is a successful
`diag`, unlike an empty `refs`, which means the lookup failed. It exits 1 only when the workspace
never loaded or the path does not exist.

### Symbol names

A dotted target is matched against the declaring document's **syntax tree**, so every segment
counts, namespaces included. The segments have to be a contiguous suffix of the declaration
path: `Greet`, `Greeter.Greet`, `Core.Greeter.Greet` and `Fixture.Core.Greeter.Greet` all select
`Fixture.Core.Greeter.Greet`, while `Wrong.Namespace.Greeter.Greet` and `Fixture.Greeter.Greet`
select nothing. Nested types are separated the same way — `Outer.Inner.Depth` does not match
`Other.Inner.Depth`.

A bare name selects the **type** when the only other candidates are its own constructors, which
Roslyn reports as separate same-named symbols. `Widget.Widget` still selects the constructors;
two constructor overloads are still ambiguous with each other.

When a target stays ambiguous — and when it matches nothing, under `candidates:` — `cslq` lists
the candidates as `sym` rows: `kind  name  container  path:line:col`, so a row can be pasted
straight back as a `file:line:col` target. They are ordered like `sym`'s, most relevant to the
name you typed first, and honour `--max` and say how many they dropped, like every other capped
output — a broad ambiguous target on a real repository is hundreds of rows otherwise.

`cslq` pins `DOTNET_CLI_UI_LANGUAGE=en` on the server so those display strings do not change with
the developer's machine locale.

## Latency

Measured on the fixture, Windows 11 / .NET 10.0.301, **Release build** (the configuration
`probes/run.sh` builds), three runs per command, 2026-09-07. The cold column is `--no-daemon`,
one private server per invocation:

| command | cold (per invocation) |
|---|---|
| `cslq ready` | ~4.4–5.8 s |
| `cslq refs` | ~7.6–8.8 s |
| `cslq def` | ~5.4–7.6 s |
| `cslq outline` | ~6.2–6.4 s |
| `cslq diag <file>` | ~9.2–10.9 s |
| `cslq diag` (whole fixture) | ~14.4–15.3 s over 12 files |

The same suite on `ubuntu-latest` reaches ready in ~12 s and runs six cases in ~39 s.

Milestone 1 started a dedicated server per invocation, so every command paid a full solution
load. Since Milestone 3 `cslq` connects to the shared daemon by default and the cost is a pipe
round-trip against an already-warm server; `--no-daemon` gets the old behaviour back. Same
fixture, same Release binary, 2026-09-07:

| command | non-daemon | daemon warm |
|---|---|---|
| `cslq ready` | 4.4–5.8 s | 2.5–2.6 s |
| `cslq refs` | 7.6–8.8 s | 2.9–4.8 s |
| `cslq def` | 5.4–7.6 s | 2.3–2.8 s |
| `cslq outline` | 6.2–6.4 s | 2.3–2.5 s |
| `cslq diag <file>` | 9.2–10.9 s | 2.9–6.1 s |

**This table is the fixture: three projects.** The daemon shares a server process, not a
loaded workspace — every attach re-runs the whole solution load, so the warm column scales with
the root's project count rather than staying flat. Measured 2026-09-10 on CommunityToolkit
(26 projects, restored not built): a warm `cslq ready` costs 28-33 s, of which 25-30 s is the
reload, and repeat attaches cost the same as the first. Above a few dozen projects the daemon
buys only process start and MEF composition, a couple of seconds, and `--no-daemon` costs about
the same per call while avoiding the queueing several concurrent clients hit. The cause is in
the server, not in `cslq`; see `FIXES-0.1.0.md`, batch 6.

About 2.5x on `refs`, with little variance across repeats once the daemon has seen the document
— the high end of each warm range is the first invocation, which still pays the `didOpen` and
the first bind of that document. The warm floor is `dotnet tool run` plus apphost startup plus
connecting the relay — not Roslyn — so it is a floor `cslq` cannot get under while it launches
through `dotnet tool run`.

One daemon is shared across every workspace on the machine, keyed by user identity and the
server's versioned path rather than by the root, and it outlives the client that started it
(900 s after the last client disconnects, by default). Two costs worth knowing:
`--log-level` is silently a no-op against a daemon someone else started, because the daemon
takes its configuration from whoever launched it; and the thin client falls back to a private
cold server without failing if it cannot reach the daemon, so `cslq` watches its stderr for that
and says `cslq: daemon unreachable` rather than leaving you to infer it from the latency.

The daemon used to inherit the stdout of whichever invocation launched it, so a piped or
captured launch — `cslq refs Foo | head`, `out=$(cslq def Bar)`, any agent harness that
captures every command — blocked for the whole keepalive and then returned with the daemon
already dead, which under the 900 s default read as a hang. `cslq` now clears the
inherit flag on its own std handles before it launches anything, so piping and capturing are
safe from the first command: measured on the fixture with a 20 s keepalive, the launching
`out=$(cslq ready --root fixture)` went from 25 s to 4 s, and the daemon survives it.

One case is left, and it is not `cslq`'s to fix: if you launch `cslq` from a process that
itself holds an inheritable capture pipe — a .NET `Process.Start` with
`RedirectStandardOutput`, whose *own* stdout is a pipe — that pipe is inherited into `cslq` as
an ordinary handle and travels on into the daemon. The concrete instance is a PowerShell-hosted
harness: measured with a 10 s keepalive, bash `out=$(cslq ready)` returns in 4 s but
`out=$(pwsh -c '$x = & cslq ready; $x')` takes 18 s, which is what an agent whose shell tool is
PowerShell — Claude Code on Windows, Actions `shell: pwsh` — sees. Redirect the intermediary to
a file rather than capturing it, take the launching call as an unredirected `cslq ready`, or
pass `--no-daemon`, whose private server exits with the client.

`probes/run.sh` scopes itself to its own daemon with
`ROSLYN_LANGUAGE_SERVER_DAEMON_PIPE_NAME` and a short keepalive, so the gate cannot inherit a
stale workspace and its opening `cslq ready` is still a real cold load. Its
`daemon-survives-captured-stdout` leg, Windows only, is what holds the paragraph above:
`probes/stdout-capture.cs` starts `cslq ready` under `RedirectStandardOutput` and asserts it
returns well inside the keepalive with the daemon still listening.

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

A PR opened with `GITHUB_TOKEN` does not get a working `probe.yml` run on its own: GitHub
creates the run with `github-actions[bot]` as the actor and parks it at `action_required`,
and it never executes unless someone approves it. `bump.yml` therefore runs the probes itself
before opening the PR — a broken pin never becomes a PR — and then dispatches `probe.yml` on
the PR branch with `gh workflow run`. `workflow_dispatch` (like `repository_dispatch`) is
exempt from the `GITHUB_TOKEN` trigger suppression, and the check runs it produces land on the
branch head under the same `probe (<os>)` names, so a bump PR carries the same three-OS gate a
human PR does. A ruleset on `main` requires those three checks and a pull request, with no bypass, so
the gate is enforcing rather than informational.

`dependabot.yml` watches the action pins and the NuGet references under `src/` and `tests/`,
monthly and grouped into one PR each. It is scoped away from `.config/dotnet-tools.json` on
purpose: the server pin is `bump.yml`'s, and moves only through the gate.

### Releasing

A release is a tag. On the merged `main` commit, `git tag v<Version> && git push origin
v<Version>`, where `<Version>` is the value in `src/Cslq/Cslq.csproj`. `release.yml` fires on
`v*` and nothing else, and its first step re-reads that `<Version>` and refuses any tag that
does not equal it — before the gate, because the tag is what names the package and a
disagreement would publish a version nobody asked for.

What it then runs, in order: `dotnet format --verify-no-changes`, so no path to nuget.org skips
the format check; `probes/run.sh`, the same gate every PR runs; `dotnet pack -c Release`;
`NuGet/login@v1`, which exchanges the job's OIDC token for a one-hour nuget.org key so no
long-lived secret exists to leak; `dotnet nuget push --skip-duplicate`, so a re-run of an
already-published version is a no-op rather than a failure; and last, `gh release create
--generate-notes` with the `.nupkg` attached, so every version on nuget.org is bound to a
GitHub Release of the same tag. The notes are the PRs merged since the previous tag, and a
bump PR's title names the server version it pins, which is what tells `0.1.3` from `0.1.4`.
The release step comes after the push so a red gate leaves nothing behind, and skips itself
when the release already exists.

`bump.yml` moves `<Version>` with every new pin, so merging a bump PR is followed by tagging it
— a merged bump is a release like any other. Nothing tags automatically. That is deliberate
until the flow has run once for real.

Two prerequisites live outside the repo: a `release` environment on GitHub, and a trusted
publishing policy on nuget.org naming this repo, `release.yml` and that environment.

`0.1.0` is the first release and goes out as-is — no `rc`.

## Dependencies

`cslq` depends on **StreamJsonRpc** (Microsoft, MIT) for `Content-Length` framing, request
correlation and notifications. Nothing C#-specific is third-party.

The LSP payload types in `src/Cslq/Protocol.cs` are ours, which is a deliberate departure — no
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
generator cannot be written without it. It is fixture-only and never loaded by `cslq`, so the
query path stays free of C#-specific third-party code. The version is pinned **low** (4.3.0,
well past `IIncrementalGenerator`'s introduction) and deliberately never tracks the server:
the analyzer is loaded by two independently-moving compilers — the SDK's `csc` during
`dotnet build` and the language server's hosted Roslyn — and Roslyn only ever moves forward,
so a low pin is permanently compatible while a tracking pin would need re-verifying on every
weekly bump.

## Failure modes this handles

| Failure mode | How |
|---|---|
| Async project load returning empty instead of erroring | `WaitReadyAsync` polls one sentinel symbol per project until every project that has one resolves it, then fails loudly on timeout. A project the scan could infer no sentinel for — one that is only top-level statements, or only Razor or resources — is not waited on, because there is nothing to ask the server for; it is named on the failure path instead, so its absence from readiness is visible rather than silent. Never `sleep`, and never block on `workspace/projectInitializationComplete` — it arrives at the end of the solution load the client's own `initialized` triggered, which the daemon re-runs on every attach, so blocking on it first buys nothing and costs the whole load. |
| A sentinel that is itself the thing being queried | Sentinels are inferred from type declarations in each project, so "symbol absent" and "workspace not loaded" stay distinguishable. No grace poll on the target: readiness covering every project is what makes an empty answer mean absent. |
| UTF-16 position encoding | The server does not advertise `positionEncoding`, which per LSP 3.17 means utf-16 — the same unit as a .NET string index. `cslq` asserts this at `initialize` and refuses to run if a future build negotiates utf-8. A fixture line carrying an astral-plane character (a surrogate pair, so utf-16 and rune counts differ) pins the reported column at 39 in three cases; an accented letter would pass even on a broken implementation. |
| A first diagnostic pull under-reporting on an unbound document | `textDocument/diagnostic` does not answer from the misc-files state and then correct itself — it **blocks until the document is bound**, so `diag` pulls once and the settle loop that used to wrap it is gone. Measured 2026-09-06: a cross-project error opened as the first document in a never-used server returns the right code on pull #1 (~4.2 s), and a second pull (~0.7 s) never once differed across six whole-fixture runs, cold and warm. (A document in **no** project is a different case: it reports nothing at all, whatever the error class. See `DESIGN.md`.) The fixture's error is deliberately *cross-project* — binding it needs Core's reference resolved — and `cold-server-diag-reports-cross-project-error` opens it as the first document of a dedicated server, which is the only state where answering early would show. |
| Roslyn ignoring unopened documents | Every query opens its document via `textDocument/didOpen` first — except source-generated ones, which the server owns and answers for without it. |
| No auto-restore | `probes/run.sh` runs `dotnet restore` on the fixture before starting the server. |
| A framework or NuGet symbol rendering as a machine-absolute temp path | Roslyn answers for one from a document it decompiles under `<temp>/MetadataAsSource/<guid>/.../<Type>.cs`. `PathUri.Display` labels it `<metadata>/<assembly>/<TypeName>.cs`, reading the assembly off the `#region Assembly` header in the document, since the URI carries only the type name. `def` at `Console.WriteLine` used to print the raw path — after a 10 s stall in the decompilation guard, which now re-asks only when the workspace also declares that type. See `DESIGN.md`. |
| Source-generated symbols rendering as a nonexistent path | Generated documents come back under a `roslyn-source-generated:` URI. `new Uri(u).LocalPath` does not throw for one, it returns `/BuildInfo.g.cs`, so `PathUri.Display` branches on the scheme and labels them `<generated>/<project>/<assembly>/<generator type name>/<hintName>`, mirroring what `EmitCompilerGeneratedFiles` writes on disk — the generator type is what separates two generators in one assembly emitting the same `hintName`. The project comes from `textDocument/_vs_getProjectContexts` — the URI names only the generator, so without it one generator serving several projects renders every one of its documents identically. Text comes from `workspace/textDocumentContent`. |
| An unbuilt source generator contributing nothing, silently | With the analyzer assembly absent the workspace still loads and the sentinel still resolves; only the generated symbol is missing, with no error or diagnostic anywhere. `probes/run.sh` builds `fixture/Gen` before starting the server, and three cases assert the generated symbol resolves. |
| Server-to-client requests faulting the connection | `LspClient.Endpoints` answers `workspace/configuration`, `client/registerCapability`, `window/workDoneProgress/create` and friends. |
| A renamed server flag failing silently | The thin client forwards unrecognised options straight through to the server, so a rename produces no error. Flags live only in `src/Cslq/ServerArgs.cs`, and the probes are the only guard. |

## Probes

```
./probes/run.sh
```

Runs `tests/Cslq.Tests` first, then restores the tool and the fixture, builds `cslq`, asserts
readiness, and runs every case in `probes/cases.jsonl`. Exits non-zero on any mismatch.

**88 legs today = the 78 rows in `cases.jsonl` + 10 scripted ones.** The scripted ten are the
three source-generator staleness legs, the framework `def` (whose two failure modes are an
absence and a duration, neither of which a `expect` substring can pin), the forced non-daemon
fallback, the cold-server `diag`, the packaged-tool install, the two first-run failures (a root
with no solution, and `dotnet` off `PATH`), and the restore that rebuilds the tool-resolver
cache. Quoting the composition rather than the total is deliberate: the next time the two
halves drift, the sum stops adding up here rather than going quietly stale. The rows include a
negative case that pins a query fired before load to a loud failure rather than an empty result.

The unit tests come first because they cost under a second and need no server: they cover the
pure logic below the transport — sentinel inference, argument parsing, path and URI rendering,
and the `sym` cap ordering — including the shapes `fixture/` cannot hold, such as prose in a
doc comment above the only declaration in a single-file project. Anything that needs a live
server belongs in a case, not a test.

`probe.yml` runs the gate on every PR and every push to `main`, as a `fail-fast: false` matrix
over `ubuntu-latest`, `windows-latest` and `macos-latest`. All three legs run `./probes/run.sh`
through the runner's `bash`, which on Windows is Git Bash — the two host-dependent cases, the
non-ASCII ones and the forced non-daemon fallback's named mutex, are what the Windows leg
watches, and `fail-fast: false` keeps a Windows-only red from cancelling the Linux and macOS
legs that say whether it is platform specific. Each leg runs
`dotnet format --verify-no-changes` before the gate; `release.yml` and `bump.yml` run it before
theirs too, so no path to a release skips it.

`cases.jsonl` is one flat JSON object per line with four string fields so `run.sh` can parse it
with `sed` alone — no `jq`, which is absent from Git Bash on the dev machine. That keeps it
running unchanged on a GitHub runner and in Git Bash.
Inside `expect`, `'` stands for `"` and `|` separates substrings that must all appear.

## Layout

```
Cslq.slnx                    src/Cslq + tests/Cslq.Tests; fixture/ is deliberately not in it
.config/dotnet-tools.json   the version pin
.github/workflows/          bump.yml (weekly cron), probe.yml (every PR, linux + windows + macos)
src/Cslq/                    the thin LSP client and CLI
  ServerArgs.cs             the only place server flags live
  Protocol.cs               hand-defined LSP payload types
  LspClient.cs              transport, initialize, readiness, didOpen
  Output.cs                 path:line + context formatting, and the outline tree
skills/                     the agent skill; the directory name is what `npx skills` installs as
  csharp-semantic-queries/SKILL.md
fixture/                    deliberately tricky solution
  Gen/                      incremental source generator; its output is referenced from App
  Ambient/Stray.cs          a document no project compiles, for the misc-files cases
  App/TypeError.cs          the deliberate cross-project type error for `cslq diag`
  App/Square.cs             the cross-project implementer of `Core/Shape.cs`, for `impl`
  Core/Party.cs             an astral-plane character on a line carrying a symbol
  Core/Split*.cs            one type in two documents, plus an overload in one of them
  Core/Empty.cs             a compilable document that declares nothing
  Core/Shape.cs             an interface whose implementers straddle two projects, for `impl`
tests/Cslq.Tests/            unit tests for the pure logic below the transport
probes/                     cases.jsonl + run.sh (runs tests/Cslq.Tests first)
```
