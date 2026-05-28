// Copyright Simgenics. All Rights Reserved.

#pragma once

// =====================================================================
// XReflectionRuntime.h -- Phase 4b.6 reflection-runtime registry.
// =====================================================================
//
// XCore-4b Rev 4, Section 10 ("XReflectionRuntime Registry API"). This
// header is the FULL Phase 4b.6 replacement of the Stage-A stub that
// shipped at Phase 4b.0 (the bare-minimum surface XHT-emitted
// `.gen.cpp` files needed to compile against XCore-Stub).
//
// Two surfaces ship side-by-side:
//
//   1. The spec-authoritative namespaced surface in
//      `namespace XCore::Reflect`. The Phase 4b.6 wait-free lookup +
//      registration API per spec §10.1; consumed by all NEW code
//      (XCoreXObject System 5, XNetworking Layer 13, XSerialization
//      Layer 9, the editor's introspection-driven tools, etc.).
//      The methods are `static`; the registry data lives behind a
//      function-local-static singleton inside the .cpp.
//
//   2. The LEGACY global-scope `class XReflectionRuntime` + opaque tag
//      types (`XClass`, `XStruct`, `XEnum`, `XInterface`,
//      `XDelegateFunction`) + descriptor records (`XClassDescriptor`
//      etc.) carried over verbatim from the Stage-A stub. The XHT
//      emitter (System 2) currently emits code against this surface;
//      keeping it alive preserves XHT-emit compatibility with no
//      regeneration cycle in Phase 4b.6. The legacy entry points
//      delegate to the namespaced surface via thin shims.
//
//      Per Stage B addendum / XHT roadmap, the legacy surface will be
//      retired once XHT is regenerated to emit namespaced calls; until
//      then both surfaces co-exist. The legacy surface is documented
//      as "compatibility-shim" and MUST NOT acquire new features.
//
// STORAGE DESIGN (spec §10.2):
//
//   The registry holds FOUR TMap<FName, const FX*> instances (one per
//   top-level reflected kind: Class, Struct, Enum, Interface), TWO
//   TArray<const FX*> iteration buffers (AllClasses, AllStructs), and
//   ONE FRWLock guarding the whole. Reads acquire the lock in shared
//   mode; writes acquire exclusive. Once a registration completes the
//   per-FName lookup is O(1) via the TMap SwissTable -- typical hot-
//   path latency <50ns on Win64 desktop (spec §13 gate E2).
//
//   FScriptStruct lookups are routed through the StructsByName map: an
//   FScriptStruct IS a FStruct (subclass), so we register both pointers
//   in the same map by the same FName, with the runtime down-cast
//   handled by the FindScriptStruct probe (which checks the registered
//   descriptor's identity against the FScriptStruct type seam).
//
// MODULE-OWNERSHIP TRACKING:
//
//   Per dispatch task: the registry tracks the OwningModule (FName) of
//   every registered descriptor so OnModuleUnload can purge a specific
//   module's contributions. Per the same dispatch task, this is
//   tracked OUT-OF-BAND (a parallel TMap<const FField*, FName> rather
//   than a per-descriptor `OwningModule` field) to AVOID ABI-breaking
//   the FStruct / FClass / FEnum / FInterface layouts (which are locked
//   by Stage B addendum at Contract Rev 13.8).
//
//   The current-module FName is set via OnModuleLoad before a module
//   calls its RegisterType-sequence; subsequent registrations until
//   OnModuleUnload are stamped with that FName. If a registration
//   happens outside an OnModuleLoad/OnModuleUnload bracket, it is
//   tagged with NAME_None ("unknown module"; engine-core
//   pre-registration before any user module loads).
//
//   The module-current FName itself is thread-local: each thread that
//   loads a module sets its own current-module slot, so concurrent
//   module loads on different threads do not interfere. The registry
//   exclusive lock still serialises the actual TMap mutations.
//
// HOT-RELOAD SAFETY (§9):
//
//   * No virtual methods on XReflectionRuntime or the registry struct.
//   * Process-singleton living inside XCore's main DLL -- survives
//     downstream-module DLL reloads.
//   * Registration is idempotent on the wire (the same descriptor
//     pointer re-registered returns the same result; an FName
//     collision with a DIFFERENT pointer is the "duplicate name"
//     failure that returns false from Register*).
//   * OnModuleUnload purges everything the named module registered;
//     a subsequent reload re-registers fresh.
//
// =====================================================================

#include <cstddef>
#include <cstdint>

// FIX-A3: heap-fallback in the templated Iterate* bodies routes
// through FMemory / FMemTag::Reflection (Prime Directive: no raw
// new[]/delete[] in engine code; memory must be tag-accountable).
#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"

// ---------------------------------------------------------------------
// xpact_compile_time_streq: constexpr string-literal equality used by
// XHT-emitted static_asserts that pin the GC root / exception ABI /
// mangling scheme tags from the manifest to the runtime defines
// (Round-2 audit C1). The function is C++17-constexpr so it works
// uniformly across gcc / clang / MSVC without relying on a builtin.
//
// PRESERVED from the Stage-A stub for backward-compatibility with
// XHT-emitted code that references this exact symbol.
// ---------------------------------------------------------------------

namespace XPactDetail
{
    constexpr bool CompileTimeStrEq(const char* a, const char* b)
    {
        // No nullptr inputs accepted; the XHT-emit always passes string
        // literals so the constexpr path is the only path.
        if (a == nullptr || b == nullptr) { return false; }
        while (*a != '\0' && *b != '\0')
        {
            if (*a != *b) { return false; }
            ++a;
            ++b;
        }
        return *a == '\0' && *b == '\0';
    }
}

// ---------------------------------------------------------------------
// XCONSTINIT: the constinit-equivalent attribute the .gen.cpp uses.
//
// PRESERVED from the Stage-A stub. The XPactMacros.h header (Phase 1g)
// migrated this define alongside the XPACT_*_TAG defines, but the
// XHT-emitted `.gen.cpp` still references XCONSTINIT via this header;
// keep the define live until XHT is regenerated against XPactMacros.h
// directly.
// ---------------------------------------------------------------------

