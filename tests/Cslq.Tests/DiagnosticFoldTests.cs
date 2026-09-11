using System.Text.Json;

namespace Cslq.Tests;

/// <summary>
/// Folding one document's diagnostics across the contexts that compile it. Both halves matter:
/// a finding every context reports must appear <em>once</em> — no per-framework duplication,
/// which testers confirmed <c>diag</c> never had and this must not introduce — and a finding
/// only one context reports must survive at all, which is T-28: a CS0029 that exists in
/// <c>net9.0</c> alone was reported in 1 unqualified pull of 4 and otherwise silently absent.
/// </summary>
public class DiagnosticFoldTests
{
    private const string Uri = "file:///repo/Multi/TfmError.cs";

    private static Diagnostic Diagnostic(string code, int line, string message = "boom") =>
        new(
            new Range(new Position(line - 1, 16), new Position(line - 1, 28)),
            1,
            JsonDocument.Parse($"\"{code}\"").RootElement,
            message);

    [Fact]
    public void A_finding_every_context_reports_is_one_row()
    {
        var reports = Program.Reports(
            Uri,
            [
                ("net10.0", [Diagnostic("IDE0002", 9)]),
                ("net9.0", [Diagnostic("IDE0002", 9)]),
            ]).ToList();

        var only = Assert.Single(reports);
        Assert.Equal(["net10.0", "net9.0"], only.In);
        Assert.Equal(2, only.Contexts);
    }

    [Fact]
    public void A_finding_one_context_reports_keeps_the_context_that_has_it()
    {
        var reports = Program.Reports(
            Uri,
            [
                ("net10.0", []),
                ("net9.0", [Diagnostic("CS0029", 10)]),
            ]).ToList();

        var only = Assert.Single(reports);
        Assert.Equal("CS0029", Output.Code(only.Diagnostic.Code));
        Assert.Equal(["net9.0"], only.In);
        Assert.Equal(2, only.Contexts);
    }

    /// <summary>
    /// The fold key is what a reader sees, message included: <c>IDE0057 Substring can be
    /// simplified</c> in one context and <c>Slice can be simplified</c> in the other at the
    /// same position are two different findings, and serilog is where that was measured.
    /// </summary>
    [Fact]
    public void Two_contexts_disagreeing_about_the_message_are_two_rows()
    {
        var reports = Program.Reports(
            Uri,
            [
                ("net10.0", [Diagnostic("IDE0057", 5, "Slice can be simplified")]),
                ("net9.0", [Diagnostic("IDE0057", 5, "Substring can be simplified")]),
            ]).ToList();

        Assert.Equal(2, reports.Count);
        Assert.Equal(["net10.0"], reports[0].In);
        Assert.Equal(["net9.0"], reports[1].In);
    }

    /// <summary>
    /// A document no project compiles is asked once with no context, and its one view is
    /// unnamed — so nothing is ever marked and an ordinary row is unchanged.
    /// </summary>
    [Fact]
    public void A_document_with_no_context_yields_plain_rows()
    {
        var reports = Program.Reports(Uri, [(string.Empty, [Diagnostic("CS0029", 10)])]).ToList();

        var only = Assert.Single(reports);
        Assert.Equal(1, only.Contexts);
        Assert.Equal([string.Empty], only.In);
    }
}
