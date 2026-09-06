// In no project: Fixture.slnx lists App, Core and Gen, and none of them compiles this
// directory. Readiness must ignore it — the sentinel is inferred per project, and a type
// declared here can never resolve, so picking one would hang every run until its timeout.
// Roslyn confirms the asymmetry: workspace/symbol does not answer for this file, and
// textDocument/diagnostic reports nothing for it, but outline still works off the syntax
// tree. Measured 2026-09-05 against 5.12.0-1.26426.8.
namespace Fixture.Ambient;

public sealed class Stray
{
    public Missing Wanted { get; } = new Missing();
}
