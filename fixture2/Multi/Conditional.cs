namespace Fixture2.Multi;

#if NET10_0
public sealed class Only10
{
    public static int Value() => 10;
}
#endif

#if NET9_0
public sealed class Only9
{
    public static int Value() => 9;
}
#endif