#if defined(__cpp_constinit) && __cpp_constinit >= 201907L
    #define XCONSTINIT constinit
#elif __cplusplus >= 202002L
    #define XCONSTINIT constinit
#else
    #define XCONSTINIT
#endif

// ---------------------------------------------------------------------
// XPACT_WITH_CONSTINIT_XOBJECT: the static_assert pin XHT emits at
// .gen.cpp scope (Section 8.2 step 3).
//
// PRESERVED from the Stage-A stub for XHT-emit compatibility.
// ---------------------------------------------------------------------

#ifndef XPACT_WITH_CONSTINIT_XOBJECT
    #define XPACT_WITH_CONSTINIT_XOBJECT 1
#endif

// ---------------------------------------------------------------------
// XPACT_GC_ROOT_ABI_TAG / XPACT_EXCEPTION_ABI_TAG / XPACT_MANGLING_SCHEME_TAG:
// string-literal ABI identifiers XHT emits as static_assert pins at
// .gen.cpp scope (Section 8.2 + Round-2 audit C1).
//
// PRESERVED from the Stage-A stub. The XPactMacros.h header migrated
// these defines, but a stale `.gen.cpp` could still reach them through
// this header; keep the defines.
// ---------------------------------------------------------------------

#ifndef XPACT_GC_ROOT_ABI_TAG
    #define XPACT_GC_ROOT_ABI_TAG "Span-based v1"
#endif

#ifndef XPACT_EXCEPTION_ABI_TAG
    #define XPACT_EXCEPTION_ABI_TAG "Tier1-Shim/Tier2-Direct"
#endif

#ifndef XPACT_MANGLING_SCHEME_TAG
    #define XPACT_MANGLING_SCHEME_TAG "Itanium-LengthPrefixed-v1"
#endif

// ---------------------------------------------------------------------
// XPACT_PROPERTY_HAS_ACCESSORS: flag indicating XPropertyDescriptor now
// carries Getter / Setter function-pointer slots per Round-2 audit
// M-XIL2CPP-Accessor.
//
// PRESERVED from the Stage-A stub.
// ---------------------------------------------------------------------

#ifndef XPACT_PROPERTY_HAS_ACCESSORS
    #define XPACT_PROPERTY_HAS_ACCESSORS 1
#endif

// =====================================================================
// XCore-4b Stage B addendum ABI layout tags (Contract Rev 13.8).
// =====================================================================
//
// Per XCore-4b Rev 4 Section 11.6 + Section 9.4: every XHT-emitted
// .gen.cpp / .gen.h pins these macros via
// `static_assert(XPactDetail::CompileTimeStrEq(...))` so a patch DLL
// compiled against a different ABI fails to link with a clean compile-
// time error before any runtime damage occurs.
//
// The macro VALUES are the contract-frozen TagContent strings the C#
// `ContractSurface.AbiLayoutTags` table mirrors. If the byte layout of
// any reflection-runtime type changes, BOTH the C# table content AND
// the macro content here must update, AND the per-version semantic tag
// embedded in the string ("-vN") must roll forward.
//
// IMPORTANT: defined as #define rather than inline constexpr
// std::string_view because the XPactDetail::CompileTimeStrEq helper
// works on `const char*` literals (preserves backward compatibility
// with the Stage-A `XPACT_GC_ROOT_ABI_TAG` / `XPACT_EXCEPTION_ABI_TAG`
// / `XPACT_MANGLING_SCHEME_TAG` pattern). The hot-reload-safety
// concern that motivated the inline-constexpr-string_view pattern in
// XPactMacros.h does not apply here because these tags are consumed
// EXCLUSIVELY at compile time (the static_asserts fire at TU compile;
// no string value is ever materialised at runtime).
// =====================================================================

#ifndef XPACT_FNAME_LAYOUT_TAG
    #define XPACT_FNAME_LAYOUT_TAG \
        "FName-v1: 4+4 / Index+SerialNumber / 8-byte total / 4-byte aligned"
#endif

#ifndef XPACT_FFIELD_LAYOUT_TAG
    #define XPACT_FFIELD_LAYOUT_TAG \
        "FField-v1: 32 bytes; ClassPrivate@0, Owner@8, Next@16, NamePrivate@24"
#endif

#ifndef XPACT_FFIELDCLASS_LAYOUT_TAG
    #define XPACT_FFIELDCLASS_LAYOUT_TAG \
        "FFieldClass-v1: 48 bytes; Name@0, Id@8, CastFlags@16, SuperClass@24, Construct@32, FakeVTable@40"
#endif

#ifndef XPACT_FFIELDVARIANT_LAYOUT_TAG
    #define XPACT_FFIELDVARIANT_LAYOUT_TAG \
        "FFieldVariant-v1: 8 bytes; Storage@0 (1-bit LSB tag, 0=FField, 1=FStruct, on 8-byte-aligned pointer)"
#endif

// XCore-4b Subagent A FIX-A7: tag wording corrected to reflect actual
// field decomposition. Prior "96 base + 8 DispatchTable" misframed the
// 96 bytes as "base" when the accurate split is FField base (32) +
// FProperty body (64). The total remains 104 bytes; static_assert(
// sizeof(FProperty) == 104) is unchanged.
#ifndef XPACT_FPROPERTY_LAYOUT_TAG
    #define XPACT_FPROPERTY_LAYOUT_TAG \
        "FProperty-v2: FField (32) + FProperty body (64) + DispatchTable pointer (8) = 104 bytes; UE-equivalent rep-meta source; FakeVTable in .rodata; FFieldVariant LSB-tag (LSB=1 means FStruct, inverse of UE)"
#endif

