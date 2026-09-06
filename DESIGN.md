# Design

Distilled from the original brief. `ROADMAP.md` tracks state; this file is the why.

## The problem

Agents in C# repos fall back on grep and reading whole files. They can't reliably find all
callers of a method, resolve types across project boundaries, or see source-generated symbols.
A language server fixes that; the existing ways to wire one to an agent are third-party wrappers
that go stale.

Two hard constraints:

1. **Official tooling only.** The C#-specific component in the hot path is Microsoft-published.
2. **Always current.** The pin updates automatically and safely, not by hand.

## Output rules

The highest-leverage part of the project. Raw LSP returns URIs and zero-based ranges, which is
near-useless to a model.

- Print `path:line` plus the matched line and a line of surrounding context.
- Paths relative to the workspace root. Lines and columns one-based.
- Cap results by default so one call can't blow the context window.
- `--json` for the probe harness to assert against.
- Every new command follows these. They are the reason this is a CLI and not a wrapper.

**`outline` is the one deliberate exception.** It prints the document path once as a header
and then one row per declaration — that declaration's own source line, indented by nesting —
with no per-row `path:line:col`, no `>` marker and no surrounding context, and `--context` is
inert for it. An outline *is* the summary the other rules exist to produce; repeating the path
on every row and padding each with context lines would make a whole file unreadable and cost
the context window the rules are meant to protect. Everything else still holds: one-based
lines, root-relative paths, `--max` (over the pre-order flattening, so a truncated tree is
always a prefix and no node outlives its parent) and the same `{ count, truncated, results }`
JSON envelope, with `path` and `generated` on the envelope because the whole document is one
URI.

**`sym` breaks one narrower rule.** It prints no source line and no `>` marker, and
`--context` is inert for it: a search result set is a list of places to go, not a place to
read, and padding every hit of a broad query with context is the exact cost the cap exists to
prevent. Everything else holds — root-relative `path:line:col` on every row, one-based, `--max`,
the same `{ count, truncated, results }` envelope — so this is a smaller exception than
`outline`'s, which also drops the per-row path. Rows carry Roslyn's `containerName` because it
is the only thing separating two symbols that share a name; it is localised display text, so
nothing asserts on it. No `|` appears in a row, unlike an outline's gutter, so a probe case can
quote one whole.

**The cap is applied in the server's order, and the display sort is cosmetic.** Roslyn answers
`workspace/symbol` in relevance order -- exact match, then prefix, then substring, across every
project rather than project by project -- so `--max` truncates that ranking and only what
survives is sorted for display. Sorting first would keep an alphabetical prefix of the hits
instead of the best ones, which does not show on a fixture where the interesting query's hits
all share a name but loses the ranking entirely on a real repository.

Source-generated locations are labelled `<generated>/<assemblyName>/<hintName>`, built only
from the URI fields that are stable across runs and machines. **Known limitation:** none of
those fields identifies the *consuming* project, so one generator applied to several projects
— an analyzer in `Directory.Build.props`, the common real-world shape — produces several
distinct documents that all render identically, and `Output` sorts and renders by that label.
A references response carries only a URI and a range, so there is nothing in it to
disambiguate with. The available disambiguator is `containerName` (`"in BuildInfo (project
Core (net10.0))"`), which arrives on `workspace/symbol` results and is already parsed by
`Program.Matches` — but it is absent from the reference locations themselves, so wiring it
through means carrying the resolved symbol's project alongside the URI. Deferred until a
fixture has two projects consuming one generator; the fixture today has one.

## Readiness

A query fired before the workspace loads answers empty rather than erroring, so every command
waits first. **Ready means every project loaded**, not merely that the server answered
something: one sentinel only ever proved *some* project was up, and the window that leaves open
produced `refs`, `impl` and `sym` answers that were silently incomplete at exit 0. So `csx`
takes one sentinel per project and requires each to resolve to a location inside that project's
own directory and **not** inside a project nested within it — never matched by `containerName`,
which is localised display text. The nested exclusion is not a corner case: `Web/` and
`Web/Tests/` both declaring `Program` is the ordinary shape, and without it Tests loading marks
Web ready, which is the exact silently-incomplete failure above.

Two limits are known and deliberate. A project the scan can infer no sentinel for — one that is
only top-level statements, or only Razor or resources — is **not** waited on: there is nothing
to ask the server for, and failing on it would break those projects outright. It is named on the
failure path instead, so its absence from readiness is visible rather than silent. And two
`.csproj` in one directory collapse to a single entry, because a hit under that directory cannot
be attributed to one of them by path — no scan-based scoping can separate them, so the second
project is covered only incidentally.

Knowing the projects means reading the root's solution, and scanning for `*.csproj` under the
root only when there is none, because **the server cannot be asked**:
`workspace/_roslyn_restorableProjects` is a server-to-client request and carries no project
list. Either way it is an approximation, and the two err in opposite directions.

The solution is read because **over-inclusion is not merely wasteful, it is fatal:** a `.csproj`
the solution excludes is never loaded, so its types are never indexed, its sentinel can never
resolve, and readiness burns the whole timeout and exits 1. Measured 2026-09-06, before the
solution was read: `csx ready` on OrchardCore v3.0.1 failed on
`src/Templates/OrchardCore.ProjectTemplates/content/*`, which are `dotnet new` template content
rather than solution projects, and the only way past it was to point `--root` below them.

