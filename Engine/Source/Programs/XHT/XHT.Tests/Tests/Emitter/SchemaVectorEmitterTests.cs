// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Emitter;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Emitter;

/// <summary>
/// Tests for <see cref="SchemaVectorEmitter"/>. Verifies the
/// schema-vector emit fragment shape + opcode-classification rules
/// match the XCoreXObject Rev 4 §7.4.1 emit-rules table.
/// </summary>
[Collection(nameof(SchemaVectorEmitterTests))]
[CollectionDefinition(nameof(SchemaVectorEmitterTests), DisableParallelization = true)]
public sealed class SchemaVectorEmitterTests
{
    private static XhtProperty Prop(string name, string typeId, bool isContainer = false)
        => new(
            Name: name,
            TypeIdentifier: typeId,
            Specifiers: System.Array.Empty<Specifier>(),
            IsContainer: isContainer,
            RepNotifyFunctionName: null,
            Category: null,
            Span: EmitterTestHarness.Span());

    // =================================================================
    // Test 1: EmitSimpleObjectProperty.
    //
    // XCLASS with a single XPtr<T> field -> 1 Object opcode emitted
    // + a Terminator. NumOps == 2.
    // =================================================================
    [Fact]
    public void EmitSimpleObjectProperty_OneOpcodePlusTerminator()
    {
        SchemaVectorEmitter emitter = new();
        IReadOnlyList<XhtProperty> properties = new[]
        {
            Prop("Owner", "XPtr<XActor>"),
        };

        string content = emitter.Render("XFoo", properties);

        Assert.Contains("static constexpr ::XCore::Reflect::FXObjectRefSchemaOp s_XFoo_RefSchemaOps[] =", content);
        Assert.Contains("::XCore::Reflect::EXObjectRefSchemaOp::Object", content);
        Assert.Contains("offsetof(XFoo, Owner)", content);
        Assert.Contains("::XCore::Reflect::EXObjectRefSchemaOp::Terminator", content);
        // NumOps INCLUDES the Terminator: 1 ref opcode + 1 Terminator = 2.
        Assert.Contains("/*NumOps=*/2u", content);
        Assert.Contains("static constexpr ::XCore::Reflect::FXObjectRefSchema s_XFoo_RefSchema", content);
    }

    // =================================================================
    // Test 2: EmitContainerProperties.
    //
    // XCLASS with TArray<XPtr> + TMap<K, XPtr> + TSet<XPtr> ->
    // 3 container opcodes + Terminator.
    // =================================================================
    [Fact]
    public void EmitContainerProperties_ArrayMapSetOpcodes()
    {
        SchemaVectorEmitter emitter = new();
        IReadOnlyList<XhtProperty> properties = new[]
        {
            Prop("Components",   "TArray<XPtr<XComponent>>", isContainer: true),
            Prop("ByName",       "TMap<FName, XPtr<XActor>>", isContainer: true),
            Prop("ActorSet",     "TSet<XPtr<XActor>>", isContainer: true),
        };

        string content = emitter.Render("XFoo", properties);

        Assert.Contains("::XCore::Reflect::EXObjectRefSchemaOp::ArrayOfObject", content);
        Assert.Contains("::XCore::Reflect::EXObjectRefSchemaOp::MapOfObject_KeyValue", content);
        Assert.Contains("::XCore::Reflect::EXObjectRefSchemaOp::SetOfObject", content);
        Assert.Contains("offsetof(XFoo, Components)", content);
        Assert.Contains("offsetof(XFoo, ByName)", content);
        Assert.Contains("offsetof(XFoo, ActorSet)", content);
        // 3 ref opcodes + 1 Terminator = 4.
        Assert.Contains("/*NumOps=*/4u", content);
    }