#ifndef XPACT_FFAKEVTABLE_LAYOUT_TAG
    #define XPACT_FFAKEVTABLE_LAYOUT_TAG \
        "FFakeVTable-v2: 8-byte header (Capabilities uint32 + _reservedHeader uint32) + 15 function-pointer slots (8 bytes each) = 128 bytes per FProperty subclass in .rodata; ConvertFromType is slot index 14 (the 15th and last; ESlot enum 0-indexed) per Rev 3 FIX-R2-HIGH-1"
#endif

// XCoreXObject Phase 5.a' Rev 13.9 micro-bump (per XCoreXObject Rev 4
// §11.1 / §11.2): XPACT_FSTRUCT_LAYOUT_TAG bumped to v5 (RefSchema
// appended at offset 112; sizeof 112 -> 120). The companion
// XPACT_FCLASS_LAYOUT_TAG bump to v6 (LifecycleTable appended at
// FClass-absolute offset 232; sizeof 224 -> 240) lands in the same
// Rev 13.9 micro-bump cascade. XPACT_FSCRIPTSTRUCT_LAYOUT_TAG v5 is
// the cascade follow-up (FStruct base growth +8 propagates).
//
// Three sources MUST stay byte-identical for the string content of
// each macro:
//   1. This file (XReflectionRuntime.h)
//   2. Engine/Source/Programs/XHT/XHT.Emitter/AbiLayoutPins.cs
//   3. Engine/Source/Programs/XBT/XBT.Manifest/ContractSurface.cs
#ifndef XPACT_FSTRUCT_LAYOUT_TAG
    #define XPACT_FSTRUCT_LAYOUT_TAG \
        "FStruct-v5 (Contract Rev 13.9 extension via XCoreXObject Rev 3): 120 bytes; Class@0 (8) + Owner@8 (8) + Next@16 (8) + Name@24 (8) + SuperStruct@32 (8) + ChildProperties@40 (8) + PropertyLink@48 (8) + ObjectRefProperties@56 (16 = TArray<FProperty*>) + SchemaHash@72 (8) + SchemaVersion@80 (4) + _pad@84 (4) + UnversionedSchema@88 (8) + SerializeStructFn@96 (8) + DestructorLink@104 (8) + RefSchema@112 (8). RefSchema is the fast-path GC walker pointer appended per FIX-A-CRIT-8 / UE-MISS-1. Conceptual layout name string -- the real field offsets per XCore-4b Rev 4 are: NamePrivate@0, SuperStruct@8, ChildProperties@16, PropertiesSize@24, MinAlignment@28, StructFlags@30, _padStructFlags@31, PropertyLink@32, DestructorLink@40, PostConstructLink@48, ObjectRefProperties@56 (24 byte TArray), SchemaHash@80, SchemaVersion@88, _padSchema@92, UnversionedSchema@96, SerializeStructFn@104, RefSchema@112 (Rev 3 appended)."
#endif

#ifndef XPACT_FSCRIPTSTRUCT_LAYOUT_TAG
    #define XPACT_FSCRIPTSTRUCT_LAYOUT_TAG \
        "FScriptStruct-v5 (Contract Rev 13.9 cascade): 120 FStruct base (with appended RefSchema@112) + 16 ICppStructOps FakeVTable pattern = 136 bytes; per-subtype FCppStructOpsFakeVTable in .rodata at 136 bytes (8-byte header + 16 handler slots) unchanged"
#endif

#ifndef XPACT_FCPPSTRUCTOPSFAKEVTABLE_LAYOUT_TAG
    #define XPACT_FCPPSTRUCTOPSFAKEVTABLE_LAYOUT_TAG \
        "FCppStructOpsFakeVTable-v1: 8-byte header (32-bit Capabilities + _reservedHeader) + 16 handler slots (8 bytes each) = 136 bytes per FScriptStruct subtype in .rodata"
#endif

#ifndef XPACT_FCLASS_LAYOUT_TAG
    #define XPACT_FCLASS_LAYOUT_TAG \
        "FClass-v6 (Contract Rev 13.9 extension via XCoreXObject Rev 3): 240 bytes = 120 FStruct base (with appended RefSchema@112) + 120 FClass-specific (with appended LifecycleTable@112 relative to FClass-specific start = FClass-absolute offset 232). FClass-specific field layout (offsets relative to FStruct end at FClass-absolute 120): ClassConstructorFn@0, ClassVTableHelperCtorCaller@8, ClassDefaultObject@16, ClassFlags@24, ClassCastFlags@32, ClassWithin@40, FirstOwnedClassRep@48, ClassRepCount@52, ClassReps@56 (24 byte TArray<FRepRecord>), NetFields@80 (24 byte TArray<FField*>), ClassConfigName@104, LifecycleTable@112 (Rev 3 appended; FClass-absolute offset 232). Per XCore-4b Rev 4 §11.6 baseline (224 = 112 + 112) + Rev 13.9 micro-bump appends RefSchema@FStruct.112 (+8) and LifecycleTable@FClass-specific.112 (+8), yielding FClass total 240 bytes."
#endif

#ifndef XPACT_FREPRECORD_LAYOUT_TAG
    #define XPACT_FREPRECORD_LAYOUT_TAG \
        "FRepRecord-v1: 16 bytes per ClassReps entry; {FProperty* Property; int32 Index} matches UE's Class.h:3984"
#endif

#ifndef XPACT_FENUM_LAYOUT_TAG
    #define XPACT_FENUM_LAYOUT_TAG \
        "FEnum-v4: 72 bytes; Values TArray @ offset 40 = 24 bytes; CppForm @ 64"
#endif

#ifndef XPACT_FINTERFACE_LAYOUT_TAG
    #define XPACT_FINTERFACE_LAYOUT_TAG \
        "FInterface-v4: 64 bytes; InterfaceFunctions TArray @ offset 32 = 24 bytes; InterfaceFlags @ 56"
#endif

