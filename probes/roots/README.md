# Probe roots

Workspace *shapes* the two fixtures cannot hold, kept here because each one is answered before
the server starts and so costs a probe case milliseconds rather than a load.

- `multilang/` — a solution listing a `.vbproj` and no C# project: the non-C# notice, and then
  the "lists no C# project" failure under it.
- `toplevel/` — one project of top-level statements, `dotnet new console`'s default: the shape
  sentinel inference cannot read a type out of.

Nothing here is ever built or restored, and no `.csproj` here is listed by `Cslq.slnx`.
