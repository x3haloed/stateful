using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Stateful.Analyzers;

namespace Stateful.Tests;

public sealed class AnalyzerTests
{
    [Fact]
    public async Task RawJsonPathAnalyzerReportsStringPatchPaths()
    {
        var source = """
            using Stateful;

            public sealed record Customer(string Name);

            public static class Demo
            {
                public static void Patch(JsonPatch<Customer> patch)
                {
                    patch.Set("$.name", "Acme");
                }
            }
            """;

        var compilation = CSharpCompilation.Create(
            "AnalyzerSample",
            [CSharpSyntaxTree.ParseText(source)],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Action).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(JsonPatch<>).Assembly.Location)
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diagnostics = await compilation
            .WithAnalyzers([new RawJsonPathAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(RawJsonPathAnalyzer.DiagnosticId, diagnostic.Id);
    }

    [Fact]
    public async Task RawJsonPathAnalyzerIgnoresTypedPaths()
    {
        var source = """
            using Stateful;

            public sealed record Customer(string Name);

            public static class Demo
            {
                public static readonly JsonPath<Customer, string> Name = JsonPath.Create<Customer, string>("$.name");

                public static void Patch(JsonPatch<Customer> patch)
                {
                    patch.Set(Name, "Acme");
                }
            }
            """;

        var compilation = CSharpCompilation.Create(
            "AnalyzerSample",
            [CSharpSyntaxTree.ParseText(source)],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Action).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(JsonPatch<>).Assembly.Location)
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diagnostics = await compilation
            .WithAnalyzers([new RawJsonPathAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();

        Assert.Empty(diagnostics);
    }
}
