# `cslq` reference

The detail SKILL.md leaves out. Read a section when SKILL.md points you here, or when an answer
does not look like the question you asked.

## Install, the session and the daemon

```
dotnet tool install -g cslq
cslq restore
```

`cslq restore` fetches the pinned language server — a one-time ~300 MB download. The first query
that needs it does the same thing, so this only keeps the download out of the middle of an
answer.

There are two background processes, they are different things, and only the first is `cslq`'s:

- **The session** is a `cslq` process holding the loaded workspace open between calls, one per
  root. It is the default. `--no-session` opts out.
- **The daemon** is the Roslyn language server, one per machine, shared across workspaces.
  `--no-daemon` opts out and starts a private one.

You manage neither, and a failure to reach either is a slower answer rather than a wrong one:
`cslq: daemon unreachable` and `cslq: session unavailable; this run loaded the workspace itself`
both ride on stderr at exit 0 beside a correct answer.

What the daemon shares is the server *process*, not a loaded workspace: **every attach re-runs
the solution load**, which is why the daemon alone never made repeat calls cheap. A few projects
is a couple of seconds; 26 projects is 28-33 s (measured 2026-09-10). The session is what fixes
that, by attaching once. Measured on a 4-project fixture, 2026-09-11: first call to a cold
session 4.9-7.6 s, then `hover` 138-188 ms, `outline` 140 ms, `ready` 145 ms, `refs` 655-734 ms;
the same `hover` under `--no-session` is 2.2-2.4 s every time. **The first call costs more than a
one-shot did** — it pays the same load plus a process start — so spend it on `cslq ready` and
then ask as many narrow questions as you like.

Re-running a query still does not fix an incomplete answer: the session is in the same state as
it was, and a warm call does not reload anything.

The session ends after 900 s idle, on `cslq session stop`, or with the terminal that owns the
console it was spawned from. It is keyed by the `cslq` version, so upgrading `cslq` never talks
to a session running the old code.

```
cslq session status          # running or not, the pipe, the root, the pid, the log
cslq session stop            # end it; not an error if there was none
CSLQ_SESSION_PIPE_NAME       # override the derived pipe name outright
CSLQ_SESSION_KEEPALIVE       # seconds idle before it exits (default 900, -1 never)
```

Both subcommands act on the session the *current* options would reach, so `--root` and
`--no-daemon` pick which one exactly as they do for a query, and `--json` works on both. The
session prints nothing to your terminal; its console goes to
`<temp>/cslq-session-<pipe>.log`, which `session status` names. `CSLQ_SESSION_PIPE_NAME`
**replaces** the derived name rather than seeding it, so one exported value puts every root on
one pipe and the first session to start declines every other root's requests — export it for one
root or not at all.

Editing a file between calls is handled: the session stats every document it holds open before
each request and re-reads the ones that changed, so it never answers from text a file no longer
has. What you do lose is streaming — a session buffers a whole response and writes it when the
command finishes, so a long `cslq diag` walk delivers all its rows, and its per-file
`cslq: skipped —` lines, at the end.

Piping and capturing are safe, the first command included — the daemon does not inherit the
launching client's stdout. The exception is a shell that is itself captured (a PowerShell-hosted
harness, whose own stdout is a pipe): there the launching command must be an unredirected
`cslq ready`, or pass `--no-daemon`.

## Targets in full

A bare name selects the type when the only other candidates are its own constructors —
`Widget.Widget` selects those. Segments must be a contiguous suffix of the declaration path, so
`Fixture.Greeter.Greet` with the `Core` missing selects nothing.

An **ambiguous** target exits 1 and lists the candidates as `sym` rows —
`kind  name  container  path:line:col`, capped by `--max` — so a row pastes straight back as a
`file:line:col` target. A target matching **nothing** lists the near misses the same way, under
`candidates:`.

A file must be a C# document (`.cs`, `.razor`, `.cshtml`). `diag`'s directory is the one
non-document target. A `.csproj`, a `.json`, or any path above `--root` is an error.

## `sym` matching