Exactly one solution counts, and only at the top of the root. Two give no basis for choosing
between them; a solution in a subdirectory describes that subtree rather than this root, and
Roslyn would not open it for this root either. Both fall back to the scan, which errs
deliberately towards over-inclusion — the opposite mistake is the incomplete-answer bug this
exists to close. A `.slnf` solution filter is not read. A project the solution lists but that is
not on disk is dropped: waiting on one is the same unresolvable sentinel by another route. A
root with no project at all fails immediately instead of timing out, naming the solution when
there is one, because "no .csproj under <root>" would be a lie about a root whose solution
simply lists no C# project. `--sentinel` bypasses all of it.

`--sentinel` is therefore the weak mode, not a neutral override: it replaces the whole
per-project set with a single root-scoped probe, giving up the all-projects-loaded guarantee.
It is the escape hatch for a layout the scan cannot read.

Sentinel candidates come from a regex, not a parser, and it matches English prose in doc
comments: "identifying the class and assembly context" yields the candidate `and`. Comments and
string literals are therefore stripped before the declaration regex runs. Taking every match in
a file rather than the first is not sufficient on its own and neither is the cap of three: one
doc-comment sentence yields `and` / `of` / `for` and fills all three slots, leaving a project
probed only by words no query can resolve. Measured 2026-09-06: `csx ready` against OrchardCore
v3.0.1 failed after 900s on fifteen projects, six of whose candidate lists were
`'and' / 'and' / 'and'`. Stripping is regex-level, not syntax-aware — parsing would mean a
Roslyn dependency the README rejects — so several candidates are still kept as a fallback chain
against a type the regex reads out of an excluded `#if` branch or a file no project compiles.

`diag`'s file enumeration is **not** scoped to project directories, and that is deliberate.
Measured 2026-09-05 against 5.12.0-1.26426.8: a `.cs` file no project compiles is invisible to
`workspace/symbol` and reports **no diagnostics at all** — only `outline` answers for it, off
the syntax tree — so scoping would suppress nothing. A file linked in from outside its project
directory (`<Compile Include="../Elsewhere/File.cs" />`) is fully indexed and does report, and
scoping would drop those diagnostics silently. Under-reporting is worse than over-walking.

`diag` pulls each document **once**. It used to re-pull until two consecutive reports agreed, on
the premise that a freshly opened document binds against the misc-files state and under-reports
until its project references resolve. Measured 2026-09-06 against 5.12.0-1.26426.8 and **the
premise is false**: `textDocument/diagnostic` does not answer early, it blocks until the document
is bound. A cross-project error opened as the first document in a never-used daemon returns the
correct `CS0029` on the first pull — that pull costs ~4.2s and the redundant second one ~0.7s.
That first-document case is what `cold-server-diag-reports-cross-project-error` runs: one named
file against `--no-daemon`, because the whole-fixture walk would open three other documents
first and never exercise it.
Across six whole-fixture runs, cold daemon and warm, 22 pulls each, the second pull never once
differed from the first. The loop bought a mandatory 250 ms delay plus a duplicate round trip per
file, about 45% of warm per-file cost, and nothing else.

Measured cost, fixture (11 files then, 3 projects), 2026-09-06: fixed ~2.5s per invocation, then
~560 ms per file warm and ~1080 ms cold. The fixed term is process start, sentinel inference and
readiness — none of which `diag` controls, and on a 233-project workspace it dominates. Any
throughput number that is not split at the readiness boundary is measuring the wrong thing.

### The measurement corpus

Throughput and divergence claims are measured against **`OrchardCMS/OrchardCore` at tag
`v3.0.1` = `b9c4b2f23e56ef11fbdbd28603c871d1b0fc9deb`** — 5,258 `.cs` across 233 `.csproj`,
`OrchardCore.slnx` at the root, BSD-3-Clause. Chosen because it is strictly single-TFM `net10.0`,
so a document belongs to one project instance and a per-document pull is unambiguous; every
larger candidate multi-targets. No Arcade, no submodules, no native code, nuget.org only.

**Pin the tag, never `main`.** `global.json` at `v3.0.1` is `10.0.200` / `rollForward:
latestMajor`; `main` has moved to `10.0.302`, which fails against an SDK below it. And set
`git config core.longpaths true` before checkout — OrchardCore has paths past Windows'
`MAX_PATH`, and the clone otherwise aborts half-written with `Filename too long`.

Scope the *file walk* with a directory argument, not with `--root`: `--root` drives project
enumeration, so a narrowed root also narrows readiness and stops meaning all-projects-loaded.
`csx diag <subdir> --root <repo>` keeps the full sentinel set and walks only the subtree.

## Settled — do not re-litigate

| Decision | Why |
|---|---|
| Server is `roslyn-language-server` | Microsoft-published, MIT, same engine as the VS Code C# extension |
| We write our own thin LSP client | A client is needed for the probes regardless; two clients would disagree about readiness |
| CLI, not an MCP server | MCP tool schemas cost context on every request; a CLI costs one paragraph in `SKILL.md`, and we control the output shape |
| Updates via cron GitHub Action | Renovate's `ignoreUnstable` default would silently never bump a train whose minor moves every release; Dependabot has a history of mangling `dotnet-tools.json`. Both beatable, neither worth the fight for one dependency |
| Pin in `.config/dotnet-tools.json` | Reproducible, in source control, bumpable by CI |
| LSP payload types are hand-defined | No maintained Microsoft package supplies them — see the README. Adding a package back is a regression, not a cleanup |
| No off-the-shelf MCP↔LSP bridge | Third-party code in the hot path, and language-agnostic bridges know nothing about Roslyn's specific failure modes |

If `--autoLoadProjects` proves insufficient for some repo shape, investigate the non-standard
`solution/open` notification (it exists in the server) rather than pulling in a wrapper.
