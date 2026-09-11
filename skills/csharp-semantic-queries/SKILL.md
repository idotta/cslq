---
name: csharp-semantic-queries
description: >-
  Use for ANY question about C# or .NET code in this repository that is about meaning rather
  than text: who calls a method, where a type or member is declared, what is in a file, what
  the compiler thinks is wrong. Run `cslq` instead of grep, ripgrep, Select-String or reading
  files to answer them. Trigger on "find all callers", "who uses", "where is X defined",
  "go to definition", "what's in this file", "does this compile", "any errors", "what does
  this class expose", "rename impact", "is this method still used", "dead code", "what
  implements this interface", "who overrides this", "find a symbol by name", "what is this",
  "what type is this", "what are the parameters", "which project is this in", and on any
  C# identifier the user names without a file path. Also use before editing an unfamiliar C#
  file, to see the declarations and the callers of what you are about to change.
---

# Semantic C# queries with `cslq`

`cslq` is a CLI over Microsoft's `roslyn-language-server` — the same engine as the VS Code C#
extension. It answers about the compiled semantic model, so it sees cross-project references,
generic instantiations, source-generated code and `partial` halves. Grep sees none of that.

`cslq` has to be installed before any of this works. If it is not on `PATH`, install it — that
is the whole procedure, and it needs nothing but the .NET 10 SDK. Not finding `cslq` is not a
reason to fall back to grep:

```
dotnet tool install -g cslq
cslq restore
```

`cslq restore` fetches the language server the tool pins — a one-time ~300 MB download. It is
optional, since the first query that needs the server does it anyway, but running it up front
keeps the download out of the middle of an answer.

## Start every session with this

```
cslq ready
```

Blocks until the workspace has loaded and exits 0. Every other command waits for readiness on
its own, so this looks optional. It is not, and latency is the smaller half of why.

`cslq` starts a shared background daemon on first use, and the first command pays the solution
load for everything after it. Piping and capturing are safe, including on that first command —
the daemon does not inherit the launching client's stdout. The exception is a shell that is
itself captured (a PowerShell-hosted harness, whose own stdout is a pipe): there the launching
command should be an unredirected `cslq ready`, or pass `--no-daemon`.

## Task → command

| Task | Use this | Do NOT |
|---|---|---|
| Every caller / user of a method, type, property | `cslq refs <symbol>` | grep the name — misses aliases, hits comments and strings |
| Where something is declared | `cslq def <symbol>` | grep `class X` — misses `partial`, generated and cross-project |
| What implements an interface or overrides a member | `cslq impl <symbol>` | grep `: IThing` — misses indirect and cross-project implementers |
| What something *is* — its type, signature, parameters, docs | `cslq hover <symbol \| file:line:col>` | read the declaration and infer — hover gives the resolved type and the doc summary in one line |
| What a parameter *means* (the `<param>` docs) | `cslq def <symbol>` and read the doc comment | expect `hover` to carry it — Roslyn's QuickInfo drops every `<param>`, so hover gives the parameter types and never their prose |
| Which project compiles a file, and for which framework | `cslq project <file>` | guess from the directory — a linked file is compiled by a project it does not sit under |
| Find a symbol when you only know part of the name | `cslq sym <query>` | grep the tree — matches comments, strings and unrelated languages |
| What a file declares, and its nesting | `cslq outline <file>` | read the whole file into context |
| Compiler / analyzer errors in a file or the tree | `cslq diag [path]` | `dotnet build` and parse the log |
| Confirm a symbol still exists at all | `cslq def <symbol>` | assume from a grep hit |
| What a framework or NuGet type looks like | `cslq hover` at a use of it, or `cslq def` | search the web for the signature |

Never answer "who calls this?" or "where is this defined?" from a text search in a C# repo.
A text search cannot tell a call from a comment, and it cannot see a caller in another project.

## Targets

`refs`, `def`, `impl`, `hover` and `outline` take either form:

- **A symbol:** `Greet`, `Greeter.Greet`, `Fixture.Core.Greeter.Greet`. Every segment is
  matched, namespaces included, against the declaring document's syntax tree; the segments must
  be a contiguous suffix of the declaration path, so `Fixture.Greeter.Greet` with the
  `Core` missing selects nothing. A bare name selects the type when the only other candidates
  are its own constructors — `Widget.Widget` selects those. An ambiguous target exits 1 and
  lists the candidates as `sym` rows — `kind  name  container  path:line:col`, capped by
  `--max` — so a row can be pasted straight back as a `file:line:col` target. A target that
  matches nothing lists the near misses the same way, under `candidates:`.
- **A position:** `App/Program.cs:9:35`, relative to the workspace root, **one-based** line and
  column, and columns are UTF-16 code units. Paste a position straight out of any `cslq` result.

