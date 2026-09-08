using System.Text.Json;

namespace Cslq.Tests;

/// <summary>
/// <c>workspace/symbol</c> is ranked by relevance — exact, then prefix, then substring,
/// across every project — and that ranking only survives if the cap is applied before the
/// sort. Proving it needs a symbol set where relevance order and alphabetical order differ,
/// which in <c>fixture/</c> took a scratch copy carrying three purpose-built types.
/// </summary>
public class OutputTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "repo");

    private static SymbolInformation Symbol(string name, string file, int line = 1, string? container = null) =>
        new(name, 5, new Location(
            PathUri.FromPath(Path.Combine(Root, file.Replace('/', Path.DirectorySeparatorChar))),
            new Range(new Position(line - 1, 0), new Position(line - 1, 4))), container);

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
    /// The cap keeps the best matches, not an alphabetical prefix of them. Answered in
    /// relevance order Zed / ZedHelper / AbcZed, a cap of two must drop AbcZed — sorting
    /// first would have dropped Zed, the exact match.
    /// </summary>
    [Fact]
    public async Task Truncation_happens_in_relevance_order_and_display_in_alphabetical_order()
    {
        var symbols = new[]
        {
            Symbol("Zed", "App/Zed.cs"),
            Symbol("ZedHelper", "Core/ZedHelper.cs"),
            Symbol("AbcZed", "Core/AbcZed.cs"),
        };

        var lines = (await CaptureAsync(() => Output.WriteSymbolsAsync(Root, symbols, max: 2, json: false, Plain)))
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

        var text = await CaptureAsync(() => Output.WriteSymbolsAsync(Root, symbols, max: 1, json: false, Plain));

        Assert.Contains("... 2 more (use --max 3 to see all)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_answer_says_so_rather_than_printing_nothing()
    {
        var text = await CaptureAsync(() => Output.WriteSymbolsAsync(Root, [], 50, json: false, Plain));

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
            () => Output.WriteSymbolsAsync(Root, symbols, max: 1, json: true, Plain))).RootElement;

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
            Output.WriteRestored(manifest, json: false);
            return Task.CompletedTask;
        });

        Assert.Equal("restored the pinned language server in " + manifest, text.Trim());

        var json = await CaptureAsync(() =>
        {
            Output.WriteRestored(manifest, json: true);
            return Task.CompletedTask;
        });

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        Assert.False(doc.RootElement.GetProperty("truncated").GetBoolean());
        var row = doc.RootElement.GetProperty("results")[0];
        Assert.True(row.GetProperty("restored").GetBoolean());
        Assert.Equal(manifest, row.GetProperty("manifest").GetString());
    }
}
