namespace Cslq;

/// <summary>
/// One project context's view of a document's declarations. <see cref="Name"/> is what a mark
/// renders as, so it is the context label rather than the raw TFM — a linked file compiled by
/// two projects needs the project in it.
/// </summary>
internal sealed record OutlineView(string Name, IReadOnlyList<DocumentSymbol> Symbols);

/// <summary>
/// A declaration as the contexts collectively reported it: the symbol itself, the contexts it
/// exists in, and its children merged the same way.
/// </summary>
internal sealed record OutlineNode(
    DocumentSymbol Symbol, IReadOnlyList<string> In, IReadOnlyList<OutlineNode> Children);

/// <summary>
/// Merging several contexts' <c>textDocument/documentSymbol</c> answers into one tree.
/// <para>
/// A multi-targeted document is parsed once per context with different preprocessor symbols,
/// so each answer holds only the declarations that context compiles: a file whose whole body
/// sits inside one <c>#if</c> answers <c>no symbols</c> in the other context, at exit 0, which
/// is T-29's wrong answer. The union is the only true outline of the file, and the contexts a
/// declaration is missing from are the interesting fact about it.
/// </para>
/// <para>
/// Every context parses the <em>same text</em>, so a declaration two contexts share has the
/// same name, kind and identifier position in both — which is what makes the merge a key
/// comparison rather than a diff. Its <em>extent</em> is not shared, though; see
/// <see cref="Widen"/>, which is where that cost a whole class of answers.
/// </para>
/// </summary>
internal static class Outline
{
    internal static IReadOnlyList<OutlineNode> Merge(IReadOnlyList<OutlineView> views) =>
        Level([.. views.Select(v => (v.Name, v.Symbols))]);

    /// <summary>
    /// The merged tree as plain symbols, for the one consumer that wants a syntax tree rather
    /// than a rendering: <c>Program.SelectAsync</c>, which reads a dotted target's declaration
    /// chain off it. Before this, that chain came from whichever context Roslyn picked, so
    /// <c>def Fixture2.Multi.Only9</c> failed with <c>no symbol matched</c> in 2 of 6 runs
    /// while the bare <c>def Only9</c> was already deterministic — the same bug one layer up
    /// from the positional request.
    /// </summary>
    internal static IReadOnlyList<DocumentSymbol> Tree(IReadOnlyList<OutlineNode> nodes) =>
    [
        .. nodes.Select(n => n.Symbol with { Children = [.. Tree(n.Children)] }),
    ];

    /// <summary>
    /// What to print beside a declaration: the contexts it exists in, or null when it exists
    /// in every context asked — which is every declaration in an unconditional file, and every
    /// declaration at all when only one context was asked. So an ordinary outline is
    /// byte-for-byte what it was.
    /// </summary>
    internal static string? Mark(OutlineNode node, int views) =>
        views < 2 || node.In.Count >= views ? null : string.Join(", ", node.In);

    private static List<OutlineNode> Level(
        IReadOnlyList<(string Name, IReadOnlyList<DocumentSymbol> Symbols)> level)
    {
        var order = new List<(string Name, int Kind, int Line, int Character)>();
        var seen = new Dictionary<(string Name, int Kind, int Line, int Character), Merged>();

        foreach (var (name, symbols) in level)
        {
            foreach (var symbol in symbols)
            {
                var key = (symbol.Name, symbol.Kind,
                    symbol.SelectionRange.Start.Line, symbol.SelectionRange.Start.Character);
                if (!seen.TryGetValue(key, out var entry))
                {
                    entry = new Merged { Symbol = symbol };
                    seen[key] = entry;
                    order.Add(key);
                }
                else
                {
                    entry.Symbol = Widen(entry.Symbol, symbol);
                }

                entry.In.Add(name);
                entry.Children.Add((name, symbol.Children ?? []));
            }
        }

        // Source order, not arrival order: a declaration only the second context reported
        // belongs where it sits in the file, not appended after the first context's.
        return
        [
            .. order
                .Select(k => seen[k])
                .Select(e => new OutlineNode(e.Symbol, e.In, Level(e.Children)))
                .OrderBy(n => n.Symbol.SelectionRange.Start.Line)
                .ThenBy(n => n.Symbol.SelectionRange.Start.Character),
        ];
    }

    /// <summary>
    /// The same declaration as two contexts see it, keeping the <em>widest</em> extent.
    /// <para>
    /// A declaration's <c>range</c> is not context-independent even though its identifier is:
    /// on <c>fixture2/Multi/Conditional.cs</c> the <c>Fixture2.Multi</c> namespace spans lines
    /// 0-6 in the <c>net10.0</c> context and 0-13 in <c>net9.0</c>, because each context sees
    /// only its own <c>#if</c> branch. Keeping the first view's range made
    /// <c>def Fixture2.Multi.Only9</c> fail <em>every</em> time rather than half the time:
    /// <c>Targets.Chain</c> walks down by full-range containment, so a child at line 10 sat
    /// outside its own merged parent and the chain came back empty. The union of the extents
    /// is the true one — the declaration really does cover every line either context gives it.
    /// </para>
    /// </summary>
    private static DocumentSymbol Widen(DocumentSymbol kept, DocumentSymbol other) =>
        kept with
        {
            Range = new Range(
                Earlier(kept.Range.Start, other.Range.Start) ? kept.Range.Start : other.Range.Start,
                Earlier(kept.Range.End, other.Range.End) ? other.Range.End : kept.Range.End),
        };

    private static bool Earlier(Position a, Position b) =>
        a.Line != b.Line ? a.Line < b.Line : a.Character < b.Character;

    private sealed class Merged
    {
        public required DocumentSymbol Symbol { get; set; }

        public List<string> In { get; } = [];

        public List<(string Name, IReadOnlyList<DocumentSymbol> Symbols)> Children { get; } = [];
    }
}