`outline` also takes a bare file path. Anything containing a separator or ending `.cs` is
treated as a file, never as a symbol.

`sym` takes neither: its argument is a **search query**, matched by the server against every
symbol name in the workspace, so a partial name works and several hits are normal rather than
an error. Use it when you do not know the exact name; use `def` when you do.

**`sym` is fuzzy, so a hit does not mean the name exists.** It is the matcher behind an IDE's
Ctrl+T. Measured: `eter` finds `Greeter` (substring), `Vol` finds `Volume` and `AudioVolume`
(prefix and substring), `AV` finds `AudioVolume` (camel humps), and `Greter` finds `Greeter`,
`Greet` and `Green` — a dropped letter still matches. Case is ignored, except that an ALL-CAPS
query is read as humps or as a whole name and never as a prefix, so `VOL` finds nothing where
`Vol` finds two. Namespaces are not indexed, and neither are locals or parameters; `*` and an
empty query match nothing. Results are ranked before `--max` cuts them, so a truncated list is
the useful end. **Never report a `sym` row as confirmation that a symbol exists** — confirm
with `def`, whose targets are matched exactly, segment by segment.

`diag` is the exception: it takes a **file or directory path, or nothing at all** — never a
symbol or a position. With no argument it walks every `.cs` file under `--root`. `project` takes
a **file path** and nothing else. Every path argument must lie under `--root`, and a file
must be a C# document (`.cs`, `.razor`, `.cshtml`); `diag`'s directory is the one non-document
target. A `.csproj`, a `.json` or a path above the root is an error.

## Output

`path:line:col` relative to the workspace root, then the matched line marked `>` with a line of
context either side. `--max N` caps results (default 50) and `--context N` widens the window.
`--json` gives `{ count, truncated, results }` for scripting — every command, `ready`
included, where the one result carries `ready`, `projects` (how many projects were actually
probed) and two arrays naming the rest, as directories relative to `--root`, forward slashes,
both empty in the ordinary case: `skipped` for a project whose `.csproj` compiles nothing of its
own or links its sources in from outside its directory, so no query could ever prove it loaded,
and `unprobed` for one that owns sources but declares no type to probe for. The three add up to
the projects the root's solution yielded, so **a `ready` that checked only part of the workspace
says so**. Plain `cslq ready` still prints the single word `ready`, and names both classes on
stderr at `--log-level Information`.

`outline` is the exception: the path once as a header, then one row per declaration indented by
nesting, no per-row position and no context. Where several declarations share a line — an enum
and its members, a multi-declarator field — each of those rows prints its own span and grows
its gutter to `line:col`, which is still a target you can paste back.

`hover` is the narrowest exception: the position as a header, then the signature and the
doc-comment summary as plain text — no fences, no source line, no `>` marker. `--context` does
nothing for it and `--max` caps the documentation's lines.

`sym` is a narrower exception: it keeps `path:line:col` on every row but prints no source line
and no `>` marker, so `--context` does nothing for it. Its third column is the container as
Roslyn displays it (`in Greeter (project Core (net10.0))`) — display text, not a namespace
path, so do not parse it.

A symbol whose source is not in the workspace — a framework or NuGet type — prints as
`<metadata>/<assembly>/<TypeName>.cs`, with the declaration and its context lines read from the
document Roslyn decompiled. That is a real answer. The path is a label, not a file you can pass
back to `cslq`: `outline System.Console` exits 1, because the document only exists once a `def`
at a use site has made Roslyn write it. Use `cslq hover` at a use of the symbol instead — it
answers with no document at all.

A file compiled by a project but living outside `--root` — a `<Compile Include="../..">` —
prints as `<external>/<path relative to the root>`. It is a real file; the label says only that
it is not in the workspace you pointed at. Like `<generated>/` and `<metadata>/`, it is **not a
target**: the `..` tells you where to open the file, but every file-taking command rejects a
path outside `--root`. Reach its declarations by name with `sym` and `def` instead, or widen
`--root`.

A source line longer than 200 characters is cut in text mode, around the column the row is
about, with a `…` at each cut end. The `path:line:col` above the row is untouched and is
what round-trips; a column counted off the printed text does not. `--json` keeps the line
whole.

Kinds are one table across commands, and three shapes it cannot express: a **delegate** reads
as `method` — `cslq` levelling `sym` down to what `documentSymbol` can say, not Roslyn's
answer, so a `method` row may be a delegate — a **record** as `class` or `struct`, and a C# 14
**`extension` block** as `class`. A constructor reads as `constructor`, which LSP does have
and Roslyn does not send.

