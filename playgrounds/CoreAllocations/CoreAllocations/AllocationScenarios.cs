using System;

namespace CoreAllocations;

[AttributeUsage(AttributeTargets.Class)]
public sealed class MetadataValuesAttribute(object value, int[] values) : Attribute
{
    public object Value { get; } = value;

    public int[] Values { get; } = values;
}

[MetadataValues(42, new[] { 1, 2 })]
public sealed class AttributeMetadataControl;

public sealed class Payload;

public sealed class UnreachablePayload;

public interface IMarker;

public readonly struct MarkerValue : IMarker;

public readonly struct SmallValue;

public static class AllocationScenarios
{
    public static void Run(bool enabled)
    {
        CreateReferenceObjects();
        CreateArrays();
        BoxValues();
        CreateInStructuralContexts(enabled);
        ExerciseNegativeControls();
    }

    private static void CreateReferenceObjects()
    {
        Payload explicitObject = new Payload();
        Payload targetTypedObject = new();
        GC.KeepAlive(explicitObject);
        GC.KeepAlive(targetTypedObject);
    }

    private static void CreateArrays()
    {
        int[] explicitArray = new int[4];
        int[] implicitArray = new[] { 1, 2, 3 };
        GC.KeepAlive(explicitArray);
        GC.KeepAlive(implicitArray);
    }

    private static void BoxValues()
    {
        MarkerValue value = default;
        object boxedObject = value;
        IMarker boxedInterface = value;
        GC.KeepAlive(boxedObject);
        GC.KeepAlive(boxedInterface);
    }

    private static void CreateInStructuralContexts(bool enabled)
    {
        for (var index = 0; index < 2; index++)
        {
            Payload loopedObject = new();
            GC.KeepAlive(loopedObject);
        }

        if (enabled)
        {
            Payload guardedObject = new();
            GC.KeepAlive(guardedObject);
        }
    }

    private static void ExerciseNegativeControls()
    {
        SmallValue valueType = new SmallValue();
        Span<int> stackMemory = stackalloc int[4];
        object referenceConversion = "already a reference";
        _ = valueType;
        _ = stackMemory.Length;
        _ = referenceConversion;
    }

    public static void Unreachable()
    {
        UnreachablePayload payload = new();
        GC.KeepAlive(payload);
    }
}
