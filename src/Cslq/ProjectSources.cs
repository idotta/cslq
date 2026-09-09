using System.Xml;
using System.Xml.Linq;

namespace Cslq;

/// <summary>
/// What a <c>.csproj</c>'s own text says about where its compilation items come from. Pure —
/// it never touches the filesystem — because the two shapes it exists to recognise are both
/// invisible to a source scan and both cost a full readiness timeout:
/// <list type="bullet">
/// <item>
/// A project with <c>EnableDefaultItems=false</c> and no <c>&lt;Compile Include&gt;</c>
/// compiles nothing at all. OrchardCore's <c>OrchardCore.ProjectTemplates</c> is one, and the
/// <c>content/**/*.cs</c> sitting under it is <c>dotnet new</c> template text Roslyn never
/// binds, so every candidate read out of it is unresolvable.
/// </item>
/// <item>
/// A project whose sources are linked in — a <c>*.projitems</c> import, or
/// <c>&lt;Compile Include="../Shared/**"&gt;</c> — owns no file under its own directory, so a
/// hit for one of its documents lands under the <em>source</em> directory and
/// <c>Sentinel.Accepts</c> can never accept it. Fourteen of CommunityToolkit's twenty-six
/// projects are this shape. Such a project cannot be scoped and so cannot be probed; it is
/// reported as skipped rather than silently counted as ready.
/// </item>
/// </list>
/// </summary>
internal static class ProjectSources
{
    /// <summary>
    /// <see cref="Elsewhere"/>: the project text says its compile items come from outside its
    /// own directory, so whatever <c>.cs</c> files sit beside the <c>.csproj</c> are not
    /// necessarily what it compiles. <see cref="None"/>: it compiles nothing of its own at
    /// all, which MSBuild settles on its own and no source scan can override — the stronger
    /// claim, and it implies the weaker one.
    /// </summary>
    internal readonly record struct Kind(bool Elsewhere, bool None);

    /// <summary>
    /// Reads the two claims off a <c>.csproj</c>. A file that does not parse is treated as an
    /// ordinary project rather than allowed to throw: a hand-edited or generator-written
    /// <c>.csproj</c> somewhere in a large tree must not take readiness inference down with
    /// it, and guessing "ordinary" only costs the candidates a scan would have found anyway.
    /// </summary>
    internal static Kind Read(string csproj)
    {
        var project = Parse(csproj);
        if (project is null) return new Kind(false, false);

        // Include only: Remove and Update name items the default globs already produced.
        var includes = Elements(project, "Compile")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .ToList();

        if (includes.Any(i => !PointsOutside(i!))) return new Kind(false, false);

        // Every remaining include points outside, so the default glob is the only thing that
        // could still compile a file of its own: with it off the project compiles nothing
        // under its own directory whether the include list is empty or not.
        var none = DefaultItemsDisabled(project);
        var elsewhere = none || includes.Count > 0 || ImportsSharedItems(project);
        return new Kind(elsewhere, none);
    }

    private static XElement? Parse(string csproj)
    {
        try
        {
            return XDocument.Parse(csproj).Root;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// By local name, at any depth: an <c>ItemGroup</c> may sit inside a <c>Target</c> or a
    /// <c>Choose</c>, and a project written against the old MSBuild namespace carries one
    /// while an SDK-style project does not.
    /// </summary>
    private static IEnumerable<XElement> Elements(XElement project, string name) =>
        project.Descendants().Where(e => e.Name.LocalName.Equals(name, StringComparison.Ordinal));

    /// <summary>
    /// Whether an <c>Include</c> can only name files above or away from the project
    /// directory. Textual by necessity — MSBuild properties and item functions are not
    /// evaluated here — so anything it cannot read is answered "inside", which leaves the
    /// project probed the way it was before. Both separators, since a <c>.csproj</c> written
    /// on Windows uses backslashes and is read here on any platform.
    /// </summary>
    private static bool PointsOutside(string include) => include
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .All(i => i.StartsWith("..\\", StringComparison.Ordinal)
               || i.StartsWith("../", StringComparison.Ordinal)
               || Path.IsPathRooted(i));

    /// <summary>
    /// A shared-items import (<c>*.projitems</c>, the Shared Project mechanism) contributes
    /// the whole compile list from another directory. The <c>Label="Shared"</c> attribute
    /// Visual Studio writes beside it is not required: the extension is the load-bearing part
    /// and the label is optional metadata.
    /// </summary>
    private static bool ImportsSharedItems(XElement project) => Elements(project, "Import")
        .Select(e => (string?)e.Attribute("Project"))
        .Any(p => p is not null &&
                  p.EndsWith(".projitems", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <c>EnableDefaultCompileItems</c> as well as <c>EnableDefaultItems</c>: the narrower
    /// property turns off exactly the <c>**/*.cs</c> glob this inference depends on, so a
    /// project setting it compiles no more of its own than one setting the broader flag.
    /// Only an <em>unconditional</em> <c>false</c> counts: a <c>Condition</c> on the element or
    /// on anything containing it — a <c>PropertyGroup</c>, a <c>When</c>, a <c>Target</c> — is
    /// not evaluated here, and a Release-only <c>false</c> would otherwise skip a project whose
    /// Debug build, which is what the server loads, compiles files of its own. Unknown leaves
    /// the project probed as before: the worst case is the timeout that was always there,
    /// against a wrongly skipped project at exit 0.
    /// </summary>
    private static bool DefaultItemsDisabled(XElement project) => project
        .Descendants()
        .Where(e => e.Name.LocalName is "EnableDefaultItems" or "EnableDefaultCompileItems")
        .Where(Unconditional)
        .Any(e => e.Value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase));

    private static bool Unconditional(XElement element) => !element
        .AncestorsAndSelf()
        .Any(e => e.Attribute("Condition") is not null);
}
