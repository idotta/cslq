namespace Fixture.Core;

/// <summary>
/// The constructor half of the symbol-targeting fixture (Decision A in `DESIGN.md`).
/// `workspace/symbol` reports an explicit constructor as a `Widget` of its own, so the bare
/// name arrives as three candidates -- the type and both overloads -- and `refs Widget` was
/// unreachable before the collapse. Two overloads are what makes the collapse a set
/// predicate rather than a pair test; `Gadget` below has one, so `Gadget.Gadget` is the
/// unambiguous constructor target.
/// </summary>
public sealed class Widget
{
    public Widget() : this(1) { }

    public Widget(int size) => Size = size;

    public int Size { get; }
}

public sealed class Gadget
{
    public Gadget(int teeth) => Teeth = teeth;

    public int Teeth { get; }
}

/// <summary>
/// The shadowed-nested-member shape: `Outer.Inner.Depth` and `Other.Inner.Depth` differ only
/// in a segment no `containerName` carries, so before chains were read off the syntax tree a
/// dotted target matched both.
/// </summary>
public static class Outer
{
    public sealed class Inner
    {
        public int Depth => 1;
    }
}

public static class Other
{
    public sealed class Inner
    {
        public int Depth => 2;
    }
}

internal static class WidgetUse
{
    public static int Sizes() => new Widget().Size + new Widget(2).Size;

    public static int Teeth() => new Gadget(3).Teeth;

    public static int Depths() => new Outer.Inner().Depth + new Other.Inner().Depth;
}