`sym` is the matcher behind an IDE's Ctrl+T. Measured: `eter` finds `Greeter` (substring), `Vol`
finds `Volume` and `AudioVolume` (prefix and substring), `AV` finds `AudioVolume` (camel humps),
and `Greter` finds `Greeter`, `Greet` and `Green` — a dropped letter still matches.

Case is ignored, except that an ALL-CAPS query is read as humps or as a whole name and never as
a prefix, so `VOL` finds nothing where `Vol` finds two. Namespaces are not indexed, and neither
are locals or parameters. `*` and an empty query match nothing. Results are ranked before `--max`
cuts them, so a truncated list is the useful end.

**Never report a `sym` row as confirmation that a symbol exists.** Confirm with `def`.

## Output shapes

The default is `path:line:col` relative to the workspace root, then the matched line marked `>`
with a line of context either side. `--max N` caps results (default 50), `--context N` widens the
window.

- **`outline`**: the path once as a header, then one row per declaration indented by nesting, no
  per-row position and no context. Where several declarations share a line — an enum and its
  members, a multi-declarator field — each of those rows prints its own span and grows its gutter
  to `line:col`, which is still a target you can paste back.
- **`hover`**: the position as a header, then the signature and the doc-comment summary as plain
  text — no fences, no source line, no `>` marker. `--context` is inert; `--max` caps the
  documentation's lines. Roslyn's QuickInfo drops every `<param>`, so hover gives parameter
  *types* and never their prose — read the doc comment at `def` for that.
- **`sym`**: `path:line:col` on every row but no source line and no `>` marker, so `--context` is
  inert. The third column is the container as Roslyn displays it
  (`in Greeter (project Core (net10.0))`) — display text, not a namespace path. Do not parse it.
- **`ready`**: the single word `ready`. Under `--json` the one result carries `ready`, `projects`
  (how many were actually probed) and two arrays naming the rest, as directories relative to
  `--root`, forward slashes, both empty in the ordinary case: `skipped` for a project whose
  `.csproj` compiles nothing of its own or links its sources in from outside its directory, so no
  query could ever prove it loaded, and `unprobed` for one that owns sources but declares no type
  to probe for. The three add up to the projects the solution yielded, so **a `ready` that
  checked only part of the workspace says so**. Plain `cslq ready` names both classes on stderr
  at `--log-level Information`.

A source line longer than 200 characters is cut in text mode, around the column the row is about,
with a `…` at each cut end. The `path:line:col` above the row is untouched and is what
round-trips; a column counted off the printed text does not. `--json` keeps the line whole.

### Labelled paths

- `<metadata>/<assembly>/<TypeName>.cs` — a framework or NuGet type, with the declaration and its
  context lines read from the document Roslyn decompiled. A real answer. Not a file you can pass
  back: `outline System.Console` exits 1, because the document exists only once a `def` at a use
  site has made Roslyn write it. Use `cslq hover` at a use instead — it needs no document at all.
- `<generated>/<project>/<assembly>/<generator type name>/<hintName>` — source-generated, no file
  on disk. Read it with `cslq outline` on the symbol, not with a file read. The leading segment is
  the project that consumed the generator, the only thing separating two documents one generator
  emitted into two projects.
- `<external>/<path relative to the root>` — a real file compiled by a project but living outside
  `--root` (a `<Compile Include="../..">`). The `..` tells you where to open it, but every
  file-taking command rejects a path outside `--root`. Reach its declarations by name with `sym`
  and `def`, or widen `--root`.

### Kinds

One table across commands, with three shapes it cannot express: a **delegate** reads as `method`
(`cslq` levelling `sym` down to what `documentSymbol` can say, so a `method` row may be a
delegate), a **record** as `class` or `struct`, and a C# 14 **`extension` block** as `class`. A
constructor reads as `constructor`, which LSP does have and Roslyn does not send.

## Exit codes and streams

| Code | Meaning |
|---|---|
| 0 | The query was answered. For `diag` this includes a clean file — no diagnostics is a successful query |
| 1 | The lookup failed: no such symbol, an ambiguous symbol, no references, no definition, no such file, or the workspace never loaded |
| 2 | The invocation could not be understood: no command, an unknown command or option, a missing or invalid option value, an argument the command does not take. The message and the usage block go to stderr |
| 127 | An unhandled internal failure |
| 130 | Interrupted |

