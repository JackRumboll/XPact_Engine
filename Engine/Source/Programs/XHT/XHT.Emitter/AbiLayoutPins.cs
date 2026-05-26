// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;

namespace Simgenics.XPact.XHT.Emitter;

/// <summary>
/// XCore-4b Stage B addendum (Toolchain Contract Rev 13.8) ABI layout
/// pin surface. Mirrors XBT's <c>ContractSurface.AbiLayoutTags</c> +
/// <c>ContractSurface.AbiTypeSizes</c> tables; the two sides MUST agree
/// byte-for-byte or the manifest-level <c>ContractVersion</c> rotation
/// will surface diagnostic <c>XHT002</c> at read time.
/// </summary>
/// <remarks>
/// <para>
/// Per XCore-4b Rev 4 Section 9.4 ("Per-DLL static_assert pins") +
/// Section 11.6 ("ContractVersion bump"): every XHT-emitted
/// <c>.gen.cpp</c> file carries
/// <c>static_assert(XPactDetail::CompileTimeStrEq(...))</c> calls that
/// compare the runtime header's <c>XPACT_*_LAYOUT_TAG</c> macro
/// expansion against the contract-frozen string. A patch DLL compiled
/// against a different ABI fails to link with a clean compile-time
/// error.
/// </para>
/// <para>
/// The companion <see cref="TypeSizes"/> table emits
/// <c>static_assert(sizeof(...) == N)</c> calls so a developer who
/// adds a member to (e.g.) <c>FProperty.h</c> without bumping Contract
/// Rev 13.8 sees the compile-time mismatch at every <c>.gen.cpp</c> TU
/// in the project. The runtime headers themselves also carry these
/// asserts, but the per-TU repetition catches "the runtime header
/// changed but the consumer module wasn't recompiled" (Live Coding
/// patch DLL scenario).
/// </para>
/// <para>
/// These two tables are duplicated in
/// <c>XBT.Manifest/ContractSurface.cs</c>; the duplication is
/// intentional (XHT does not link XBT.Manifest at runtime per XHT.html
/// Section 25.2 item 1 standalone-tool discipline). The
/// <c>ContractVersion</c> mismatch detection (XbtManifestReader)
/// guards against drift.
/// </para>
/// </remarks>
public static class AbiLayoutPins
{
    /// <summary>
    /// ABI layout-tag pin set per XCore-4b Rev 4 Section 11.6. Each
    /// entry is (TagMacro, TagContent); the runtime header
    /// <c>XReflectionRuntime.h</c> defines each <c>TagMacro</c> as a
    /// <c>#define</c> producing the literal <c>TagContent</c> string.
    /// </summary>
    public static readonly IReadOnlyList<(string TagMacro, string TagContent)> LayoutTags = new (string, string)[]
    {
        (
            "XPACT_FNAME_LAYOUT_TAG",
            "FName-v1: 4+4 / Index+SerialNumber / 8-byte total / 4-byte aligned"
        ),
        (
            "XPACT_FFIELD_LAYOUT_TAG",
            "FField-v1: 32 bytes; ClassPrivate@0, Owner@8, Next@16, NamePrivate@24"
        ),
        (
            "XPACT_FFIELDCLASS_LAYOUT_TAG",
            "FFieldClass-v1: 48 bytes; Name@0, Id@8, CastFlags@16, SuperClass@24, Construct@32, FakeVTable@40"
        ),
        (
            "XPACT_FFIELDVARIANT_LAYOUT_TAG",
            "FFieldVariant-v1: 8 bytes; Storage@0 (1-bit LSB tag, 0=FField, 1=FStruct, on 8-byte-aligned pointer)"
        ),
        (
            "XPACT_FPROPERTY_LAYOUT_TAG",
            "FProperty-v2: 96 base + 8 DispatchTable = 104 bytes; UE-equivalent rep-meta source; FakeVTable in .rodata; FFieldVariant LSB-tag (LSB=1 means FStruct, inverse of UE)"
        ),
        (
            "XPACT_FFAKEVTABLE_LAYOUT_TAG",
            "FFakeVTable-v2: 8-byte header (Capabilities uint32 + _reservedHeader uint32) + 15 function-pointer slots (8 bytes each) = 128 bytes per FProperty subclass in .rodata; ConvertFromType is slot index 14 (the 15th and last; ESlot enum 0-indexed) per Rev 3 FIX-R2-HIGH-1"
        ),
        (
            "XPACT_FSTRUCT_LAYOUT_TAG",
            "FStruct-v4: 112 bytes; ObjectRefProperties TArray @ offset 56 = 24 bytes; SchemaHash @ 80; SerializeStructFn @ 104"
        ),
        (
            "XPACT_FSCRIPTSTRUCT_LAYOUT_TAG",
            "FScriptStruct-v4: 112 FStruct base + 16 ICppStructOps FakeVTable pattern = 128 bytes; per-subtype FCppStructOpsFakeVTable in .rodata at 136 bytes (8-byte header + 16 handler slots)"
        ),
        (
            "XPACT_FCPPSTRUCTOPSFAKEVTABLE_LAYOUT_TAG",
            "FCppStructOpsFakeVTable-v1: 8-byte header (32-bit Capabilities + _reservedHeader) + 16 handler slots (8 bytes each) = 136 bytes per FScriptStruct subtype in .rodata"
        ),
        (
            "XPACT_FCLASS_LAYOUT_TAG",
            "FClass-v4: 112 FStruct base + 112 FClass-specific = 224 bytes; ClassReps is TArray<FRepRecord> (24 bytes); NetFields is TArray<FField*> (24 bytes); ObjectRefProperties is dense TArray on FStruct (24 bytes); reflects XCore-4a DefaultAllocator carrying 2-byte FMemTag for per-container memory attribution per Phase 4b.5 verification"
        ),
        (
            "XPACT_FREPRECORD_LAYOUT_TAG",
            "FRepRecord-v1: 16 bytes per ClassReps entry; {FProperty* Property; int32 Index} matches UE's Class.h:3984"
        ),
        (
            "XPACT_FENUM_LAYOUT_TAG",
            "FEnum-v4: 72 bytes; Values TArray @ offset 40 = 24 bytes; CppForm @ 64"
        ),
        (
            "XPACT_FINTERFACE_LAYOUT_TAG",
            "FInterface-v4: 64 bytes; InterfaceFunctions TArray @ offset 32 = 24 bytes; InterfaceFlags @ 56"
        ),
        (
            "XPACT_FCUSTOMVERSION_LAYOUT_TAG",
            "FCustomVersion-v1: Key(FGuid 16) + Version(int32) + FriendlyName(FName)"
        ),
        (
            "XPACT_REPMETA_LAYOUT_TAG",
            "RepMeta-v2: 15-condition ELifetimeCondition(1) + RepIndex(2) + RepNotifyFunc-as-FName(8); UE-equivalent pre-Iris set"
        ),
    };

