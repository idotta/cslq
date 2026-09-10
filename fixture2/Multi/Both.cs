namespace Fixture2.Multi;

/// <summary>
/// Compiled in both contexts, and the readiness sentinel for this project: the candidate
/// scan is most-shallow-file-first and takes three names, so an always-compiled type has to
/// be among them or the conditional ones would have to resolve for `ready` to return.
/// </summary>
public sealed class Both
{
    public static string Name() => "both";
}
