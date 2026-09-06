namespace Fixture.Core;

/// <summary>
/// A pair whose `workspace/symbol` relevance order is the reverse of their alphabetical one:
/// `sym Volume` ranks the exact match first, while sorting by name puts `AudioVolume` there.
/// `sym Area` cannot tell the two apart -- its relevance order and its path order coincide --
/// so a regression to sort-then-truncate would leave every other `sym` case green.
/// </summary>
public sealed class Volume
{
    public int Litres => 1;
}

public sealed class AudioVolume
{
    public int Decibels => 1;
}