#ifndef XPACT_FCUSTOMVERSION_LAYOUT_TAG
    #define XPACT_FCUSTOMVERSION_LAYOUT_TAG \
        "FCustomVersion-v1: Key(FGuid 16) + Version(int32) + FriendlyName(FName)"
#endif

#ifndef XPACT_REPMETA_LAYOUT_TAG
    #define XPACT_REPMETA_LAYOUT_TAG \
        "RepMeta-v2: 15-condition ELifetimeCondition(1) + RepIndex(2) + RepNotifyFunc-as-FName(8); UE-equivalent pre-Iris set"
#endif

// =====================================================================
// XCoreXObject Phase 5.a' Rev 13.9 micro-bump: 9 new XObject-side
// addendum tags per XCoreXObject Rev 4 §11.1.
//
// These tags pin the byte layouts of the XObject-side ABI surface
// that XCoreXObject (System 5; not yet shipped at Phase 5.a') will
// introduce. They are added at Phase 5.a' (Contract prerequisite)
// so the per-DLL static_assert pins XHT emits can verify against the
// frozen layouts before XCoreXObject's runtime types land.
//
// Until XCoreXObject ships, the XObject / FXObjectArrayEntry /
// XObjectKey / XWeakPtr / XPtr / FXObjectLifecycleTable /
// FXObjectRefSchema / XGCCardTable types do NOT exist as concrete C++
// types in this header; the tags are pure string-literal pins for the
// downstream consumers (AbiLayoutPins.cs + ContractSurface.cs) and
// the XHT-emit static_assert(CompileTimeStrEq(...)) calls that
// reference them indirectly via the Contract surface.
//
// The three sources MUST stay byte-identical for the string content
// of each macro:
//   1. This file (XReflectionRuntime.h)
//   2. Engine/Source/Programs/XHT/XHT.Emitter/AbiLayoutPins.cs
//   3. Engine/Source/Programs/XBT/XBT.Manifest/ContractSurface.cs
// =====================================================================

#ifndef XPACT_XOBJECT_LAYOUT_TAG
    #define XPACT_XOBJECT_LAYOUT_TAG \
        "XObject-v2: 56 bytes; ClassPrivate@0, InternalIndex@8, SerialNumber@12, Outer@16, NamePrivate@24, ObjectFlags@32, ReachabilityFlag@36 (Rev 3 per FIX-M-R2-3 moved from FXObjectArrayEntry), _reservedCluster0@40, _reservedCluster1@48; alignof = 8; no virtuals on the GC-relevant surface (FakeVTable pattern). Rev 2: cluster reservation expanded to 16 bytes; remote-id reservation dropped (FIX-A-MIN-49 / O5). Rev 3: per-object reachability flag consumes former _padObjectFlags slot."
#endif

#ifndef XPACT_XGC_CARDTABLE_LAYOUT_TAG
    #define XPACT_XGC_CARDTABLE_LAYOUT_TAG \
        "XGCCardTable-v1: byte-per-card flat array; card size = 512 bytes; max heap = 4 GB; card table = 8 MB; clean = 0x00, dirty = 0x01"
#endif

#ifndef XPACT_XOBJECTARRAY_ENTRY_LAYOUT_TAG
    #define XPACT_XOBJECTARRAY_ENTRY_LAYOUT_TAG \
        "FXObjectArrayEntry-v1: 32 bytes; Object@0, SerialNumber@8, ClusterRootIndex@12, StateBits@16 (atomic; pending-destroy + root-pinned + hot-reload + garbage bits), _reserved@24; alignof = 8. Rev 3: rotating reachability flag moved to XObject header per FIX-M-R2-3."
#endif

#ifndef XPACT_XOBJECTKEY_LAYOUT_TAG
    #define XPACT_XOBJECTKEY_LAYOUT_TAG \
        "XObjectKey-v1: 8 bytes; InternalIndex@0 (int32), SerialNumber@4 (uint32); ABI-compatible with XWeakPtr; alignof = 4"
#endif

#ifndef XPACT_XWEAKPTR_LAYOUT_TAG
    #define XPACT_XWEAKPTR_LAYOUT_TAG \
        "XWeakPtr-v1: 8 bytes; InternalIndex@0 (int32), SerialNumber@4 (uint32); matches XCore-4b's FWeakObjectPtr placeholder shape; alignof = 4"
#endif

#ifndef XPACT_XPTR_LAYOUT_TAG
    #define XPACT_XPTR_LAYOUT_TAG \
        "XPtr-v1: 8 bytes; Ptr@0 (raw T* compatible); ABI-equivalent to T*; alignof = 8"
#endif

#ifndef XPACT_XOBJECT_LIFECYCLE_TABLE_TAG
    #define XPACT_XOBJECT_LIFECYCLE_TABLE_TAG \
        "FXObjectLifecycleTable-v1: 72 bytes per FClass; 8-byte header (Capabilities@0 + _pad@4) + 8 slots * 8 bytes = 64 bytes; alignof = 8. Serialize slot signature: void(*)(XObject*, FArchive&, const FArchiveContext*) per FIX-A-HIGH-13."
#endif

#ifndef XPACT_FXOBJECTREFSCHEMA_LAYOUT_TAG
    #define XPACT_FXOBJECTREFSCHEMA_LAYOUT_TAG \
        "FXObjectRefSchema-v1: 24 bytes; NumOps@0 (u32), Version@4 (u32), Ops@8 (const FXObjectRefSchemaOp*), _padTail@16; alignof = 8. FXObjectRefSchemaOp is 24 bytes per opcode; Op@0 (u8), _padOp@1, ArrayDim@2 (u16), Offset@4 (i32), StrideBytes@8 (i32), NestedSchema@16 (const FXObjectRefSchema*); alignof = 8 (4 bytes of pad at offset 12 to align NestedSchema to 8). Rev 3: 22 active opcodes (added Interface, ClassProperty, SoftClass, Delegate, MulticastInlineDelegate, MulticastSparseDelegate per FIX-H-R2-3)."
#endif

