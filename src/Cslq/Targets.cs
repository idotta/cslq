namespace Cslq;

/// <summary>
/// The pure half of resolving a dotted symbol target. A candidate's <em>chain</em> is the
/// declaration path Roslyn's own syntax tree gives — <c>["Fixture","Core","Greeter","Greet"]</c>
/// — read off <c>textDocument/documentSymbol</c> for the declaring document. Everything here
/// is a predicate over that chain; the request that produces the tree lives in
/// <c>Program</c>.
/// </summary>
internal static class Targets
{
    // The kinds live in Kinds, which is what sym and outline render through: a second copy
    // here was how a constructor could be kind 9 to this file and "method" to the renderer.
    private const int Method = Kinds.Method;

    /// <summary>
    /// A candidate reduced to what the two decisions need: the kind
    /// <c>workspace/symbol</c> reported, and the chain of its declaration.
    /// </summary>
    internal sealed record Candidate(int Kind, IReadOnlyList<string> Chain);

    /// <summary>
    /// The declaration path containing <paramref name="position"/>, top-down, the leaf
    /// included. A namespace node's name is already dotted (<c>Fixture.Core</c>) and is split;
    /// every other name is stripped of the suffix <c>documentSymbol</c> renders into it —
    /// <c>Greet(string) : string</c> and <c>Box&lt;T&gt;</c> both reduce to their identifier.
    /// Containment is on the full <c>range</c> rather than <c>selectionRange</c>: only the
    /// former spans a declaration's body, which is what makes an ancestor an ancestor.
    /// </summary>
    internal static List<string> Chain(IReadOnlyList<DocumentSymbol> symbols, Position position)
    {
        var chain = new List<string>();
        var level = symbols;

        while (level.FirstOrDefault(s => Contains(s.Range, position)) is { } node)
        {
            chain.AddRange(Segments(node.Name));
            level = node.Children ?? [];
        }

        return chain;
    }

    /// <summary>
    /// A dotted target matches when its segments are a contiguous suffix of the chain, so
    /// <c>Fixture.Core.Greeter.Greet</c>, <c>Core.Greeter.Greet</c> and <c>Greeter.Greet</c>
    /// all select <c>Greeter.Greet</c> while <c>Wrong.Namespace.Greeter.Greet</c> and
    /// <c>Fixture.Greeter.Greet</c> select nothing. Contiguity is the whole point: a target
    /// that only had to appear in order would keep matching <c>Other.Inner.Depth</c> for
    /// <c>Outer.Inner.Depth</c>, one segment of which is a lie.
    /// </summary>
    internal static bool ChainMatches(IReadOnlyList<string> chain, string target)
    {
        var segments = target.Split('.');
        if (segments.Length > chain.Count) return false;

        for (var i = 0; i < segments.Length; i++)
        {
            if (!string.Equals(chain[chain.Count - segments.Length + i], segments[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A constructor is a method whose chain repeats its own name, which is what the enclosing
    /// type being the declaration's parent means. The chain answers this and
    /// <c>containerName</c> does not: that string is localised display text.
    /// </summary>
    internal static bool IsConstructor(Candidate candidate) =>
        candidate.Kind == Method
        && candidate.Chain.Count >= 2
        && string.Equals(candidate.Chain[^1], candidate.Chain[^2], StringComparison.Ordinal);

    /// <summary>
    /// Decision A: the index of the type a bare name selects over its own constructors, or -1
    /// when the set is genuinely ambiguous. It collapses only the exact shape that made a type
    /// with a constructor unreachable by name — one type-kind candidate plus constructors of
    /// that same declaration, matched by chain rather than by document, since two types of one
    /// name in one file are still two answers. Anything else stays ambiguous: two types, or a
    /// same-named method that is not a constructor, are real alternatives.
    /// </summary>
    internal static int TypeOverConstructors(IReadOnlyList<Candidate> candidates)
    {
        if (candidates.Count < 2) return -1;

        var type = -1;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (!IsType(candidates[i].Kind)) continue;
            if (type >= 0) return -1;
            type = i;
        }

        if (type < 0) return -1;

        for (var i = 0; i < candidates.Count; i++)
        {
            if (i == type) continue;
            if (!IsConstructor(candidates[i])) return -1;
            if (!Declares(candidates[type].Chain, candidates[i].Chain)) return -1;
        }

        return type;
    }

    internal static bool IsType(int kind) =>
        kind is Kinds.Class or Kinds.Interface or Kinds.Struct or Kinds.Enum;

    // The constructor's chain minus its last element is the type's own chain.
    private static bool Declares(IReadOnlyList<string> type, IReadOnlyList<string> constructor) =>
        constructor.Count == type.Count + 1
        && type.SequenceEqual(constructor.Take(type.Count), StringComparer.Ordinal);

    private static IEnumerable<string> Segments(string name) => Kinds.Bare(name).Split('.');

    private static bool Contains(Range range, Position position) =>
        Before(range.Start, position) && Before(position, range.End);

    private static bool Before(Position a, Position b) =>
        a.Line < b.Line || (a.Line == b.Line && a.Character <= b.Character);
}
