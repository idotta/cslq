# cslq

Semantic C# queries for coding agents, over Microsoft's official
[`roslyn-language-server`](https://www.nuget.org/packages/roslyn-language-server) — the same
engine behind the VS Code C# extension. `cslq` is a thin LSP client that turns LSP's URIs and
zero-based ranges into `path:line` plus source context, so an agent can find every caller of a
method instead of grepping for its name.

## Install

`cslq` is built for coding agents, so there are two pieces: the tool on `PATH`, and the skill
that makes an agent reach for it instead of grep. The tool on its own is inert — nothing will
invoke it.

Prerequisites: the **.NET 10 SDK** and **git**. Nothing else — `cslq` fetches the language
server itself on first run.

```
dotnet tool install -g cslq       # the tool
npx skills add idotta/cslq -g     # the skill, into every agent on the machine
```

The second command is [`npx skills`](https://github.com/vercel-labs/skills), which installs
`skills/cslq/` from the repository into each agent's skills directory —
`~/.claude/skills/cslq/` for Claude Code. Drop `-g` to install into the current repository
instead. Without `npx`, copy that directory there yourself: two plain markdown files, `SKILL.md`
with the YAML frontmatter and `REFERENCE.md` beside it, and nothing else on disk.

Or hand both steps to the agent:

> Install `cslq` and its skill so you can answer C# questions semantically instead of grepping:
> run `dotnet tool install -g cslq`, then `npx skills add idotta/cslq -g`, then `cslq restore`.
> Then run `cslq ready --root .` here and tell me what it printed.

The first command that needs the language server restores it for you and says so:

```
cslq: the pinned language server is not restored; restoring it in <dir>. This is a one-time ~300 MB download.
```

`<dir>` is the tool's *own* manifest directory — the `.config/dotnet-tools.json` packed
alongside the binary, never your repository. The restore is idempotent and later runs skip it.
When an update brings a new pin, the restore that fetches it deletes every other version of the
server from NuGet's global packages folder (`~/.nuget/packages` unless `NUGET_PACKAGES` or a
NuGet.Config moves it) and says which, so updating weekly does not stack 300 MB payloads.
`cslq restore` does it on demand, with no workspace, for a Dockerfile or a CI step that would
rather not pay the download inside the first query.

Then, in the repository you want to query:

```
dotnet restore                 # not required, but a restore that fails is invisible without it
cslq ready --root <dir>
```

The server restores as part of its design-time build — measured on a never-restored solution,
ready in 7 s. What it cannot do is tell you when that restore *failed*: every project then
loads empty, and `dotnet restore` is what names the package.

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
cslq session <status | stop>                  # the background session for these options
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

## The session

A background `cslq` holds the loaded workspace open between calls, so only the first call pays
the solution load. Measured on a 4-project fixture, Release, 2026-09-11: that first call
4.9-7.6 s — **more** than a one-shot, since it pays the load plus a process start — and every
call after it 138-188 ms, against 2.2-2.4 s per call with `--no-session`. So spend the first
call on `cslq ready` and then ask narrow questions freely.

It ends after 900 s idle (`CSLQ_SESSION_KEEPALIVE`), on `cslq session stop`, or with the
terminal. `cslq session status` names its pipe, root, pid and log. It is not the Roslyn daemon,
which is a separate shared process `--no-daemon` opts out of.

The [README on GitHub](https://github.com/idotta/cslq/blob/main/README.md) has the rest:
the agent skill, measured latency and the shared daemon, every failure mode `cslq` handles,
and the evidence behind the dependency choices.
