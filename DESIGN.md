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
- Location rows are folded on their rendered label plus range, and ordered source, then
  generated, then metadata, before the cap.
- `--json` for the probe harness to assert against. Every row carries `generated` and
  `metadata` booleans so a caller never has to parse the `<generated>/` or `<metadata>/`
  prefix back off `path`.
- **An envelope key that could only ever be null is omitted, not emitted as null.** A field a
  caller has to interpret is worse than a field that is not there: `null` reads as "the answer
  is unknown" when the truth is "the question does not apply to this command". So `tfm` sits on
  an envelope only where a single context produced the answer — `hover` and `def` — and is
  absent from a union's, where `contexts` and the footer say what was merged. This is the same
  rule T-77 asks for about `diag`'s always-null `source`, decided here first so that batch 8
  inherits it. It applies to envelope keys, not to row keys: a **row**'s null `tfm` is a value,
  not an absence — it means that row is in every context asked, which is exactly the thing its
  marked neighbours are not — and row keys stay stable across the rows of one answer.
- **In text mode stdout carries the answer and nothing else: every non-answer and every
  failure is one `cslq: `-prefixed line on stderr.** `refs`' `no results` and `project`'s
  `no project` used to print on stdout without the prefix, so a script treating stdout as data
  stored `no results` as a hit while every other failure was already on stderr. They exit
  non-zero, which is what makes them failures. `diag`'s `no diagnostics` and `outline`'s
  `no symbols` stay on stdout because they exit **0** — a clean file and an empty document are
  answers, and that is the whole distinction: the exit code says which stream carries the
  message. An empty answer's context note rides on the same `cslq:` line, after a `; `, rather
  than sitting under it as its own paragraph, because a failure is one line. Stderr is not
  failures-only, and the rule does not make it so: `not probed —`, `daemon unreachable` and
  the one-time restore notice are advisories that ride there at exit 0 beside a perfectly good
  answer on stdout. What the rule fixes is where a command's **own** message goes, and the
  exit code is the whole of the answer to that.
- **`--json` is honoured on every path, including the ones that exit non-zero, and `error` is
  the discriminator.** An empty answer keeps the ordinary `{ count, truncated, results }`
  envelope with `count: 0`: a caller parsing JSON never meets a missing body. A failure prints
  a single `{ "error": "<the same message>" }` object on stdout, and the human line still goes
  to stderr so a log reads. An answer envelope never carries `error` and an error object never
  carries `count`, so **one field** separates the two shapes a caller can be handed at the same
  exit code — which was the complaint: `refs <a position with no hits> --json` answered with an
  empty envelope and `refs NoSuch --json` with no stdout at all, both at exit 1, and readiness
  timeouts, ambiguity and unresolved targets were plain text whatever was asked for. `--json`
  is read off `argv` rather than off the parsed options, because half the failures are thrown
  by the parse itself. Exit codes keep the meanings they had; only the channel and the `--json`
  coverage move.
- **The usage text is inside the rule too, and exit 2 is what it means.** A usage error — no
  command, an unknown command or option, a missing or invalid option value, an argument the
  command does not take — is the `cslq: ` line followed by the usage block on **stderr**, at
  exit **2**, with `{ "error": "<the same line>" }` on stdout under `--json` like every other
  failure. The usage block stays out of the JSON: it is thirty lines of prose for a human
  reading the log, and folding it in would make one JSON string of them. The two usage
  failures used to disagree — no arguments printed usage on *stdout* at exit 2, an unknown
  command printed it on stderr at exit 1 — and exit 2 was documented nowhere. It now says one
  thing: the command line is what has to change, not the workspace. Exit 1 keeps everything
  the parser understood and the query then failed on, `no such directory` and a malformed
  position included. `UsageException` carries the distinction, so which exit code a check
  produces is visible at the `throw` rather than at the catch. `--help` and `--version` are
  answers rather than errors and stay exit 0 on stdout.
- A candidate listing — an ambiguous target's, `outline`'s per-document one, the
  `candidates:` dump of a target that matched nothing — is `sym`'s shape and `sym`'s order,
  so every row it prints is a `path:line:col` the caller can paste straight back as a target.
- Every new command follows these. They are the reason this is a CLI and not a wrapper.

