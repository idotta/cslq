# C# Language Query

Semantic C# queries for coding agents, over Microsoft's official
[`roslyn-language-server`](https://www.nuget.org/packages/roslyn-language-server) — the same
engine behind the VS Code C# extension. `cslq` is a thin LSP client that turns LSP's URIs and
zero-based ranges into `path:line` plus source context, so an agent can find every caller of a
method instead of grepping for its name.

Two constraints drive the design:

1. **Official tooling only.** The C#-specific component in the query path is Microsoft-published.
2. **Always current.** A weekly cron bumps the pin and a probe suite gates the bump.

Status: **all five milestones done; `0.3.0` adds the background session on top of `0.2.0` and
the eight fix batches behind it** (PRs #21-#28: readiness per project, symbol targeting,
multi-targeted contexts, and one rule for non-answers and usage errors). Eleven commands —
`ready`, `refs`, `def`, `impl`, `hover`, `sym`, `outline`, `diag`, `project`, `restore` and
`session` — over a cross-project fixture with source-generated, non-ASCII, metadata and
deliberate-error cases; a session holding the loaded workspace open between calls, so a warm
query is ~150 ms rather than seconds; the shared server daemon on by default; `skills/cslq/SKILL.md` for the
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

`skills/cslq/` is what makes an agent reach for `cslq` instead of grep. Two plain markdown
files: `SKILL.md`, which the agent loads whenever the skill fires, and `REFERENCE.md` beside it,
which it reads only for the rare case. Installing means putting the directory where the agent
looks:

```
npx skills add idotta/cslq -g
```

That is [`npx skills`](https://github.com/vercel-labs/skills), which reads GitHub as its
registry rather than a package feed: it detects the coding agents on the machine and installs
to each one's skills directory — `~/.claude/skills/cslq/` for Claude Code.
Drop `-g` to install into the current repository (`.claude/skills/...`) instead, which is what
to do when only one project you work on is C#. It is the one step that wants Node; the
prerequisites above do not.

Without `npx`, copy the directory — that is the whole of the installation, and the skill needs
nothing else on disk. For Claude Code it goes to `.claude/skills/cslq/` in the repository you
want to query, or the same path under `~` to have it everywhere; the `name` and `description` in
`SKILL.md`'s frontmatter are what Claude Code matches against. Other agents read the same files —
their bodies name no tool but `cslq` — so paste them into whatever that agent takes as standing
instructions.

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
dotnet restore                 # not required, but a restore that fails is invisible without it
cslq ready --root <dir>
```

The server does restore: measured 2026-09-10 on a never-restored solution, `cslq ready`
answered in 7 s and `obj/project.assets.json` appeared afterwards. Restoring first is still
worth the second it costs, because the boundary is a restore that *fails* — an unresolvable
`PackageReference` leaves every project loading empty, and `cslq` can then only tell you that
it happened, not which package it was. `dotnet restore` tells you the package.

`--root` must be **the directory holding the `.sln` or `.slnx`** — `cslq` loads the projects
that solution lists. A root with no solution at its top is an error, reported in about a second
rather than after the timeout, and a solution one directory down does not count. A checkout
whose path contains a `%XX` sequence (`.../pct%20x`) never loads at all — MSBuild unescapes it,
so `dotnet restore` on such a tree fails too — and nothing in `cslq` can fix that: rename or
move the checkout.

Two `.csproj` in one directory are indistinguishable to readiness, since a hit under that
directory cannot be attributed to one of them; the second is covered only incidentally. That
limit is worse in practice than it sounds, and it does not announce itself: on such a pair,
testers measured `sym BType` answering with a fuzzy `AType` row at exit 0 — a *wrong* row
rather than a missing one, because `sym` matches approximately — while `hover` and `def` at a
position inside `BType.cs` answered `no results`. `--no-daemon` answered both correctly. Give
each project its own directory; there is no flag that fixes this one.

The discovery above — read the root's solution, fail fast when there is none — is written for
the **daemon**, which is the default, and `--no-daemon` is measurably more forgiving. Measured
2026-09-10 on a staged tree: a root whose solution sits one directory down is ready in 6 s
under `--no-daemon` and times out at 45 s under the daemon, with the same `--sentinel`. A root
holding a bare `.csproj` and no solution at all loads under neither on the current server pin,
though testers saw it load under `--no-daemon` on 0.1.0's; a `.slnf`-only root was theirs to
measure and has not been re-checked here. So the rule to work to is the one the fail-fast
states, and a layout that only `--no-daemon` can load is a layout to fix rather than a mode to
switch to.

A solution listing VB.NET or F# projects gets one stderr line — `cslq: 2 non-C# project(s) not
searched: Legacy.vbproj, Calc.fsproj` — and then answers for its C# projects as usual. The
server is a C# one, so those projects are not loaded, and the part that would otherwise be
invisible is that a reference to a C# symbol *from* VB or F# source is simply absent from
`refs` at exit 0.

## Use

```
cslq ready                                    # block until the workspace has loaded
cslq refs <symbol | file:line:col> [--tfm T]  # every reference, with context
cslq def <symbol | file:line:col> [--tfm T]   # where it is declared
cslq impl <symbol | file:line:col> [--tfm T]  # what implements or overrides it
cslq hover <symbol | file:line:col> [--tfm T] # what it is: type, signature, docs
cslq sym <query> [--max N]                    # search the workspace by name
cslq outline <file | symbol> [--tfm T]        # the declarations in one document
cslq diag [path] [--errors-only] [--tfm T]    # compiler and analyzer diagnostics
cslq project <file> [--tfm T]                 # every .csproj + TFM that compiles it
cslq restore                                  # fetch the pinned language server, then exit
cslq session <status | stop>                  # the background session for these options
```

Add `--no-session` to any of them to load the workspace in this process instead of asking the
background session that holds it open between calls, and `--no-daemon` to start a private
Roslyn server instead of sharing the machine-wide one. They are different things; see
[The session](#the-session) and [Latency](#latency).

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
declaration's own source line, indented by nesting. `--context` does not apply to it. Where
several declarations start on one line — `public enum Colour { Red, Green, Blue }`, or a
multi-declarator field — printing "that declaration's own source line" printed the line once
per declaration, so those rows print the declaration's **own span** instead and their gutter
grows the identifier's column:

```
$ cslq outline Core/Kinds.cs --root fixture --max 5
Core/Kinds.cs
      1 | namespace Fixture.Core;
  13:13 |   public enum Colour { Red, Green, Blue }
  13:22 |     Red
  13:27 |     Green
  13:34 |     Blue
```

The column is the one `--json` already reported, so a crowded row is still a `line:col` you
can paste straight back as a target. A document whose declarations each have a line to
themselves renders exactly as it always did. Its
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

**`hover` drops every `<param>` doc.** The signature carries the parameter *types*, and the
summary and `<remarks>` come through, but the per-parameter prose does not: Roslyn's QuickInfo
does not put it in the hover response, so there is nothing for `cslq` to print. It is the part
of a doc comment an agent most wants before writing a call, so when it matters, `def` the
symbol and read the doc comment at the declaration.

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
A file no project compiles answers `cslq: no project` on stderr at exit 1 — which is also why
`sym` cannot see the types declared in it and `diag` reports nothing for it.

```
$ cslq sym Area --root fixture
method  Area  in Square (project App (net10.0))   App/Square.cs:12:16
method  Area  in IShape (project Core (net10.0))  Core/Shape.cs:11:9
method  Area  in Unit (project Core (net10.0))    Core/Shape.cs:16:16
```

Kinds are one table across `sym` and `outline`, and two of them are worth knowing. A
**constructor** renders as `constructor`: LSP has the kind and Roslyn reports one as a method
from both requests, so `cslq` recovers it from the declaring type's name.

**A delegate renders as `method`, and that is `cslq` levelling the two commands rather than
Roslyn's answer.** `workspace/symbol` does say `function` for one — but
`textDocument/documentSymbol` says `method`, and carries nothing else to tell a delegate from
a method, so `outline` cannot be raised to match. The kind is levelled down instead: a kind
that depends on which command you asked was the bug, and LSP has no `delegate` kind to invent.
So a `method` row may be a delegate, and `sym` no longer distinguishes one. Three shapes in
all that the kind table cannot express and nothing is invented for: a **delegate** reads as
`method`, a **record** as `class` or `struct`, and a C# 14 **`extension` block** as `class`.

A source line longer than 200 characters is elided in text mode, around the column the row is
about, with a `…` at each cut end — a hit on a 20,079-character line used to print the whole
line. The `path:line:col` above the row is untouched and is what round-trips; a column counted
off the printed text does not. `--json` keeps the line whole, so a machine consumer can slice
it for itself.

A file a project compiles from outside `--root` — a `<Compile Include="../../Elsewhere/File.cs" />`
— is indexed as fully as any other, and prints as `<external>/<path relative to the root>`
rather than as the machine-absolute path it used to. Every JSON row carries `external` beside
`generated` and `metadata`, so no label prefix has to be parsed back off `path`.

**Like `<generated>/` and `<metadata>/`, it is a label and not a target.** The `..` in it says
where the file is so you can open it, not that you can hand it back: every file-taking command
rejects a path outside `--root` before it starts the server, so `cslq outline
<external>/../Elsewhere/File.cs` exits 1. Point `--root` at a directory containing both trees
if you need to query one of these files, or reach its declarations by name with `sym` and
`def`, which answer for it perfectly well.

`sym` is a search, so the query goes to the server as written and every answer is a result —
no ambiguity error, no candidate dump. It is the second command that bends the output rules,
more narrowly than `outline`: it keeps `path:line:col` on every row but prints no source line
and no `>` marker, so `--context` is inert for it. The container column is Roslyn's localised
display text, not a namespace path, and is there to separate two symbols that share a name.

**And the search is fuzzy, which is the thing to know about it.** `workspace/symbol` is the
matcher behind Ctrl+T in an IDE, so a row means "this is close to what you typed", never "this
name exists". Measured on the fixture 2026-09-10:

| Query | Matches | Because |
|---|---|---|
| `Vol` | `Volume`, `AudioVolume` | prefix, and substring — `AudioVolume` has no such prefix |
| `eter` | `Greeter` | substring |
| `AV` | `AudioVolume` | camel humps |
| `Greter` | `Greeter`, `Greet`, `Green` | a dropped letter is still a match, and so is a near miss |
| `VOL` | nothing | an ALL-CAPS query is read as humps or as a whole name, never as a prefix |
| `VOLUME`, `AUDIOVOLUME` | `Volume`, `AudioVolume` | a whole name still matches, case ignored |
| `Fixture`, `Core` | nothing | namespaces are not indexed |
| `*`, or nothing | nothing | neither is a pattern; there is no "list everything" |

Case is ignored otherwise. Declarations are what is indexed — types, members and local
functions, but never locals or parameters — and matches are ranked before `--max` cuts them,
most relevant to the name you typed first, so a truncated list is the useful end of the list.
When you need an exact answer rather than a close one, use `def`, whose dotted targets are
matched segment by segment against the syntax tree.

```
$ cslq diag App/TypeError.cs --root fixture
App/TypeError.cs:18:36 error CS0029: Cannot implicitly convert type 'string' to 'int'
  17 | {
> 18 |     internal static int Wrong() => Greeter.Farewell("x");
  19 | }
```

`diag` rows carry no `source`. LSP has the field and this server never sends one: measured
2026-09-10 over `fixture`, `fixture2` and this repository, 53 findings — compiler `CS`, IDE
analyzer `IDE` and `Microsoft.CodeAnalysis.NetAnalyzers` `CA` alike — every one of them null.
A key that could only ever be null is dropped rather than emitted.

`diag` takes a C# file (`.cs`, `.razor`, `.cshtml`), a directory, or nothing at all — with no
argument it walks every `.cs` file under `--root`, skipping `bin` and `obj`. Anything else, a
`.csproj` or a `.json` or a path above `--root`, is an argument error rather than a document
parsed as C#. It does *not* use `workspace/diagnostic`: the server answers that endpoint but
returns zero reports, which is what the `workspaceDiagnostics: false` in its dynamic
registration means.

Options: `--root <dir>` (default: cwd), `--sentinel <symbol>`, `--max N` (default 50),
`--context N` (default 1; inert for `outline`, `sym` and `hover`), `--timeout N` seconds
(default 180), `--log-level L`, `--errors-only` (only `diag` reads it; it is accepted and inert
elsewhere, unlike `--tfm`, which is rejected where it would filter nothing), `--tfm T`,
`--json`.

**Options may appear anywhere** — before the command, between the command and its argument, or
after both — the way every other `dotnet` CLI takes them. The set is closed and each member is
either a flag or takes exactly one value, so the positionals are simply what is left:
`cslq --root . --timeout 600 def ContentItem` and `cslq def ContentItem --root . --timeout 600`
are the same invocation. A value is consumed by the option that asked for it, so
`--sentinel ready refs Greet` runs `refs`.

`--log-level L` takes one of the seven names the server's own `--logLevel` parses — `Trace`,
`Debug`, `Information`, `Warning` (the default), `Error`, `Critical`, `None` — and anything else
is rejected before a server starts. It cannot change a daemon that is already running: see
[Latency](#latency).

`--tfm T` is rejected by `sym`, `ready` and `restore` rather than accepted and ignored.
`workspace/symbol` is context-independent and the other two resolve no document, so there is no
project context for the option to choose; it used to filter nothing and leave the caller reading
an unfiltered answer as filtered.

`--tfm T` answers in one target framework's context. A file in a `net10.0;net9.0` project is
compiled twice, so a type inside `#if NET9_0` exists in one context and not the other, and every
command that asks something *of a document* would otherwise answer for whichever context Roslyn
happened to bind it to — which was not stable between runs.

Each of them now handles that itself, and the rule follows the shape of the answer.
`hover` and `def` return one thing, so they ask the contexts in a fixed order and stop at the
first that answers, reporting which: `answered in net9.0 of 2 contexts: net10.0, net9.0`.
`refs`, `impl`, `outline` and `diag` return a *set*, so they ask every context and union the
results — a reference inside an `#if NET9_0` block exists only in that context, and stopping
early would drop it — and say `merged from 2 contexts: net10.0, net9.0`. `outline` marks the
declarations that are not in every context (`public sealed class Only9  [net9.0]`) and `diag`
marks the diagnostics that are not reported by every one, which is how a `net9.0`-only error
becomes visible at all. `project` lists every context, one row each.

So `--tfm` is not needed to get a correct answer; it is for asking a *specific* framework —
"does net9.0 see this", "does net8.0 build" — and for the case where you want one view rather
than the union. A framework the document has no context for is an error naming the ones it has.
Under `--json`, `contexts` on the envelope is how many the document has, `tfm` is the context
that answered where one did — absent rather than null where none did, which is every union —
and `outline` and `diag` rows carry their own `tfm`, null when every context agrees.

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

### Exit codes

| Code | Meaning |
|---|---|
| 0 | The query was answered. A clean `diag` and an empty `outline` are answers |
| 1 | The query failed: no results, no such symbol, an ambiguous target, no such file, the workspace never loaded |
| 2 | The invocation could not be understood: no command, an unknown command or option, a missing or invalid option value, an argument the command does not take |
| 127 | An unhandled internal failure — a bug; the stack trace is the report |
| 130 | Interrupted (Ctrl+C) |

Exit 2 follows the same rule as every other failure: the `cslq: ` line and the usage block go
to **stderr**, and `--json` still puts `{ "error": "<message>" }` on stdout. `--help` and
`--version` are answers, so they stay exit 0 on stdout.

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

**A workspace of nothing but top-level statements is the shape that needs it, and the obvious
`--sentinel` for it is a trap.** `dotnet new console` declares no type at all, so inference has
nothing to read and refuses in about 90 ms — correctly, but the repair anyone reaches for,
`--sentinel Program`, waits out the entire timeout: the `Program` class such a file compiles to
is generated by the compiler and `workspace/symbol` does not index it. Any method or local
function declared in that file does resolve, in about two seconds, and the refusal now says so.
The same shape as one project among many is the documented limit above it: nothing probes it,
it is named on the readiness failure path, and a query against it can answer at exit 0 while
that project is still loading.

`refs` exits 1 with `cslq: no results` when a symbol resolves but has no references, and 1 with a
diagnostic when the symbol does not resolve or the workspace never loaded. `def`, `impl`, `sym`
and `hover` follow the same rule.

`diag` exits 0 whenever the query was answered, findings or not — a clean file is a successful
`diag`, unlike an empty `refs`, which means the lookup failed. It exits 1 only when the workspace
never loaded or the path does not exist.

**One rule for non-answers and failures.** In text mode stdout carries the answer and nothing
else: every non-answer that exits non-zero — `no results`, `no project` — and every failure is
one `cslq: `-prefixed line on **stderr**, so a script may treat stdout as data. `diag`'s
`no diagnostics` and `outline`'s `no symbols` stay on stdout because they exit 0, which makes
them answers; the exit code is what says which stream to read. With `--json` the answer is
always a JSON object on stdout, on the failing paths too: an empty answer is the ordinary
`{ "count": 0, "truncated": false, "results": [] }` envelope, and a failure is
`{ "error": "<message>" }` with the same human line still on stderr. An answer envelope never
carries `error` and an error object never carries `count`, so one field tells them apart.

```
$ cslq refs NoSuch --root fixture --json
{
  "error": "no symbol matched 'NoSuch'"
}
```

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

**A CJK identifier may not be findable by name, and this one is an open question rather than a
documented limit.** Testers measured, from PowerShell where the argv arrives intact, `refs 名前`
and `sym 名前` answering nothing while the same declaration answered correctly by
`file:line:col`; `Größe` and `ﬁle` (U+FB01, which folds to `fi`) were both found by name in the
same run. It has not been reproduced here, Cyrillic and Greek were never tried, and there are
two candidate causes nobody has separated — `workspace/symbol`'s matcher on a script with no
case, or the argv itself. From Git Bash the same command is certainly argv: MSYS hands a native
.NET process its arguments through the ANSI code page and `cslq` sees `??`, which nothing
`cslq` does can undo. If you hit it, query by position; a `file:line:col` target never goes
through the matcher.

## The session

`cslq` keeps a **session** — a background `cslq` process of its own that holds the loaded
workspace open between calls — and uses it by default. The first call pays the solution load;
every later one is a round trip to a process that is already holding the answer. On the fixture
that is 4.9–7.6 s and then 138–188 ms on Windows, against 2.2–2.4 s for every call without
it. The gate times the same warm `hover` with and against `--no-session` on every platform it
runs: 190 ms vs 2951 ms on windows, 131 ms vs 2377 ms on ubuntu, 67 ms vs 1429 ms on macos.

**The session is not the daemon, and they are independent.**

| | the session | the daemon |
|---|---|---|
| what it is | a `cslq` process holding a loaded workspace | a Roslyn `roslyn-language-server` process |
| whose | `cslq`'s | Microsoft's, shared machine-wide |
| how many | one per root (per log level, per daemon-or-not, per user, per `cslq` version) | one per user and server version |
| what it saves | the solution load, which the daemon does not save | process start and MEF composition |
| opt out | `--no-session` | `--no-daemon` |

`--no-daemon` means exactly what it always meant: a private Roslyn server. A session with
`--no-daemon` is an ordinary session that happens to own its server. Opting out of both is the
Milestone 1 behaviour, one cold everything per invocation.

**When it starts.** On the first command that needs a workspace, and never for `restore` (no
workspace to hold) or for `cslq session status`/`cslq session stop` (which are *about* one).
Starting it is guarded by a named mutex, so two `cslq` calls racing at a cold prompt produce one
session rather than two. A client that cannot get that lock within 10 s loads the workspace
itself and says `cslq: session unavailable; this run loaded the workspace itself` on stderr —
the answer is still correct, just slow.

**When it stops.** After 900 s idle, on `cslq session stop`, or with the terminal that owns the
console it was spawned from. It ignores Ctrl+C on purpose: it is shared background state, not a
child of whoever happened to spawn it, so a Ctrl+C in one shell must not take it down under
every other client. It is keyed by the `cslq` version, so an upgraded `cslq` never talks to a
session running the old code — it starts its own and the old one idles out.

**What it does about your edits.** A one-shot `cslq` re-read every file it touched, because the
process died with the answer. A session holds `didOpen` across your edits, and Roslyn owns an
open document's text rather than re-reading the file — so before every request the session
stats each open document and re-sends the text of any file that changed, closing the ones that
were deleted. Without that a session would answer from the text as it first read it: wrong line
numbers, wrong context lines, a renamed symbol still found, all at exit 0.

**The levers.**

```
cslq session status          # running or not, the pipe, the root, the pid, the log
cslq session stop            # end it; not an error if there was none
--no-session                 # this call loads in-process instead
CSLQ_SESSION_PIPE_NAME       # override the derived pipe name outright
CSLQ_SESSION_KEEPALIVE       # seconds idle before it exits (default 900, -1 never)
```

`session status` and `session stop` act on the session *the current options would reach*, so
`--root` and `--no-daemon` pick which one exactly as they do for a query. `--json` works on
both. The session writes nothing to your terminal; its console goes to
`<temp>/cslq-session-<pipe>.log`, which `session status` names.

`CSLQ_SESSION_PIPE_NAME` **replaces** the derived name rather than seeding it, so one exported
value puts every root on one pipe and the first session to start declines every other root's
requests. Export it for one root, or not at all. `probes/run.sh` derives one name per root from
a per-run prefix for exactly this reason, and kills every session it started on the way out.

One thing changes in the output and nothing else does: a session buffers a whole response and
writes it when the command finishes, so a long `cslq diag` walk no longer streams its rows as
it goes. Same rows, same streams, same exit code, all at the end.

## Latency

Since the session landed, **the number that matters is not in the tables below**. They measure
the path that threw the attach away: `--no-session`, which every call used to take. Measured on
the fixture (4 projects), Windows 11 / .NET 10, **Release build** (the configuration
`probes/run.sh` builds), 2026-09-11:

| | through the session (default) | `--no-session`, daemon warm |
|---|---|---|
| first call, cold session | 4.9–7.6 s | — |
| `cslq hover` | 138–188 ms | 2.2–2.4 s |
| `cslq def` | 229 ms | — |
| `cslq sym` | 182 ms | — |
| `cslq outline` | 140 ms | — |
| `cslq ready` | 145 ms | — |
| `cslq refs` | 655–734 ms | — |
| `cslq session status` | 158 ms | — |
| `cslq --version` (starts nothing) | 69–77 ms | 69–77 ms |

**The honest trade is the first row**: the first call into a cold session costs *more* than a
one-shot did, because it pays the same load plus a process start, and every call after it is a
round trip. On four projects that is one call at ~5 s buying calls at ~150 ms; on 26 projects it
is one call at ~30 s buying the same ~150 ms, which is where the session earns most. `--version`
is the floor a call that starts nothing sits at, and the ~150 ms warm figure is within a factor
of two of it: what is left is process start and a pipe round trip, not Roslyn.

Every figure in that table is Windows, and a latency number is a claim about one platform. The
only figures measured everywhere are the gate's own, from `session-beats-no-session` on PR #30:
the warm `hover` costs 190 ms against 2951 ms under `--no-session` on windows, 131 ms against
2377 ms on ubuntu, and 67 ms against 1429 ms on macos.

The rest of this section is the one-shot path — what `--no-session` costs, and what the daemon
does and does not buy underneath it.

Measured on the fixture, Windows 11 / .NET 10.0.301, **Release build** (the configuration
`probes/run.sh` builds), three runs per command, 2026-09-07, every call `--no-session`. The cold
column is `--no-daemon`, one private server per invocation:

| command | cold (per invocation) |
|---|---|
| `cslq ready` | ~4.4–5.8 s |
| `cslq refs` | ~7.6–8.8 s |
| `cslq def` | ~5.4–7.6 s |
| `cslq outline` | ~6.2–6.4 s |
| `cslq diag <file>` | ~9.2–10.9 s |
| `cslq diag` (whole fixture) | ~14.4–15.3 s over 12 files |

The file count in that last row is the fixture as it stood on the measurement date; it holds 19
`.cs` files today, so read the row as a per-file cost and not as a current total.

A whole `probe.yml` run — checkout, restore, build and every leg — took 9-12 minutes over
PRs #24-#28, `ubuntu-latest` included.

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

**This table is the fixture: three projects, and every call is `--no-session`.** The daemon
shares a server process, not a loaded workspace — every attach re-runs the whole solution load,
so the warm column scales with the root's project count rather than staying flat. That is what
the session exists to fix, and it is why the daemon alone was never enough: it saves process
start, and the solution load is the expensive part. Measured 2026-09-10 on CommunityToolkit
(26 projects, restored not built): a warm `cslq ready` costs 28-33 s, of which 25-30 s is the
reload, and repeat attaches cost the same as the first. Above a few dozen projects the daemon
buys only process start and MEF composition, a couple of seconds, and `--no-daemon` costs about
the same per call while avoiding the queueing several concurrent clients hit. The cause is in
the server, not in `cslq`: each attach runs `AutoLoadProjectsInitializer`, starts a fresh
`BuildHost` and reloads every `.csproj`, and the only lever a client holds — not sending
`workspaceFolders` — leaves it with an empty workspace instead. The measurements and the log
trace behind that are in `CLAUDE.md`, under the daemon bullet.

About 2.5x on `refs`, with little variance across repeats once the daemon has seen the document
— the high end of each warm range is the first invocation, which still pays the `didOpen` and
the first bind of that document. The warm floor is `dotnet tool run` plus apphost startup plus
connecting the relay — not Roslyn — so it is a floor `cslq` cannot get under while it launches
through `dotnet tool run`.

One daemon is shared across every workspace on the machine, keyed by user identity and the
server's versioned path rather than by the root, and it outlives the client that started it
(900 s after the last client disconnects, by default). Two costs worth knowing:
`--log-level` is a no-op against a daemon someone else started, because the daemon takes its
configuration from whoever launched it — the name is validated client-side, so a typo is exit 2
either way, but a valid level cannot raise a running daemon's; and the thin client falls back
to a private cold server without failing if it cannot reach the daemon, so `cslq` watches its stderr for that
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
`captured-stdout-does-not-stall` leg is what holds the paragraph above:
`probes/stdout-capture.cs` starts `cslq ready` under `RedirectStandardOutput` and asserts it
returns well inside the keepalive with the daemon still listening. It ran on Windows alone
until the same leak was measured off it — the handle flag the Windows fix sets does nothing on
Unix, where a child inherits fds 0/1/2 whole unless the parent redirects them — so it now runs
on all three platforms.

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

`0.1.0` was the first release and went out as-is — no `rc`. `0.2.0` was the second: the minor
moved because the eight fix batches changed observable behaviour (exit 2 for a usage error,
readiness covering every project, a context-pinned positional answer). `0.3.0` is the third,
and the minor moves again for the session — a new default path, two new subcommands, two new
environment variables and a `--no-session` opt-out. `bump.yml` continues from it one patch at a
time.

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
| A first diagnostic pull under-reporting on an unbound document | `textDocument/diagnostic` does not answer from the misc-files state and then correct itself — it **blocks until the document is bound**, so `diag` pulls once and the settle loop that used to wrap it is gone. Measured 2026-09-06: a cross-project error opened as the first document in a never-used server returns the right code on pull #1 (~4.2 s), and a second pull (~0.7 s) never once differed across six whole-fixture runs, cold and warm. (A document in **no** project is a different case and is skipped outright: it has no project context, so an unqualified pull is answered out of Roslyn's misc-files workspace with analyzer rows about a file no compilation contains — which a one-shot never saw, because it exited before the document bound, and a session did. See `DESIGN.md`.) The fixture's error is deliberately *cross-project* — binding it needs Core's reference resolved — and `cold-server-diag-reports-cross-project-error` opens it as the first document of a dedicated server, which is the only state where answering early would show. |
| Roslyn ignoring unopened documents | Every query opens its document via `textDocument/didOpen` first — except source-generated ones, which the server owns and answers for without it. |
| A source file that is not valid UTF-8 | Decoding it with substitutions is silent and wrong: invalid bytes collapse to one U+FFFD, so the text `cslq` sends and the text Roslyn parses off disk stop agreeing and every later column on the line is short. Measured — column 48 where the editor showed 49, and `def` at the position `cslq` printed answering `no results` at exit 0. The decoder throws instead, with BOM detection left on so UTF-8 and UTF-16 BOM files are unaffected. Named as a target it is exit 1; found by `diag`'s walk it is named on stderr and skipped, so one undecodable file cannot hide the diagnostics of every other. |
| A workspace whose design-time build fails | `projectInitializationComplete` fired and *every* probed project empty is the signature: the solution loaded and compiled nothing. Only in that state, and only after the wait has already failed, `cslq` runs `dotnet --version` in the root (exit 155 when a `global.json` pins an SDK nobody has) and looks for `obj/project.assets.json` per project, and names what it finds. A staged root went from 181.7 s with no cause to 23 s with one. |
| A restore the gate does not leave to the server | `probes/run.sh` runs `dotnet restore` on the fixture before starting the server — not because the server will not (it does, as part of its design-time build), but to keep the cold `ready` a measurement of load time and a failed restore loud. |
| A framework or NuGet symbol rendering as a machine-absolute temp path | Roslyn answers for one from a document it decompiles under `<temp>/MetadataAsSource/<guid>/.../<Type>.cs`. `PathUri.Display` labels it `<metadata>/<assembly>/<TypeName>.cs`, reading the assembly off the `#region Assembly` header in the document, since the URI carries only the type name. `def` at `Console.WriteLine` used to print the raw path — after a 10 s stall in the decompilation guard, which now re-asks only when the workspace also declares that type. See `DESIGN.md`. |
| Source-generated symbols rendering as a nonexistent path | Generated documents come back under a `roslyn-source-generated:` URI. `new Uri(u).LocalPath` does not throw for one, it returns `/BuildInfo.g.cs`, so `PathUri.Display` branches on the scheme and labels them `<generated>/<project>/<assembly>/<generator type name>/<hintName>`, mirroring what `EmitCompilerGeneratedFiles` writes on disk — the generator type is what separates two generators in one assembly emitting the same `hintName`. The project comes from `textDocument/_vs_getProjectContexts` — the URI names only the generator, so without it one generator serving several projects renders every one of its documents identically. Text comes from `workspace/textDocumentContent`. |
| An unbuilt source generator contributing nothing, silently | With the analyzer assembly absent the workspace still loads and the sentinel still resolves; only the generated symbol is missing, with no error or diagnostic anywhere. `probes/run.sh` builds `fixture/Gen` before starting the server, and three cases assert the generated symbol resolves. |
| Server-to-client requests faulting the connection | `LspClient.Endpoints` answers `workspace/configuration`, `client/registerCapability`, `window/workDoneProgress/create` and friends. |
| A renamed server flag failing silently | The thin client forwards unrecognised options straight through to the server, so a rename produces no error. Flags live only in `src/Cslq/ServerArgs.cs`, and the probes are the only guard. |
| A session answering from the text a file used to hold | A one-shot died with its answer, so it re-read every file next time; a session holds `didOpen` across an edit and Roslyn owns an open document's text. Before every request the session stats each open document (write time and length — a `stat`, not a hash, because it runs per request) and re-opens the changed ones, `didClose`s the deleted ones and clears the cached context lines of generated documents, which have no file a stamp can watch. Without it: wrong line numbers, wrong context lines, a renamed symbol still found, at exit 0. |
| One client hanging up taking the whole session down | A client that hangs up mid-handshake races `WaitForConnectionAsync`, which then throws `IOException` instead of accepting. Rethrowing it ended the session, so the caller fell back and the next call paid the whole load again — 4.5 s and three processes for one query, on roughly one run in eight. The accept loop logs and continues. `session-survives-hangups` generates the race with `probes/hangup.cs` and asserts the log carries exactly one session pid. |
| Two cold calls racing into two sessions | The spawn is guarded by a machine-wide named mutex and the connect is retried under it, so the loser talks to the winner's session instead of starting a second attach beside it. A client that cannot take the lock in 10 s loads in-process and says `cslq: session unavailable` rather than stalling. The mutex runs on a thread of its own because `ReleaseMutex` has thread affinity and `await` resumes anywhere — which used to throw out of the `finally` and answer the same query twice. |

## Probes

```
./probes/run.sh
```

Runs `tests/Cslq.Tests` first, then restores the tool and the fixture, builds `cslq`, asserts
readiness, and runs every case in `probes/cases.jsonl`. Exits non-zero on any mismatch.

**164 legs today = the 140 rows in `cases.jsonl` + 24 scripted ones.** The scripted
twenty-four are the three source-generator staleness legs, the framework `def` (whose two
failure modes are an absence and a duration, neither of which an `expect` substring can pin),
the forced non-daemon fallback, the cold-server `diag`, the packaged-tool install, the
captured-stdout leg, the restore that rebuilds the tool-resolver cache, the five
first-run failures (a root with no solution, a root with two, `dotnet` off `PATH`, a candidate
that cannot resolve after the load notification, and a design-time build that fails), and ten
for the session: the second call's latency, the warm `hover` timed with and against
`--no-session`, the hangup race and its pid count, the reattach when the solution changes, the
forced in-process fallback, and five document-staleness legs that edit, rename and restore a
file under a live session. All 164 run on all three platforms. `session-pipe-smoke` is not one
of them: it runs before the fixture restore and hard-exits rather than counting.
Quoting the composition rather than the total is deliberate: the next time the two
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
  LspClient.cs              transport, initialize, readiness, didOpen/didClose
  Session.cs                the background session: pipe, spawn, serve loop, keepalive
  Staleness.cs              the stat a session checks before it trusts an open document
  Output.cs                 path:line + context formatting, and the outline tree
skills/                     the agent skill; the directory name is what `npx skills` installs as
  cslq/SKILL.md + REFERENCE.md
fixture/                    deliberately tricky solution
  Gen/                      incremental source generator; its output is referenced from App
  Ambient/Stray.cs          a document no project compiles, for the misc-files cases
  App/TypeError.cs          the deliberate cross-project type error for `cslq diag`
  App/Square.cs             the cross-project implementer of `Core/Shape.cs`, for `impl`
  Core/Party.cs             an astral-plane character on a line carrying a symbol
  Core/Split*.cs            one type in two documents, plus an overload in one of them
  Core/Empty.cs             a compilable document that declares nothing
  Core/Shape.cs             an interface whose implementers straddle two projects, for `impl`
  Core/Kinds.cs             crowded declaration lines, a delegate, a record, an `extension`
  encoding/                 three files no project compiles, one of them not valid UTF-8
fixture2/                   two consumers of one generator, and the only multi-targeted project
fixture-linked/Outside.cs   linked into fixture/Core from outside fixture/, for `<external>/`
tests/Cslq.Tests/            unit tests for the pure logic below the transport
probes/                     cases.jsonl + run.sh (runs tests/Cslq.Tests first)
  hangup.cs                 hangs up on a session's pipe 50 times, for the accept-loop race
  roots/                    workspace shapes answered before the server starts: a solution
                            listing only a .vbproj, and a top-level-statements-only project
```
