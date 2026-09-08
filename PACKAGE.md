# cslq

Semantic C# queries for coding agents, over Microsoft's official
[`roslyn-language-server`](https://www.nuget.org/packages/roslyn-language-server) — the same
engine behind the VS Code C# extension. `cslq` is a thin LSP client that turns LSP's URIs and
zero-based ranges into `path:line` plus source context, so an agent can find every caller of a
method instead of grepping for its name.

## Install

Prerequisites: the **.NET 10 SDK** and **git**. Nothing else — `cslq` fetches the language
server itself on first run.

```
dotnet tool install -g cslq
```

The first command that needs the language server restores it for you and says so:

```
cslq: the pinned language server is not restored; restoring it in <dir>. This is a one-time ~300 MB download.
```

`<dir>` is the tool's *own* manifest directory — the `.config/dotnet-tools.json` packed
alongside the binary, never your repository. The restore is idempotent and later runs skip it.
When an update brings a new pin, the restore that fetches it deletes every other version of the
server from `~/.nuget/packages` and says which, so updating weekly does not stack 300 MB payloads.
`cslq restore` does it on demand, with no workspace, for a Dockerfile or a CI step that would
rather not pay the download inside the first query.

Then, in the repository you want to query:

```
dotnet restore                 # the server does not restore your projects
cslq ready --root <dir>
```

`--root` must be **the directory holding the `.sln` or `.slnx`** — `cslq` loads the projects
that solution lists. A root with no solution at its top is an error, reported in about a second
rather than after the timeout, and a solution one directory down does not count.

## Use

```
cslq ready                                    # block until the workspace has loaded
cslq refs <symbol | file:line:col> [--max N]  # every reference, with context
cslq def <symbol | file:line:col>             # where it is declared
cslq impl <symbol | file:line:col>            # what implements or overrides it
cslq hover <symbol | file:line:col>           # what it is: type, signature, docs
cslq sym <query> [--max N]                    # search the workspace by name
cslq outline <file | symbol> [--max N]        # the declarations in one document
cslq diag [path] [--errors-only]              # compiler and analyzer diagnostics
cslq project <file>                           # which .csproj compiles it, for which TFM
cslq restore                                  # fetch the pinned language server, then exit
```

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

Paths are relative to `--root`; lines and columns are one-based. Add `--json` for a
`{ count, truncated, results }` envelope.

The [README on GitHub](https://github.com/idotta/cslq/blob/main/README.md) has the rest:
the agent skill, measured latency and the shared daemon, every failure mode `cslq` handles,
and the evidence behind the dependency choices.