**`outline` is the one deliberate exception.** It prints the document path once as a header
and then one row per declaration — that declaration's own source line, indented by nesting —
with no per-row `path:line:col`, no `>` marker and no surrounding context, and `--context` is
inert for it. An outline *is* the summary the other rules exist to produce; repeating the path
on every row and padding each with context lines would make a whole file unreadable and cost
the context window the rules are meant to protect. Everything else still holds: one-based
lines, root-relative paths, `--max` (over the pre-order flattening, so a truncated tree is
always a prefix and no node outlives its parent) and the same `{ count, truncated, results }`
JSON envelope, with `path`, `generated` and `metadata` on the envelope because the whole
document is one URI.

**`hover` breaks the narrowest rule of the three.** Its answer is prose, not a place: the
position the hover applies to is still a root-relative one-based `path:line:col` header, but the
body is a signature and a doc-comment summary, so there is no source line, no `>` marker, and
`--context` is inert. `--max` caps the documentation's *lines*, because the single result is
never what a cap could usefully trim, and `count` is 1 for a hover and 0 for none. The signature
already carries the parameter types, which is why no `signatureHelp` command exists:
`textDocument/signatureHelp` would be a second request for information already in the first, and
it only answers inside an argument list rather than at a symbol. The client declares
`contentFormat: ["plaintext"]` for the same reason the rest of this section exists — markdown
would mean fenced blocks and `&nbsp;` runs that the caller has to undo.

**`sym` breaks one narrower rule.** It prints no source line and no `>` marker, and
`--context` is inert for it: a search result set is a list of places to go, not a place to
read, and padding every hit of a broad query with context is the exact cost the cap exists to
prevent. Everything else holds — root-relative `path:line:col` on every row, one-based, `--max`,
the same `{ count, truncated, results }` envelope — so this is a smaller exception than
`outline`'s, which also drops the per-row path. Rows carry Roslyn's `containerName` because it
is the only thing separating two symbols that share a name; it is localised display text, so
nothing asserts on it. No `|` appears in a row, unlike an outline's gutter, so a probe case can
quote one whole.

**`sym` ranks its answer itself, and the display sort is cosmetic.** `--max` is a relevance
cut: hits are ordered by how their name answers the query -- exact, then prefix, then substring,
case-insensitively -- then source before generated before metadata, then by URI and position as a
stable tiebreak, and only what survives that is sorted for display — on the same keys, with
the label in place of the URI, so the most relevant row is still the first one printed. The cap used to be taken in
the server's arrival order, on the measured premise that `workspace/symbol` answers globally
ranked. It does on `fixture/`; on three real corpora it does not -- the answer arrives grouped
per project and per target framework with generated copies first, so `sym Startup --max 10`
showed substring hits while 140 exact matches went unshown. Ranking here makes the cut mean the
same thing on every repository. The label projection stays *after* the cut, because resolving a
generated document's label costs a request and a broad query drops most of its hits, so the
tiebreak is on the raw URI rather than on what the row will render as.

Source-generated locations are labelled
`<generated>/<consuming project directory>/<assemblyName>/<typeName>/<hintName>`, where
`typeName` is the generator's full type name. That mirrors the layout
`EmitCompilerGeneratedFiles` writes under
`obj/.../generated/<assembly>/<generator full type name>/<hintName>`, which is the one place an
agent may already have seen these files. The generator type is in the label always rather than
only when something in the result set collides with it: Roslyn keys a generated document by
(generator type, hintName), so two generators in one assembly emitting the same hintName were
one string for both — an unpickable duplicate in a "pick one" list — and a label whose shape
depends on what else is in the result set is worse for a caller than a longer stable one. A
field the server does not send renders as `?`, like a missing assembly. Everything after the
project comes from the URI's stable fields; the project does not, and cannot. Those fields name
the *generator* — one generator applied to several projects, an analyzer in
`Directory.Build.props` being the common real-world shape, yields several distinct documents
whose URIs differ only in an authority guid that is regenerated on every workspace load. Before
the project was resolved they all rendered identically, and `Output` sorts and renders by that
label, so two rows tied on every visible key.

The project comes from `textDocument/_vs_getProjectContexts`, a VS protocol extension rather
than LSP. Its `_vs_id` is `<projectId guid>|<absolute .csproj> ($<tfm>)`; only the path half is
read, the guid being as unstable as the URI's own. Its `_vs_label` (`"Core (net10.0)"`) is
display text and is not parsed, for the same reason `containerName` is not — that was the other
candidate disambiguator, and it is both localised and absent from reference locations, which
carry a URI and a range and nothing else. The server neither advertises the request nor
requires a matching client capability, verified against 5.12.0-1.26426.8 on 2026-09-06. It is
asked only for a generated URI, at most once per document, and a failure falls back to the
generator-only label rather than to a guess. The project's *directory* is rendered rather than
its file name, so two same-named projects in different directories stay distinct.

