using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Rig.Analysis.Extraction;

internal readonly record struct AllocationSizeEstimate(long? Bytes, string Confidence, string Basis)
{
    public static AllocationSizeEstimate Unknown(string basis) => new(null, "unknown", basis);
}

// Conservative x64 shallow-size estimates. These are object-local bytes, not retained size, and deliberately
// return unknown whenever the static shape or element count is not defensible.
internal static class AllocationSizeEstimator
{
    private const int ObjectHeader = 16;
    private const int ArrayHeader = 24;
    private const int PointerSize = 8;

    public static AllocationSizeEstimate Object(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named || !named.IsReferenceType || named.TypeKind == TypeKind.Delegate)
        {
            return AllocationSizeEstimate.Unknown("runtime-dependent object layout");
        }

        long fields = 0;
        for (
            INamedTypeSymbol? current = named;
            current is not null && current.SpecialType != SpecialType.System_Object;
            current = current.BaseType
        )
        {
            if (current.IsRecord && current.TypeKind == TypeKind.Class)
            {
                // Records are still ordinary classes; their synthesized backing fields are included below.
            }

            foreach (var field in current.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
            {
                if (!TryWidth(field.Type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), out var width))
                {
                    return AllocationSizeEstimate.Unknown("x64 object header; one or more instance-field widths are runtime-dependent");
                }
                fields += width;
            }
        }

        var bytes = Align8(Math.Max(24, ObjectHeader + fields));
        return new(
            bytes,
            "estimated",
            "x64 16-byte object header + statically known instance fields, 8-byte aligned; runtime layout may differ"
        );
    }

    public static AllocationSizeEstimate Array(IArrayTypeSymbol? type, int? constantLength)
    {
        if (type is null || constantLength is null || constantLength < 0)
        {
            return AllocationSizeEstimate.Unknown("x64 array header; element count is not statically known");
        }
        if (!TryWidth(type.ElementType, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), out var width))
        {
            return AllocationSizeEstimate.Unknown("x64 array header; element width is runtime-dependent");
        }

        return new(
            Align8(ArrayHeader + (long)constantLength.Value * width),
            "estimated",
            $"x64 24-byte array header + {constantLength.Value} x {width}-byte elements, 8-byte aligned"
        );
    }

    public static AllocationSizeEstimate Boxing(ITypeSymbol? type)
    {
        if (!TryWidth(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), out var width))
        {
            return AllocationSizeEstimate.Unknown("x64 object header; boxed value width is runtime-dependent");
        }

        return new(
            Align8(Math.Max(24, ObjectHeader + width)),
            "estimated",
            $"x64 16-byte object header + {width}-byte value payload, minimum 24 bytes, 8-byte aligned"
        );
    }

    public static int? ConstantArrayLength(IArrayCreationOperation operation)
    {
        if (operation.Initializer is { ElementValues.Length: var count })
        {
            return count;
        }
        if (operation.DimensionSizes.Length == 1 && operation.DimensionSizes[0].ConstantValue is { HasValue: true, Value: int length })
        {
            return length;
        }
        return null;
    }

    public static AllocationSizeEstimate String(int? characterCount, string reason)
    {
        if (characterCount is null || characterCount < 0)
        {
            return AllocationSizeEstimate.Unknown(reason);
        }

        return new(
            Align8(ObjectHeader + 4 + ((long)characterCount.Value + 1) * 2),
            "estimated",
            $"x64 string header + length + {characterCount.Value} UTF-16 chars + terminator, 8-byte aligned"
        );
    }

    private static bool TryWidth(ITypeSymbol? type, HashSet<ITypeSymbol> visiting, out long width)
    {
        width = 0;
        if (type is null)
        {
            return false;
        }
        if (type.IsReferenceType || type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer || type is ITypeParameterSymbol)
        {
            width = PointerSize;
            return true;
        }
        if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol { EnumUnderlyingType: { } underlying })
        {
            return TryWidth(underlying, visiting, out width);
        }

        width = type.SpecialType switch
        {
            SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte => 1,
            SpecialType.System_Char or SpecialType.System_Int16 or SpecialType.System_UInt16 => 2,
            SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Single => 4,
            SpecialType.System_Int64
            or SpecialType.System_UInt64
            or SpecialType.System_Double
            or SpecialType.System_IntPtr
            or SpecialType.System_UIntPtr => 8,
            SpecialType.System_Decimal => 16,
            _ => 0,
        };
        if (width != 0)
        {
            return true;
        }
        if (type is not INamedTypeSymbol named || !named.IsValueType || !visiting.Add(type))
        {
            return false;
        }

        foreach (var field in named.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
        {
            if (!TryWidth(field.Type, visiting, out var fieldWidth))
            {
                visiting.Remove(type);
                return false;
            }
            width += fieldWidth;
        }
        visiting.Remove(type);
        return true;
    }

    private static long Align8(long value) => (value + 7) & ~7L;
}