// =====================================================================
// LEGACY SURFACE -- compatibility-shim for XHT-emitted code.
//
// The Section-10 emit symbols are flat (no namespace) so XHT-generated
// `extern "C" const struct XClass* ...` declarations resolve as
// global-scope types. The opaque tag types and descriptor structs
// below are PRESERVED VERBATIM from the Stage-A stub so XHT-emit
// continues to compile without regeneration.
//
// The descriptor struct layouts are documented in the Stage-A stub as
// "provisional"; the Stage B addendum (Contract Rev 13.8) freezes the
// LIVE descriptor layouts on the FStruct/FClass/FScriptStruct/FEnum/
// FInterface namespaced types, NOT on these legacy stub records. The
// legacy records remain provisional; do not persist them to disk.
// =====================================================================

struct XClass {};
struct XStruct {};
struct XEnum {};
struct XInterface {};
struct XDelegateFunction {};

// XPropertyDescriptor (Round-2 audit M-XIL2CPP-Accessor):
// adds Getter / Setter function-pointer slots so C# auto-properties and
// computed properties can be reflected. Phase 1 XHT-emit fills Getter /
// Setter as nullptr; direct-field properties (the C++ case + C# auto-
// property case) use Offset for member access.
struct XPropertyDescriptor
{
    const char* Name;
    const char* TypeName;
    uint32_t Offset;       // Used for direct-field access (C++ field / C# auto-property).
    uint32_t Size;
    uint32_t Flags;
    // Optional accessor slots (Round-2 audit M-XIL2CPP-Accessor). Phase 1 fills
    // nullptr; XIL2CPP Phase 2 wires up managed-side getter/setter thunks here.
    void* (*Getter)(const void* instance);
    void (*Setter)(void* instance, const void* value);
};

struct XFunctionDescriptor
{
    const char* Name;
    const char* ReturnTypeName;
    uint32_t NumParameters;
    const char** ParameterNames;
    const char** ParameterTypeNames;
    uint32_t Flags;
};

struct XEnumValueDescriptor
{
    const char* Name;
    int64_t Value;
};

struct XClassDescriptor
{
    const char* Name;
    const char* SuperName;
    const XPropertyDescriptor* Properties;
    uint32_t NumProperties;
    const XFunctionDescriptor* Functions;
    uint32_t NumFunctions;
    uint32_t Flags;
};

struct XStructDescriptor
{
    const char* Name;
    const char* SuperName;
    const XPropertyDescriptor* Properties;
    uint32_t NumProperties;
    uint32_t Flags;
};

struct XEnumDescriptor
{
    const char* Name;
    const XEnumValueDescriptor* Values;
    uint32_t NumValues;
    uint32_t Flags;
};

struct XInterfaceDescriptor
{
    const char* Name;
    const char* SuperName;
    const XFunctionDescriptor* Functions;
    uint32_t NumFunctions;
    uint32_t Flags;
};

struct XDelegateDescriptor
{
    const char* Name;
    const char* ReturnTypeName;
    uint32_t NumParameters;
    const char** ParameterTypeNames;
    bool IsMulticast;
    uint32_t Flags;
};

// XDelegateFunctionDescriptor: the descriptor XHT emits for XDelegate-
// reflected types (RoleToken == "XDelegateFunction", per Section 10.2).
struct XDelegateFunctionDescriptor
{
    const char* Name;
    const char* SuperName;
    const XPropertyDescriptor* Properties;
    uint32_t NumProperties;
    const XFunctionDescriptor* Functions;
    uint32_t NumFunctions;
    uint32_t Flags;
};

// ---------------------------------------------------------------------
// Legacy global-scope XReflectionRuntime.
//
// The class surface is PRESERVED from the Stage-A stub. The bodies
// now delegate to the namespaced `XCore::Reflect::XReflectionRuntime`
// implementation; nothing is inlined here so consumers do not pay the
// header-compile cost of pulling FName / FStruct / TMap.
//
// COMPATIBILITY POSTURE:
//   * `GetX*FromConstInit(const X*Descriptor*)` -- the legacy descriptor
//     records do NOT carry enough information to build a full FClass /
//     FStruct / FEnum / FInterface (no SchemaHash, no FFakeVTable
//     pointer, no ObjectRefProperties dense array, etc.). The Phase
//     4b.6 implementation returns nullptr from these methods until
//     XHT is regenerated to emit FClassDescriptor records directly.
//     This is the documented Stage-A -> Stage-B migration ramp; the
//     XHT integration phase (Phase 4b.7+) regenerates emit against the
//     new namespaced API directly.
//   * `RegisterType(const X*)` -- the legacy `RegisterType` takes the
//     opaque XClass/XStruct/etc. tag types. Phase 4b.6 implementation
//     accepts the calls (no-op on the legacy types because their
//     descriptor pointers are nullptr per the Get*FromConstInit
//     deferral above); the legacy callers continue to compile + link
//     without crashing. Production registration flows through the
//     namespaced surface.
//
// The legacy surface is OPAQUE-pointer-typed and accepts only the
// legacy opaque tag types; the rich FClass/FStruct/etc. namespaced
// pointers are NOT passable through these entry points.
// ---------------------------------------------------------------------

class XReflectionRuntime
{
public:
    // Construct-from-ConstInit getters. Each returns a pointer to a
    // runtime XClass / XStruct / XEnum / XInterface / XDelegateFunction
    // representing the descriptor; Phase 4b.6 returns nullptr (Stage-A
    // compatibility) pending XHT-emit regeneration against the
    // namespaced surface (Phase 4b.7+).
    static const XClass*             GetXClassFromConstInit(const XClassDescriptor* desc);
    static const XStruct*            GetXStructFromConstInit(const XStructDescriptor* desc);
    static const XEnum*              GetXEnumFromConstInit(const XEnumDescriptor* desc);
    static const XInterface*         GetXInterfaceFromConstInit(const XInterfaceDescriptor* desc);
    static const XDelegateFunction*  GetXDelegateFunctionFromConstInit(const XDelegateFunctionDescriptor* desc);