`fixture2/` exists for this and nothing else: `Alpha` and `Beta` both consume `Gen2`, which
emits one identical `Stamp.g.cs` into each. They deliberately do not reference each other, so
both compilations can hold `Fixture2.Generated.Stamp` without CS0433. The shape cannot be added
to `fixture/` — `App` references `Core`, so a second copy of the generated type would collide at
the use site in `App/Program.cs`.

Metadata locations are labelled `<metadata>/<assembly>/<TypeName>.cs`, in the same spirit as
`<generated>/`, and for the same reason: the real URI is unprintable. It is a file URI, but the
path is `<temp>/MetadataAsSource/<guid>/DecompilationMetadataAsSourceFileProvider/<guid>/<Type>.cs`
— machine-absolute, and both guids are regenerated per server instance. `def` at
`Console.WriteLine` used to print exactly that, at exit 0. The type name is the file name; the
assembly is not in the URI at all and comes off the `#region Assembly` header Roslyn writes into
the document, which is why the label needs a lookup the way a generated document's does. Context
lines come off that file, which unlike a generated document is really on disk.

`outline` on such a document works when it is given the absolute path, but `outline
System.Console` does not and is not made to: `workspace/symbol` indexes source only, and the
document does not exist until a `textDocument/definition` at a use site makes Roslyn write it,
so there is no request that turns a type name into one. `hover` answers that question without a
document at all.

**The decompilation guard is kept, and now asks rather than assumes.** `SettleAsync` re-asks
while an answer is decompiled metadata, because a `ProjectReference` binds to the referenced
project's built assembly until that project loads. Measured 2026-09-07 against
5.12.0-1.26426.8, the two cases it could not tell apart: the stale binding did not occur once in
four cold runs of a cross-project `def` against `fixture/`, readiness-per-project having closed
the window it needs, while a framework `def` fired the guard 39 times over its whole 10 s budget
and then returned the answer it had had on the first query — that document *is* the definition.
Deleting the guard on an absence of evidence over three projects would have been the wrong
inference from the right measurement, so the discriminator was added instead: re-ask only when
the workspace *also* declares the decompiled document's type, which is exactly what separates
"bound to an assembly whose source is right here" from "bound to an assembly because that is all
there is". It costs one `workspace/symbol` query on the metadata path where it cost ten seconds,
and `def` at `Console.WriteLine` went 12.5 s to 2.5 s. A workspace that declares its own
`Console` pays the budget on a framework `def`; that is the accepted cost of keeping the guard.
The stale case cannot be reproduced by a probe — that is what the measurement says — so only the
pure halves of the discriminator are pinned, by `PathUriTests`.

**A multi-targeted document has several project contexts, and every one of them is a different
answer.** `net10.0;net9.0` means Roslyn compiles the file twice, with different preprocessor
symbols, so a type inside `#if NET9_0` exists in one context and not the other. Positional and
document requests are answered in **one** context, and the client is what picks it.

The contexts come from `textDocument/_vs_getProjectContexts`, one per `(.csproj, TFM)` pair, and
`cslq` orders them itself — `.csproj` path, then TFM, both ordinal. **Never the order the
server sent and never `_vs_defaultIndex`.** Measured 2026-09-10 on `fixture2/Multi` against
5.12.0-1.26426.8: `_vs_defaultIndex` was `0` in 6 of 6 runs while the array order around it
varied per attach, and the unqualified answer followed `contexts[0]` in 6 of 6 — which is the
whole of T-26 (`project` naming a different TFM each run) and T-27 (`hover`/`def` on a
conditional type answering 4 times in 8). The index carries no information; the load order it
reflects is not ours to control; an order of our own is the only thing that makes an answer
repeatable.

The chosen context rides on the request as `_vs_projectContext` inside the
`TextDocumentIdentifier`, carrying the `_vs_id` **verbatim** — Roslyn matches on that alone, and
the projectId guid inside it is regenerated on every attach, so the fetch and the use have to
happen in one process. With it, 36 of 36 forced requests answered from the context asked for,
including 24 whose `contexts[0]` was the other TFM, and 12 of 12 forced at the *wrong* context
answered empty. Both directions are the measurement: the field decides the answer.