`refs`, `def`, `impl`, `sym` and `hover` exit 1 on an empty result, because an empty answer means
the target was not what you thought. `project` exits 1 with `no project` for a `.cs` file no
project compiles — which is also why `sym` cannot find the types in it and `diag` reports nothing
for it. `diag` and `outline` exit 0 on an empty result, because nothing to report is an answer.

In text mode **stdout carries the answer and nothing else**: every non-answer that exits non-zero
(`no results`, `no project`) and every failure is one `cslq: `-prefixed line on stderr, so stdout
is data. `no diagnostics` and `no symbols` are on stdout because they exit 0. With `--json` the
answer is a JSON object on stdout on every path, failures included: an empty answer is the
ordinary `{ "count": 0, ..., "results": [] }` envelope, a failure is `{ "error": "<message>" }`,
and the human line still goes to stderr. Check for an `error` key to tell the two apart; an
answer envelope never has one.

## Multi-targeted projects and `#if`

A file in a `net10.0;net9.0` project is compiled twice with different preprocessor symbols, so a
type inside `#if NET9_0` exists in one context and not the other. **You do not need `--tfm` to
get a correct answer** — every command that asks something of a document handles it:

- `hover` and `def` answer with one thing, so they ask the contexts in a fixed order, stop at the
  first that answers, and say which: `answered in net9.0 of 2 contexts: net10.0, net9.0`.
- `refs`, `impl`, `outline` and `diag` answer with a set, so they ask **every** context and union
  it — a reference inside an `#if NET9_0` block exists only there — and say
  `merged from 2 contexts: net10.0, net9.0` (and `tried all 2 contexts: …` when the union came
  back empty). `outline` marks declarations that are not in every context,
  `public sealed class Only9  [net9.0]`, and `diag` marks diagnostics not reported by every one,
  `error CS0029: ... [net9.0]`, which is how a framework-specific error becomes visible at all.
- `project` prints one row per context, `count` = contexts, so it answers "do I have to reason
  about `#if` branches here" directly.

Use `--tfm` to ask a *specific* framework — "does net9.0 see this", "does net8.0 build" — or to
get one view instead of the union. A framework the document has no context for is an error naming
the ones it has. It is rejected outright on `sym`, `ready` and `restore`: `workspace/symbol` is
context-independent and the other two name no document, so there is nothing to choose.

Under `--json`: `contexts` on the envelope is how many contexts the document has; `tfm` on the
envelope is the one that answered, and is **absent** rather than null where no single context did
— which is every union. `outline` and `diag` rows carry a per-row `tfm` that is null when every
context asked has that row. Every location row carries `generated`, `metadata` and `external`
booleans, so the label prefix never has to be parsed off `path`. `diag` rows carry no `source`:
this server never sends one.

## Other options

`--log-level` is checked against its seven names before anything starts, but it cannot change a
daemon that is already running: the daemon keeps the level whoever launched it asked for, so
raising it means `--no-daemon` or a daemon that has expired.

`--no-session` loads the workspace in the calling process instead of asking the background
session. It is what you want when you are measuring a cold load, or when a session would hold a
root you are about to delete or rewrite; it is not a fix for a wrong answer, since a session
re-reads every file that changed before it answers.

`--sentinel` does not speed a run up. By default `cslq` waits for every project under the root to
load, one probe per project the root's solution lists; a root holding no solution, or two of
them, is an error rather than a `.csproj` scan. `--sentinel` adds one more probe, scoped to the
root, on top of that set — every project still has to load, so the guarantee holds. It stands
alone only where that inference finds nothing at all, which is the layout it is the escape hatch
for.

## When a query comes back empty

1. **A root with no solution at its top is an error, not a hang.** `cslq` exits 1 in about a
   second with a message saying it loads the projects the root's solution lists, so `--root` must
   be the directory holding the `.sln`/`.slnx`. A solution one directory down does not count —
   and `--no-daemon` will sometimes load such a root anyway, which is not a fix but a difference
   between the two servers. This is the most common cause by far, and it announces itself: you
   will not see it as an empty answer.
