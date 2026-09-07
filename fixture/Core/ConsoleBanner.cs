namespace Fixture.Core;

/// <summary>
/// A type whose name merely contains `Console`, which is what arms the framework `def` leg.
/// `workspace/symbol` answers substring matches, so without the exact-name filter in
/// `LspClient.StaleBindingAsync` this declaration makes the decompiled `System.Console`
/// document look like a not-yet-loaded `ProjectReference`, and `def` at `Console.WriteLine`
/// re-asks until its whole 10 s budget is gone. Nothing else in the suite queries this name.
/// </summary>
internal static class ConsoleBanner
{
    public static string Line => "--";
}
