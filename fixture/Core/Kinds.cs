namespace Fixture.Core;

// The rendering fixture: the declaration shapes whose kind or whose source line the output
// rules had to settle, kept in one file so a change to either shows up in one outline.
//
// `Colour` is the crowded line -- an enum and its three members all declare themselves on
// one line, and an outline that prints "that declaration's own source line" printed that
// line four times. `Crowded.First`/`Second` are the same shape for a field.
//
// `Measure` is the delegate whose kind the two commands disagreed about, `Point` and `Pair`
// the record type kinds LSP cannot express, and `Widget`'s constructors -- next door -- the
// ones reported as methods.
public enum Colour { Red, Green, Blue }

public delegate int Measure(IShape shape);

public sealed record Point(int X, int Y);

public readonly record struct Pair(int A, int B);

public static class Nest
{
    public delegate int Tally(int x);

    public static int Local()
    {
        int Helper(int y) => y + 1;
        return Helper(1);
    }
}

public static class Crowded
{
    public static int First, Second;
}

public static class Extensions
{
    extension(IShape shape)
    {
        public int Doubled => shape.Area() * 2;
    }
}

// The long-line fixture (named so no existing case can fuzzy-match it): `Marker` is referenced past column 400 of a line nothing would
// print whole, which is what `refs Marker` elides around in text mode and keeps entire
// under `--json`.
internal static class Stretch
{
    public static string Marker() => "wide";

    public static string Use() => "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" + Marker() + "ZZZZ";
}