    // =================================================================
    // Test 3: EmitNestedStruct.
    //
    // XCLASS with a nested FStruct member (carrying refs) -> Struct
    // opcode with NestedSchema pointing at the nested struct's
    // RefSchema symbol.
    //
    // Pure-POD math structs (FVector, FRotator, FTransform, etc.) are
    // recognised as non-ref structs and SKIP -- per spec §7.4.1
    // ("non-ref nested structs don't emit any opcode at all").
    // =================================================================
    [Fact]
    public void EmitNestedStruct_StructOpcodeWithNestedSchemaSymbol()
    {
        SchemaVectorEmitter emitter = new();
        IReadOnlyList<XhtProperty> properties = new[]
        {
            // FBarStruct (assumed user-defined; carries refs).
            Prop("Bar", "FBarStruct"),
        };

        string content = emitter.Render("XFoo", properties);

        Assert.Contains("::XCore::Reflect::EXObjectRefSchemaOp::Struct", content);
        Assert.Contains("&s_FBarStruct_RefSchema", content);
        // 1 Struct + 1 Terminator = 2.
        Assert.Contains("/*NumOps=*/2u", content);

        // FVector is a known non-ref struct; emit must skip it.
        IReadOnlyList<XhtProperty> vecOnly = new[]
        {
            Prop("Location", "FVector"),
        };
        string vecContent = emitter.Render("XFoo", vecOnly);
        Assert.Equal(string.Empty, vecContent);
    }

    // =================================================================
    // Test 4: EmitDelegateProperty.
    //
    // XCLASS with TMulticastDelegate / TDelegate -> appropriate
    // delegate opcode emitted.
    // =================================================================
    [Fact]
    public void EmitDelegateProperty_DelegateOpcodes()
    {
        SchemaVectorEmitter emitter = new();
        IReadOnlyList<XhtProperty> properties = new[]
        {
            Prop("OnTrigger",     "TDelegate<void(int32)>"),
            Prop("OnMulticast",   "TMulticastDelegate<void(int32)>"),
            Prop("OnSparse",      "TSparseMulticastDelegate<void(int32)>"),
        };

        string content = emitter.Render("XFoo", properties);

        Assert.Contains("::XCore::Reflect::EXObjectRefSchemaOp::Delegate", content);
        Assert.Contains("::XCore::Reflect::EXObjectRefSchemaOp::MulticastInlineDelegate", content);
        Assert.Contains("::XCore::Reflect::EXObjectRefSchemaOp::MulticastSparseDelegate", content);
        Assert.Contains("offsetof(XFoo, OnTrigger)", content);
        Assert.Contains("offsetof(XFoo, OnMulticast)", content);
        Assert.Contains("offsetof(XFoo, OnSparse)", content);
    }

    // =================================================================
    // Test 5: EmitNoRefProperties.
    //
    // XCLASS with only primitive properties -> Render returns the
    // empty string (no schema vector emitted; the FStruct's
    // RefSchema slot stays nullptr per spec §7.4 prose).
    // =================================================================
    [Fact]
    public void EmitNoRefProperties_EmptyResult()
    {
        SchemaVectorEmitter emitter = new();
        IReadOnlyList<XhtProperty> properties = new[]
        {
            Prop("Score",      "int32"),
            Prop("Speed",      "float"),
            Prop("Active",     "bool"),
            Prop("Label",      "FName"),
            Prop("DisplayName","FString"),
            Prop("Position",   "FVector"),         // pure-POD math; skip
        };

        string content = emitter.Render("XFoo", properties);
        Assert.Equal(string.Empty, content);
    }