    // RegisterType: per-DLL-load init aggregator calls these to install
    // each reflected type into the runtime registry. Phase 4b.6 accepts
    // nullptr inputs (the Stage-A delegates above produce nullptr); a
    // non-null legacy pointer would have to come from somewhere other
    // than Get*FromConstInit and is treated as a structural error
    // (logged + ignored) rather than a fatal crash so a partial-
    // migration build can still link.
    static void RegisterType(const XClass* xclass);
    static void RegisterType(const XStruct* xstruct);
    static void RegisterType(const XEnum* xenum);
    static void RegisterType(const XInterface* xinterface);
    static void RegisterType(const XDelegateFunction* xdelegate);
};

// =====================================================================
// SPEC-AUTHORITATIVE SURFACE -- the Phase 4b.6 deliverable.
//
// The class below is the load-bearing reflection-runtime registry per
// XCore-4b Rev 4 §10. All new code (XCoreXObject, XNetworking,
// XSerialization, the editor's reflection-driven tools) consumes this
// surface.
// =====================================================================

// Forward declarations so this header stays lean. The full types are
// included from the .cpp; consumers calling Find* must include the
// matching descriptor header to use the returned pointer.
namespace XCore::Reflect
{
    struct FName;
    struct FStruct;
    struct FScriptStruct;
    struct FClass;
    struct FEnum;
    struct FInterface;
} // namespace XCore::Reflect

