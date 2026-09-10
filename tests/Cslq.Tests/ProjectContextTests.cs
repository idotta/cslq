using System.Text.Json;

namespace Cslq.Tests;

/// <summary>
/// Choosing a project context, which is all a multi-targeted document's answers depend on.
/// Measured 2026-09-10 on <c>fixture2/Multi</c>: <c>_vs_defaultIndex</c> was 0 in 6 of 6
/// runs while the context array's own order varied per attach, and an unqualified positional
/// request followed the array — so the order has to be ours, and it has to be a function of
/// the contexts alone.
/// </summary>
public class ProjectContextTests
{
    private static DocumentContext Context(string csproj, string? tfm) =>
        new($"3fa8|{csproj}{(tfm is null ? string.Empty : $" (${tfm})")}", csproj, tfm, $"P ({tfm})");

    /// <summary>
    /// The path half of a <c>_vs_id</c>, with the framework the id carries for a
    /// multi-targeted project kept rather than thrown away — <c>cslq project</c> is what wants
    /// it. The guid half is regenerated on every workspace load and is never read.
    /// </summary>
    [Theory]
    [InlineData(
        "3fa8|C:\\repo\\App\\App.csproj ($net10.0)", "C:\\repo\\App\\App.csproj", "net10.0")]
    [InlineData("3fa8|/repo/App/App.csproj", "/repo/App/App.csproj", null)]
    public void A_project_context_id_yields_the_csproj_and_the_framework(
        string id, string file, string? tfm)
    {
        Assert.Equal((file, tfm), Contexts.Parse(id));
    }

    /// <summary>
    /// Anything shaped unexpectedly yields nothing rather than a guess: a wrong project in a
    /// label is worse than no project.
    /// </summary>
    [Theory]
    [InlineData("no-bar-at-all")]
    [InlineData("3fa8|C:\\repo\\App\\App.vbproj")]
    public void An_unexpected_project_context_id_yields_nothing(string id)
    {
        Assert.Equal((null, null), Contexts.Parse(id));
    }

    /// <summary>
    /// A context whose <c>_vs_id</c> does not name a <c>.csproj</c> is not a project context,
    /// and dropping it is the answer rather than a defence: Roslyn hands a file no project
    /// compiles a miscellaneous-files context, and keeping it would make <c>cslq project</c>
    /// answer at exit 0 for a file nothing compiles.
    /// </summary>
    [Fact]
    public void A_miscellaneous_files_context_is_not_a_project_context()
    {
        var misc = new ProjectContext(
            "eeae|LanguageServerWorkspace Files Project for /repo/Ambient/Stray.cs",
            "Arquivos Diversos");

        Assert.Empty(Contexts.Read([misc]));
    }

    [Fact]
    public void Read_parses_orders_and_keeps_the_project_contexts()
    {
        var contexts = Contexts.Read(
        [
            new ProjectContext("4d7b|/repo/Multi/Multi.csproj ($net9.0)", "Multi (net9.0)"),
            new ProjectContext("16c1|/repo/Multi/Multi.csproj ($net10.0)", "Multi (net10.0)"),
        ]);

        Assert.Equal(["net10.0", "net9.0"], contexts.Select(c => c.Name));
        Assert.Equal("/repo/Multi/Multi.csproj", contexts[0].File);
    }

    /// <summary>
    /// Project first, then framework, both ordinal — and the input order is deliberately the
    /// reverse of the answer, because the server's order is what must not survive.
    /// </summary>
    [Fact]
    public void Contexts_are_ordered_by_project_then_framework()
    {
        var ordered = Contexts.Order(
        [
            Context("/repo/Z/Z.csproj", "net9.0"),
            Context("/repo/A/A.csproj", "net9.0"),
            Context("/repo/A/A.csproj", "net10.0"),
        ]);

        Assert.Equal(
            ["/repo/A/A.csproj net10.0", "/repo/A/A.csproj net9.0", "/repo/Z/Z.csproj net9.0"],
            ordered.Select(c => $"{c.File} {c.Tfm}"));
    }

    /// <summary>A framework the id did not carry sorts first and renders as <c>?</c>.</summary>
    [Fact]
    public void A_context_with_no_framework_is_named_rather_than_dropped()
    {
        var ordered = Contexts.Order(
            [Context("/repo/A/A.csproj", "net10.0"), Context("/repo/A/A.csproj", null)]);

        Assert.Equal(["?", "net10.0"], ordered.Select(c => c.Name));
    }

    [Fact]
    public void Without_tfm_every_context_is_asked()
    {
        var ordered = Contexts.Order(
            [Context("/repo/A/A.csproj", "net10.0"), Context("/repo/A/A.csproj", "net9.0")]);

        Assert.Equal(ordered, Contexts.Select(ordered, null, "A/File.cs"));
    }

    /// <summary>
    /// Case-insensitively: the framework is MSBuild's own spelling and a caller typing
    /// <c>NET9.0</c> has asked an unambiguous question.
    /// </summary>
    [Theory]
    [InlineData("net9.0")]
    [InlineData("NET9.0")]
    public void Tfm_keeps_only_the_matching_context(string asked)
    {
        var ordered = Contexts.Order(
            [Context("/repo/A/A.csproj", "net10.0"), Context("/repo/A/A.csproj", "net9.0")]);

        var only = Assert.Single(Contexts.Select(ordered, asked, "A/File.cs"));
        Assert.Equal("net9.0", only.Tfm);
    }

