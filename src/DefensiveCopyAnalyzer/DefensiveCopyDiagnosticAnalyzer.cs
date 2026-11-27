using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using static DefensiveCopyAnalyzer.DiagnosticDescriptors;

namespace DefensiveCopyAnalyzer;

/// <summary>
/// 检测结构体成员调用中“修改落到隐式副本”的分析器：
/// <list type="bullet">
/// <item>DLD001 — 只读存储位置上的防御性拷贝；</item>
/// <item>DLD002 — 按值返回临时值上的副本；</item>
/// <item>DLD003 — 接口装箱/泛型装箱产生的副本。</item>
/// </list>
/// 语法节点负责覆盖各类调用形态，IOperation 负责识别语法层不可见的装箱转换。
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DefensiveCopyDiagnosticAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Dld001, Dld002, Dld003);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
        context.RegisterOperationAction(AnalyzeBoxingConversion, OperationKind.Conversion);
    }

    // -----------------------------------------------------------------------
    // 方法调用：foo.Mutate(...)、foo?.Mutate()、隐式 this 的 Mutate()
    // -----------------------------------------------------------------------
    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var model = context.SemanticModel;
        var token = context.CancellationToken;

        var symbol = model.GetSymbolInfo(invocation, token).Symbol as IMethodSymbol;
        if (symbol is null || symbol.IsStatic || symbol.MethodKind == MethodKind.LocalFunction)
        {
            return;
        }

        switch (invocation.Expression)
        {
            // 常规实例调用：receiver.Mutate(...)
            case MemberAccessExpressionSyntax memberAccess:
                AnalyzeExplicitReceiver(context, memberAccess.Expression, symbol, invocation.GetLocation());
                return;

            // null 条件调用：receiver?.Mutate()
            case MemberBindingExpressionSyntax:
                AnalyzeConditionalCall(context, invocation, symbol);
                return;

            // 隐式 this 调用：readonly 实例成员中调用同类的非 readonly 实例方法
            case IdentifierNameSyntax:
                AnalyzeImplicitThisCall(context, invocation, symbol);
                return;

            default:
                return;
        }
    }

    private static void AnalyzeExplicitReceiver(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax receiverExpression,
        IMethodSymbol symbol,
        Location location)
    {
        var model = context.SemanticModel;
        var token = context.CancellationToken;
        var receiverType = model.GetTypeInfo(receiverExpression, token).Type;

        // 泛型类型参数接收者：装箱与否取决于约束。
        if (receiverType is ITypeParameterSymbol typeParameter)
        {
            AnalyzeGenericReceiver(context, location, receiverExpression, symbol, typeParameter);
            return;
        }

        if (!SymbolRules.IsMutableValueType(receiverType))
        {
            return;
        }

        // 接口调度（含显式接口实现）：接收者静态类型已是接口，装箱在转换点由 DLD003 单独报告；
        // 此处兜底处理直接以接口成员符号发起的调用。
        if (SymbolRules.IsInterfaceDispatch(symbol, out var interfaceType))
        {
            ReportBoxing(context, location, receiverExpression, receiverType!, interfaceType!, symbol);
            return;
        }

        if (!SymbolRules.IsPotentiallyMutatingInstanceMember(symbol))
        {
            return;
        }

        ReportByStorage(context, location, receiverExpression, receiverType!, symbol, model, token);
    }

    /// <summary>receiver?.Mutate()：值类型的 null 条件访问必然经 Nullable 解包产生临时值。</summary>
    private static void AnalyzeConditionalCall(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        IMethodSymbol symbol)
    {
        var conditional = invocation.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>();
        if (conditional is null)
        {
            return;
        }

        var model = context.SemanticModel;
        var receiverType = model.GetTypeInfo(conditional.Expression, context.CancellationToken).Type;

        // 解包 Nullable<T>，取底层值类型；非 Nullable 的值类型不允许 ?. 调用（编译期即报错）。
        var underlying = UnwrapNullable(receiverType);
        if (!SymbolRules.IsMutableValueType(underlying))
        {
            return;
        }
        if (!SymbolRules.IsPotentiallyMutatingInstanceMember(symbol))
        {
            return;
        }

        // 诊断挂在整个条件表达式上（maybe?.Mutate()），便于标记与说明定位。
        context.ReportDiagnostic(Diagnostic.Create(
            Dld002,
            conditional.GetLocation(),
            "null 条件访问链",
            underlying!.ToDisplayString(),
            symbol.Name));
    }

    /// <summary>readonly 实例成员内对 this 调用非 readonly 成员，触发防御性拷贝。</summary>
    private static void AnalyzeImplicitThisCall(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        IMethodSymbol symbol)
    {
        var enclosing = context.SemanticModel.GetEnclosingSymbol(invocation.SpanStart, context.CancellationToken) as IMethodSymbol;
        if (enclosing is not { IsReadOnly: true, IsStatic: false })
        {
            return;
        }

        var structType = symbol.ContainingType;
        if (!SymbolRules.IsMutableValueType(structType))
        {
            return;
        }
        if (!SymbolRules.IsPotentiallyMutatingInstanceMember(symbol))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Dld001,
            invocation.GetLocation(),
            "readonly 成员中的 this",
            structType.ToDisplayString(),
            symbol.Name));
    }

    // -----------------------------------------------------------------------
    // 属性成员访问：foo.MutableProperty（非 readonly getter，且无后续调用时仍会拷贝）
    // -----------------------------------------------------------------------
    private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
    {
        var memberAccess = (MemberAccessExpressionSyntax)context.Node;

        // 作为调用表达式的一部分时已在 AnalyzeInvocation 中处理。
        if (memberAccess.Parent is InvocationExpressionSyntax invoke && invoke.Expression == memberAccess)
        {
            return;
        }

        var symbol = context.SemanticModel.GetSymbolInfo(memberAccess, context.CancellationToken).Symbol;
        if (symbol is not IPropertySymbol property || property.IsStatic)
        {
            return;
        }

        var receiverType = context.SemanticModel.GetTypeInfo(memberAccess.Expression, context.CancellationToken).Type;
        if (!SymbolRules.IsMutableValueType(receiverType))
        {
            return;
        }
        if (!SymbolRules.IsPotentiallyMutatingInstanceMember(property))
        {
            return;
        }

        ReportByStorage(context, memberAccess.GetLocation(), memberAccess.Expression, receiverType!, property,
            context.SemanticModel, context.CancellationToken);
    }

    // -----------------------------------------------------------------------
    // DLD003：IOperation 层面识别装箱（值类型 -> 接口/引用类型的非用户定义转换）
    // -----------------------------------------------------------------------
    private static void AnalyzeBoxingConversion(OperationAnalysisContext context)
    {
        if (context.Operation is not IConversionOperation conversion)
        {
            return;
        }

        var sourceType = conversion.Operand.Type;
        var targetType = conversion.Type;
        if (sourceType is null || targetType is null)
        {
            return;
        }

        var isBoxing = sourceType.IsValueType
                       && targetType.IsReferenceType
                       && !conversion.Conversion.IsUserDefined;
        if (!isBoxing)
        {
            return;
        }

        // 只对目标为接口的装箱报警：装箱到 object 不直接造成“方法修改丢失”，噪声过大。
        if (targetType.TypeKind != TypeKind.Interface)
        {
            return;
        }
        if (!SymbolRules.IsMutableValueType(sourceType))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Dld003,
            conversion.Syntax.GetLocation(),
            "结构体到接口的装箱转换",
            targetType.ToDisplayString(),
            sourceType.ToDisplayString(),
            "装箱转换"));
    }

    // -----------------------------------------------------------------------
    // 泛型接收者：按约束区分装箱（DLD003）与只读位置防御性拷贝（DLD001）
    // -----------------------------------------------------------------------
    private static void AnalyzeGenericReceiver(
        SyntaxNodeAnalysisContext context,
        Location location,
        ExpressionSyntax receiverExpression,
        IMethodSymbol method,
        ITypeParameterSymbol typeParameter)
    {
        if (method.IsStatic)
        {
            return;
        }

        var (kind, label) = ReceiverClassifier.Classify(receiverExpression, context.SemanticModel, context.CancellationToken);

        if (SymbolRules.GenericCallBoxes(typeParameter))
        {
            // 无约束/class 约束：值类型实参每次调用都 box，修改落在装箱副本。
            var constraintText = typeParameter.HasReferenceTypeConstraint ? "class 约束" : "无值类型约束";
            context.ReportDiagnostic(Diagnostic.Create(
                Dld003,
                location,
                $"泛型参数带{constraintText}",
                method.ContainingType.ToDisplayString(),
                typeParameter.ToDisplayString(),
                method.Name));
            return;
        }

        // struct/unmanaged 约束：constrained 调用不装箱；仅只读位置上的非 readonly 成员产生防御性拷贝。
        if (kind == StorageKind.ReadOnlyLocation && SymbolRules.IsPotentiallyMutatingInstanceMember(method))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Dld001,
                location,
                label!,
                typeParameter.ToDisplayString(),
                method.Name));
        }
    }

    /// <summary>按接收者的存储分类发出 DLD001 / DLD002。</summary>
    private static void ReportByStorage(
        SyntaxNodeAnalysisContext context,
        Location location,
        ExpressionSyntax receiverExpression,
        ITypeSymbol receiverType,
        ISymbol member,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var (kind, label) = ReceiverClassifier.Classify(receiverExpression, model, cancellationToken);
        switch (kind)
        {
            case StorageKind.ReadOnlyLocation:
                context.ReportDiagnostic(Diagnostic.Create(
                    Dld001,
                    location,
                    label!,
                    receiverType.ToDisplayString(),
                    member.Name));
                break;

            case StorageKind.Temporary:
                context.ReportDiagnostic(Diagnostic.Create(
                    Dld002,
                    location,
                    DescribeTemporarySource(receiverExpression),
                    receiverType.ToDisplayString(),
                    member.Name));
                break;

            case StorageKind.Writable:
            default:
                break;
        }
    }

    private static void ReportBoxing(
        SyntaxNodeAnalysisContext context,
        Location location,
        ExpressionSyntax receiverExpression,
        ITypeSymbol valueType,
        INamedTypeSymbol interfaceType,
        ISymbol member)
    {
        var (kind, label) = ReceiverClassifier.Classify(receiverExpression, context.SemanticModel, context.CancellationToken);
        var prefix = kind == StorageKind.ReadOnlyLocation
            ? $"{label}上的结构体经接口变量访问"
            : "结构体经接口变量访问";

        context.ReportDiagnostic(Diagnostic.Create(
            Dld003,
            location,
            prefix,
            interfaceType.ToDisplayString(),
            valueType.ToDisplayString(),
            member.Name));
    }

    private static ITypeSymbol? UnwrapNullable(ITypeSymbol? type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            return nullable.TypeArguments[0];
        }
        return type;
    }

    private static string DescribeTemporarySource(ExpressionSyntax receiver) => receiver switch
    {
        InvocationExpressionSyntax => "方法调用结果",
        ElementAccessExpressionSyntax => "索引器",
        ConditionalAccessExpressionSyntax => "null 条件访问链",
        MemberAccessExpressionSyntax => "属性访问器",
        _ => "按值表达式",
    };
}