**Determinism alone would be a regression, and this is the part worth remembering.** A symbol
inside `#if NET9_0` does not exist in the `net10.0` context, so a fixed first context turns a
coin flip into a *guaranteed* miss for every symbol living in the other branch — `hover Only9`
would go from 4 misses in 8 to 8 in 8. So a context-bound request asks the contexts **in
order, stopping at the first that answers**, and reports the one that did. Every context
answering empty returns the first context's answer, so "nothing" is one determinate answer
rather than whichever context was tried last.

`--tfm <name>` restricts the set to the matching contexts — plural, because a file linked into
two projects can be compiled for one framework twice — and a framework the document has no
context for is an error naming the ones it has. It is what answers "does net9.0 build" without
reading a `net10.0` view, and the escape hatch if a later server stops honouring
`_vs_projectContext`.

`project` prints **one row per context**, in that same order, with `count` equal to the number
of contexts: it exists to tell an agent whether it has to reason about `#if` branches at all,
and one row with `count: 1` said the file had a single home. An answer from a document with
more than one context carries the context it came from — a trailing
`answered in net9.0 of 2 contexts: net10.0, net9.0` in text, `tfm` and `contexts` on the
`--json` envelope — and an empty one says how many were tried. A single-context document says
nothing, which is every document in an ordinary repository: a note on every answer would train
a caller to skip it.

**Nothing but a pinned deterministic answer can catch a server that drops
`_vs_projectContext`.** `_vs_getProjectContexts` either answers or fails, and the failure is
handled; an unrecognised *member of a request payload* is silently ignored, and the symptom is
the intermittency of T-27 coming back — an answer that is right most of the time. So
`tfm-excludes-the-other-branch` in `cases.jsonl` asserts that `hover Only10 --tfm net9.0` finds
**nothing**: it can only pass if the server honoured the context it was handed. Keep it, and
keep it as an absence.

**An answer that is a *set* asks every context and unions them, and that is a different rule
from `hover` and `def`.** A single-answer command can stop at the first context that answers,
because there is one right answer and the retry finds it. `refs`, `impl`, `outline` and `diag`
answer with a set, and a set that stops early is *wrong*: a reference inside an `#if NET9_0`
block exists only in that context, and omitting it is silent. So these four ask every context in
the set — all of them, or the `--tfm` subset — and union what comes back:

- **`refs` and `impl`** concatenate, and the existing fold does the rest: rows are folded on
  their rendered label plus range, before `--max`, so a hit both contexts report is one row.
  That is the same fold that already collapsed a generated document's per-framework twins, and
  it is why the union does not reintroduce the duplication T-30 was about.
- **`outline`** unions by declaration, keyed on name, kind and identifier position — every
  context parses the same text, so those agree. A declaration's *extent* does not: on
  `fixture2/Multi/Conditional.cs` the namespace ends at line 6 in `net10.0` and line 13 in
  `net9.0`, each context seeing only its own branch, so a merged node takes the **widest**
  range. Keeping the first context's made a child sit outside its own parent, and
  `Targets.Chain` walks down by full-range containment, so `def Fixture2.Multi.Only9` went from
  failing 2 runs in 6 to failing 4 in 4. Declarations that are not in every context asked carry
  them — `public sealed class Only9  [net9.0]` — and the ones that are carry nothing, so an
  unconditional file renders exactly as it did before.
- **`diag`** pulls every context, folds rows on what a reader sees (position, severity, code and
  message) and labels a row only some contexts report. This is T-28: `net9.0`-only CS0029 in
  `TfmError.cs` was reported by an unqualified pull in 1 run of 4 and silently absent in the
  other 3. The fold key includes the message because two contexts disagreeing about the *text*
  at one position are two findings — serilog answers `Substring can be simplified` in one
  context and `Slice can be simplified` in another — while an identical row from both is one
  finding, which is what keeps `diag` free of the per-framework duplication testers confirmed it
  never had.

The cost is one extra pull per context per document, and it was measured before the rule was
adopted rather than after. On `fixture2` (6 documents, 3 of them two-context) a whole-tree
`diag` walk went from 2694/2740/2956 ms to 2827/2885/3030 ms warm, about +5%; on `fixture` (4
single-context documents) 2690/2925 ms to 2534/2592/2732 ms, which is no change at all. A
single-context document pays nothing but the one `_vs_getProjectContexts` request every
context-bound command makes, and a document with N contexts pays N pulls because N pulls is
what the answer is made of.