2. **A root holding *several* solutions is an error too**, naming both files: point `--root` at a
   directory holding the one solution you mean.
3. **Run `dotnet restore` first.** Not because the server will not — it does, as part of its
   design-time build — but because a restore that *fails* is invisible from here: every project
   loads empty and `cslq` can say only that it happened. `dotnet restore` names the package.
4. **A source generator has to be built** before its output exists. If a generated symbol is
   missing, build the analyzer project.
5. **Check the symbol with `cslq def`** before concluding anything about `refs`.
6. **Read the readiness failure; it names what it found.** *"the workspace is still loading …
   Raise --timeout"* means exactly that — the load had not finished inside the deadline, and the
   only lever is a larger `--timeout`. *"every probed project answered empty"* is the other one:
   the solution loaded and compiled nothing, which is a failed design-time build, and the
   `cause:` line under it names what `cslq` found — an SDK a `global.json` pins but nobody has
   installed, or a restore that did not succeed. Fix that, not the query.
7. **A non-Latin identifier may not be findable by name.** A CJK name was measured answering
   nothing to `refs` and `sym` while the same declaration answered correctly by `file:line:col`;
   accented Latin and ligatures were fine, and Cyrillic and Greek were never tried. The cause is
   unsettled — the server's matcher, or the argument never arriving intact. Query by position,
   which never goes through the matcher, and say that is why.
8. **`diag` says nothing about a file no project compiles.** Not a bug and not a silence worth
   chasing: such a document has no project context, so every diagnostic Roslyn would offer for
   it comes from its misc-files workspace and describes a file no compilation contains. Check
   with `cslq project <file>` — no project is the answer. A file *linked* into a project from
   outside its directory has a real context and does report.
9. **A file that is not valid UTF-8 is refused, not guessed at.** `cslq` reads sources as UTF-8 (a
   UTF-8 or UTF-16 BOM is honoured), and a file that is neither is an error when you name it and a
   skipped line on stderr when `diag` walks over it. Decoding it anyway would move every column
   after the bad bytes, which is a wrong answer rather than a wrong-looking one. Re-save the file
   as UTF-8.

## Limits that are `cslq`'s, not the user's

`cslq ready` covers every project it can infer a readiness probe for. Two shapes fall outside
that, and in both of them a query can answer at exit 0 with a project's hits missing. Neither is
misconfiguration, so do not send the user off to fix their setup over one:

- **A project with no type declaration to probe** — one that is only top-level statements, or only
  Razor or resources — is not waited on, because there is nothing to ask the server for. It is
  named on the readiness failure path, so its absence is at least visible.
- **Two `.csproj` in one directory** collapse to a single probe: a hit under that directory cannot
  be attributed to one of them by path, so the second project is covered only incidentally. This
  is worse than that suggests — measured on such a pair, `sym BType` answered with a fuzzy `AType`
  row at exit 0, a wrong row rather than a missing one, while `hover` and `def` inside `BType.cs`
  answered `no results`. Giving each project its own directory is the only fix; no flag helps.

Re-running the query does not fix either shape: every invocation reloads the solution and waits
for readiness again, so the second run is in the same state as the first. Say which project may be
missing instead.

Two more, neither of them about readiness:

- **A `.vbproj` or `.fsproj` is not searched.** The server is a C# one. `cslq` prints
  `N non-C# project(s) not searched: ...` on stderr when the root's solution lists any, and the
  part to carry into an answer is that a reference to a C# symbol *from* VB or F# source is absent
  from `refs` at exit 0. Say so rather than reporting the C# hits as the whole picture.
- **A workspace of nothing but top-level statements cannot infer a sentinel** — `dotnet new
  console` declares no type — and `cslq` refuses in about 90 ms saying so. Do not reach for
  `--sentinel Program`: that class is compiler-generated, `workspace/symbol` does not index it,
  and the run waits out the entire timeout. Pass a method or a local function declared in that
  file instead; it resolves in about two seconds.
