using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static DefensiveCopyAnalyzer.DiagnosticDescriptors;

namespace DefensiveCopyAnalyzer;

/// <summary>
/// 三类修复：
/// 1. 将结构体声明为 readonly struct（仅当所有实例成员均为 readonly，保证可编译）；
/// 2. 移除触发防御性拷贝的 readonly 字段修饰符；
/// 3. 将按值返回的属性/索引器/方法改为按引用返回（仅处理字段直返的简单形态）。
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(DefensiveCopyCodeFixProvider))]
public sealed class DefensiveCopyCodeFixProvider : CodeFixProvider
{
    private const string MakeReadonlyStructTitle = "改为 readonly 结构体（成员均为只读，可安全标注）";
    private const string RemoveFieldReadonlyTitle = "移除字段的 readonly 修饰符（使调用直接作用于原实例）";
    private const string RefReturnTitle = "改为按引用返回（ref return），消除临时副本";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(Dld001Id, Dld002Id, Dld003Id);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        var diagnosticNode = root.FindNode(context.Span);
        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (semanticModel is null)
        {
            return;
        }

        foreach (var diagnostic in context.Diagnostics)
        {
            switch (diagnostic.Id)
            {
                case Dld001Id:
                    RegisterReadOnlyStructFix(context, diagnosticNode, semanticModel, diagnostic);
                    RegisterRemoveFieldReadonlyFix(context, diagnosticNode, semanticModel, diagnostic);
                    break;

                case Dld002Id:
                    RegisterReadOnlyStructFix(context, diagnosticNode, semanticModel, diagnostic);
                    RegisterRefReturnFix(context, diagnosticNode, semanticModel, diagnostic);
                    break;

                case Dld003Id:
                    RegisterReadOnlyStructFix(context, diagnosticNode, semanticModel, diagnostic);
                    break;
            }
        }
    }

    // -------------------------------------------------------------------
    // Fix 1：结构体整体改为 readonly struct
    // -------------------------------------------------------------------
    private static void RegisterReadOnlyStructFix(
        CodeFixContext context,
        SyntaxNode node,
        SemanticModel model,
        Diagnostic diagnostic)
    {
        // 接收者结构体通常定义在调用点所在类之外（如账户状态对象里的 LoginState 字段），
        // 不能沿调用点祖先找声明，必须从接收者表达式的类型符号定位。
        var typeSymbol = ResolveReceiverStructType(node, model, context.CancellationToken);
        if (typeSymbol is null || typeSymbol.IsReadOnly || !CanSafelyBecomeReadOnly(typeSymbol))
        {
            return;
        }

        var structDeclaration = typeSymbol.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(context.CancellationToken))
            .OfType<StructDeclarationSyntax>()
            .FirstOrDefault();
        if (structDeclaration is null)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                MakeReadonlyStructTitle,
                ct => ApplyReadonlyStructAsync(context.Document, structDeclaration, ct),
                equivalenceKey: MakeReadonlyStructTitle),
            diagnostic);
    }

    /// <summary>从诊断节点解析接收者的可变值类型；找不到则回退到节点所在的外层结构体。</summary>
    private static INamedTypeSymbol? ResolveReceiverStructType(SyntaxNode node, SemanticModel model, CancellationToken token)
    {
        ExpressionSyntax? receiver = node switch
        {
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } => access.Expression,
            MemberAccessExpressionSyntax memberAccess => memberAccess.Expression,
            _ => null,
        };

        if (receiver is not null)
        {
            var type = model.GetTypeInfo(receiver, token).Type;
            if (type is INamedTypeSymbol named && named.TypeKind == TypeKind.Struct)
            {
                return named;
            }
        }

        // 回退：诊断节点本身位于某个结构体成员内（如 readonly 方法中的 this 调用）。
        var enclosingStruct = node.AncestorsAndSelf().OfType<StructDeclarationSyntax>().FirstOrDefault();
        return enclosingStruct is null ? null : model.GetDeclaredSymbol(enclosingStruct, token);
    }

    /// <summary>
    /// 按 C# 编译器规则判定结构体能否安全标注为 readonly struct：
    /// 所有实例字段均为 readonly，且不存在可写的实例属性/索引器（set 访问器会写入隐藏字段），
    /// 也不存在实例字段式事件。满足该条件时，任何实例方法都不可能写入 this，标注不会引入编译错误，
    /// 方法本身无需预先标注 readonly（readonly struct 的实例方法默认按 readonly this 处理）。
    /// </summary>
    private static bool CanSafelyBecomeReadOnly(INamedTypeSymbol type)
    {
        foreach (var member in type.GetMembers())
        {
            if (member.IsStatic || member.IsImplicitlyDeclared)
            {
                continue;
            }

            switch (member)
            {
                case IFieldSymbol { IsReadOnly: false }:
                    return false;

                case IPropertySymbol { SetMethod: not null }:
                    return false;

                case IEventSymbol:
                    // 实例事件通常带可写后备字段，保守拒绝。
                    return false;
            }
        }
        return true;
    }

    private static Task<Document> ApplyReadonlyStructAsync(
        Document document,
        StructDeclarationSyntax declaration,
        CancellationToken cancellationToken)
    {
        var readonlyToken = SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword).WithTrailingTrivia(SyntaxFactory.Space);
        var newDeclaration = declaration.WithModifiers(declaration.Modifiers.Add(readonlyToken));
        return ReplaceNodeAsync(document, declaration, newDeclaration, cancellationToken);
    }

    // -------------------------------------------------------------------
    // Fix 2：移除 readonly 字段修饰符
    // -------------------------------------------------------------------
    private static void RegisterRemoveFieldReadonlyFix(
        CodeFixContext context,
        SyntaxNode node,
        SemanticModel model,
        Diagnostic diagnostic)
    {
        // 从诊断节点向内找到接收者表达式，解析其字段符号。
        var receiver = node.DescendantNodesAndSelf()
            .OfType<ExpressionSyntax>()
            .Select(e => model.GetSymbolInfo(e, context.CancellationToken).Symbol as IFieldSymbol)
            .FirstOrDefault(f => f is { IsReadOnly: true, IsStatic: false });

        if (receiver is null)
        {
            return;
        }

        var fieldNode = receiver.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(context.CancellationToken))
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(d => d.Parent is VariableDeclarationSyntax { Parent: FieldDeclarationSyntax });

        if (fieldNode?.Parent?.Parent is not FieldDeclarationSyntax fieldDeclaration
            || !fieldDeclaration.Modifiers.Any(SyntaxKind.ReadOnlyKeyword))
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                RemoveFieldReadonlyTitle,
                ct => Task.FromResult(RemoveReadonlyModifier(context.Document, fieldDeclaration, ct)),
                equivalenceKey: RemoveFieldReadonlyTitle),
            diagnostic);
    }

    private static Document RemoveReadonlyModifier(
        Document document,
        FieldDeclarationSyntax fieldDeclaration,
        CancellationToken cancellationToken)
    {
        var oldToken = fieldDeclaration.Modifiers.First(m => m.IsKind(SyntaxKind.ReadOnlyKeyword));
        var newModifiers = fieldDeclaration.Modifiers.Remove(oldToken);
        var newDeclaration = fieldDeclaration.WithModifiers(newModifiers);
        var root = document.GetSyntaxRootAsync(cancellationToken).GetAwaiter().GetResult()!;
        return document.WithSyntaxRoot(root.ReplaceNode(fieldDeclaration, newDeclaration));
    }

    // -------------------------------------------------------------------
    // Fix 3：按值返回改为 ref 返回（仅处理“直接返回实例字段”的简单形态）
    // -------------------------------------------------------------------
    private static void RegisterRefReturnFix(
        CodeFixContext context,
        SyntaxNode node,
        SemanticModel model,
        Diagnostic diagnostic)
    {
        // 关键：要修改的是“产生临时副本的接收者成员”（属性/索引器/方法），
        // 而不是临时副本上被调用的那个变异方法。
        var returningMember = ResolveReturningMember(node, model, context.CancellationToken);
        if (returningMember is not (IPropertySymbol or IMethodSymbol { MethodKind: MethodKind.Ordinary }))
        {
            return;
        }

        // 已经是 ref 返回则无需修复。
        if (returningMember is IPropertySymbol { ReturnsByRef: true }
            or IMethodSymbol { ReturnsByRef: true })
        {
            return;
        }

        var declaration = returningMember.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(context.CancellationToken))
            .FirstOrDefault();

        // 仅支持表达式体成员与单语句 get 访问器，且返回的是可写字段。
        if (!TryGetReturnedField(declaration, out var fieldIdentifier) || fieldIdentifier is null)
        {
            return;
        }

        if (IsBackingFieldReadOnly(returningMember, fieldIdentifier))
        {
            // 字段只读时只能给 ref readonly，调用方仍无法修改；不提供半成品修复。
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                RefReturnTitle,
                ct => ApplyRefReturnAsync(context.Document, declaration!, ct),
                equivalenceKey: RefReturnTitle),
            diagnostic);
    }

    /// <summary>
    /// 从诊断节点（调用或成员访问）中解析产生按值临时副本的接收者成员。
    /// </summary>
    private static ISymbol? ResolveReturningMember(SyntaxNode node, SemanticModel model, CancellationToken token)
    {
        // receiver.Mutate(...)：receiver 是产生临时值的表达式。
        if (node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax invocationAccess })
        {
            return ResolveExpressionMember(invocationAccess.Expression, model, token);
        }

        // receiver.MutableProperty（无后续调用）。
        if (node is MemberAccessExpressionSyntax propertyAccess)
        {
            return ResolveExpressionMember(propertyAccess, model, token);
        }

        // 兜底：节点内首个按值返回的属性/方法。
        foreach (var expression in node.DescendantNodesAndSelf().OfType<ExpressionSyntax>())
        {
            var symbol = ResolveExpressionMember(expression, model, token);
            if (symbol is IPropertySymbol or IMethodSymbol)
            {
                return symbol;
            }
        }
        return null;
    }

    private static ISymbol? ResolveExpressionMember(ExpressionSyntax expression, SemanticModel model, CancellationToken token)
    {
        var symbol = model.GetSymbolInfo(expression, token).Symbol;
        if (symbol is IMethodSymbol { MethodKind: MethodKind.PropertyGet } getter)
        {
            return getter.AssociatedSymbol;
        }
        return symbol;
    }

    private static bool IsBackingFieldReadOnly(ISymbol returningMember, string fieldName)
    {
        var containingType = returningMember.ContainingType;
        var field = containingType.GetMembers(fieldName).OfType<IFieldSymbol>().FirstOrDefault();
        return field is { IsReadOnly: true };
    }

    /// <summary>提取表达式体或 get 访问器中直接 return 的字段名；形态不符则返回 false。</summary>
    private static bool TryGetReturnedField(SyntaxNode? declaration, out string? fieldName)
    {
        fieldName = null;

        switch (declaration)
        {
            case PropertyDeclarationSyntax { ExpressionBody: { } exprBody }:
                return TryGetFieldName(exprBody.Expression, out fieldName);

            case IndexerDeclarationSyntax { ExpressionBody: { } indexerBody }:
                return TryGetFieldName(indexerBody.Expression, out fieldName);

            case MethodDeclarationSyntax { ExpressionBody: { } methodBody }:
                return TryGetFieldName(methodBody.Expression, out fieldName);

            case PropertyDeclarationSyntax { AccessorList.Accessors: { Count: 1 } accessors }
                when accessors[0].IsKind(SyntaxKind.GetAccessorDeclaration):
                return TryGetFieldFromAccessor(accessors[0], out fieldName);

            case IndexerDeclarationSyntax { AccessorList.Accessors: { Count: 1 } indexerAccessors }
                when indexerAccessors[0].IsKind(SyntaxKind.GetAccessorDeclaration):
                return TryGetFieldFromAccessor(indexerAccessors[0], out fieldName);

            default:
                return false;
        }
    }

    private static bool TryGetFieldFromAccessor(AccessorDeclarationSyntax accessor, out string? fieldName)
    {
        fieldName = null;
        if (accessor.ExpressionBody is { } body)
        {
            return TryGetFieldName(body.Expression, out fieldName);
        }

        if (accessor.Body is { Statements.Count: 1 } &&
            accessor.Body.Statements[0] is ReturnStatementSyntax returnStatement)
        {
            return TryGetFieldName(returnStatement.Expression, out fieldName);
        }
        return false;
    }

    private static bool TryGetFieldName(ExpressionSyntax? expression, out string? fieldName)
    {
        fieldName = null;
        switch (expression)
        {
            case IdentifierNameSyntax id:
                fieldName = id.Identifier.Text;
                return true;
            case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } member:
                fieldName = member.Name.Identifier.Text;
                return true;
            default:
                return false;
        }
    }

    private static async Task<Document> ApplyRefReturnAsync(
        Document document,
        SyntaxNode declaration,
        CancellationToken cancellationToken)
    {
        var refToken = SyntaxFactory.Token(SyntaxKind.RefKeyword).WithTrailingTrivia(SyntaxFactory.Space);

        SyntaxNode newDeclaration = declaration switch
        {
            PropertyDeclarationSyntax property => property
                .WithType(PrependRef(property.Type, refToken))
                .WithExpressionBody(WithRefExpression(property.ExpressionBody!)),

            IndexerDeclarationSyntax indexer => indexer
                .WithType(PrependRef(indexer.Type, refToken))
                .WithExpressionBody(WithRefExpression(indexer.ExpressionBody!)),

            MethodDeclarationSyntax method => method
                .WithReturnType(PrependRef(method.ReturnType, refToken))
                .WithExpressionBody(WithRefExpression(method.ExpressionBody!)),

            _ => declaration,
        };

        return await ReplaceNodeAsync(document, declaration, newDeclaration, cancellationToken).ConfigureAwait(false);
    }

    private static TypeSyntax PrependRef(TypeSyntax type, SyntaxToken refToken)
    {
        var leading = type.GetLeadingTrivia();
        return SyntaxFactory.RefType(type.WithLeadingTrivia(SyntaxFactory.Space))
            .WithRefKeyword(refToken)
            .WithLeadingTrivia(leading);
    }

    private static ArrowExpressionClauseSyntax WithRefExpression(ArrowExpressionClauseSyntax body)
        => body.WithExpression(SyntaxFactory.RefExpression(body.Expression.WithoutLeadingTrivia())
            .WithRefKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword).WithTrailingTrivia(SyntaxFactory.Space)));

    private static async Task<Document> ReplaceNodeAsync(
        Document document,
        SyntaxNode oldNode,
        SyntaxNode newNode,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return document;
        }
        return document.WithSyntaxRoot(root.ReplaceNode(oldNode, newNode));
    }
}