    /// <summary>
    /// One framework, two projects, is a linked file — both contexts are kept, because they
    /// are two different answers and neither is the one the caller meant to exclude.
    /// </summary>
    [Fact]
    public void Tfm_keeps_one_framework_in_every_project_that_compiles_the_file()
    {
        var ordered = Contexts.Order(
            [Context("/repo/P/P.csproj", "net10.0"), Context("/repo/Q/Q.csproj", "net10.0")]);

        Assert.Equal(2, Contexts.Select(ordered, "net10.0", "Shared/Common.cs").Count);
    }

    /// <summary>
    /// A framework the document has no context for is an error naming what it does have:
    /// answering from an unfiltered context is exactly the bug <c>--tfm</c> exists to fix.
    /// </summary>
    [Fact]
    public void An_unmatched_tfm_names_the_contexts_the_document_has()
    {
        var ordered = Contexts.Order(
            [Context("/repo/A/A.csproj", "net10.0"), Context("/repo/A/A.csproj", "net9.0")]);

        var ex = Assert.Throws<CslqException>(() => Contexts.Select(ordered, "net8.0", "A/File.cs"));
        Assert.Equal("no context for --tfm net8.0: A/File.cs has net10.0, net9.0", ex.Message);
    }

    [Fact]
    public void An_unmatched_tfm_on_a_file_no_project_compiles_says_so()
    {
        var ex = Assert.Throws<CslqException>(() => Contexts.Select([], "net8.0", "Ambient/Stray.cs"));
        Assert.Equal("no context for --tfm net8.0: Ambient/Stray.cs has no project context", ex.Message);
    }

    /// <summary>
    /// Names are the framework alone while one project compiles the file, and carry the
    /// project once more than one does: a linked file's two <c>net10.0</c> contexts are two
    /// different answers, and printing the framework twice would say otherwise.
    /// </summary>
    [Fact]
    public void Names_carry_the_project_only_when_the_contexts_span_more_than_one()
    {
        var oneProject = Contexts.Order(
            [Context("/repo/A/A.csproj", "net10.0"), Context("/repo/A/A.csproj", "net9.0")]);
        Assert.Equal("net10.0, net9.0", Contexts.Names(oneProject));

        var twoProjects = Contexts.Order(
            [Context("/repo/P/P.csproj", "net10.0"), Context("/repo/Q/Q.csproj", "net10.0")]);
        Assert.Equal("P (net10.0), Q (net10.0)", Contexts.Names(twoProjects));
    }

    /// <summary>
    /// The wire shape of a two-context answer, and the one field that travels back: the
    /// <c>_vs_id</c> verbatim. <c>_vs_defaultIndex</c> is deserialised and deliberately
    /// unused — pinned here so a reader can see it is read and discarded on purpose.
    /// </summary>
    [Fact]
    public void A_two_context_response_deserialises_with_both_ids()
    {
        var wire =
            "{\"_vs_projectContexts\":["
            + "{\"_vs_id\":\"16c1|C:\\\\repo\\\\Multi\\\\Multi.csproj ($net10.0)\","
            + "\"_vs_label\":\"Multi (net10.0)\"},"
            + "{\"_vs_id\":\"4d7b|C:\\\\repo\\\\Multi\\\\Multi.csproj ($net9.0)\","
            + "\"_vs_label\":\"Multi (net9.0)\"}],"
            + "\"_vs_defaultIndex\":0}";

        var list = JsonSerializer.Deserialize<ProjectContextList>(wire, Lsp.Options);

        Assert.NotNull(list);
        Assert.Equal(0, list.DefaultIndex);
        Assert.Equal(2, list.Contexts!.Length);
        Assert.Equal("Multi (net10.0)", list.Contexts[0].Label);

        var ordered = Contexts.Read(list.Contexts);
        Assert.Equal(["net10.0", "net9.0"], ordered.Select(c => c.Name));
        Assert.Equal("16c1|C:\\repo\\Multi\\Multi.csproj ($net10.0)", ordered[0].Wire.Id);
        Assert.Equal("Multi (net10.0)", ordered[0].Wire.Label);
    }

    /// <summary>
    /// The context a request carries is absent from the payload unless one was chosen, so a
    /// single-context document's request is byte-for-byte the one the server answered before
    /// any of this existed.
    /// </summary>
    [Fact]
    public void An_identifier_with_no_context_serialises_without_the_field()
    {
        var plain = JsonSerializer.Serialize(new TextDocumentIdentifier("file:///x.cs"), Lsp.Options);
        Assert.DoesNotContain("_vs_projectContext", plain);

        var forced = JsonSerializer.Serialize(
            new TextDocumentIdentifier("file:///x.cs")
            {
                ProjectContext = Context("/repo/A/A.csproj", "net9.0").Wire,
            },
            Lsp.Options);
        Assert.Contains("\"_vs_projectContext\"", forced);
        Assert.Contains("\"_vs_id\"", forced);
    }
}