// The FName full type IS required by the public Find* signatures (it
// is passed by value, not by pointer). Include it via the lean header
// path that does NOT pull FString.h transitively.
#include "Reflection/FName.h"

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // XReflectionRuntime -- process-singleton reflection registry.
    //
    // Per spec §10.1 ("XReflectionRuntime Registry API") + dispatch
    // task scope (Phase 4b.6).
    //
    // ALL METHODS ARE STATIC. The singleton state lives behind a
    // function-local-static in the .cpp; no instance is exposed at
    // the API boundary.
    //
    // The class is `final` and has no virtual methods (hot-reload
    // safety; §9). It is non-copyable / non-movable (it IS the
    // singleton).
    // -----------------------------------------------------------------
    class XReflectionRuntime final
    {
    public:
        // =============================================================
        // Lookup-by-FName (O(1) via TMap; wait-free read in steady state).
        //
        // Returns nullptr if no descriptor with the given Name is
        // registered. NAME_None always returns nullptr (the registry
        // rejects NAME_None at Register-time).
        //
        // The returned pointer is stable for the lifetime of the
        // process unless the owning module is unloaded via
        // OnModuleUnload (after which the pointer is dangling -- the
        // caller's responsibility to not hold descriptor pointers
        // across module unloads).
        //
        // Acquires the registry's FRWLock in shared mode for the
        // duration of the TMap probe.
        // =============================================================
        [[nodiscard]] static const FClass*        FindClass(FName Name) noexcept;
        [[nodiscard]] static const FStruct*       FindStruct(FName Name) noexcept;
        [[nodiscard]] static const FScriptStruct* FindScriptStruct(FName Name) noexcept;
        [[nodiscard]] static const FEnum*         FindEnum(FName Name) noexcept;
        [[nodiscard]] static const FInterface*    FindInterface(FName Name) noexcept;

        // =============================================================
        // Registration (writes; exclusive-lock-acquired).
        //
        // Each Register* probes the appropriate TMap for the descriptor's
        // FName, then either:
        //
        //   * Inserts the new (Name -> Descriptor) pair if Name is not
        //     already registered. Returns true.
        //   * Detects a duplicate-name collision (Name already maps to
        //     a DIFFERENT descriptor pointer). Returns false; the
        //     existing registration is NOT replaced. Callers should
        //     log a duplicate-class diagnostic and abort the module
        //     init.
        //   * Detects a benign re-registration (Name maps to the SAME
        //     descriptor pointer). Returns true; no state change.
        //     This is the idempotency property the hot-reload
        //     contract relies on (a patched module may re-call
        //     RegisterType with the same descriptor that was already
        //     registered pre-patch; the second call is a no-op).
        //
        // NAME_None inputs are rejected (returns false).
        //
        // FScriptStruct registers into BOTH the StructsByName map
        // (as a FStruct*) and the dedicated FScriptStruct map slot
        // (Phase 4b.6 implementation -- see §10.2 "scriptstruct routing"
        // note in the .cpp).
        // =============================================================
        static bool RegisterClass(const FClass* Class) noexcept;
        static bool RegisterStruct(const FStruct* Struct) noexcept;
        static bool RegisterScriptStruct(const FScriptStruct* ScriptStruct) noexcept;
        static bool RegisterEnum(const FEnum* Enum) noexcept;
        static bool RegisterInterface(const FInterface* Interface) noexcept;

        // =============================================================
        // Unregistration (rare; used by hot-reload module-unload paths).
        //
        // UnregisterClass removes the (Name -> FClass*) entry from the
        // ClassesByName map and the matching AllClasses TArray entry.
        // No-op if the Name is not registered.
        //
        // Acquires the registry's FRWLock in exclusive mode.
        // =============================================================
        static void UnregisterClass(FName Name) noexcept;
        static void UnregisterStruct(FName Name) noexcept;
        static void UnregisterScriptStruct(FName Name) noexcept;
        static void UnregisterEnum(FName Name) noexcept;
        static void UnregisterInterface(FName Name) noexcept;

        // =============================================================
        // Hot-reload module-load / module-unload hooks.
        //
        // OnModuleLoad is called by XLiveCoding (or the engine's main
        // bootstrap for engine-internal modules) BEFORE a module starts
        // its RegisterType-sequence. The implementation sets the
        // thread-local "current module" slot so subsequent Register*
        // calls on this thread are stamped with the module's FName.
        //
        // OnModuleUnload purges every (Class / Struct / Enum / Interface)
        // descriptor whose OwningModule equals the given ModuleName,
        // then clears the thread-local "current module" slot if it
        // matches.
        //
        // Both hooks acquire the exclusive lock.
        //
        // OnModuleLoad with NAME_None is rejected (no-op); modules MUST
        // identify themselves with a non-None FName.
        // =============================================================
        static void OnModuleLoad(FName ModuleName) noexcept;
        static void OnModuleUnload(FName ModuleName) noexcept;

        // =============================================================
        // GetCurrentModuleName -- diagnostic accessor for the thread-
        // local module slot (introspection; tests).
        //
        // Returns NAME_None when no OnModuleLoad has been called on
        // this thread.
        // =============================================================
        [[nodiscard]] static FName GetCurrentModuleName() noexcept;

        // =============================================================
        // GetOwningModule -- diagnostic / hot-reload-introspection.
        //
        // Returns the FName of the module that originally registered
        // the descriptor at the given Name. Returns NAME_None if the
        // descriptor is not registered or was registered outside an
        // OnModuleLoad/OnModuleUnload bracket (e.g., engine-core
        // pre-registration).
        //
        // Acquires the registry's FRWLock in shared mode.
        // =============================================================
        [[nodiscard]] static FName GetClassOwningModule(FName ClassName) noexcept;
        [[nodiscard]] static FName GetStructOwningModule(FName StructName) noexcept;
        [[nodiscard]] static FName GetEnumOwningModule(FName EnumName) noexcept;
        [[nodiscard]] static FName GetInterfaceOwningModule(FName InterfaceName) noexcept;

        // =============================================================
        // Diagnostic counts (snapshot under shared lock).
        //
        // Returned counts are point-in-time; they may have changed by
        // the next call. Intended for `xreflection status` CVars,
        // tests, and editor introspection panels.
        // =============================================================
        [[nodiscard]] static ::int32 GetClassCount() noexcept;
        [[nodiscard]] static ::int32 GetStructCount() noexcept;
        [[nodiscard]] static ::int32 GetScriptStructCount() noexcept;
        [[nodiscard]] static ::int32 GetEnumCount() noexcept;
        [[nodiscard]] static ::int32 GetInterfaceCount() noexcept;

        // =============================================================
        // Iteration -- visit every registered descriptor of a kind.
        //
        // The visitor is invoked once per registered descriptor; the
        // iteration order is REGISTRATION ORDER (the AllClasses /
        // AllStructs / AllEnums / AllInterfaces TArrays are append-
        // only modulo unregistration).
        //
        // The iteration acquires the registry's FRWLock in SHARED
        // mode for the duration of the walk. The visitor MUST NOT
        // call any Register* / Unregister* / OnModule* method (that
        // would deadlock on the same thread re-acquiring the
        // exclusive lock).
        //
        // The visitor MAY call any Find* method (Find* acquires the
        // shared lock which is recursive-acquirable on Win64 SRWLock
        // and POSIX pthread_rwlock with PTHREAD_RWLOCK_PREFER_WRITER
        // semantics). However, the iteration body is the
        // recommended place for read-only consumption; nested
        // Find*-while-iterating is a defensive backstop only.
        //
        // The visitor signature is `void (const FX*)` for the
        // appropriate FX type. Templated so the visitor is
        // monomorphised at each call site (no virtual / std::function
        // indirection).
        // =============================================================
        template <typename FnT>
        static void IterateAllClasses(FnT&& Visitor) noexcept;

        template <typename FnT>
        static void IterateAllStructs(FnT&& Visitor) noexcept;

        template <typename FnT>
        static void IterateAllEnums(FnT&& Visitor) noexcept;

        template <typename FnT>
        static void IterateAllInterfaces(FnT&& Visitor) noexcept;

        // =============================================================
        // EmptyForTesting -- reset the registry to empty.
        //
        // Intended for unit tests that want a clean slate before
        // exercising a registration sequence. Production code MUST NOT
        // call this (the singleton accumulates entries from every
        // loaded module; an EmptyForTesting in production would erase
        // another module's registrations).
        //
        // Acquires the registry's FRWLock in exclusive mode.
        // =============================================================
        static void EmptyForTesting() noexcept;

        // Non-copyable / non-movable (it IS the singleton).
        XReflectionRuntime()                                     = delete;
        ~XReflectionRuntime()                                    = delete;
        XReflectionRuntime(const XReflectionRuntime&)            = delete;
        XReflectionRuntime(XReflectionRuntime&&)                 = delete;
        XReflectionRuntime& operator=(const XReflectionRuntime&) = delete;
        XReflectionRuntime& operator=(XReflectionRuntime&&)      = delete;

    private:
        // =============================================================
        // Internal helpers consumed by the templated Iterate*<T> bodies.
        //
        // These helpers acquire the registry's shared lock, snapshot
        // the AllClasses / AllStructs / etc. TArrays into a local
        // contiguous buffer, release the lock, then walk the snapshot
        // calling the visitor. Snapshotting the TArrays (rather than
        // holding the lock across the visitor call) avoids deadlocks
        // if the visitor accidentally calls a Register* (the call
        // would acquire the exclusive lock; the iteration's shared
        // lock would block the exclusive acquisition, leading to
        // hang on the thread). The trade-off is one allocation per
        // iteration of count = AllClasses.Num() pointers; bounded
        // by the registered-type count (typically <5000 in a large
        // project per Master Plan budget).
        //
        // The helpers are declared here so the templated Iterate*
        // bodies (defined at end-of-header below) can call them; the
        // bodies live in the .cpp where TArray and FClass are fully
        // included.
        //
        // Each Get*Snapshot writes pointer-data + count to the caller-
        // provided OutBuffer (caller-allocated; size up to OutCapacity);
        // returns the count actually written. If the caller's buffer
        // is too small the return value indicates the required size and
        // OutBuffer is untouched -- the caller should grow and retry.
        // =============================================================
        static ::int32 GetClassSnapshot(const FClass** OutBuffer,    ::int32 OutCapacity) noexcept;
        static ::int32 GetStructSnapshot(const FStruct** OutBuffer,   ::int32 OutCapacity) noexcept;
        static ::int32 GetEnumSnapshot(const FEnum** OutBuffer,       ::int32 OutCapacity) noexcept;
        static ::int32 GetInterfaceSnapshot(const FInterface** OutBuffer, ::int32 OutCapacity) noexcept;
    };

    // -----------------------------------------------------------------
    // Templated Iterate* bodies.
    //
    // The iteration pattern:
    //   1. Call GetXxxSnapshot with a stack-allocated guess (32 entries).
    //   2. If the registry has more than 32 entries, retry with a
    //      heap-allocated buffer sized to the actual count.
    //   3. Walk the snapshot calling the visitor.
    //
    // The stack-allocation cap is intentional: 32 pointers fit easily
    // in any reasonable stack frame, and the vast majority of editor /
    // test iterations touch <32 types. Heap fallback is reserved for
    // large-project iteration paths.
    //
    // XCore-4b Subagent A FIX-A3: heap-fallback routes through
    // ::XCore::HAL::FMemory::MallocOrAbort / FMemory::Free with
    // FMemTag::Reflection attribution (Prime Directive: no raw
    // new[]/delete[] in engine code; memory must be accountable to
    // the per-tag tracking and leak-tracker subsystem).
    // -----------------------------------------------------------------

    template <typename FnT>
    void XReflectionRuntime::IterateAllClasses(FnT&& Visitor) noexcept
    {
        constexpr ::int32 kStackCap = 32;
        const FClass* StackBuffer[kStackCap];
        const ::int32 Count = GetClassSnapshot(StackBuffer, kStackCap);
        if (Count <= kStackCap)
        {
            for (::int32 I = 0; I < Count; ++I)
            {
                Visitor(StackBuffer[I]);
            }
            return;
        }
        // Heap fallback (FIX-A3): FMemory tag-attributed allocation.
        const FClass** HeapBuffer = static_cast<const FClass**>(
            ::XCore::HAL::FMemory::MallocOrAbort(
                static_cast<::SIZE_T>(Count) * sizeof(const FClass*),
                alignof(const FClass*),
                ::XCore::HAL::FMemTag::Reflection));
        const ::int32 Actual = GetClassSnapshot(HeapBuffer, Count);
        for (::int32 I = 0; I < Actual; ++I)
        {
            Visitor(HeapBuffer[I]);
        }
        ::XCore::HAL::FMemory::Free(HeapBuffer);
    }

    template <typename FnT>
    void XReflectionRuntime::IterateAllStructs(FnT&& Visitor) noexcept
    {
        constexpr ::int32 kStackCap = 32;
        const FStruct* StackBuffer[kStackCap];
        const ::int32 Count = GetStructSnapshot(StackBuffer, kStackCap);
        if (Count <= kStackCap)
        {
            for (::int32 I = 0; I < Count; ++I)
            {
                Visitor(StackBuffer[I]);
            }
            return;
        }
        const FStruct** HeapBuffer = static_cast<const FStruct**>(
            ::XCore::HAL::FMemory::MallocOrAbort(
                static_cast<::SIZE_T>(Count) * sizeof(const FStruct*),
                alignof(const FStruct*),
                ::XCore::HAL::FMemTag::Reflection));
        const ::int32 Actual = GetStructSnapshot(HeapBuffer, Count);
        for (::int32 I = 0; I < Actual; ++I)
        {
            Visitor(HeapBuffer[I]);
        }
        ::XCore::HAL::FMemory::Free(HeapBuffer);
    }

    template <typename FnT>
    void XReflectionRuntime::IterateAllEnums(FnT&& Visitor) noexcept
    {
        constexpr ::int32 kStackCap = 32;
        const FEnum* StackBuffer[kStackCap];
        const ::int32 Count = GetEnumSnapshot(StackBuffer, kStackCap);
        if (Count <= kStackCap)
        {
            for (::int32 I = 0; I < Count; ++I)
            {
                Visitor(StackBuffer[I]);
            }
            return;
        }
        const FEnum** HeapBuffer = static_cast<const FEnum**>(
            ::XCore::HAL::FMemory::MallocOrAbort(
                static_cast<::SIZE_T>(Count) * sizeof(const FEnum*),
                alignof(const FEnum*),
                ::XCore::HAL::FMemTag::Reflection));
        const ::int32 Actual = GetEnumSnapshot(HeapBuffer, Count);
        for (::int32 I = 0; I < Actual; ++I)
        {
            Visitor(HeapBuffer[I]);
        }
        ::XCore::HAL::FMemory::Free(HeapBuffer);
    }

    template <typename FnT>
    void XReflectionRuntime::IterateAllInterfaces(FnT&& Visitor) noexcept
    {
        constexpr ::int32 kStackCap = 32;
        const FInterface* StackBuffer[kStackCap];
        const ::int32 Count = GetInterfaceSnapshot(StackBuffer, kStackCap);
        if (Count <= kStackCap)
        {
            for (::int32 I = 0; I < Count; ++I)
            {
                Visitor(StackBuffer[I]);
            }
            return;
        }
        const FInterface** HeapBuffer = static_cast<const FInterface**>(
            ::XCore::HAL::FMemory::MallocOrAbort(
                static_cast<::SIZE_T>(Count) * sizeof(const FInterface*),
                alignof(const FInterface*),
                ::XCore::HAL::FMemTag::Reflection));
        const ::int32 Actual = GetInterfaceSnapshot(HeapBuffer, Count);
        for (::int32 I = 0; I < Actual; ++I)
        {
            Visitor(HeapBuffer[I]);
        }
        ::XCore::HAL::FMemory::Free(HeapBuffer);
    }

} // namespace XCore::Reflect
