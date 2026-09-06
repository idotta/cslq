using System.Text.Json;

namespace Csx.Tests;

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

    private static string Capture(Action action)
    {
        var original = Console.Out;
        var buffer = new StringWriter();
        Console.SetOut(buffer);
        try
        {
            action();
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
    public void Truncation_happens_in_relevance_order_and_display_in_alphabetical_order()
    {
        var symbols = new[]
        {
            Symbol("Zed", "App/Zed.cs"),
            Symbol("ZedHelper", "Core/ZedHelper.cs"),
            Symbol("AbcZed", "Core/AbcZed.cs"),
        };

        var lines = Capture(() => Output.WriteSymbols(Root, symbols, max: 2, json: false))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("Zed ", lines[0], StringComparison.Ordinal);
        Assert.Contains("App/Zed.cs:1:1", lines[0], StringComparison.Ordinal);
        Assert.Contains("ZedHelper", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain(lines, l => l.Contains("AbcZed", StringComparison.Ordinal));
    }

    [Fact]
    public void Truncation_says_how_many_were_dropped_and_how_to_see_them()
    {
        var symbols = new[] { Symbol("A", "Core/A.cs"), Symbol("B", "Core/B.cs"), Symbol("C", "Core/C.cs") };

        var text = Capture(() => Output.WriteSymbols(Root, symbols, max: 1, json: false));

        Assert.Contains("... 2 more (use --max 3 to see all)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_answer_says_so_rather_than_printing_nothing()
    {
        Assert.Equal("no results", Capture(() => Output.WriteSymbols(Root, [], 50, json: false)).Trim());
    }

    /// <summary>
    /// Positions are one-based on the way out and zero-based on the wire, in both renderings.
    /// </summary>
    [Fact]
    public void Json_reports_one_based_positions_and_whether_it_truncated()
    {
        var symbols = new[] { Symbol("A", "Core/A.cs", line: 9), Symbol("B", "Core/B.cs") };

        var json = JsonDocument.Parse(Capture(() => Output.WriteSymbols(Root, symbols, max: 1, json: true))).RootElement;

        Assert.Equal(2, json.GetProperty("count").GetInt32());
        Assert.True(json.GetProperty("truncated").GetBoolean());
        var only = Assert.Single(json.GetProperty("results").EnumerateArray().ToList());
        Assert.Equal("A", only.GetProperty("name").GetString());
        Assert.Equal("class", only.GetProperty("kind").GetString());
        Assert.Equal("Core/A.cs", only.GetProperty("path").GetString());
        Assert.Equal(9, only.GetProperty("line").GetInt32());
        Assert.Equal(1, only.GetProperty("column").GetInt32());
        Assert.False(only.GetProperty("generated").GetBoolean());
    }

    [Theory]
    [InlineData(1, "error")]
    [InlineData(2, "warning")]
    public void Severities_render_by_name(int severity, string expected)
    {
        Assert.Equal(expected, Output.Severity(severity));
    }
}
