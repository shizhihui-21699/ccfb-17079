using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DefensiveCopyAnalyzer;

/// <summary>
/// 表达式作为结构体成员调用的接收者时的存储分类。
/// 分类直接对应 C# 规范中“变量（可写/只读）”与“值（临时值）”的区分。
/// </summary>
internal enum StorageKind
{
    /// <summary>可写变量：成员调用直接作用于原实例，不产生隐式拷贝。</summary>
    Writable,

    /// <summary>只读变量（readonly 字段、in 参数、readonly 成员中的 this 等）：非 readonly 成员调用触发防御性拷贝。</summary>
    ReadOnlyLocation,

    /// <summary>按值产生的临时值（方法/属性/索引器按值返回、装箱结果、条件访问链等）：调用作用于临时副本。</summary>
    Temporary,
}

/// <summary>
/// 接收者分类器：判定一个表达式在作为实例成员调用的接收者时，
/// 其状态修改会落到原实例、只读位置的防御性拷贝，还是按值临时值的副本上。
/// </summary>
internal static class ReceiverClassifier
{
    public static (StorageKind Kind, string? LocationLabel) Classify(
        ExpressionSyntax? receiver,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        // 扩展方法与静态调用没有实例接收者；隐式 this 的情形由调用方传入 ThisExpression 语义处理。
        if (receiver is null)
        {
            return (StorageKind.Writable, null);
        }

        // 剥掉纯语法层的括号、无副作用的显式引用转换。
        var current = receiver;
        while (true)
        {
            switch (current)
            {
                case ParenthesizedExpressionSyntax p:
                    current = p.Expression;
                    continue;
                case CastExpressionSyntax cast:
                {
                    var conv = Microsoft.CodeAnalysis.CSharp.CSharpExtensions.GetConversion(
                        model, cast, cancellationToken);
                    // 仅剥离恒等转换与引用转换（不复制数据）；值转换会产生新的临时值。
                    if (!conv.IsUserDefined && (conv.IsIdentity || conv.IsReference))
                    {
                        current = cast.Expression;
                        continue;
                    }
                    return (StorageKind.Temporary, "强制转换结果");
                }
                default:
                    goto Done;
            }
        }

    Done:
        switch (current)
        {
            // 可写/只读的局部变量与参数。
            case IdentifierNameSyntax id:
                return ClassifySymbol(model.GetSymbolInfo(current, cancellationToken).Symbol, model);

            // this：readonly 结构体成员中为只读变量，否则可写（ref struct 等同样适用）。
            case ThisExpressionSyntax:
                var method = model.GetEnclosingSymbol(current.SpanStart, cancellationToken) as IMethodSymbol;
                if (method is { IsReadOnly: true } ||
                    (method is null && IsContainingAccessorReadOnly(model, current)))
                {
                    return (StorageKind.ReadOnlyLocation, "readonly 成员中的 this");
                }
                return (StorageKind.Writable, null);

            // 字段访问（含静态成员的实例接收者链）。
            case MemberAccessExpressionSyntax memberAccess:
            {
                var symbol = model.GetSymbolInfo(current, cancellationToken).Symbol;
                if (symbol is IFieldSymbol field)
                {
                    if (field.IsReadOnly)
                    {
                        return (StorageKind.ReadOnlyLocation, field.IsStatic ? "static readonly 字段" : "readonly 字段");
                    }
                    if (field.IsStatic)
                    {
                        return (StorageKind.Writable, null);
                    }
                    // 可变实例字段：跟随其接收者链的存储分类（只读位置上的可变字段仍为只读）。
                    return Classify(memberAccess.Expression, model, cancellationToken);
                }

                if (symbol is IPropertySymbol property)
                {
                    // 纯 ref 返回属性返回的是变量；按值属性返回临时值。
                    if (property.ReturnsByRefReadonly)
                    {
                        return (StorageKind.ReadOnlyLocation, "ref readonly 属性返回值");
                    }
                    return property.ReturnsByRef
                        ? (StorageKind.Writable, null)
                        : (StorageKind.Temporary, "按值返回的属性");
                }

                if (symbol is IEventSymbol)
                {
                    return (StorageKind.Temporary, "事件");
                }

                return (StorageKind.Temporary, null);
            }

            // 索引器与数组元素：数组元素始终可写；结构体索引器按 ref/值区分。
            case ElementAccessExpressionSyntax elementAccess:
            {
                // 数组元素访问的 GetSymbolInfo 不返回符号，需按接收者的类型判定。
                var receiverType = model.GetTypeInfo(elementAccess.Expression, cancellationToken).Type;
                if (receiverType is IArrayTypeSymbol)
                {
                    return (StorageKind.Writable, null);
                }

                var symbol = model.GetSymbolInfo(current, cancellationToken).Symbol;
                if (symbol is IPropertySymbol indexer)
                {
                    if (indexer.ReturnsByRefReadonly)
                    {
                        return (StorageKind.ReadOnlyLocation, "ref readonly 索引器返回值");
                    }
                    if (indexer.ReturnsByRef)
                    {
                        return (StorageKind.Writable, null);
                    }

                    // List<T>.this[] 等按值索引器：其接收者是只读位置时，语义上仍是“只读位置 + 防御性拷贝”。
                    var receiverKind = Classify(elementAccess.Expression, model, cancellationToken).Kind;
                    return receiverKind == StorageKind.ReadOnlyLocation
                        ? (StorageKind.ReadOnlyLocation, "按值索引器（只读接收者）")
                        : (StorageKind.Temporary, "按值返回的索引器");
                }
                return (StorageKind.Temporary, null);
            }

            // 方法调用结果：按 ref 返回变量，按值返回临时值。
            case InvocationExpressionSyntax:
            {
                var symbol = model.GetSymbolInfo(current, cancellationToken).Symbol as IMethodSymbol;
                if (symbol is null)
                {
                    return (StorageKind.Temporary, null);
                }
                if (symbol.ReturnsByRefReadonly)
                {
                    return (StorageKind.ReadOnlyLocation, "ref readonly 方法返回值");
                }
                return symbol.ReturnsByRef
                    ? (StorageKind.Writable, null)
                    : (StorageKind.Temporary, "按值返回的方法结果");
            }

            // 条件访问链 x?.Member：Value 语义，总是按值临时值（ref-conditional 极少见，保守按临时值处理）。
            case ConditionalAccessExpressionSyntax:
                return (StorageKind.Temporary, "null 条件访问结果");

            // 默认值、default(T)、nameof 之类。
            case DefaultExpressionSyntax:
                return (StorageKind.Temporary, "default 临时值");

            // 赋值表达式的结果是被赋值的变量（仅普通 = 与 ref 赋值保持可写；复合赋值结果按临时值保守处理）。
            case AssignmentExpressionSyntax assignment when assignment.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleAssignmentExpression):
                return (StorageKind.Writable, null);

            case RefExpressionSyntax refExpr:
                return Classify(refExpr.Expression, model, cancellationToken);

            // 其余表达式（运算符调用、插值、switch 表达式等）统一按临时值处理。
            default:
                return (StorageKind.Temporary, null);
        }
    }

    private static (StorageKind, string?) ClassifySymbol(ISymbol? symbol, SemanticModel model)
    {
        switch (symbol)
        {
            case IParameterSymbol parameter:
                if (parameter.RefKind is RefKind.In or RefKind.RefReadOnlyParameter)
                {
                    return (StorageKind.ReadOnlyLocation, "in/readonly ref 参数");
                }
                return parameter.RefKind == RefKind.Ref
                    ? (StorageKind.Writable, null)
                    : (StorageKind.Writable, null);

            case ILocalSymbol local:
                if (local.RefKind == RefKind.RefReadOnly)
                {
                    return (StorageKind.ReadOnlyLocation, "ref readonly 局部变量");
                }
                return (StorageKind.Writable, null);

            case IFieldSymbol field:
                return field.IsReadOnly
                    ? (StorageKind.ReadOnlyLocation, field.IsStatic ? "static readonly 字段" : "readonly 字段")
                    : (StorageKind.Writable, null);

            case IRangeVariableSymbol:
                // 查询表达式中的范围变量：按值拷贝，保守按临时值处理。
                return (StorageKind.Temporary, "查询范围变量");

            default:
                return (StorageKind.Temporary, null);
        }
    }

    private static bool IsContainingAccessorReadOnly(SemanticModel model, SyntaxNode node)
    {
        var accessor = node.Ancestors().OfType<AccessorDeclarationSyntax>().FirstOrDefault();
        if (accessor is null)
        {
            return false;
        }
        return model.GetDeclaredSymbol(accessor) is { IsReadOnly: true };
    }
}
