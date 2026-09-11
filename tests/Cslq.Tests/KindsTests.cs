namespace Cslq.Tests;

/// <summary>
/// The one kind table both <c>sym</c> and <c>outline</c> render through. They disagreed:
/// measured on <c>fixture/Core/Kinds.cs</c> on 2026-09-10, <c>workspace/symbol</c> answers a
/// delegate <c>function</c> and a local function <c>method</c>, while
/// <c>textDocument/documentSymbol</c> answers both <c>method</c> and carries nothing else to
/// separate them. So <c>function</c> folds into <c>method</c>, and a constructor — which LSP
/// can express and Roslyn never sends — is recovered from the declaring type's name.
/// </summary>
public class KindsTests
{
    [Fact]
    public void A_delegate_reads_the_same_from_either_request()
    {
        Assert.Equal(
            Kinds.Name(Kinds.Normalise(Kinds.Function)),
            Kinds.Name(Kinds.Normalise(Kinds.Method)));
        Assert.Equal("method", Kinds.Name(Kinds.Normalise(Kinds.Function)));
    }

    [Fact]
    public void A_method_named_after_its_declaring_type_is_a_constructor()
    {
        Assert.Equal(Kinds.Constructor, Kinds.Of(Kinds.Method, "Widget(int)", "Widget"));
        Assert.Equal("constructor", Kinds.Name(Kinds.Of(Kinds.Method, "Widget()", "Widget")));
    }

    /// <summary>
    /// The signature <c>documentSymbol</c> renders into a name is not part of the identifier,
    /// on either side of the comparison: a generic type's parameters are stripped too.
    /// </summary>
    [Fact]
    public void The_comparison_is_of_identifiers_not_of_rendered_names()
    {
        Assert.Equal(Kinds.Constructor, Kinds.Of(Kinds.Method, "Box(T) : void", "Box<T>"));
        Assert.Equal("Greet", Kinds.Bare("Greet(string) : string"));
        Assert.Equal("Box", Kinds.Bare("Box<T>"));
        Assert.Equal("Colour", Kinds.Bare("Colour"));
    }

    [Fact]
    public void An_ordinary_method_and_a_top_level_declaration_are_left_alone()
    {
        Assert.Equal(Kinds.Method, Kinds.Of(Kinds.Method, "Sizes() : int", "WidgetUse"));
        Assert.Equal(Kinds.Method, Kinds.Of(Kinds.Method, "Measure(IShape) : int", null));
        Assert.Equal(Kinds.Class, Kinds.Of(Kinds.Class, "Widget", "Fixture.Core"));
    }

    /// <summary>
    /// A delegate declared at namespace level is a <c>method</c> whose parent is the
    /// namespace — never a constructor, whatever the namespace is called. The check is on the
    /// declaring name and a namespace named after its only type is a real shape.
    /// </summary>
    [Fact]
    public void A_namespace_named_like_its_member_still_only_matches_on_the_name()
    {
        Assert.Equal(Kinds.Constructor, Kinds.Of(Kinds.Method, "Fixture(int)", "Fixture"));
        Assert.Equal(Kinds.Method, Kinds.Of(Kinds.Method, "Measure(IShape) : int", "Fixture.Core"));
    }

    /// <summary>
    /// An unmapped kind renders as its integer rather than "unknown": the server is free to
    /// add kinds, and dropping the one fact there is helps nobody. <c>function</c> is folded
    /// away before the table, so it renders as its number if one ever gets past — the loud
    /// version of the same rule.
    /// </summary>
    [Fact]
    public void An_unmapped_kind_renders_as_its_number()
    {
        Assert.Equal("99", Kinds.Name(99));
        Assert.Equal("12", Kinds.Name(Kinds.Function));
        Assert.Equal("enumMember", Kinds.Name(22));
    }
}
