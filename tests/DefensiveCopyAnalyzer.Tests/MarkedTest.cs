using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DefensiveCopyAnalyzer.Tests;

/// <summary>
/// 基于 /*DLD00x*/ 标记的断言：每个标记必须落在一条同编号诊断的位置区间内，且数量一致。
/// </summary>
internal static class MarkedTest
{
    private static readonly Regex Marker = new(@"/\*(DLD\d{3})\*/", RegexOptions.Compiled);

    public static async Task RunAsync(string markedSource)
    {
        var expectedPositions = new List<(string Id, int Position)>();
        var removedBefore = 0;
        foreach (Match match in Marker.Matches(markedSource))
        {
            expectedPositions.Add((match.Groups[1].Value, match.Index - removedBefore));
            removedBefore += match.Length;
        }

        var clean = Marker.Replace(markedSource, string.Empty);

        var compilation = TestHarness.CreateCompilation(clean);
        var analyzer = new DefensiveCopyDiagnosticAnalyzer();
        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(analyzer),
            new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty));

        var actual = await withAnalyzers.GetAnalyzerDiagnosticsAsync(CancellationToken.None);

        if (actual.Length != expectedPositions.Count)
        {
            throw new Xunit.Sdk.XunitException(
                $"诊断数量不符。期望 {expectedPositions.Count} 条，实际 {actual.Length} 条：\n" +
                string.Join("\n", actual.Select(d =>
                    $"  {d.Id} @ [{d.Location.SourceSpan.Start}..{d.Location.SourceSpan.End}] : {d.GetMessage()}")));
        }

        foreach (var (id, position) in expectedPositions)
        {
            var matched = actual.Any(d =>
                d.Id == id &&
                d.Location.SourceSpan.Start <= position &&
                position <= d.Location.SourceSpan.End);
            if (!matched)
            {
                throw new Xunit.Sdk.XunitException(
                    $"标记 {id}@{position} 未被同编号诊断覆盖。实际诊断：\n" +
                    string.Join("\n", actual.Select(d =>
                        $"  {d.Id} @ [{d.Location.SourceSpan.Start}..{d.Location.SourceSpan.End}]")));
            }
        }
    }
}
