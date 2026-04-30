using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Stateful.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RawJsonPathAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "STF001";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Prefer typed JSON path symbols",
        "Prefer a JsonPath<TDocument, TValue> symbol over raw JSON path '{0}'",
        "Stateful",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Raw JSON paths are valid escape hatches, but generated JsonPath symbols let the compiler check document and value types.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, Microsoft.CodeAnalysis.CSharp.SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return;
        }

        var methodName = memberAccess.Name.Identifier.ValueText;
        if (methodName is not ("Set" or "Remove"))
        {
            return;
        }

        var firstArgument = invocation.ArgumentList.Arguments.FirstOrDefault();
        if (firstArgument?.Expression is not LiteralExpressionSyntax literal ||
            literal.Token.Value is not string path ||
            !path.StartsWith("$.", StringComparison.Ordinal))
        {
            return;
        }

        var symbol = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol as IMethodSymbol;
        if (symbol is null || !IsStatefulPatchMethod(symbol))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, literal.GetLocation(), path));
    }

    private static bool IsStatefulPatchMethod(IMethodSymbol symbol)
    {
        var containingType = symbol.ContainingType;
        if (containingType is null)
        {
            return false;
        }

        var typeName = containingType.OriginalDefinition.ToDisplayString();
        return typeName is "Stateful.JsonPatch<T>" or "Stateful.JsonPatchBuilder" or "Stateful.JsonPatchBuilder<TDocument>";
    }
}