    /// <summary>
    /// Per-type sizeof pin set per XCore-4b Rev 4 Section 11.2 / 11.3
    /// tables. Each entry is (TypeName, ExpectedBytes); the emitter
    /// produces one <c>static_assert(sizeof(TypeName) == ExpectedBytes,
    /// "...")</c> line per entry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The TypeName values are bare (unqualified) since the .gen.cpp
    /// <c>#include "Reflection/F*.h"</c> brings the reflection-runtime
    /// types into the <c>XCore::Reflect</c> namespace via the headers'
    /// own namespace blocks; the emit uses
    /// <c>XCore::Reflect::TypeName</c> to avoid ambiguity with any
    /// user-side type that happens to share a name.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<(string TypeName, int ExpectedBytes)> TypeSizes = new (string, int)[]
    {
        ("FName", 8),
        ("FField", 32),
        ("FFieldClass", 48),
        ("FFieldVariant", 8),
        ("FProperty", 104),
        ("FFakeVTable", 128),
        ("FStruct", 112),
        ("FScriptStruct", 128),
        ("FCppStructOpsFakeVTable", 136),
        ("FClass", 224),
        ("FRepRecord", 16),
        ("FEnum", 72),
        ("FInterface", 64),
        ("FCustomVersion", 32),
    };
}
