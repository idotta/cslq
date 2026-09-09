namespace Cslq.Tests;

/// <summary>
/// The two decisions of "Targeting a symbol by name" in <c>DESIGN.md</c>, pinned below the
/// transport. <see cref="Targets"/> is a set of predicates over a candidate's declaration
/// <em>chain</em>; the <c>textDocument/documentSymbol</c> request that produces the tree stays
/// in <c>Program</c>, so everything here is a hand-built tree and a string.
/// </summary>
public class SymbolTargetTests
{
    private const int Class = 5;
    private const int Method = 6;
    private const int Property = 7;

    private static DocumentSymbol Node(
        string name, int kind, int startLine, int endLine, params DocumentSymbol[] children) =>
        new(name, name, kind,
            new Range(new Position(startLine, 0), new Position(endLine, 80)),
            new Range(new Position(startLine, 4), new Position(startLine, 8)),
            children);

    // namespace Fixture.Core; class Greeter { string Greet(string); }
    private static DocumentSymbol[] Greeter() =>
    [
        Node("Fixture.Core", 3, 0, 20,
            Node("Greeter", Class, 2, 10,
                Node("Greet(string) : string", Method, 4, 4))),
    ];

    /// <summary>
    /// A namespace node's name is already dotted and is split; a member's carries its
    /// signature and return type, a generic's its type parameters, and neither belongs in a
    /// segment comparison.
    /// </summary>
    [Fact]
    public void A_chain_is_the_declaration_path_with_every_name_reduced_to_its_identifier()
    {
        Assert.Equal(
            ["Fixture", "Core", "Greeter", "Greet"],
            Targets.Chain(Greeter(), new Position(4, 25)));

        DocumentSymbol[] generic = [Node("Boxes", 3, 0, 9, Node("Box<T>", Class, 1, 8))];
        Assert.Equal(["Boxes", "Box"], Targets.Chain(generic, new Position(1, 6)));
    }

    /// <summary>
    /// A position inside no declaration is not an error — an empty chain simply matches no
    /// dotted target — and the leaf is identified by position, so the enclosing type's own
    /// range answers with the type rather than with one of its members.
    /// </summary>
    [Fact]
    public void A_chain_stops_at_the_deepest_declaration_containing_the_position()
    {
        Assert.Equal(["Fixture", "Core", "Greeter"], Targets.Chain(Greeter(), new Position(2, 20)));
        Assert.Empty(Targets.Chain(Greeter(), new Position(60, 0)));
    }

    [Fact]
    public void A_dotted_target_matches_a_contiguous_suffix_of_the_chain()
    {
        string[] chain = ["Fixture", "Core", "Greeter", "Greet"];

        Assert.True(Targets.ChainMatches(chain, "Greet"));
        Assert.True(Targets.ChainMatches(chain, "Greeter.Greet"));
        Assert.True(Targets.ChainMatches(chain, "Core.Greeter.Greet"));
        Assert.True(Targets.ChainMatches(chain, "Fixture.Core.Greeter.Greet"));
    }

    /// <summary>
    /// The three failures the old <c>containerName</c> token test could not tell apart: a
    /// namespace that is wrong, one whose segments are right but not contiguous, and a target
    /// longer than the declaration path it names.
    /// </summary>
    [Fact]
    public void A_dotted_target_whose_segments_are_not_contiguous_matches_nothing()
    {
        string[] chain = ["Fixture", "Core", "Greeter", "Greet"];

        Assert.False(Targets.ChainMatches(chain, "Wrong.Namespace.Greeter.Greet"));
        Assert.False(Targets.ChainMatches(chain, "Fixture.Greeter.Greet"));
        Assert.False(Targets.ChainMatches(chain, "Extra.Fixture.Core.Greeter.Greet"));
        Assert.False(Targets.ChainMatches(chain, "Greeter.Farewell"));
    }

    /// <summary>
    /// Two nested types of one name under different parents. The segment that separates
    /// them is in neither <c>containerName</c> nor a hover, which is why the chain is read off
    /// the syntax tree.
    /// </summary>
    [Fact]
    public void A_shadowed_nested_member_is_separated_by_its_own_parent()
    {
        string[] outer = ["Fixture", "Core", "Outer", "Inner", "Depth"];
        string[] other = ["Fixture", "Core", "Other", "Inner", "Depth"];

        Assert.True(Targets.ChainMatches(outer, "Outer.Inner.Depth"));
        Assert.False(Targets.ChainMatches(other, "Outer.Inner.Depth"));
        Assert.True(Targets.ChainMatches(outer, "Inner.Depth"));
        Assert.True(Targets.ChainMatches(other, "Inner.Depth"));
    }

    private static Targets.Candidate Type(params string[] chain) => new(Class, chain);

    private static Targets.Candidate Constructor(params string[] chain) => new(Method, chain);

    /// <summary>
    /// A type with an explicit constructor: <c>workspace/symbol</c> reports the constructor as
    /// a same-named method, so every such type was ambiguous with itself. Two overloads rather
    /// than one, because the collapse is a predicate over the whole set.
    /// </summary>
    [Fact]
    public void A_bare_name_selects_the_type_over_its_own_constructors()
    {
        var index = Targets.TypeOverConstructors([
            Constructor("Fixture", "Core", "Widget", "Widget"),
            Type("Fixture", "Core", "Widget"),
            Constructor("Fixture", "Core", "Widget", "Widget"),
        ]);

        Assert.Equal(1, index);
    }

    /// <summary>
    /// A method that merely shares the type's name is a real alternative, and the chain is
    /// what tells it from a constructor: a constructor's chain repeats its own name.
    /// </summary>
    [Fact]
    public void A_same_named_method_that_is_not_a_constructor_stays_ambiguous()
    {
        Assert.Equal(-1, Targets.TypeOverConstructors([
            Type("Fixture", "Core", "Widget"),
            Constructor("Fixture", "Core", "Widget", "Widget"),
            Constructor("Fixture", "Core", "Helpers", "Widget"),
        ]));
    }

    /// <summary>
    /// The collapse is scoped to constructors of <em>that</em> declaration. A constructor of a
    /// same-named type elsewhere is another type's, and two types of one name are two answers
    /// however few constructors they have.
    /// </summary>
    [Fact]
    public void Neither_two_types_nor_another_types_constructor_collapses()
    {
        Assert.Equal(-1, Targets.TypeOverConstructors([
            Type("Fixture", "Core", "Widget"),
            Type("Fixture", "App", "Widget"),
        ]));

        Assert.Equal(-1, Targets.TypeOverConstructors([
            Type("Fixture", "Core", "Widget"),
            Constructor("Fixture", "App", "Widget", "Widget"),
        ]));

        Assert.Equal(-1, Targets.TypeOverConstructors([
            Type("Fixture", "Core", "Widget"),
        ]));
    }

    /// <summary>
    /// A property or a field of the type's own name cannot exist in C#, but the predicate does
    /// not rely on that: anything that is not a method with a repeating chain blocks the
    /// collapse.
    /// </summary>
    [Fact]
    public void A_candidate_that_is_neither_the_type_nor_a_constructor_blocks_the_collapse()
    {
        Assert.Equal(-1, Targets.TypeOverConstructors([
            Type("Fixture", "Core", "Widget"),
            new Targets.Candidate(Property, ["Fixture", "Core", "Widget", "Widget"]),
        ]));
    }
}
