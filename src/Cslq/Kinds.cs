namespace Cslq;

/// <summary>
/// The one kind table both <c>sym</c> and <c>outline</c> render through, and the two
/// normalisations that make them agree.
/// <para>
/// They did not. Measured on <c>fixture/Core/Kinds.cs</c> against 5.12.0-1.26426.8 on
/// 2026-09-10: <c>workspace/symbol</c> answers a delegate as <see cref="Function"/> and a
/// local function as <see cref="Method"/>, while <c>textDocument/documentSymbol</c> answers
/// <em>both</em> as <see cref="Method"/> and carries nothing else to tell them apart — a
/// delegate's <c>name</c> and <c>detail</c> are a method's (<c>Inner(int) : int</c>), and a
/// delegate nested in a class is a sibling of the class's methods. So the disagreement is
/// settled toward the kind both requests can support: <see cref="Function"/> is folded into
/// <see cref="Method"/>, and a delegate reads as a method everywhere. Rendering it as
/// <c>function</c> in one command and <c>method</c> in the other was the bug; rendering it as
/// <c>delegate</c> would be an invention, since LSP has no such kind and neither request
/// reports one. The README carries the three gaps LSP cannot express — delegate, record,
/// and C# 14's <c>extension</c> block.
/// </para>
/// <para>
/// A constructor is the other way round: LSP <em>can</em> say <see cref="Constructor"/> and
/// Roslyn never does, reporting one as a <see cref="Method"/> from both requests. It is
/// recoverable rather than invented — a method whose name repeats its declaring type's is a
/// constructor and nothing else in C# is — so <see cref="Of"/> recovers it wherever the
/// parent is a type that could declare one, which is every row of an outline. <c>sym</c> has no parent in hand and
/// asks for a declaration chain instead; see <c>Targets.IsConstructor</c>.
/// </para>
/// </summary>
internal static class Kinds
{
    internal const int Class = 5;
    internal const int Method = 6;
    internal const int Constructor = 9;
    internal const int Enum = 10;
    internal const int Interface = 11;
    internal const int Function = 12;
    internal const int Struct = 23;

    /// <summary>
    /// The kind to render a declaration as, given what the server said and the name
    /// <em>and kind</em> of the declaration containing it — null at the top level, where C#
    /// has no constructors.
    /// <para>
    /// The parent's kind is load-bearing and the name alone was a bug: an outline's parent is
    /// whatever node encloses the row, and a namespace is one of them. So
    /// <c>namespace Widget { delegate void Widget(int n); }</c> — a namespace named after the
    /// only type in it, an ordinary shape — rendered its delegate as <c>constructor</c>,
    /// measured on a staged root 2026-09-10. Only a type that can <em>declare</em> a
    /// constructor counts: class, struct and record (which reports as a class), plus
    /// interface, whose static constructor is the one member that may repeat the type's name.
    /// An enum cannot, and neither can a namespace.
    /// </para>
    /// </summary>
    internal static int Of(int kind, string name, string? parent, int parentKind) =>
        Normalise(kind) == Method && parent is not null && Constructible(parentKind) &&
        string.Equals(Bare(name), Bare(parent), StringComparison.Ordinal)
            ? Constructor
            : Normalise(kind);

    /// <summary>Whether a declaration of this kind can declare a constructor at all.</summary>
    internal static bool Constructible(int kind) =>
        kind is Class or Struct or Interface;

    /// <summary>
    /// The kind as the two requests would have to agree on it, before anything a container
    /// knows is applied. Only <see cref="Function"/> moves; see the type remarks.
    /// </summary>
    internal static int Normalise(int kind) => kind == Function ? Method : kind;

    /// <summary>
    /// The LSP <c>SymbolKind</c> table. An unmapped value renders as its integer rather than
    /// "unknown": the server is free to add kinds, and dropping the one fact we have helps
    /// nobody. <see cref="Function"/> is absent on purpose — <see cref="Normalise"/> folds it
    /// away before anything reaches here — and it renders as its integer if one ever gets
    /// past, which is the loud version of the same rule.
    /// </summary>
    internal static string Name(int kind) => kind switch
    {
        1 => "file",
        2 => "module",
        3 => "namespace",
        4 => "package",
        Class => "class",
        Method => "method",
        7 => "property",
        8 => "field",
        Constructor => "constructor",
        Enum => "enum",
        Interface => "interface",
        13 => "variable",
        14 => "constant",
        15 => "string",
        16 => "number",
        17 => "boolean",
        18 => "array",
        19 => "object",
        20 => "key",
        21 => "null",
        22 => "enumMember",
        Struct => "struct",
        24 => "event",
        25 => "operator",
        26 => "typeParameter",
        _ => kind.ToString(),
    };

    /// <summary>
    /// The identifier alone. <c>documentSymbol</c> renders a member's signature and return
    /// type into its name (<c>Greet(string) : string</c>) and a generic's type parameters
    /// (<c>Box&lt;T&gt;</c>), none of which a target segment or a type name carries.
    /// </summary>
    internal static string Bare(string name)
    {
        var cut = name.IndexOfAny(['(', '<', ' ']);
        return cut < 0 ? name : name[..cut];
    }
}
