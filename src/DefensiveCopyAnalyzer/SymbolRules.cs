using Microsoft.CodeAnalysis;

namespace DefensiveCopyAnalyzer;

/// <summary>
/// 符号层面的判定规则：哪些结构体、哪些成员会产生隐式拷贝，以及泛型约束下的装箱规则。
/// </summary>
internal static class SymbolRules
{
    /// <summary>
    /// 判断类型是否为需要分析的“可变值类型”。
    /// 只读结构体、枚举、Nullable&lt;T&gt;、元组与编译器内置基元类型一律排除，
    /// 以避免对坐标、金额这类纯值结构体的只读用法产生误报。
    /// </summary>
    public static bool IsMutableValueType(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named || named.TypeKind != TypeKind.Struct)
        {
            return false;
        }

        if (named.IsReadOnly)
        {
            return false;
        }

        if (named.IsRefLikeType)
        {
            // ref struct 同样遵循防御性拷贝规则，仍需分析。
        }

        // Nullable<T> 的成员调用由编译器直接处理 Nullable 的底层包装，不生成用户结构体的防御性拷贝。
        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            return false;
        }

        if (named.EnumUnderlyingType is not null)
        {
            return false;
        }

        if (named.IsTupleType)
        {
            return false;
        }

        if (IsBuiltinPrimitive(named.SpecialType))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 判断实例成员是否为“可能修改实例状态”的非 readonly 成员。
    /// 只读成员（readonly 方法/属性/索引器、readonly 结构体的全部成员）不会触发防御性拷贝。
    /// </summary>
    public static bool IsPotentiallyMutatingInstanceMember(ISymbol? member)
    {
        if (member is null || member.IsStatic)
        {
            return false;
        }

        // 访问器跟随其所属属性/索引器的 readonly 特性。
        if (member is IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet } accessor)
        {
            member = accessor.AssociatedSymbol ?? member;
        }

        switch (member)
        {
            case IMethodSymbol method:
                if (method.IsReadOnly)
                {
                    return false;
                }
                // 构造函数（ctor）不会在已存在实例上以“调用方法”的形式出现。
                if (method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor
                    or MethodKind.LocalFunction)
                {
                    return false;
                }
                // object 上继承而来的非变异方法：编译器对重写版本不生成防御性拷贝，非重写版本只装箱不拷贝调用方实例。
                if (IsObjectInheritedMethod(method))
                {
                    return false;
                }
                return true;

            case IPropertySymbol property:
                return !property.IsReadOnly;

            case IFieldSymbol:
                // 字段访问本身不触发方法调用拷贝规则（对只读字段赋值是编译错误 CS0191）。
                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// 泛型类型参数在调用接口成员/虚方法时是否会发生装箱，
    /// 对应 IL 层面能否发出 <c>constrained.</c> 前缀直接调用。
    /// </summary>
    public static bool GenericCallBoxes(ITypeParameterSymbol typeParameter)
    {
        // struct / unmanaged 约束：JIT 对值类型实参使用 constrained 调用，无装箱。
        if (HasStructConstraint(typeParameter) || HasUnmanagedConstraint(typeParameter))
        {
            return false;
        }

        // 无约束（隐含 System.Object 等）或 class 约束：值类型实参必须装箱后才能接口/虚调度。
        return true;
    }

    /// <summary>成员是否直接继承自 System.Object（ToString/Equals/GetHashCode/GetType/MemberwiseClone）。</summary>
    public static bool IsObjectInheritedMethod(IMethodSymbol method)
    {
        var current = method.OriginalDefinition;
        while (current is not null)
        {
            if (current.ContainingType?.SpecialType == SpecialType.System_Object)
            {
                return true;
            }
            current = current.OverriddenMethod;
        }
        return false;
    }

    /// <summary>是否为显式/隐式接口实现成员（通过接口调度时走接口槽位）。</summary>
    public static bool IsInterfaceDispatch(ISymbol? member, out INamedTypeSymbol? interfaceType)
    {
        interfaceType = null;
        if (member is null)
        {
            return false;
        }

        if (member.ContainingType?.TypeKind == TypeKind.Interface)
        {
            interfaceType = member.ContainingType;
            return true;
        }

        // 显式接口实现：语法上不可通过结构体实例直接访问，只能经接口变量调用，必然装箱。
        if (member is IMethodSymbol { ExplicitInterfaceImplementations.Length: > 0 } method)
        {
            interfaceType = method.ExplicitInterfaceImplementations[0].ContainingType;
            return true;
        }

        if (member is IPropertySymbol { ExplicitInterfaceImplementations.Length: > 0 } property)
        {
            interfaceType = property.ExplicitInterfaceImplementations[0].ContainingType;
            return true;
        }

        return false;
    }

    private static bool HasStructConstraint(ITypeParameterSymbol tp)
        => tp.HasValueTypeConstraint;

    private static bool HasUnmanagedConstraint(ITypeParameterSymbol tp)
        // unmanaged 约束在 Roslyn 中表现为值类型约束 + HasUnmanagedTypeConstraint 标志。
        => tp.HasValueTypeConstraint && tp.HasUnmanagedTypeConstraint;

    private static bool IsBuiltinPrimitive(SpecialType specialType) => specialType switch
    {
        SpecialType.System_Boolean
            or SpecialType.System_Byte
            or SpecialType.System_Char
            or SpecialType.System_Decimal
            or SpecialType.System_Double
            or SpecialType.System_Int16
            or SpecialType.System_Int32
            or SpecialType.System_Int64
            or SpecialType.System_UInt16
            or SpecialType.System_UInt32
            or SpecialType.System_UInt64
            or SpecialType.System_SByte
            or SpecialType.System_Single => true,
        _ => false,
    };
}