**The dotted-target chain reads the union too, and ignores `--tfm` doing it.** `SelectAsync`
resolves `Fixture2.Multi.Only9` by reading the declaration chain off the document's syntax tree;
one context's tree cannot see the other branch's declaration at all, so the target failed with
`no symbol matched` in 2 runs of 6 while the bare `Only9` was already deterministic. It now
merges every context's tree — every context, not the `--tfm` subset, because this is "where in
the file is this declared" and `--tfm` has no business constraining a lookup. The option still
constrains the answer.

A set answer's note names the contexts it **merged**, since every context asked contributed to
it: `merged from 2 contexts: net10.0, net9.0`, or `merged from net9.0 of 2 contexts: …` under
`--tfm`. `tried all 2 contexts: …` is kept for the *empty* answer, where nothing was found and
it is the only true thing to say — a correct answer followed by "tried" reads as a failure the
caller then has to rule out, which is a cost paid on every successful call. `hover` and `def`
keep `answered in <tfm> of N contexts: …`, because for them one context really did answer.

`--json` carries `contexts` on the envelope for all four, and **no `tfm`** — see the envelope
rule above; the per-row `tfm` is where the fact lives: on an `outline` node, the contexts that
declare it; on a `diag` row, the contexts that report it; null on both when every context asked
has it.

`diag` takes a note only when it was given a single file. A note names one document's contexts
and a walk spans documents with different context sets; the per-row labels are what carry the
fact there.

## Targeting a symbol by name

A dotted target is verified against the **syntax tree**, one
`textDocument/documentSymbol` per candidate document, never against `containerName` and never
against `hover`. The candidate's *chain* is the declaration path that request gives —
`["Fixture","Core","Greeter","Greet"]` for `Greet` in `Greeter` in `Fixture.Core` — and a
dotted target matches when its segments are a **contiguous suffix** of that chain. So
`Fixture.Core.Greeter.Greet`, `Core.Greeter.Greet` and `Greeter.Greet` all select, while
`Wrong.Namespace.Greeter.Greet` and `Fixture.Greeter.Greet` select nothing.

The two rejected alternatives were rejected on measurement, not on taste.

`containerName` is what this used to test, and it can only ever have narrowed by *enclosing
type*: it is localised display text (`in Greeter (project Core (net10.0))`), so for a top-level
type the string is `project <name> (<tfm>)` and the only namespace segment that could pass was
one equal to the project name. `Fixture.Core.Greeter` passed in the probe suite for exactly
that reason — the project is called `Core` — which masked the defect for four milestones.
Meanwhile the test was a token test over one segment, so a wrong namespace matched at exit 0
and a right one failed on five of five top-level types on Serilog and three of three on
OrchardCore.

`hover` was the proposed replacement, and it cannot do it. Measured on the fixture 2026-09-09:
hover's first line is fully qualified **for types only** — `class Fixture.Core.Greeter` — while
a member prints the minimal form, `string Greeter.Greet(string name)` and
`int Volume.Litres { get; }`. A member's namespace is therefore not in the answer at all, and
a member's namespace is the case that was wrong. `documentSymbol` does carry the chain: its
namespace node is named `Fixture.Core`, already dotted, with `Greeter` as its child.

Chains are computed only where they are needed — a dotted target, or a bare name with more than
one distinct candidate — so a bare unambiguous name, the common case, costs no extra request.
One request per distinct document, cached for the call. Node names are reduced to their
identifier first: `documentSymbol` renders a member's signature and return type into its name
(`Greet(string) : string`) and a generic's type parameters (`Box<T>`), and a namespace's name is
split on its dots.

Contiguity rather than "in order" is what fixes the nested case. `Outer.Inner.Depth` and
`Other.Inner.Depth` differ in a segment no display string carries, so an in-order test would
keep matching both — the chain's parent of `Inner` is the discriminator, and it is only there
because the tree was read.

