using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DefensiveCopyAnalyzer.Tests;

/// <summary>
/// 测试公共工具：用最新语言版本编译测试代码。
/// </summary>
internal static class TestHarness
{
    private static readonly MetadataReference[] BaseReferences =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Runtime.CompilerServices.RuntimeHelpers).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        MetadataReference.CreateFromFile(System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!, "netstandard.dll")),
        MetadataReference.CreateFromFile(System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
        MetadataReference.CreateFromFile(System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Collections.dll")),
        MetadataReference.CreateFromFile(System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Console.dll")),
    };

    public static CSharpCompilation CreateCompilation(string source)
        => CSharpCompilation.Create(
            assemblyName: "TestAssembly",
            syntaxTrees: new[]
            {
                CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))
            },
            references: BaseReferences,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
}