Source-generated locations print as
`<generated>/<project>/<assembly>/<generator type name>/<hintName>` and have no
file on disk. That is a real answer, not an error — read the source with `cslq outline` on the
symbol, not with a file read. The leading segment is the project that consumed the generator,
which is the only thing separating two documents one generator emitted into two projects.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | The query was answered. For `diag` this includes a clean file — no diagnostics is a successful query |
| 1 | The lookup failed: no such symbol, an ambiguous symbol, no references, no definition, no such file, or the workspace never loaded |
| 2 | The invocation could not be understood: no command, an unknown command or option, a missing or invalid option value, an argument the command does not take. The message and the usage block go to stderr |
| 127 | An unhandled internal failure |
| 130 | Interrupted |

`refs`, `def`, `impl`, `sym` and `hover` exit 1 on an empty result, because an empty answer
means the target was not what you thought. `project` exits 1 with `no project` for a `.cs` file
no project compiles — which is also why `sym` cannot find the types in it and `diag` reports
nothing for it. `diag` and `outline` exit 0 on an empty result, because nothing
to report is an answer.

In text mode **stdout carries the answer and nothing else**: every non-answer that exits
non-zero (`no results`, `no project`) and every failure is one `cslq: `-prefixed line on
stderr, so you can treat stdout as data. `no diagnostics` and `no symbols` are on stdout
because they exit 0. With `--json` the answer is a JSON object on stdout on every path,
failures included: an empty answer is the ordinary `{ "count": 0, ..., "results": [] }`
envelope, a failure is `{ "error": "<message>" }` — and the human line still goes to stderr.
Check for an `error` key to tell the two apart; an answer envelope never has one.

One trap in `impl`: a member with no implementations does **not** come back empty. Roslyn falls
through to the declaration, so `cslq impl` on an ordinary method prints the same thing `cslq def`
would. Read a single result at the symbol's own declaration as "nothing implements this", not
as "this implements something".

## Options worth knowing

```
--root <dir>        workspace root (default: the current directory)
--max N             cap results (default 50)
--context N         source lines either side of a hit (default 1; inert for outline, sym, hover)
--timeout N         seconds to wait for the workspace to load (default 180)
--tfm T             answer in this target framework's context only (multi-targeted files)
--json              machine-readable output
--sentinel <sym>    also require this symbol to resolve before answering
--no-daemon         start a private server instead of sharing the daemon
--log-level L       server log level: Trace, Debug, Information, Warning (default),
                    Error, Critical, None
```

Options go anywhere — before the command, between it and its argument, or after both — so
`cslq --root . --timeout 600 def ContentItem` is the same invocation as
`cslq def ContentItem --root . --timeout 600`. An unknown one is exit 2, not a silent pass.

`--log-level` is checked against those seven names before anything starts, but it cannot
change a daemon that is already running: the daemon keeps the level whoever launched it asked
for, so raising it means `--no-daemon` or a daemon that has expired.

`--tfm` is rejected on `sym`, `ready` and `restore` rather than ignored: `workspace/symbol` is
context-independent and the other two name no document, so there is no context to choose and
the option filtered nothing.

`--tfm T` is for multi-targeted projects, and **you do not need it to get a correct answer**.
A file in a `net10.0;net9.0` project is compiled twice with different preprocessor symbols, so a
type inside `#if NET9_0` exists in one context and not the other. Every command that asks
something of a document handles that itself:

- `hover` and `def` answer with one thing, so they ask the contexts in a fixed order, stop at
  the first that answers, and say which: `answered in net9.0 of 2 contexts: net10.0, net9.0`.
- `refs`, `impl`, `outline` and `diag` answer with a set, so they ask **every** context and
  union it — a reference inside an `#if NET9_0` block exists only there — and say
  `merged from 2 contexts: net10.0, net9.0` (and `tried all 2 contexts: …` when the union came
  back empty). `outline` marks declarations that are not in every
  context, `public sealed class Only9  [net9.0]`, and `diag` marks diagnostics that are not
  reported by every one, `error CS0029: ... [net9.0]`, which is how a framework-specific error
  becomes visible at all.
- `project` prints one row per context, `count` = contexts, so it answers "do I have to reason
  about `#if` branches here" directly.

Use `--tfm` to ask a *specific* framework — "does net9.0 see this", "does net8.0 build" — or to
get one view instead of the union. A framework the document has no context for is an error
naming the ones it has. Under `--json`: `contexts` on the envelope is how many contexts the
document has; `tfm` on the envelope is the one that answered, and is **absent** rather than
null where no single context did — which is every union. `outline` and `diag` rows carry a
per-row `tfm` that is null when every context asked has that row. Every location row carries
`generated`, `metadata` and `external` booleans, so the label prefix never has to be parsed
off `path`. `diag` rows carry no `source`: this server never sends one.

`--sentinel` does not speed a run up. By default `cslq` waits for every project under the root
to load, one probe per project the root's solution lists; a root holding no solution, or two of
them, is an error rather than a `.csproj` scan. `--sentinel` adds one more probe, scoped to the
root, on top of that set — every project still has to load, so the guarantee holds. It stands
alone only where that inference finds nothing at all, which is the layout it is the escape
hatch for.

