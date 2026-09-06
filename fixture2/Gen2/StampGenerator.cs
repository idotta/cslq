using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace Fixture2.Gen;

/// <summary>
/// One generator, two consumers, one identical emitted document. That is the whole point of
/// this fixture: the generated document's URI names the generator assembly and the hint name
/// -- both identical for Alpha and Beta -- so before csx asked the server which project
/// consumed it, the two distinct documents rendered under one label.
/// </summary>
[Generator]
public sealed class StampGenerator : IIncrementalGenerator
{
    private const string Source = """
        namespace Fixture2.Generated
        {
            public static class Stamp
            {
                public static string Value() => "two-consumers";
            }
        }
        """;

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var anchor = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => ((ClassDeclarationSyntax)ctx.Node).Identifier.ValueText)
            .Where(static name => name == "Anchor")
            .Collect();

        context.RegisterSourceOutput(anchor, static (ctx, names) =>
        {
            if (names.IsDefaultOrEmpty) return;
            ctx.AddSource("Stamp.g.cs", SourceText.From(Source, Encoding.UTF8));
        });
    }
}
