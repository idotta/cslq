# Security

Report a vulnerability in `cslq` privately through
[GitHub's private vulnerability reporting](https://github.com/idotta/cslq/security/advisories/new).
Do not open a public issue for it. Expect an acknowledgement within a week.

Only the latest release on nuget.org is supported. Fixes ship as a new version, not as a patch
to an old one.

`cslq` runs Microsoft's `roslyn-language-server`, pinned in `.config/dotnet-tools.json`. A
vulnerability in the server itself belongs to [dotnet/roslyn](https://github.com/dotnet/roslyn/security);
a problem in how `cslq` launches or talks to it belongs here.
