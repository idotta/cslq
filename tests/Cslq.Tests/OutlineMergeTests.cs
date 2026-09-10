namespace Cslq.Tests;

/// <summary>
/// Unioning several contexts' views of one document. A multi-targeted file is parsed once per
/// context with different preprocessor symbols, so each view holds only what that context
/// compiles: a file whose whole body sits inside one <c>#if</c> answered <c>no symbols</c> at
/// exit 0 in the other context, which is T-29. Every view parses the same text, which is what
/// makes the merge a key comparison — of names, kinds and identifier positions, since a
/// declaration's extent is the one thing that does differ between contexts.
/// </summary>
public class OutlineMergeTests
{
    private static DocumentSymbol Symbol(
        string name, int line, int kind = 5, params DocumentSymbol[] children) =>
        new(
            name,
            name,
            kind,
            new Range(new Position(line - 1, 0), new Position(line + 2, 0)),
            new Range(new Position(line - 1, 20), new Position(line - 1, 20 + name.Length)),
            children);

    [Fact]
    public void One_view_is_its_own_outline_and_nothing_is_marked()
    {
        var merged = Outline.Merge([new OutlineView(string.Empty, [Symbol("Greeter", 3)])]);

        var only = Assert.Single(merged);
        Assert.Equal("Greeter", only.Symbol.Name);
        Assert.Null(Outline.Mark(only, views: 1));
    }

    /// <summary>
    /// The union, in source order — not arrival order. A declaration only the second context
    /// reported belongs where it sits in the file, and the second context here is deliberately
    /// the one holding the earlier declaration.
    /// </summary>
    [Fact]
    public void Declarations_from_every_view_appear_in_source_order()
    {
        var merged = Outline.Merge(
        [
            new OutlineView("net9.0", [Symbol("Only9", 11)]),
            new OutlineView("net10.0", [Symbol("Only10", 4)]),
        ]);

        Assert.Equal(["Only10", "Only9"], merged.Select(n => n.Symbol.Name));
        Assert.Equal("net10.0", Outline.Mark(merged[0], views: 2));
        Assert.Equal("net9.0", Outline.Mark(merged[1], views: 2));
    }

    /// <summary>
    /// A declaration every context compiles is unmarked, so an unconditional file renders
    /// exactly as it did before contexts existed.
    /// </summary>
    [Fact]
    public void A_declaration_in_every_view_is_not_marked()
    {
        var merged = Outline.Merge(
        [
            new OutlineView("net10.0", [Symbol("Both", 8)]),
            new OutlineView("net9.0", [Symbol("Both", 8)]),
        ]);

        var only = Assert.Single(merged);
        Assert.Equal(["net10.0", "net9.0"], only.In);
        Assert.Null(Outline.Mark(only, views: 2));
    }

    /// <summary>
    /// Children merge on their own, so a type both contexts compile can carry a member only
    /// one of them does — the <c>#if</c> inside a class body, which is the common real shape.
    /// </summary>
    [Fact]
    public void Children_merge_independently_of_their_parent()
    {
        var merged = Outline.Merge(
        [
            new OutlineView("net10.0", [Symbol("Api", 3, 5, Symbol("Common", 5, 6), Symbol("Fast", 9, 6))]),
            new OutlineView("net9.0", [Symbol("Api", 3, 5, Symbol("Common", 5, 6))]),
        ]);

        var api = Assert.Single(merged);
        Assert.Null(Outline.Mark(api, views: 2));
        Assert.Equal(["Common", "Fast"], api.Children.Select(c => c.Symbol.Name));
        Assert.Null(Outline.Mark(api.Children[0], views: 2));
        Assert.Equal("net10.0", Outline.Mark(api.Children[1], views: 2));
    }

    /// <summary>
    /// Nothing is marked when only one context was asked: with <c>--tfm</c> everything the
    /// caller sees is that context by construction, and the note says which.
    /// </summary>
    [Fact]
    public void A_single_asked_context_marks_nothing()
    {
        var merged = Outline.Merge([new OutlineView("net9.0", [Symbol("Only9", 11)])]);

        Assert.Null(Outline.Mark(Assert.Single(merged), views: 1));
    }

    /// <summary>
    /// A merged declaration keeps the <em>widest</em> extent, because a declaration's range is
    /// context-dependent even where its identifier is not: on the fixture the namespace spans
    /// lines 0-6 in <c>net10.0</c> and 0-13 in <c>net9.0</c>. Keeping the first view's range
    /// put a child outside its own parent, and <c>Targets.Chain</c> — which walks down by
    /// full-range containment — then answered an empty chain, so
    /// <c>def Fixture2.Multi.Only9</c> failed 4 of 4 rather than 2 of 6.
    /// </summary>
    [Fact]
    public void A_merged_declaration_spans_every_context_that_has_it()
    {
        var narrow = new DocumentSymbol(
            "Fixture2.Multi",
            null,
            3,
            new Range(new Position(0, 0), new Position(6, 1)),
            new Range(new Position(0, 10), new Position(0, 24)),
            [Symbol("Only10", 4)]);
        var wide = narrow with
        {
            Range = new Range(new Position(0, 0), new Position(13, 1)),
            Children = [Symbol("Only9", 11)],
        };

        var merged = Outline.Merge(
            [new OutlineView("net10.0", [narrow]), new OutlineView("net9.0", [wide])]);

        var ns = Assert.Single(merged);
        Assert.Equal(13, ns.Symbol.Range.End.Line);
        Assert.Equal(0, ns.Symbol.Range.Start.Line);
        Assert.Equal(["Only10", "Only9"], ns.Children.Select(c => c.Symbol.Name));
    }

    /// <summary>
    /// The merged tree flattens back to plain symbols for the dotted-target chain, children
    /// included: <c>def Fixture2.Multi.Only9</c> read its chain off one context's tree and
    /// answered <c>no symbol matched</c> in 2 of 6 runs before this.
    /// </summary>
    [Fact]
    public void The_merged_tree_rebuilds_as_plain_symbols()
    {
        var merged = Outline.Merge(
        [
            new OutlineView("net10.0", [Symbol("Ns", 1, 3, Symbol("Only10", 4))]),
            new OutlineView("net9.0", [Symbol("Ns", 1, 3, Symbol("Only9", 11))]),
        ]);

        var tree = Outline.Tree(merged);

        var ns = Assert.Single(tree);
        Assert.Equal(["Only10", "Only9"], ns.Children!.Select(c => c.Name));
    }
}
