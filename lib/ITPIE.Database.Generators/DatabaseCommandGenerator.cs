using System.Collections.Immutable;
using System.Text;

using ITPIE.Database.Generators.Model;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace ITPIE.Database.Generators;

[Generator]
public sealed partial class DatabaseCommandGenerator : IIncrementalGenerator
{
    private static readonly string GeneratedCodeAttribute =
        $"global::System.CodeDom.Compiler.GeneratedCodeAttribute(\"{typeof(DatabaseCommandGenerator).Assembly.GetName().Name}\", \"{typeof(DatabaseCommandGenerator).Assembly.GetName().Version}\")";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<ClassDeclarationSyntax?> classes = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: Parser.DatabaseCommandAttributeFullName,
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: static (context, _) => context.TargetNode.Parent as ClassDeclarationSyntax
            )
            .Where(static m => m is not null);

        IncrementalValuesProvider<AdditionalText> texts = context.AdditionalTextsProvider
            .Where(static text => text.Path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase));

        IncrementalValueProvider<(Compilation Compilation, (ImmutableArray<ClassDeclarationSyntax?> Classes,
            ImmutableArray<AdditionalText> Texts) Values)> source =
            context.CompilationProvider.Combine(classes.Collect().Combine(texts.Collect()));

        context.RegisterSourceOutput(
            source,
            static (spc, source) =>
                Execute(source.Compilation, source.Values, spc)
        );
    }

    private static void Execute(
        Compilation compilation,
        (ImmutableArray<ClassDeclarationSyntax?> Classes, ImmutableArray<AdditionalText> Texts) values,
        SourceProductionContext context
    )
    {
        if (values.Classes.IsDefaultOrEmpty)
        {
            return;
        }

        Parser p = new(compilation, context.ReportDiagnostic, context.CancellationToken);
        IReadOnlyList<DatabaseCommandClass> classes = p.GetDatabaseCommandClasses(
            values.Classes.Distinct(),
            values.Texts
        );

        // If tests are failing to produce output, uncomment this line and see
        // what's what in the debugger.
        //
        // TODO: open an issue on dotnet/roslyn-sdk with a minimal repro
        //
        // var d = compilation.GetDiagnostics();

        if (classes.Count == 0)
        {
            return;
        }

        Emitter e = new();
        ValidatorEmitter ve = new();

        foreach (DatabaseCommandClass c in classes)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            string r = e.Emit(c);
            string vr = ve.Emit(c);

            context.AddSource($"{c.Name}.g.cs", SourceText.From(r, Encoding.UTF8));
            context.AddSource($"{c.Name}.Validator.g.cs", SourceText.From(vr, Encoding.UTF8));
        }
    }
}