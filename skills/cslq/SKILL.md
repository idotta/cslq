---
name: cslq
description: >-
  Semantic C#/.NET queries — run `cslq` instead of grep, ripgrep, Select-String or reading files
  for any question about meaning rather than text. Use for "find all callers", "who uses",
  "where is X defined", "go to definition", "what's in this file", "does this compile", "any
  errors", "what does this class expose", "rename impact", "is this still used", "dead code",
  "what implements this interface", "who overrides this", "find a symbol by name", "what is
  this", "what type is this", "what are the parameters", "which project is this in" — and on any
  C# identifier named without a file path. Also before editing an unfamiliar C# file.
---

# `cslq`

A CLI over Microsoft's `roslyn-language-server` — the engine behind the VS Code C# extension. It
answers from the semantic model, so it sees cross-project references, generics, source-generated
code and `partial` halves. Grep sees none of that. Never answer "who calls this" or "where is
this defined" from a text search in a C# repo.

Not installed is not a reason to fall back to grep — `dotnet tool install -g cslq`, then
`cslq restore` (one-time ~300 MB; optional, the first query does it anyway).

**Start every session with `cslq ready`.** It gives the workspace one place to fail, so a load
failure is not mistaken for an empty answer — and it now pays the load for you: a background
session holds the workspace open, so `ready` is the slow call and everything after it is fast.

## Task → command

| Task | Command |
|---|---|
| Every caller / user of a method, type, property | `cslq refs <symbol>` |
| Where something is declared | `cslq def <symbol>` |
| What implements an interface or overrides a member | `cslq impl <symbol>` |
| What something *is* — type, signature, parameters, docs | `cslq hover <symbol \| file:line:col>` |
| What a parameter *means* (`<param>` prose) | `cslq def <symbol>`, read the doc comment — hover drops every `<param>` |
| Which project compiles a file, and for which framework | `cslq project <file>` |
| Find a symbol when you know only part of the name | `cslq sym <query>` |
| What a file declares, and its nesting | `cslq outline <file>` |
| Compiler / analyzer errors | `cslq diag [path]` |
| Confirm a symbol exists at all | `cslq def <symbol>` — never `sym` |
| What a framework or NuGet type looks like | `cslq hover` at a use of it, or `cslq def` |

## Targets

`refs`, `def`, `impl`, `hover`, `outline` take a **symbol** (`Greet`, `Greeter.Greet`,
`Fixture.Core.Greeter.Greet` — segments must be a contiguous suffix of the declaration path,
namespaces included) or a **position** (`App/Program.cs:9:35`, one-based, relative to the root,
columns in UTF-16 code units). Paste any position out of any `cslq` result straight back.
`outline` also takes a bare file path; anything with a separator or ending `.cs` is a file.

`sym` takes a **search query** instead — fuzzy, IDE Ctrl+T matching. `diag` takes a file or
directory path or nothing; `project` takes a file path. Every path must lie under `--root`.

## Output

`path:line:col` relative to the root, then the matched line marked `>` with one context line
either side. `--json` gives `{ count, truncated, results }` on every command and every path,
failures included as `{ "error": ... }`. In text mode stdout is the answer and nothing else.

| Code | Meaning |
|---|---|
| 0 | Answered (`diag`/`outline` with nothing to report included) |
| 1 | Lookup failed: no such symbol, ambiguous, no references, or the workspace never loaded |
| 2 | Invocation not understood (unknown command or option, bad value) |
| 127 / 130 | Internal failure / interrupted |

## Traps — wrong answers at exit 0

- **`sym` is fuzzy.** `Greter` finds `Greeter`, `Greet` and `Green`. A `sym` row is never
  confirmation that a name exists — confirm with `def`, which matches exactly.
- **`impl` on a member with no implementations is not empty.** Roslyn falls through to the
  declaration, so it prints what `def` would. One result at the symbol's own declaration means
  "nothing implements this".
- **`<metadata>/…`, `<generated>/…`, `<external>/…` are real answers, not paths.** They label a
  decompiled, source-generated or outside-the-root document. Do not pass one back to `cslq` and
  do not read it as a file; reach those declarations by name instead.
- **A `.vbproj`/`.fsproj` is not searched**, and two `.csproj` in one directory answer wrongly.
  Both leave `refs` incomplete at exit 0 — see REFERENCE.md before reporting hits as complete.

## Options

```
--root <dir>     workspace root (default: cwd; must hold exactly one .sln/.slnx)
--max N          cap results (default 50)          --context N   lines either side (default 1)
--timeout N      seconds to wait for load (180)    --tfm T       one target framework only
--errors-only    diag: drop warnings and info      --json        machine-readable
--sentinel <sym> also require this to resolve      --no-daemon   private server
--no-session     load in this process instead of the background session
--log-level L    Trace|Debug|Information|Warning|Error|Critical|None
```

Options go anywhere in the command line. An unknown one is exit 2.

`--tfm` is **not needed for a correct answer**: `hover`/`def` pick a context and say which,
`refs`/`impl`/`outline`/`diag` union every context. Use it only to ask about a specific
framework.

The first call to a cold session pays the whole solution load and costs **more** than a one-shot
would — a few seconds on a small root, ~30 s on 26 projects — and every call after it is ~150 ms.
So spend the first call on `cslq ready` and then ask freely: narrow questions are now cheap, and
folding several into one broad query buys nothing. `--no-session` pays the load on every call
instead.

## REFERENCE.md

Beside this file. Read it for: an empty answer you cannot explain, the full output and `--json`
rules, `sym`'s matching behaviour, multi-targeted projects and `#if` branches, kinds, and the
readiness limits that let a project's hits go missing.