`cslq` shares one background server (the daemon) across invocations. You do not need to manage
it. If a run prints `cslq: daemon unreachable`, the answer is still correct — it was just slow.
What the daemon shares is the server *process*, not a loaded workspace: every invocation
re-runs the solution load, so the per-call cost scales with the size of `--root` and does not
fall away after the first call. A few projects is a couple of seconds; 26 projects is 28-33 s
per call, measured 2026-09-10. Budget for that on a large repository, and prefer one broad
query to several narrow ones.

## When a query comes back empty

1. **A root with no solution at its top is now an error, not a hang.** `cslq` exits 1 in about
   a second with a message saying it loads the projects the root's solution lists, so `--root`
   must be the directory holding the `.sln`/`.slnx`. A solution one directory down does not
   count — and `--no-daemon` will sometimes load such a root anyway, which is not a fix but a
   difference between the two servers: point `--root` at the solution. This is the most common
   cause by far, and it announces itself — you will not see it as an empty answer.
2. **A solution at the root is also what scopes readiness.** `cslq` waits for every project the
   root's `.sln`/`.slnx` lists. A root holding *several* of them gives no basis for choosing
   one and is an error too, naming both files: point `--root` at a directory holding the one
   solution you mean.
3. **Run `dotnet restore` first.** Not because the server will not — it does, as part of its
   design-time build — but because a restore that *fails* is invisible from here: every
   project loads empty and `cslq` can say only that it happened. `dotnet restore` names the
   package.
4. **A source generator has to be built** before its output exists. If a generated symbol is
   missing, build the analyzer project.
5. **Check the symbol with `cslq def`** before concluding anything about `refs`.
6. **Read the readiness failure, it names what it found.** *"the workspace is still loading …
   Raise --timeout"* means exactly that — the load had not finished inside the deadline, and
   the only lever is a larger `--timeout`. *"every probed project answered empty"* is the
   other one: the solution loaded and compiled nothing, which is a failed design-time build,
   and the `cause:` line under it names what `cslq` found — an SDK a `global.json` pins but
   nobody has installed, or a restore that did not succeed. Fix that, not the query.
7. **A non-Latin identifier may not be findable by name.** A CJK name was measured answering
   nothing to `refs` and `sym` while the same declaration answered correctly by
   `file:line:col`; accented Latin and ligatures were fine, and Cyrillic and Greek were never
   tried. The cause is unsettled — the server's matcher, or the argument never arriving intact.
   Query by position, which never goes through the matcher, and say that is why.
8. **A file that is not valid UTF-8 is refused, not guessed at.** `cslq` reads sources as
   UTF-8 (a UTF-8 or UTF-16 BOM is honoured), and a file that is neither is an error when you
   name it and a skipped line on stderr when `diag` walks over it. Decoding it anyway would
   move every column after the bad bytes, which is a wrong answer rather than a wrong-looking
   one. Re-save the file as UTF-8.

## Limits that are `cslq`'s, not the user's

`cslq ready` covers every project it can infer a readiness probe for. Two shapes fall outside
that, and in both of them a query can answer at exit 0 with a project's hits missing. Neither is
misconfiguration, so do not send the user off to fix their setup over one:

- **A project with no type declaration to probe** — one that is only top-level statements, or
  only Razor or resources — is not waited on, because there is nothing to ask the server for.
  It is named on the readiness failure path, so its absence is at least visible.
- **Two `.csproj` in one directory** collapse to a single probe: a hit under that directory
  cannot be attributed to one of them by path, so the second project is covered only
  incidentally. This one is worse than "covered incidentally" suggests: measured on such a
  pair, `sym BType` answered with a fuzzy `AType` row at exit 0 — a wrong row, not a missing
  one — while `hover` and `def` inside `BType.cs` answered `no results`. Giving each project
  its own directory is the only fix; no flag helps.

If a result looks like it is missing a project's hits in either shape, re-run the query — the
second one is against a fully loaded workspace.

Two more, neither of them about readiness:

- **A `.vbproj` or `.fsproj` is not searched.** The server is a C# one. `cslq` prints
  `N non-C# project(s) not searched: ...` on stderr when the root's solution lists any, and the
  part to carry into an answer is that a reference to a C# symbol *from* VB or F# source is
  absent from `refs` at exit 0. Say so rather than reporting the C# hits as the whole picture.
- **A workspace of nothing but top-level statements cannot infer a sentinel** — `dotnet new
  console` declares no type — and `cslq` refuses in about 90 ms saying so. Do not reach for
  `--sentinel Program`: that class is compiler-generated, `workspace/symbol` does not index it,
  and the run waits out the entire timeout. Pass a method or a local function declared in that
  file instead; it resolves in about two seconds.