**A bare name selects the type over its own constructors.** `workspace/symbol` reports an
explicit constructor as a separate symbol of the type's own name and of kind `method`, so every
type with a constructor was ambiguous with itself and had no symbol-form route at all: on
CleanArchitecture every handler, validator, behaviour and the DbContext, with `TodoItem` and
`BaseEntity` working only because they have none. A candidate is a constructor when its kind is
`method` and its chain repeats its own last name — the chain answers this, `containerName`
again being display text. When the distinct candidates are exactly one type-kind symbol plus
constructors whose chain is that type's chain plus one, the bare name selects the type.
Anything else stays ambiguous, because anything else is a real alternative: two types of one
name, or a same-named method that is not a constructor. `Widget.Widget` still selects the
constructors — the suffix match holds on a constructor's chain and not on the type's — so
overloaded constructors remain ambiguous with each other; a selector for them is a separate
design.

The kind a row prints is the one `workspace/symbol` reported, except where the chain has
already been read. `sym` prints `method` for a constructor, because `sym` must not spend a
`documentSymbol` request per hit and the chain is the only thing that identifies one — LSP kind
9 is mapped, but the server never sends it. The ambiguity listing runs after the selection that
computes those chains, so it carries them through and prints `constructor`; the `candidates:`
dump has no chains and prints what the server said. No listing asks for one.

## Readiness

A query fired before the workspace loads answers empty rather than erroring, so every command
waits first. **Ready means every project loaded**, not merely that the server answered
something: one sentinel only ever proved *some* project was up, and the window that leaves open
produced `refs`, `impl` and `sym` answers that were silently incomplete at exit 0. So `cslq`
takes one sentinel per project and requires each to resolve to a location inside that project's
own directory and **not** inside a project nested within it — never matched by `containerName`,
which is localised display text. The nested exclusion is not a corner case: `Web/` and
`Web/Tests/` both declaring `Program` is the ordinary shape, and without it Tests loading marks
Web ready, which is the exact silently-incomplete failure above.

The wait is bounded once, and only on a cold load. When `projectInitializationComplete` fires
**in this process** — `--no-daemon`, or the first client of a fresh daemon — the still-pending
sentinels get 20 s more and then the wait fails with the message that names them. Measured on
the wire: against a cold server `workspace/symbol` answers nothing until that notification and
then jumps straight to complete, so a candidate unresolved after it is not going to resolve.
The shape that produces one is a project whose only type declaration sits inside an `#if false`
branch: the regex reads it, no compilation ever contains it, and the `.csproj` is otherwise
ordinary, so none of the skip rules above can see it. On a daemon attach the notification fired
before this process existed and there is nothing to bound from — and that is precisely the case
where the incomplete-answer window lives — so the full `--timeout` stands there. A `--timeout`
shorter than the grace still wins.

A root whose path contains a `%XX` sequence never becomes ready, and the cause is outside
`cslq`. `new Uri(path).AbsoluteUri` escapes a literal `%` to `%25`, so `.../pct%20x` becomes
`file:///.../pct%2520x` and `PathUri.ToPath` gives the directory back unchanged; the URIs `cslq`
sends are correct. MSBuild is what unescapes `%XX` in the paths it reads, so the projects never
load for the server any more than `dotnet restore` on the same tree succeeds — it fails naming
`pct x`. Verified 2026-09-09 both ways: a `%` not followed by two hex digits (`pct%zzx`) is a
~4 s `ready`. There is nothing to fix here, only the round-trip to keep pinned.

Two limits are known and deliberate. A project the scan can infer no sentinel for — one that is
only top-level statements, or only Razor or resources — is **not** waited on: there is nothing
to ask the server for, and failing on it would break those projects outright. It is named on the
failure path instead, so its absence from readiness is visible rather than silent. And two
`.csproj` in one directory collapse to a single entry, because a hit under that directory cannot
be attributed to one of them by path — no scan-based scoping can separate them, so the second
project is covered only incidentally.

A third class cannot be probed even in principle, and is reported rather than waited on. A
project whose sources are linked in from outside its own directory — a `*.projitems` import,
or `<Compile Include="../Shared/**">` — owns no document whose location can be scoped to it:
every hit sits under the *source* directory, which `Sentinel.Accepts` rejects for the same
reason it rejects a nested project's hit. Fourteen of CommunityToolkit's twenty-six projects
are that shape.

Both unprobed classes are therefore reported, not merely counted: `ready --json`'s `projects` is
the number actually probed, `skipped` names the linked-only ones and `unprobed` the ones with
sources but no type declaration, each root-relative with forward slashes, and
`projects + skipped + unprobed` is every project the solution yielded. Two separate arrays
rather than one, because the two reasons are not interchangeable — "no sources of their own" is
a lie about a project that has them — and both are named rather than left out, because a
project absent from all three would read as loaded when nothing had checked it. That is the
every-project-loaded guarantee reported as held when it had not been tested, which is exactly
what `projects: 26` on a workspace with fourteen linked projects used to claim. At
`--log-level Information` the same two lists go to stderr, which is the only channel text mode
has.