    // =================================================================
    // Test 6: EmitMixedHierarchy.
    //
    // XCLASS with primitives + refs + container of struct ->
    // primitives skipped; ref + container opcodes emitted in
    // declaration order.
    // =================================================================
    [Fact]
    public void EmitMixedHierarchy_RefsEmittedInOrder()
    {
        SchemaVectorEmitter emitter = new();
        IReadOnlyList<XhtProperty> properties = new[]
        {
            Prop("Score",      "int32"),                                        // skip
            Prop("Owner",      "XPtr<XActor>"),                                  // Object
            Prop("Speed",      "float"),                                         // skip
            Prop("Children",   "TArray<XPtr<XActor>>", isContainer: true),      // ArrayOfObject
            Prop("Nested",     "FMyStruct"),                                     // Struct
        };

        string content = emitter.Render("XFoo", properties);

        // Opcode order in the emitted text == declaration order, minus
        // primitives. We look for the substrings in textual order.
        int objIdx   = content.IndexOf("::XCore::Reflect::EXObjectRefSchemaOp::Object,",
            System.StringComparison.Ordinal);
        int arrIdx   = content.IndexOf("::XCore::Reflect::EXObjectRefSchemaOp::ArrayOfObject",
            System.StringComparison.Ordinal);
        int structIdx = content.IndexOf("::XCore::Reflect::EXObjectRefSchemaOp::Struct,",
            System.StringComparison.Ordinal);

        Assert.True(objIdx > 0, "Object opcode missing");
        Assert.True(arrIdx > 0, "ArrayOfObject opcode missing");
        Assert.True(structIdx > 0, "Struct opcode missing");
        Assert.True(objIdx < arrIdx,
            $"Object opcode (@{objIdx}) should precede ArrayOfObject (@{arrIdx})");
        Assert.True(arrIdx < structIdx,
            $"ArrayOfObject (@{arrIdx}) should precede Struct (@{structIdx})");

        // Primitives are NOT in the emit.
        Assert.DoesNotContain("offsetof(XFoo, Score)", content);
        Assert.DoesNotContain("offsetof(XFoo, Speed)", content);

        // 3 ref opcodes + 1 Terminator = 4.
        Assert.Contains("/*NumOps=*/4u", content);
    }

    // =================================================================
    // Test 7: Opcode-value byte identity.
    //
    // The SchemaOpKind enum values MUST match the runtime
    // EXObjectRefSchemaOp values byte-for-byte (the load-bearing
    // emit/runtime invariant).
    // =================================================================
    [Fact]
    public void SchemaOpKind_MatchesRuntimeEnum()
    {
        // 22 active opcodes + Terminator (0) + sequential layout.
        Assert.Equal((byte)0,  (byte)SchemaOpKind.Terminator);
        Assert.Equal((byte)1,  (byte)SchemaOpKind.Object);
        Assert.Equal((byte)2,  (byte)SchemaOpKind.WeakObject);
        Assert.Equal((byte)3,  (byte)SchemaOpKind.SoftObject);
        Assert.Equal((byte)4,  (byte)SchemaOpKind.ArrayOfObject);
        Assert.Equal((byte)5,  (byte)SchemaOpKind.ArrayOfStruct);
        Assert.Equal((byte)6,  (byte)SchemaOpKind.StridedArrayOfObject);
        Assert.Equal((byte)7,  (byte)SchemaOpKind.MapOfObject_KeyValue);
        Assert.Equal((byte)8,  (byte)SchemaOpKind.SetOfObject);
        Assert.Equal((byte)9,  (byte)SchemaOpKind.Struct);
        Assert.Equal((byte)10, (byte)SchemaOpKind.FieldPath);
        Assert.Equal((byte)11, (byte)SchemaOpKind.FieldPathArray);
        Assert.Equal((byte)12, (byte)SchemaOpKind.OptionalObject);
        Assert.Equal((byte)13, (byte)SchemaOpKind.DynamicallyTypedValue);
        Assert.Equal((byte)14, (byte)SchemaOpKind.ARO);
        Assert.Equal((byte)15, (byte)SchemaOpKind.SlowARO);
        Assert.Equal((byte)16, (byte)SchemaOpKind.MemberARO);
        Assert.Equal((byte)17, (byte)SchemaOpKind.Interface);
        Assert.Equal((byte)18, (byte)SchemaOpKind.ClassProperty);
        Assert.Equal((byte)19, (byte)SchemaOpKind.SoftClass);
        Assert.Equal((byte)20, (byte)SchemaOpKind.Delegate);
        Assert.Equal((byte)21, (byte)SchemaOpKind.MulticastInlineDelegate);
        Assert.Equal((byte)22, (byte)SchemaOpKind.MulticastSparseDelegate);
    }
}
