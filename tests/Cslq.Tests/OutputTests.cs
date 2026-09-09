using System.Text.Json;

namespace Cslq.Tests;

/// <summary>
/// <c>sym</c> ranks its answer against the query itself — exact, then prefix, then substring,
/// source before generated — and cuts <c>--max</c> out of that, because the server's own
/// ordering was measured absent on three real corpora. Proving it needs a symbol set where
/// relevance order and alphabetical order differ, and an arrival order that is neither.
/// </summary>
public class OutputTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "repo");

    private static SymbolInformation Symbol(
        string name, string file, int line = 1, string? container = null, int column = 1, int kind = 5) =>
        new(name, kind, new Location(
            PathUri.FromPath(Path.Combine(Root, file.Replace('/', Path.DirectorySeparatorChar))),
            new Range(new Position(line - 1, column - 1), new Position(line - 1, column + 3))), container);

    private static SymbolRow Row(SymbolInformation symbol) => new(symbol, symbol.Kind);

    /// <summary>
    /// No label lookups: these cases render file URIs, which carry their own path. The
    /// generated and metadata labels are <see cref="PathUriTests"/>'s, where no capture is
    /// needed. <c>Lines</c> answers for whatever is asked, so a renderer that reaches for a
    /// context line gets one rather than an exception.
    /// </summary>
    private static readonly Documents Plain = new(
        _ => Task.FromResult<string[]>(["line one", "line two"]),
        _ => Task.FromResult<string?>(null),
        _ => Task.FromResult<string?>(null));

    private static async Task<string> CaptureAsync(Func<Task> action)
    {
        var original = Console.Out;
        var buffer = new StringWriter();
        Console.SetOut(buffer);
        try
        {
            await action();
        }
        finally
        {
            Console.SetOut(original);
        }

        return buffer.ToString();
    }

    /// <summary>
    /// The cap keeps the best matches, not an alphabetical prefix of them and not an arrival
    /// prefix either: answered AbcZed / ZedHelper / Zed, a cap of two keeps the exact match
    /// and the prefix match, and displays them alphabetically.
    /// </summary>
    [Fact]
    public async Task Truncation_happens_in_relevance_order_and_display_in_alphabetical_order()
    {
        var symbols = new[]
        {
            Symbol("AbcZed", "Core/AbcZed.cs"),
            Symbol("ZedHelper", "Core/ZedHelper.cs"),
            Symbol("Zed", "App/Zed.cs"),
        };

        var lines = (await CaptureAsync(() => Output.WriteSymbolsAsync(Root, "Zed", symbols, max: 2, json: false, Plain)))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("Zed ", lines[0], StringComparison.Ordinal);
        Assert.Contains("App/Zed.cs:1:1", lines[0], StringComparison.Ordinal);
        Assert.Contains("ZedHelper", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain(lines, l => l.Contains("AbcZed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Truncation_says_how_many_were_dropped_and_how_to_see_them()
    {
        var symbols = new[] { Symbol("A", "Core/A.cs"), Symbol("B", "Core/B.cs"), Symbol("C", "Core/C.cs") };

        var text = await CaptureAsync(() => Output.WriteSymbolsAsync(Root, "A", symbols, max: 1, json: false, Plain));

        Assert.Contains("... 2 more (use --max 3 to see all)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_answer_says_so_rather_than_printing_nothing()
    {
        var text = await CaptureAsync(() => Output.WriteSymbolsAsync(Root, "A", [], 50, json: false, Plain));

        Assert.Equal("no results", text.Trim());
    }

    /// <summary>
    /// Positions are one-based on the way out and zero-based on the wire, in both renderings.
    /// </summary>
    [Fact]
    public async Task Json_reports_one_based_positions_and_whether_it_truncated()
    {
        var symbols = new[] { Symbol("A", "Core/A.cs", line: 9), Symbol("B", "Core/B.cs") };

        var json = JsonDocument.Parse(await CaptureAsync(
            () => Output.WriteSymbolsAsync(Root, "A", symbols, max: 1, json: true, Plain))).RootElement;

        Assert.Equal(2, json.GetProperty("count").GetInt32());
        Assert.True(json.GetProperty("truncated").GetBoolean());
        var only = Assert.Single(json.GetProperty("results").EnumerateArray().ToList());
        Assert.Equal("A", only.GetProperty("name").GetString());
        Assert.Equal("class", only.GetProperty("kind").GetString());
        Assert.Equal("Core/A.cs", only.GetProperty("path").GetString());
        Assert.Equal(9, only.GetProperty("line").GetInt32());
        Assert.Equal(1, only.GetProperty("column").GetInt32());
        // Both label flags, so an agent never has to parse the <generated>/ or <metadata>/
        // prefix off `path` to know what kind of document it is looking at.
        Assert.False(only.GetProperty("generated").GetBoolean());
        Assert.False(only.GetProperty("metadata").GetBoolean());
    }

    /// <summary>
    /// The cut is a relevance cut, not an arrival cut. Roslyn was measured answering a
    /// substring hit ahead of the exact match on three real corpora, so a cap of one taken in
    /// arrival order showed <c>BomUser</c> for the query <c>Use</c> and reported the exact
    /// match only as one of the hits it dropped.
    /// </summary>
    [Fact]
    public async Task An_exact_match_survives_the_cap_over_a_substring_hit_that_arrived_first()
    {
        var symbols = new[] { Symbol("BomUser", "Core/BomUser.cs"), Symbol("Use", "App/Use.cs") };

        var json = JsonDocument.Parse(await CaptureAsync(
            () => Output.WriteSymbolsAsync(Root, "Use", symbols, max: 1, json: true, Plain))).RootElement;

        Assert.True(json.GetProperty("truncated").GetBoolean());
        var only = Assert.Single(json.GetProperty("results").EnumerateArray().ToList());
        Assert.Equal("Use", only.GetProperty("name").GetString());
        Assert.Equal("App/Use.cs", only.GetProperty("path").GetString());
    }

    /// <summary>
    /// Two hits equally exact are separated by where they live: the corpora answered the
    /// generated copies of a name first, so a cap taken in arrival order kept the copy and
    /// dropped the declaration the caller came for.
    /// </summary>
    [Fact]
    public async Task A_source_hit_survives_the_cap_over_an_equally_exact_generated_one()
    {
        var symbols = new[]
        {
            new SymbolInformation(
                "Stamp", 5, Loc(GeneratedUri("8d1e6a04-06c5-4f6d-9f1d-8b0e2a7c1234", "Stamp.g.cs"), 3), null),
            Symbol("Stamp", "Core/Stamp.cs"),
        };

        var json = JsonDocument.Parse(await CaptureAsync(
            () => Output.WriteSymbolsAsync(Root, "Stamp", symbols, max: 1, json: true, Plain))).RootElement;

        var only = Assert.Single(json.GetProperty("results").EnumerateArray().ToList());
        Assert.Equal("Core/Stamp.cs", only.GetProperty("path").GetString());
        Assert.False(only.GetProperty("generated").GetBoolean());
    }

    /// <summary>
    /// A candidate listing is <c>sym</c>'s rows: the same order — exact name first, then
    /// source before generated, then label, line and column — and a <c>path:line:col</c> a
    /// caller can paste straight back as a target, which the old listing's <c>path:line</c>
    /// could not be.
    /// </summary>
    [Fact]
    public async Task A_candidate_listing_orders_by_relevance_then_rank_then_position()
    {
        var candidates = new[]
        {
            Row(Symbol("AbcZed", "App/AbcZed.cs")),
            Row(new SymbolInformation(
                "Zed", 5, Loc(GeneratedUri("8d1e6a04-06c5-4f6d-9f1d-8b0e2a7c1234", "Zed.g.cs"), 3), null)),
            Row(Symbol("Zed", "Zzz/Zed.cs", line: 4, column: 12)),
            Row(Symbol("Zed", "App/Zed.cs", line: 2, column: 7)),
        };

        var lines = (await Output.SymbolListingAsync(Root, "Zed", candidates, max: 10, Plain)).Split('\n');

        Assert.Equal(4, lines.Length);
        Assert.EndsWith("App/Zed.cs:2:7", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("Zzz/Zed.cs:4:12", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("Zed.g.cs:3:1", lines[2], StringComparison.Ordinal);
        Assert.EndsWith("App/AbcZed.cs:1:1", lines[3], StringComparison.Ordinal);
        Assert.StartsWith("  class  Zed ", lines[0], StringComparison.Ordinal);
    }

    /// <summary>The cap is the one the output rules require, footer included.</summary>
    [Fact]
    public async Task A_candidate_listing_caps_at_max_and_says_how_many_it_dropped()
    {
        var candidates = new[]
        {
            Row(Symbol("Zed", "App/Zed.cs")),
            Row(Symbol("Zed", "Core/Zed.cs")),
            Row(Symbol("Zed", "Web/Zed.cs")),
        };

        var lines = (await Output.SymbolListingAsync(Root, "Zed", candidates, max: 1, Plain)).Split('\n');

        Assert.Equal(2, lines.Length);
        Assert.EndsWith("App/Zed.cs:1:1", lines[0], StringComparison.Ordinal);
        Assert.Equal("... 2 more (use --max 3 to see all)", lines[1]);
    }

    /// <summary>
    /// <c>workspace/symbol</c> reports a constructor as a method; the caller that already read
    /// the declaration chain says otherwise, and the listing prints what it was told.
    /// </summary>
    [Fact]
    public async Task A_constructor_renders_as_one_when_the_chain_says_so()
    {
        var candidates = new[]
        {
            new SymbolRow(Symbol("Widget", "Core/Widget.cs", line: 13, column: 12, kind: 6), 9),
            new SymbolRow(Symbol("Widget", "Core/Widget.cs", line: 15, column: 12, kind: 6), 9),
        };

        var lines = (await Output.SymbolListingAsync(Root, "Widget", candidates, max: 10, Plain)).Split('\n');

        Assert.Equal("  constructor  Widget    Core/Widget.cs:13:12", lines[0]);
        Assert.EndsWith("Core/Widget.cs:15:12", lines[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// The first non-empty line is the signature and the rest is documentation. Roslyn sends
    /// CRLF regardless of platform and always a trailing newline, so both are normalised away
    /// — otherwise the text form prints a phantom blank row and the JSON carries a stray
    /// <c>\r</c>.
    /// </summary>
    [Fact]
    public void A_hover_splits_into_a_signature_and_the_documentation_after_it()
    {
        var (signature, documentation) = Output.HoverText(
            "void Console.WriteLine(string? value) (+ 19 overloads)\r\n"
            + "Writes the specified string value.\r\n\r\nExceptions:\r\n  IOException\r\n");

        Assert.Equal("void Console.WriteLine(string? value) (+ 19 overloads)", signature);
        Assert.Equal("Writes the specified string value.\n\nExceptions:\n  IOException", documentation);
    }

    /// <summary>An undocumented member is a signature and nothing else, not a blank line.</summary>
    [Fact]
    public void A_hover_with_no_documentation_is_a_signature_alone()
    {
        var (signature, documentation) = Output.HoverText("string Greeter.Greet(string name)\r\n");

        Assert.Equal("string Greeter.Greet(string name)", signature);
        Assert.Equal(string.Empty, documentation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\r\n\r\n")]
    public void A_hover_with_nothing_in_it_yields_nothing(string? value)
    {
        Assert.Equal((string.Empty, string.Empty), Output.HoverText(value));
    }

    /// <summary>
    /// The header is the hover's own range, not the position asked about: the server widens a
    /// column inside an identifier to the whole identifier, which is the better answer.
    /// </summary>
    [Fact]
    public async Task A_hover_prints_its_position_then_the_signature_and_documentation()
    {
        var text = await CaptureAsync(() => Output.WriteHoverAsync(
            Root, Uri("App/Program.cs"), new Position(8, 20), Hover("void C.W(string? v)\nWrites it."),
            50, json: false, Plain));

        Assert.Equal(
            ["App/Program.cs:9:17", "void C.W(string? v)", "Writes it."],
            text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// <c>--max</c> caps the documentation's lines — the one result is never what a cap could
    /// usefully trim — and says so the way every other truncation does.
    /// </summary>
    [Fact]
    public async Task Hover_documentation_is_capped_by_max()
    {
        var text = await CaptureAsync(() => Output.WriteHoverAsync(
            Root, Uri("App/Program.cs"), new Position(8, 16), Hover("sig\none\ntwo\nthree"),
            max: 1, json: false, documents: Plain));

        Assert.Contains("one", text, StringComparison.Ordinal);
        Assert.DoesNotContain("two", text, StringComparison.Ordinal);
        Assert.Contains("... 2 more (use --max 3 to see all)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hover_json_carries_the_envelope_and_the_split_text()
    {
        var json = JsonDocument.Parse(await CaptureAsync(() => Output.WriteHoverAsync(
            Root, Uri("App/Program.cs"), new Position(8, 16), Hover("sig\ndoc"),
            50, json: true, Plain))).RootElement;

        Assert.Equal(1, json.GetProperty("count").GetInt32());
        Assert.False(json.GetProperty("truncated").GetBoolean());
        var only = Assert.Single(json.GetProperty("results").EnumerateArray().ToList());
        Assert.Equal("App/Program.cs", only.GetProperty("path").GetString());
        Assert.Equal(9, only.GetProperty("line").GetInt32());
        Assert.Equal(17, only.GetProperty("column").GetInt32());
        Assert.Equal("sig", only.GetProperty("signature").GetString());
        Assert.Equal("doc", only.GetProperty("documentation").GetString());
    }

    /// <summary>
    /// A position that resolves to no symbol still has to answer through the envelope rather
    /// than printing an empty one.
    /// </summary>
    [Fact]
    public async Task An_absent_hover_is_an_empty_envelope_and_no_results()
    {
        var json = JsonDocument.Parse(await CaptureAsync(() => Output.WriteHoverAsync(
            Root, Uri("App/Program.cs"), new Position(11, 0), null, 50, json: true, Plain))).RootElement;

        Assert.Equal(0, json.GetProperty("count").GetInt32());
        Assert.Empty(json.GetProperty("results").EnumerateArray().ToList());

        var text = await CaptureAsync(() => Output.WriteHoverAsync(
            Root, Uri("App/Program.cs"), new Position(11, 0), null, 50, json: false, Plain));

        Assert.Equal("no results", text.Trim());
    }

    /// <summary>
    /// <c>ready</c> printed the literal <c>ready</c> under <c>--json</c> too, which broke the
    /// envelope on the one command every session runs first. Text mode still prints it, so a
    /// shell test stays a string comparison.
    /// </summary>
    [Fact]
    public async Task Ready_honours_the_envelope_under_json_and_stays_one_word_without_it()
    {
        var json = JsonDocument.Parse(
            await CaptureAsync(() => { Output.WriteReady(3, json: true); return Task.CompletedTask; })).RootElement;

        Assert.Equal(1, json.GetProperty("count").GetInt32());
        Assert.False(json.GetProperty("truncated").GetBoolean());
        var only = Assert.Single(json.GetProperty("results").EnumerateArray().ToList());
        Assert.True(only.GetProperty("ready").GetBoolean());
        Assert.Equal(3, only.GetProperty("projects").GetInt32());

        var text = await CaptureAsync(() => { Output.WriteReady(3, json: false); return Task.CompletedTask; });
        Assert.Equal("ready", text.Trim());
    }

    [Fact]
    public async Task Project_renders_the_csproj_root_relative_with_its_framework()
    {
        var csproj = Path.Combine(Root, "App", "App.csproj");
        var file = Path.Combine(Root, "App", "Program.cs");

        var text = await CaptureAsync(() =>
        {
            Output.WriteProject(Root, file, csproj, "net10.0", json: false);
            return Task.CompletedTask;
        });
        Assert.Equal("App/App.csproj  net10.0", text.Trim());

        var json = JsonDocument.Parse(await CaptureAsync(() =>
        {
            Output.WriteProject(Root, file, csproj, "net10.0", json: true);
            return Task.CompletedTask;
        })).RootElement;
        var only = Assert.Single(json.GetProperty("results").EnumerateArray().ToList());
        Assert.Equal("App/Program.cs", only.GetProperty("path").GetString());
        Assert.Equal("App/App.csproj", only.GetProperty("project").GetString());
        Assert.Equal("net10.0", only.GetProperty("tfm").GetString());
        Assert.False(only.GetProperty("generated").GetBoolean());
        Assert.False(only.GetProperty("metadata").GetBoolean());
    }

    /// <summary>
    /// A file no project compiles is answered, not errored: it is the reason <c>sym</c> cannot
    /// see the types in it and <c>diag</c> reports nothing for it.
    /// </summary>
    [Fact]
    public async Task A_file_no_project_compiles_says_no_project()
    {
        var text = await CaptureAsync(() =>
        {
            Output.WriteProject(Root, Path.Combine(Root, "Ambient", "Stray.cs"), null, null, json: false);
            return Task.CompletedTask;
        });

        Assert.Equal("no project", text.Trim());
    }

    /// <summary>
    /// A source-generated URI for <paramref name="hint"/>, carrying the fields that are
    /// regenerated on every workspace load. Two of these differing only in
    /// <paramref name="authority"/> are what a multi-targeted project answers with: different
    /// URIs, one label. See <see cref="PathUriTests"/> for the shape.
    /// </summary>
    private static string GeneratedUri(
        string authority,
        string hint = "BuildInfo.g.cs",
        string generator = "Fixture.Gen.BuildInfoGenerator") =>
        $"roslyn-source-generated://{authority}/{hint}"
        + $"?documentId={authority}&assemblyName=Fixture.App&assemblyVersion=1.0.0.0"
        + $"&typeName={generator}&hintName={hint}";

    private static Location Loc(string uri, int line, int column = 1) =>
        new(uri, new Range(new Position(line - 1, column - 1), new Position(line - 1, column + 4)));

    /// <summary>
    /// Roslyn answers the declaration of a type with a primary constructor twice — once for
    /// the type, once for the constructor — at byte-identical positions, and the count used to
    /// include the twin. Rows are folded on what they render as plus the range.
    /// </summary>
    [Fact]
    public async Task Two_locations_that_render_identically_are_one_row()
    {
        var twins = new[] { Loc(Uri("App/Square.cs"), 2, 22), Loc(Uri("App/Square.cs"), 2, 22) };

        var json = JsonDocument.Parse(await CaptureAsync(
            () => Output.WriteLocationsAsync(Root, twins, 50, 0, json: true, Plain))).RootElement;

        Assert.Equal(1, json.GetProperty("count").GetInt32());
        Assert.False(json.GetProperty("truncated").GetBoolean());
        Assert.Single(json.GetProperty("results").EnumerateArray().ToList());

        var text = await CaptureAsync(
            () => Output.WriteLocationsAsync(Root, twins, 50, 0, json: false, Plain));

        Assert.Equal(
            ["App/Square.cs:2:22", "> 2 | line two"],
            text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// A multi-targeted project answers one generated document once per framework, under URIs
    /// whose authority guid and documentId differ while the label does not. The fold is on the
    /// label, so those collapse; folding on the URI would not have touched them.
    /// </summary>
    [Fact]
    public async Task Generated_twins_that_differ_only_in_the_volatile_uri_fields_fold()
    {
        var twins = new[]
        {
            Loc(GeneratedUri("8d1e6a04-06c5-4f6d-9f1d-8b0e2a7c1234"), 5),
            Loc(GeneratedUri("1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d"), 5),
        };

        var json = JsonDocument.Parse(await CaptureAsync(
            () => Output.WriteLocationsAsync(Root, twins, 50, 0, json: true, Plain))).RootElement;

        Assert.Equal(1, json.GetProperty("count").GetInt32());
        var only = Assert.Single(json.GetProperty("results").EnumerateArray().ToList());
        Assert.Equal(
            "<generated>/Fixture.App/Fixture.Gen.BuildInfoGenerator/BuildInfo.g.cs",
            only.GetProperty("path").GetString());
        Assert.True(only.GetProperty("generated").GetBoolean());
    }

    /// <summary>
    /// Enough generated hits will fill the cap on their own, and the source hits are what the
    /// caller came for, so the cap applies to source rows first.
    /// </summary>
    [Fact]
    public async Task Source_rows_come_before_generated_ones_so_the_cap_drops_generated_first()
    {
        var mixed = new[]
        {
            Loc(GeneratedUri("8d1e6a04-06c5-4f6d-9f1d-8b0e2a7c1234"), 5),
            Loc(Uri("App/Program.cs"), 10),
        };

        var json = JsonDocument.Parse(await CaptureAsync(
            () => Output.WriteLocationsAsync(Root, mixed, max: 1, 0, json: true, Plain))).RootElement;

        Assert.Equal(2, json.GetProperty("count").GetInt32());
        Assert.True(json.GetProperty("truncated").GetBoolean());
        var only = Assert.Single(json.GetProperty("results").EnumerateArray().ToList());
        Assert.Equal("App/Program.cs", only.GetProperty("path").GetString());
        Assert.False(only.GetProperty("generated").GetBoolean());
    }

    private static string Uri(string file) => PathUri.FromPath(
        Path.Combine(Root, file.Replace('/', Path.DirectorySeparatorChar)));

    private static Hover Hover(string value) =>
        new(new MarkupContent("plaintext", value), new Range(new Position(8, 16), new Position(8, 25)));

    [Theory]
    [InlineData(1, "error")]
    [InlineData(2, "warning")]
    public void Severities_render_by_name(int severity, string expected)
    {
        Assert.Equal(expected, Output.Severity(severity));
    }

    /// <summary>
    /// <c>restore</c> reaches no workspace, so its one line names the absolute manifest
    /// directory it restored to rather than anything root-relative, and its JSON goes through
    /// the same <c>{ count, truncated, results }</c> envelope as every other command.
    /// </summary>
    [Fact]
    public async Task Restore_reports_where_it_restored_to()
    {
        var manifest = Path.Combine(Path.GetTempPath(), "tools", "net10.0", "any");

        var text = await CaptureAsync(() =>
        {
            Output.WriteRestored(manifest, pruned: null, json: false);
            return Task.CompletedTask;
        });

        Assert.Equal("restored the pinned language server in " + manifest, text.Trim());

        var json = await CaptureAsync(() =>
        {
            Output.WriteRestored(manifest, pruned: null, json: true);
            return Task.CompletedTask;
        });

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        Assert.False(doc.RootElement.GetProperty("truncated").GetBoolean());
        var row = doc.RootElement.GetProperty("results")[0];
        Assert.True(row.GetProperty("restored").GetBoolean());
        Assert.Equal(manifest, row.GetProperty("manifest").GetString());
        Assert.Equal(0, row.GetProperty("removed").GetArrayLength());
    }

    /// <summary>
    /// The prune after a restore is the only thing that ever deletes from the shared packages
    /// folder, so what it removed and what it could not are both named, in text and in JSON.
    /// </summary>
    [Fact]
    public async Task Restore_reports_what_the_prune_removed_and_what_it_could_not()
    {
        var pruned = new Prune.Result(
            "/home/u/.nuget/packages",
            ["roslyn-language-server.linux-x64/5.11.0-2.26311.5", "roslyn-language-server/5.11.0-2.26311.5"],
            [("roslyn-language-server.linux-x64/5.10.0-1.26201.4", "in use.")]);

        var lines = Output.PruneLines(pruned).ToArray();

        Assert.Equal(2, lines.Length);
        Assert.Equal(
            "removed 2 other version(s) of the language server from /home/u/.nuget/packages: "
            + "roslyn-language-server.linux-x64/5.11.0-2.26311.5, roslyn-language-server/5.11.0-2.26311.5",
            lines[0]);
        Assert.StartsWith(
            "could not remove roslyn-language-server.linux-x64/5.10.0-1.26201.4 from /home/u/.nuget/packages: in use.",
            lines[1]);

        var json = await CaptureAsync(() =>
        {
            Output.WriteRestored("/m", pruned, json: true);
            return Task.CompletedTask;
        });

        using var doc = JsonDocument.Parse(json);
        var row = doc.RootElement.GetProperty("results")[0];
        Assert.Equal("/home/u/.nuget/packages", row.GetProperty("packages").GetString());
        Assert.Equal(2, row.GetProperty("removed").GetArrayLength());
        Assert.Equal("in use.", row.GetProperty("kept")[0].GetProperty("why").GetString());
    }
}