Knowing the projects means reading the root's solution — because **the server cannot be
asked**: `workspace/_roslyn_restorableProjects` is a server-to-client request and carries no
project list. A root with no solution, or with two of them, is an error rather than a
`*.csproj` scan, for the reasons the next paragraphs give. The solution's list is still an
approximation of what Roslyn loaded, but it errs the way that is survivable.

The solution is read because **over-inclusion is not merely wasteful, it is fatal:** a `.csproj`
the solution excludes is never loaded, so its types are never indexed, its sentinel can never
resolve, and readiness burns the whole timeout and exits 1. Measured 2026-09-06, before the
solution was read: `cslq ready` on OrchardCore v3.0.1 failed on
`src/Templates/OrchardCore.ProjectTemplates/content/*`, which are `dotnet new` template content
rather than solution projects, and the only way past it was to point `--root` below them.

Reading the solution is not enough on its own, because **a `.csproj` the solution does not list
is still a nesting boundary**. `OrchardCore.slnx` lists the wrapper
`src/Templates/OrchardCore.ProjectTemplates` and excludes only the five `content/*/*.csproj`
under it, so the wrapper's own candidate scan kept reading types out of `content/**/*.cs` and
`cslq ready` on the full root still burned 900 s. Both the file filter and `Sentinel.Nested` are
therefore computed from the `.csproj` files **on disk**, unioned with the discovered list so a
project the solution places outside the root is still a boundary. That is one of two independent
fixes for the same failure: the other reads the project's own `.csproj` as text, where
`EnableDefaultItems=false` with no `<Compile Include>` says the project compiles nothing at all
— which the wrapper does say — so it contributes no candidate however many `.cs` files sit
under it. A `.csproj` that does not parse reads as an ordinary project rather than throwing:
one unparseable file in a tree of a couple of hundred must not take readiness down, and
guessing "ordinary" costs only the candidates a scan would have found anyway.

Exactly one solution counts, and only at the top of the root. Two of them is an error naming
both files, thrown before the server starts: there is no basis for choosing between them, and
the `.csproj` scan that used to answer instead is over-inclusive, so a project neither solution
loads gets a sentinel that can never resolve and readiness burns its whole timeout — three runs
out of three when testers hit it. A solution in a subdirectory describes that subtree rather
than this root, and Roslyn would not open it for this root either, so a root holding only that
is a root with no solution. A `.slnf` solution filter is not read. A project the solution lists
but that is not on disk is dropped: waiting on one is the same unresolvable sentinel by another
route. A root whose solution lists no C# project fails immediately instead of timing out,
naming the solution, because "no .csproj under <root>" would be a lie about a root whose
projects are sitting right there. A `.slnx` that no longer parses fails the same way rather
than as an unhandled `XmlException`: `Main` catches `CslqException` and nothing else, so a
hand-edited solution file would otherwise be answered with a stack trace and exit 127.

`--sentinel` adds a root-scoped probe carrying the named symbol to the inferred set; it does
not replace it. Replacing it was the earlier design, and it handed the incomplete-answer bug
back through the escape hatch: testers reaching for `--sentinel` to get past the failure above
had `impl StartupBase` answer 321 hits against 331, at exit 0. Every project still has to
resolve, and so does the explicit probe. It stands alone only when inference throws — no
solution, two solutions, no C# project, no candidate anywhere — which is the layout it is the
escape hatch for. It is not a project: `ready --json` neither counts nor lists it, and the
failure path names it `explicit sentinel 'X'`.

Sentinel candidates come from a regex, not a parser, and it matches English prose in doc
comments: "identifying the class and assembly context" yields the candidate `and`. Comments and
string literals are therefore stripped before the declaration regex runs. Taking every match in
a file rather than the first is not sufficient on its own and neither is the cap of three: one
doc-comment sentence yields `and` / `of` / `for` and fills all three slots, leaving a project
probed only by words no query can resolve. Measured 2026-09-06: `cslq ready` against OrchardCore
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
`cslq diag <subdir> --root <repo>` keeps the full sentinel set and walks only the subtree.

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
