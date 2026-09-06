namespace Fixture2.Beta;

/// <summary>
/// What the generator keys on. Alpha and Beta do not reference each other, so both
/// compilations can hold Fixture2.Generated.Stamp without colliding.
/// </summary>
public sealed class Anchor
{
    public static string Use() => Fixture2.Generated.Stamp.Value();
}
