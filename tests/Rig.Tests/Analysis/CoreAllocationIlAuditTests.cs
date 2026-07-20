using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Analysis;

// Development-only ground truth for the compiler lowerings represented by the source-semantic allocation
// facts. This deliberately stays in tests: production indexing must not depend on a fresh output assembly.
public sealed class CoreAllocationIlAuditTests
{
    private static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields()
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .GroupBy(opcode => opcode.Value)
        .ToDictionary(group => group.Key, group => group.First());

    [Test]
    public async Task Owned_playground_lowerings_have_the_expected_il_allocation_boundaries()
    {
        using var playground = await TempPlayground.CreateCoreAllocationsAsync();
        await playground.BuildAsync();

        var assemblyPath = Path.Combine(playground.WorkingDirectory, "CoreAllocations", "bin", "Debug", "net10.0", "CoreAllocations.dll");
        File.Exists(assemblyPath).ShouldBeTrue();

        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();

        Instructions(pe, metadata, "CoreAllocations.CompilerLoweredScenarios", "CreateCapturingLambda")
            .Count(instruction => instruction == OpCodes.Newobj)
            .ShouldBe(2, "the capturing lambda constructs one display class and one delegate");

        Instructions(pe, metadata, "CoreAllocations.CompilerLoweredScenarios", "CreateIterator")
            .ShouldContain(OpCodes.Newobj, "calling the iterator method constructs its generated state machine");

        Instructions(pe, metadata, "CoreAllocations.CompilerLoweredScenarios", "CallWithImplicitParamsArray")
            .ShouldContain(OpCodes.Newarr, "expanded params arguments lower to a new array");

        var stringRange = Instructions(pe, metadata, "CoreAllocations.CompilerLoweredScenarios", "SliceRawEndTag");
        stringRange.ShouldNotContain(OpCodes.Newobj);
        stringRange.ShouldNotContain(OpCodes.Newarr);
        // The string Range indexer lowers through calls which can return a newly allocated substring; there
        // is no allocation opcode in the caller to audit. This is why rig detects this case from Roslyn's
        // System.String + System.Range semantics rather than treating caller IL opcodes as an allocation oracle.
    }

    private static IReadOnlyList<OpCode> Instructions(PEReader pe, MetadataReader metadata, string qualifiedTypeName, string methodName)
    {
        var method = metadata
            .TypeDefinitions.Select(handle => metadata.GetTypeDefinition(handle))
            .Where(type => QualifiedName(metadata, type) == qualifiedTypeName)
            .SelectMany(type => type.GetMethods())
            .Select(handle => metadata.GetMethodDefinition(handle))
            .Single(method => metadata.GetString(method.Name) == methodName);

        method.RelativeVirtualAddress.ShouldNotBe(0);
        var bytes =
            pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()
            ?? throw new InvalidOperationException($"Method {qualifiedTypeName}.{methodName} has no IL body.");
        return Decode(bytes);
    }

    private static string QualifiedName(MetadataReader metadata, TypeDefinition type)
    {
        var @namespace = metadata.GetString(type.Namespace);
        var name = metadata.GetString(type.Name);
        return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
    }

    private static IReadOnlyList<OpCode> Decode(byte[] bytes)
    {
        var result = new List<OpCode>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var first = bytes[offset++];
            var value = first == 0xfe ? (short)(0xfe00 | ReadByte(bytes, ref offset)) : (short)first;
            OpCodesByValue.TryGetValue(value, out var opcode).ShouldBeTrue($"unknown IL opcode 0x{value:x4}");
            result.Add(opcode);
            offset += OperandSize(opcode.OperandType, bytes, offset);
            (offset <= bytes.Length).ShouldBeTrue($"operand for {opcode.Name} extends past the method body");
        }
        return result;
    }

    private static int OperandSize(OperandType operandType, byte[] bytes, int operandOffset) =>
        operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget
            or OperandType.InlineField
            or OperandType.InlineI
            or OperandType.InlineMethod
            or OperandType.InlineSig
            or OperandType.InlineString
            or OperandType.InlineTok
            or OperandType.InlineType
            or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 + checked(ReadInt32(bytes, operandOffset) * 4),
            _ => throw new InvalidOperationException($"Unsupported IL operand type {operandType}."),
        };

    private static byte ReadByte(byte[] bytes, ref int offset)
    {
        (offset < bytes.Length).ShouldBeTrue("truncated two-byte IL opcode");
        return bytes[offset++];
    }

    private static int ReadInt32(byte[] bytes, int offset)
    {
        (offset + 4 <= bytes.Length).ShouldBeTrue("truncated inline-switch operand");
        return bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24);
    }
}
