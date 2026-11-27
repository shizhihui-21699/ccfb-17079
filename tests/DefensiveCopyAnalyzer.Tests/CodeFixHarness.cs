using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace DefensiveCopyAnalyzer.Tests;

/// <summary>
/// 不依赖外部 Testing 包的轻量 Code Fix 验证：
/// 在 AdhocWorkspace 中构建文档 → 跑分析器 → 触发指定 EquivalenceKey 的修复 →
/// 比对规范化后的输出文本，并确认修复后同编号诊断消失且编译无错误。
/// </summary>
internal static class CodeFixHarness
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

    public static System.Collections.Generic.IReadOnlyList<MetadataReference> MetadataReferences() => BaseReferences;

    public static Document CreateDocument(string source)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        workspace.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Default,
            name: "TestProject",
            assemblyName: "TestAssembly",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            metadataReferences: BaseReferences));
        return workspace.AddDocument(projectId, "Test.cs", SourceText.From(source));
    }

    public static Task<ImmutableArray<Diagnostic>> GetDiagnosticsPublicAsync(Document document)
        => GetAnalyzerDiagnosticsAsync(document, CancellationToken.None);

    /// <summary>对文档上指定编号诊断收集全部已注册的代码修复操作。</summary>
    public static async Task<System.Collections.Generic.List<CodeAction>> CollectFixesAsync(
        Document document,
        string diagnosticId,
        CancellationToken cancellationToken = default)
    {
        var diagnostics = await GetAnalyzerDiagnosticsAsync(document, cancellationToken);
        var targets = diagnostics.Where(d => d.Id == diagnosticId).ToImmutableArray();
        if (targets.IsEmpty)
        {
            throw new Xunit.Sdk.XunitException($"源码上未产生期望的 {diagnosticId} 诊断。");
        }

        var actions = new System.Collections.Generic.List<CodeAction>();
        var fixProvider = new DefensiveCopyCodeFixProvider();
        foreach (var diagnostic in targets)
        {
            var context = new CodeFixContext(
                document,
                diagnostic,
                (action, _) => actions.Add(action),
                cancellationToken);
            await fixProvider.RegisterCodeFixesAsync(context);
        }
        return actions;
    }

    public static async Task ApplyAndVerifyAsync(
        string source,
        string expectedFixedSource,
        string diagnosticId,
        string equivalenceKey)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Default,
            name: "TestProject",
            assemblyName: "TestAssembly",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            metadataReferences: BaseReferences);

        workspace.AddProject(projectInfo);
        var document = workspace.AddDocument(projectId, "Test.cs", SourceText.From(source));

        // 1. 运行分析器，拿到目标诊断。
        var analyzerDiagnostics = await GetAnalyzerDiagnosticsAsync(document, CancellationToken.None);
        var targetDiagnostics = analyzerDiagnostics.Where(d => d.Id == diagnosticId).ToImmutableArray();
        if (targetDiagnostics.IsEmpty)
        {
            throw new Xunit.Sdk.XunitException($"源码上未产生期望的 {diagnosticId} 诊断。");
        }

        // 2. 注册并选取指定的代码修复操作。
        var fixProvider = new DefensiveCopyCodeFixProvider();
        var actions = new System.Collections.Generic.List<CodeAction>();
        foreach (var diagnostic in targetDiagnostics)
        {
            var context = new CodeFixContext(
                document,
                diagnostic,
                (action, _) => actions.Add(action),
                CancellationToken.None);
            await fixProvider.RegisterCodeFixesAsync(context);
        }

        var selected = actions.FirstOrDefault(a => a.EquivalenceKey == equivalenceKey);
        if (selected is null)
        {
            throw new Xunit.Sdk.XunitException(
                $"未找到 EquivalenceKey 为“{equivalenceKey}”的修复。可用修复：\n" +
                string.Join("\n", actions.Select(a => $"  {a.EquivalenceKey}")));
        }

        // 3. 应用修复并比对输出（规范化空白后比较，避免 trivia 噪声）。
        var operations = await selected.GetOperationsAsync(CancellationToken.None);
        foreach (var operation in operations)
        {
            operation.Apply(workspace, CancellationToken.None);
        }

        var changedDocument = workspace.CurrentSolution.GetDocument(document.Id);
        if (changedDocument is null)
        {
            throw new Xunit.Sdk.XunitException("修复操作未返回变更后的文档。");
        }

        var actualText = await changedDocument.GetTextAsync(CancellationToken.None);
        var actualNormalized = Normalize(actualText.ToString());
        var expectedNormalized = Normalize(expectedFixedSource);
        if (actualNormalized != expectedNormalized)
        {
            throw new Xunit.Sdk.XunitException(
                "修复输出与预期不符。\n--- 实际 ---\n" + actualNormalized +
                "\n--- 预期 ---\n" + expectedNormalized);
        }

        // 4. 修复后的代码必须可编译，且同编号诊断消失。
        var remaining = await GetAnalyzerDiagnosticsAsync(changedDocument, CancellationToken.None);
        var sameId = remaining.Where(d => d.Id == diagnosticId).ToImmutableArray();
        if (!sameId.IsEmpty)
        {
            throw new Xunit.Sdk.XunitException(
                $"修复后仍存在 {diagnosticId} 诊断：\n" +
                string.Join("\n", sameId.Select(d => d.GetMessage())));
        }

        var fixedCompilation = await changedDocument.Project.GetCompilationAsync(CancellationToken.None);
        var compileErrors = fixedCompilation!.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();
        if (!compileErrors.IsEmpty)
        {
            throw new Xunit.Sdk.XunitException(
                "修复后的代码存在编译错误：\n" +
                string.Join("\n", compileErrors.Select(d => d.ToString())));
        }
    }

    private static async Task<ImmutableArray<Diagnostic>> GetAnalyzerDiagnosticsAsync(
        Document document,
        CancellationToken cancellationToken)
    {
        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        var withAnalyzers = compilation!.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new DefensiveCopyDiagnosticAnalyzer()));
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken);    }

    private static string Normalize(string source)
        => CSharpSyntaxTree.ParseText(source)
            .GetRoot()
            .NormalizeWhitespace()
            .ToFullString();
}
