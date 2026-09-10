namespace Fixture2.Multi;

#if NET9_0
public static class TfmError
{
    // CS0029 in the net9.0 context and no code at all in the net10.0 one. Whether `diag`
    // reports it says which context it pulled from.
    public static int Broken()
    {
        int n = "not an int";
        return n;
    }
}
#endif
