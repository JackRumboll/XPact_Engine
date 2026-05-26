// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FProperty.h -- the FField subclass that anchors every reflected
// property (XCore-4b §5.3 + §5.4 + §11.2).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.3 ("FProperty base") + Section 5.4
// ("FProperty FakeVTable dispatch") + Section 11.2 layout row
// `FProperty: 104 bytes`.
//
// FProperty extends FField with:
//
//   * Type-shape fields (ArrayDim / ElementSize / Offset + alignment pad)
//   * EPropertyFlags (64-bit bitmask carrying replication / persistence
//     / C# binding bits per §5.3 + §5.8 + FIX-1 + FIX-18 + FIX-20)
//   * UE-equivalent pre-Iris replication metadata (RepIndex,
//     BlueprintReplicationCondition, RepNotifyFunc) per FIX-1
//   * Blueprint + Editor metadata slots (BlueprintFlags, EditFlags)
//   * Intra-struct linkage pointers (PropertyLinkNext, DestructorLinkNext)
//   * A SINGLE 8-byte DispatchTable pointer to a per-subclass FFakeVTable
//     in `.rodata` (Rev 2 FIX-2)
//
// LAYOUT (locked at 104 bytes per Contract Rev 13.8 §11.2 +
// XPACT_FPROPERTY_LAYOUT_TAG):
//
//   struct alignas(8) FProperty : FField {
//       // FField base @ 0-31 (32 bytes)
//       int32              ArrayDim;                     // 32  +4
//       int32              ElementSize;                  // 36  +4
//       int32              Offset;                       // 40  +4
//       uint32             _padTypeShape;                // 44  +4
//       EPropertyFlags     PropertyFlags;                // 48  +8
//       uint16             RepIndex;                     // 56  +2
//       ELifetimeCondition BlueprintReplicationCondition;// 58  +1
//       uint8              _padRepMetadata;              // 59  +1
//       uint32             _padRepMetadata2;             // 60  +4
//       FName              RepNotifyFunc;                // 64  +8
//       EBlueprintFlags    BlueprintFlags;               // 72  +4
//       EEditFlags         EditFlags;                    // 76  +4
//       FProperty*         PropertyLinkNext;             // 80  +8
//       FProperty*         DestructorLinkNext;           // 88  +8
//       const FFakeVTable* DispatchTable;                // 96  +8
//   };
//
// sizeof(FProperty) == 104 (FField 32 + FProperty body 72 = 104).
//
// HOT-RELOAD SAFETY (§5.4):
//
//   * No virtual methods. All dispatch is via DispatchTable -> FFakeVTable
//     -> Slots[ESlot::X].
//   * Standard-layout struct (FField is standard-layout, every FProperty
//     member is public + same access).
//   * The DispatchTable pointer is structurally immutable post-construction
//     -- the FProperty's FFieldClass owns the per-subclass FakeVTable
//     in .rodata, and the ctor reads the pointer from ClassPrivate at
//     construction time.
//
// DISPATCH WRAPPERS:
//
// FProperty exposes type-safe wrappers around the FFakeVTable slots
// (GetValue / SetValue / CopySingleValue / Identical / etc.). The
// wrappers:
//
//   1. Read DispatchTable (8 bytes off `this`).
//   2. Look up the slot in DispatchTable->Slots[ESlot::X].
//   3. Cast the type-erased `void(*)(void)` to the typed signature.
//   4. Invoke through the typed pointer with the caller's arguments.
//
// The wrappers are FORCEINLINE-hinted so the cost is one indexed load
// + one indirect call. Per spec §5.4.1: the fixed-table form is
// structurally one instruction faster than UE's variable-length form.
//
// FArchive + FStructProperty FORWARD-DECLARED:
//
// Two of the slot signatures reference types that land in later phases:
//
//   * FArchive lands in XSerialization (Layer 9; post-XCore-4b).
//   * FStructProperty lands in Phase 4b.4b.
//
// Both are forward-declared here. The corresponding wrapper methods
// (SerializeItem / NetSerializeItem / ContainsObjectReference) take
// the forward-declared types by pointer / reference; callers needing
// to dereference must include the full headers when they land.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Reflection/EBlueprintFlags.h"
#include "Reflection/EClassCastFlags.h"
#include "Reflection/EEditFlags.h"
#include "Reflection/ELifetimeCondition.h"
#include "Reflection/EPropertyFlags.h"
#include "Reflection/FFakeVTable.h"
#include "Reflection/FField.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FFieldVariant.h"
#include "Reflection/FName.h"

#include <cstddef>
#include <type_traits>

// Forward declarations for XCore-4a containers used in slot signatures.
//
// TArray is parameterised by (T, AllocatorT = DefaultAllocator) per
// XCore-4a §5.1; the forward declaration here must match the primary
// template so consumers can name `TArray<FStructProperty*>` without
// pulling the full TArray header into FProperty.h.
//
// FString is referenced by ExportText / ImportText slot signatures
// for plain-text round-trip; forward-declare here, full include at
// the .cpp site.
#include "Macros/XCoreFwd.h"

namespace XCore::Serialization
{
    // Forward declaration. FArchive lands in XSerialization (Layer 9;
    // post-XCore-4b). Slot signatures (SerializeItem / NetSerializeItem)
    // reference this type by reference / pointer; callers must include
    // FArchive.h once it ships.
    class FArchive;

    // Forward declaration. FPropertyTag is the serialised header for a
    // property value (name + type + size + flags). Used by
    // ConvertFromType for schema migration. Lands in XSerialization.
    struct FPropertyTag;
}

namespace XCore::Reflect
{
    // Forward declarations for slot signatures.
    //
    // FStructProperty lands in Phase 4b.4b (the container-aware subclass
    // for FVector / FRotator / FTransform values). The
    // ContainsObjectReference slot takes a TArray<const FStructProperty*>
    // to track encountered struct properties during GC reference walks.
    struct FStructProperty;

    // The "convert-from-type" result enum used by the ConvertFromType
    // dispatch slot (Rev 3 added per FIX-R2-HIGH-1). Returned by
    // FProperty::ConvertFromTypeImpl indicating whether the legacy
    // serialised type was successfully migrated to the runtime type.
    //
    // Per spec §5.4: "Returns `Converted`, `UseSerializeItem`, or
    // `CannotConvert`."
    enum class EConvertFromTypeResult : ::uint8
    {
        Converted        = 0,  // value migrated; consumer should advance to next prop
        UseSerializeItem = 1,  // no migration needed; consumer should call SerializeItem
        CannotConvert    = 2,  // migration failed; consumer should skip or error
    };

    // Forward declaration. FStruct lands in Phase 4b.5; the
    // ConvertFromType slot signature takes an FStruct* (the "defaults"
    // struct for the property's owner).
    struct FStruct;

    // -----------------------------------------------------------------
    // FProperty -- 104-byte FField subclass + reflection-property anchor.
    //
    // Per spec §5.3: alignas(8). NO virtual methods. Standard layout.
    //
    // The struct IS trivially copyable (every member is POD: pointer,
    // integer, FName, etc.). The defaulted copy/move constructors are
    // bitwise. Trivially destructible.
    //
    // CONSTRUCTION DISCIPLINE: subclass constructors invoke the
    // FProperty(InClass, InOwner, InName) protected constructor which
    // delegates to FField(InClass, InOwner, InName) and then populates
    // the DispatchTable pointer from InClass->FakeVTable. The remaining
    // fields (ArrayDim / Offset / PropertyFlags / etc.) are set by the
    // XHT codegen at FClass::Link time -- the FProperty constructor
    // does NOT take them.
    // -----------------------------------------------------------------
    struct alignas(8) FProperty : public FField
    {
        // ---- Type-shape fields (offsets 32-47) ----

        ::int32  ArrayDim;       // 32  +4   fixed-array dimension; 1 for non-array
        ::int32  ElementSize;    // 36  +4   single-element size in bytes
        ::int32  Offset;         // 40  +4   byte offset within owner struct/class
        ::uint32 _padTypeShape;  // 44  +4   pad to 8-byte boundary

        // ---- Property flags (offset 48; 8 bytes) ----

        EPropertyFlags PropertyFlags;                       // 48  +8

        // ---- UE-equivalent replication metadata source (FIX-1) ----

        ::uint16           RepIndex;                        // 56  +2   per-class repl index
        ELifetimeCondition BlueprintReplicationCondition;   // 58  +1   COND_None..COND_Never
        ::uint8            _padRepMetadata;                 // 59  +1   pad
        ::uint32           _padRepMetadata2;                // 60  +4   pad to 8-byte boundary
        FName              RepNotifyFunc;                   // 64  +8   FName of OnRep_ callback

        // ---- Editor / blueprint metadata (Layer 20 + Layer 21 populate) ----

        EBlueprintFlags BlueprintFlags;                     // 72  +4
        EEditFlags      EditFlags;                          // 76  +4

        // ---- Intra-struct linkage (matches UE PropertyLinkNext /
        //      DestructorLinkNext) ----

        FProperty* PropertyLinkNext;                        // 80  +8
        FProperty* DestructorLinkNext;                      // 88  +8

        // ---- Per-subclass FakeVTable pointer (FIX-2) ----
        //
        // Single 8-byte pointer to a per-subclass FFakeVTable in
        // `.rodata`. Replaces Rev 1's 40-byte inlined dispatch table.
        // Structurally redundant with ClassPrivate->FakeVTable (both
        // resolve to the same FFakeVTable address); the duplicate
        // exists so dispatch is ONE indexed load
        // (`this->DispatchTable->Slots[X]`) rather than TWO
        // (`this->ClassPrivate->FakeVTable->Slots[X]`).
        //
        // The redundancy is 8 bytes per FProperty instance and
        // saves one indirect load per dispatch -- net positive at
        // any reasonable property population.

        const FFakeVTable* DispatchTable;                   // 96  +8

        // -------------------------------------------------------------
        // Construction.
        //
        // The default ctor zero-initialises every field (the
        // "uninitialised" sentinel; an FProperty in this state is
        // structurally broken and SHOULD NOT be used). It exists so
        // constinit arrays of FProperty subclass storage can be
        // declared and later populated in-place by ConstructFn.
        //
        // The protected explicit ctor takes the FFieldClass + Owner +
        // Name and delegates to FField. The DispatchTable pointer is
        // read from InClass->FakeVTable at construction time. The
        // remaining fields default-initialise to zero / NAME_None;
        // XHT codegen or programmatic construction populates them
        // after the FProperty is constructed.
        // -------------------------------------------------------------

        // Default ctor: every field zero. Constexpr so constinit
        // aggregator paths (XHT-emitted .gen.cpp) can use it.
        constexpr FProperty() noexcept
            : FField()
            , ArrayDim(1)            // 1 = non-array default
            , ElementSize(0)
            , Offset(0)
            , _padTypeShape(0)
            , PropertyFlags(EPropertyFlags::CPF_None)
            , RepIndex(0)
            , BlueprintReplicationCondition(ELifetimeCondition::COND_None)
            , _padRepMetadata(0)
            , _padRepMetadata2(0)
            , RepNotifyFunc()        // NAME_None
            , BlueprintFlags(EBlueprintFlags::BPF_None)
            , EditFlags(EEditFlags::EF_None)
            , PropertyLinkNext(nullptr)
            , DestructorLinkNext(nullptr)
            , DispatchTable(nullptr)
        {
        }

        // Explicit ctor used by subclass constructors. Sets the FField
        // base (Class+Owner+Name) and reads the DispatchTable pointer
        // from InClass->FakeVTable. Remaining fields are zero / default.
        //
        // The ctor IS NOT constexpr because reading
        // `InClass->FakeVTable` through a pointer is not constexpr
        // (the FFieldClass instance is a runtime address). The XHT
        // codegen path uses brace-init with a pre-resolved
        // DispatchTable pointer for constinit data; programmatic
        // construction goes through this ctor.
        FProperty(const FFieldClass* InClass, FFieldVariant InOwner, FName InName) noexcept
            : FField(InClass, InOwner, InName)
            , ArrayDim(1)
            , ElementSize(0)
            , Offset(0)
            , _padTypeShape(0)
            , PropertyFlags(EPropertyFlags::CPF_None)
            , RepIndex(0)
            , BlueprintReplicationCondition(ELifetimeCondition::COND_None)
            , _padRepMetadata(0)
            , _padRepMetadata2(0)
            , RepNotifyFunc()
            , BlueprintFlags(EBlueprintFlags::BPF_None)
            , EditFlags(EEditFlags::EF_None)
            , PropertyLinkNext(nullptr)
            , DestructorLinkNext(nullptr)
            , DispatchTable(InClass != nullptr ? InClass->FakeVTable : nullptr)
        {
        }

        // Defaulted copy/move. The FField base + FProperty body are
        // all POD; the defaulted operations are bitwise.
        FProperty(const FProperty&) noexcept            = default;
        FProperty(FProperty&&) noexcept                 = default;
        FProperty& operator=(const FProperty&) noexcept = default;
        FProperty& operator=(FProperty&&) noexcept      = default;
        ~FProperty() noexcept                           = default;

        // -------------------------------------------------------------
        // Accessors (mirroring UE FProperty surface).
        // -------------------------------------------------------------

        [[nodiscard]] XPACT_FORCEINLINE constexpr ::int32 GetArrayDim() const noexcept
        {
            return ArrayDim;
        }

        [[nodiscard]] XPACT_FORCEINLINE constexpr ::int32 GetElementSize() const noexcept
        {
            return ElementSize;
        }

        [[nodiscard]] XPACT_FORCEINLINE constexpr ::int32 GetOffset_ForInternal() const noexcept
        {
            return Offset;
        }

        [[nodiscard]] XPACT_FORCEINLINE constexpr EPropertyFlags GetPropertyFlags() const noexcept
        {
            return PropertyFlags;
        }

        [[nodiscard]] XPACT_FORCEINLINE constexpr ::uint16 GetRepIndex() const noexcept
        {
            return RepIndex;
        }

        [[nodiscard]] XPACT_FORCEINLINE constexpr ELifetimeCondition GetReplicationCondition() const noexcept
        {
            return BlueprintReplicationCondition;
        }

        [[nodiscard]] XPACT_FORCEINLINE constexpr FName GetRepNotifyFunc() const noexcept
        {
            return RepNotifyFunc;
        }

        [[nodiscard]] XPACT_FORCEINLINE constexpr EBlueprintFlags GetBlueprintFlags() const noexcept
        {
            return BlueprintFlags;
        }

        [[nodiscard]] XPACT_FORCEINLINE constexpr EEditFlags GetEditFlags() const noexcept
        {
            return EditFlags;
        }

        [[nodiscard]] XPACT_FORCEINLINE constexpr const FFakeVTable* GetDispatchTable() const noexcept
        {
            return DispatchTable;
        }

        // -------------------------------------------------------------
        // Flag predicates.
        // -------------------------------------------------------------

        [[nodiscard]] XPACT_FORCEINLINE constexpr bool HasAnyPropertyFlags(EPropertyFlags Flags) const noexcept
        {
            return (PropertyFlags & Flags) != EPropertyFlags::CPF_None;
        }

        [[nodiscard]] XPACT_FORCEINLINE constexpr bool HasAllPropertyFlags(EPropertyFlags Flags) const noexcept
        {
            return (PropertyFlags & Flags) == Flags;
        }

        // -------------------------------------------------------------
        // ContainerPtrToValuePtr -- compute the address of this
        // property's value within a containing struct's instance.
        //
        // Given a pointer to an instance of the containing struct,
        // returns `BasePtr + Offset + ElementIndex * ElementSize`
        // (the address of the ElementIndex-th element of the
        // fixed-array, or the address of the value for non-array
        // properties with ElementIndex == 0).
        //
        // Mirrors UE's `FProperty::ContainerPtrToValuePtr`
        // (`UnrealType.h:1109`). The function is O(1); no validation
        // beyond a Debug/Dev XPACT_CHECK that ElementIndex is in
        // [0, ArrayDim).
        // -------------------------------------------------------------

        [[nodiscard]] XPACT_FORCEINLINE void* ContainerPtrToValuePtr(
            void*  ContainerPtr,
            ::int32 ElementIndex = 0) const noexcept
        {
            XPACT_CHECK(ContainerPtr != nullptr);
            XPACT_CHECK(ElementIndex >= 0 && ElementIndex < ArrayDim);

            ::uint8* BytePtr = static_cast<::uint8*>(ContainerPtr);
            return BytePtr + Offset + ElementIndex * ElementSize;
        }

        [[nodiscard]] XPACT_FORCEINLINE const void* ContainerPtrToValuePtr(
            const void* ContainerPtr,
            ::int32     ElementIndex = 0) const noexcept
        {
            XPACT_CHECK(ContainerPtr != nullptr);
            XPACT_CHECK(ElementIndex >= 0 && ElementIndex < ArrayDim);

            const ::uint8* BytePtr = static_cast<const ::uint8*>(ContainerPtr);
            return BytePtr + Offset + ElementIndex * ElementSize;
        }

        // -------------------------------------------------------------
        // Dispatch wrappers.
        //
        // Each wrapper:
        //
        //   1. Reads the DispatchTable pointer (single 8-byte load off
        //      `this`).
        //   2. Looks up the slot in DispatchTable->Slots[ESlot::X].
        //      The HasSlot() probe is folded into the wrapper so
        //      callers don't have to.
        //   3. Casts the type-erased function pointer to the typed
        //      signature.
        //   4. Invokes through the typed pointer.
        //
        // The wrappers are NOT virtual; the dispatch is through the
        // FFakeVTable in .rodata.
        //
        // SIGNATURE NOTE: the spec lists slot signatures with a leading
        // `FArchive&` parameter for serialization slots. FArchive lands
        // in XSerialization (Layer 9); Phase 4b.4a forward-declares it
        // and DEFERS the SerializeItem / NetSerializeItem wrapper
        // bodies. The wrappers are declared here so the API surface is
        // present; calling them at Phase 4b.4a returns false (the
        // declared signatures take the forward-declared type by
        // pointer / reference and only the no-archive paths are
        // exercised by the Phase 4b.4a tests).
        // -------------------------------------------------------------

        // ESlot::GetValue --
        //   void(*)(const void* Instance, int32 ElementIndex, void* OutValue)
        //
        // Read element ElementIndex from the property's slot on
        // Instance into OutValue. Caller-owned buffer.
        using FGetValueFn = void (*)(const void* Instance, ::int32 ElementIndex, void* OutValue);

        XPACT_FORCEINLINE void GetValue(
            const void* Instance,
            void*       OutValue,
            ::int32     ElementIndex = 0) const noexcept
        {
            XPACT_CHECK(DispatchTable != nullptr);
            const auto Fn = DispatchTable->GetSlot<FGetValueFn>(ESlot::GetValue);
            XPACT_CHECK(Fn != nullptr);
            Fn(Instance, ElementIndex, OutValue);
        }

        // ESlot::SetValue --
        //   void(*)(void* Instance, int32 ElementIndex, const void* InValue)
        using FSetValueFn = void (*)(void* Instance, ::int32 ElementIndex, const void* InValue);

        XPACT_FORCEINLINE void SetValue(
            void*       Instance,
            const void* InValue,
            ::int32     ElementIndex = 0) const noexcept
        {
            XPACT_CHECK(DispatchTable != nullptr);
            const auto Fn = DispatchTable->GetSlot<FSetValueFn>(ESlot::SetValue);
            XPACT_CHECK(Fn != nullptr);
            Fn(Instance, ElementIndex, InValue);
        }

        // ESlot::CopySingleValue --
        //   void(*)(void* Dest, const void* Src)
        using FCopySingleValueFn = void (*)(void* Dest, const void* Src);

        XPACT_FORCEINLINE void CopySingleValue(void* Dest, const void* Src) const noexcept
        {
            XPACT_CHECK(DispatchTable != nullptr);
            const auto Fn = DispatchTable->GetSlot<FCopySingleValueFn>(ESlot::CopySingleValue);
            XPACT_CHECK(Fn != nullptr);
            Fn(Dest, Src);
        }

        // ESlot::CopyCompleteValue --
        //   void(*)(void* Dest, const void* Src, int32 Count)
        using FCopyCompleteValueFn = void (*)(void* Dest, const void* Src, ::int32 Count);

        XPACT_FORCEINLINE void CopyCompleteValue(void* Dest, const void* Src, ::int32 Count) const noexcept
        {
            XPACT_CHECK(DispatchTable != nullptr);
            const auto Fn = DispatchTable->GetSlot<FCopyCompleteValueFn>(ESlot::CopyCompleteValue);
            XPACT_CHECK(Fn != nullptr);
            Fn(Dest, Src, Count);
        }

        // ESlot::InitializeValue --
        //   void(*)(void* Dest, int32 Count)
        using FInitializeValueFn = void (*)(void* Dest, ::int32 Count);

        XPACT_FORCEINLINE void InitializeValue(void* Dest, ::int32 Count = 1) const noexcept
        {
            XPACT_CHECK(DispatchTable != nullptr);
            const auto Fn = DispatchTable->GetSlot<FInitializeValueFn>(ESlot::InitializeValue);
            XPACT_CHECK(Fn != nullptr);
            Fn(Dest, Count);
        }

        // ESlot::DestroyValue --
        //   void(*)(void* Dest, int32 Count)
        using FDestroyValueFn = void (*)(void* Dest, ::int32 Count);

        XPACT_FORCEINLINE void DestroyValue(void* Dest, ::int32 Count = 1) const noexcept
        {
            XPACT_CHECK(DispatchTable != nullptr);
            const auto Fn = DispatchTable->GetSlot<FDestroyValueFn>(ESlot::DestroyValue);
            XPACT_CHECK(Fn != nullptr);
            Fn(Dest, Count);
        }

        // ESlot::Identical --
        //   bool(*)(const void* A, const void* B, uint32 PortFlags)
        using FIdenticalFn = bool (*)(const void* A, const void* B, ::uint32 PortFlags);

        [[nodiscard]] XPACT_FORCEINLINE bool Identical(
            const void* A,
            const void* B,
            ::uint32    PortFlags = 0) const noexcept
        {
            XPACT_CHECK(DispatchTable != nullptr);
            const auto Fn = DispatchTable->GetSlot<FIdenticalFn>(ESlot::Identical);
            XPACT_CHECK(Fn != nullptr);
            return Fn(A, B, PortFlags);
        }

        // ESlot::SerializeItem --
        //   void(*)(FArchive& Ar, void* Value, const void* Defaults)
        //
        // Body deferred until XSerialization (Layer 9) lands. Phase
        // 4b.4a populates this slot with a nullptr-safe stub on every
        // subclass; the call-site wrapper here probes HasSlot before
        // dispatch, so calling SerializeItem at Phase 4b.4a is a no-op.
        using FSerializeItemFn = void (*)(::XCore::Serialization::FArchive& Ar,
                                          void* Value,
                                          const void* Defaults);

        XPACT_FORCEINLINE void SerializeItem(
            ::XCore::Serialization::FArchive& Ar,
            void*       Value,
            const void* Defaults = nullptr) const noexcept
        {
            XPACT_CHECK(DispatchTable != nullptr);
            if (!DispatchTable->HasSlot(ESlot::SerializeItem))
            {
                return;  // slot not populated; no-op
            }
            const auto Fn = DispatchTable->GetSlot<FSerializeItemFn>(ESlot::SerializeItem);
            Fn(Ar, Value, Defaults);
        }

        // ESlot::NetSerializeItem --
        //   bool(*)(FArchive& Ar, class UPackageMap* Map, void* Value)
        //
        // Phase 4b.4a Note: UPackageMap is a UE-side type; XPact's
        // equivalent (XPackageMap or XNetSerializeContext) lands at
        // XNetworking time. For the Phase 4b.4a forward-declared
        // signature we use `void*` for the map parameter so the slot
        // signature is stable and the XSerialization landing can
        // refine the type without breaking the FFakeVTable layout.
        using FNetSerializeItemFn = bool (*)(::XCore::Serialization::FArchive& Ar,
                                             void* Map,
                                             void* Value);

        XPACT_FORCEINLINE bool NetSerializeItem(
            ::XCore::Serialization::FArchive& Ar,
            void* Map,
            void* Value) const noexcept
        {
            XPACT_CHECK(DispatchTable != nullptr);
            if (!DispatchTable->HasSlot(ESlot::NetSerializeItem))
            {
                return false;  // slot not populated; reject
            }
            const auto Fn = DispatchTable->GetSlot<FNetSerializeItemFn>(ESlot::NetSerializeItem);
            return Fn(Ar, Map, Value);
        }

        // ESlot::ContainsObjectReference --
        //   bool(*)(TArray<const FStructProperty*>& EncounteredStructProps)
        //
        // Used by FStruct::ObjectRefProperties population (FIX-13) at
        // FClass::Link time to walk every reflected property and
        // determine which contain XObject references. The
        // EncounteredStructProps TArray tracks nested FStructProperty
        // chains to avoid infinite recursion on circular struct
        // definitions.
        //
        // FStructProperty forward-declared above; the corresponding
        // subclass lands at Phase 4b.4b.
        using FContainsObjectReferenceFn = bool (*)(
            ::XCore::TArray<const FStructProperty*>& EncounteredStructProps);

        [[nodiscard]] XPACT_FORCEINLINE bool ContainsObjectReference(
            ::XCore::TArray<const FStructProperty*>& EncounteredStructProps) const noexcept
        {
            XPACT_CHECK(DispatchTable != nullptr);
            if (!DispatchTable->HasSlot(ESlot::ContainsObjectReference))
            {
                return false;  // slot not populated; primitive types return false
            }
            const auto Fn = DispatchTable->GetSlot<FContainsObjectReferenceFn>(
                ESlot::ContainsObjectReference);
            return Fn(EncounteredStructProps);
        }

        // ESlot::ExportText --
        //   void(*)(FString& OutStr, const void* PropertyValue,
        //           const void* DefaultValue, int32 PortFlags)
        using FExportTextFn = void (*)(::XCore::FString& OutStr,
                                       const void* PropertyValue,
                                       const void* DefaultValue,
                                       ::int32 PortFlags);

        XPACT_FORCEINLINE void ExportText(
            ::XCore::FString& OutStr,
            const void* PropertyValue,
            const void* DefaultValue = nullptr,
            ::int32     PortFlags    = 0) const noexcept
        {
            XPACT_CHECK(DispatchTable != nullptr);
            if (!DispatchTable->HasSlot(ESlot::ExportText))
            {
                return;
            }
            const auto Fn = DispatchTable->GetSlot<FExportTextFn>(ESlot::ExportText);
            Fn(OutStr, PropertyValue, DefaultValue, PortFlags);
        }

        // ESlot::ImportText --
        //   const char*(*)(const char* Buffer, void* PropertyValue, int32 PortFlags)
        using FImportTextFn = const char* (*)(const char* Buffer,
                                              void* PropertyValue,
                                              ::int32 PortFlags);

        XPACT_FORCEINLINE const char* ImportText(
            const char* Buffer,
            void*       PropertyValue,
            ::int32     PortFlags = 0) const noexcept
        {
            XPACT_CHECK(DispatchTable != nullptr);
            if (!DispatchTable->HasSlot(ESlot::ImportText))
            {
                return Buffer;  // no advance
            }
            const auto Fn = DispatchTable->GetSlot<FImportTextFn>(ESlot::ImportText);
            return Fn(Buffer, PropertyValue, PortFlags);
        }

        // ESlot::GetValueTypeHash --
        //   uint64(*)(const void* PropertyValue)
        using FGetValueTypeHashFn = ::uint64 (*)(const void* PropertyValue);

        [[nodiscard]] XPACT_FORCEINLINE ::uint64 GetValueTypeHash(
            const void* PropertyValue) const noexcept
        {
            XPACT_CHECK(DispatchTable != nullptr);
            if (!DispatchTable->HasSlot(ESlot::GetValueTypeHash))
            {
                return 0;  // not TMap-key-hashable
            }
            const auto Fn = DispatchTable->GetSlot<FGetValueTypeHashFn>(ESlot::GetValueTypeHash);
            return Fn(PropertyValue);
        }

        // ESlot::AppendToSchemaHash --
        //   void(*)(FBlake3& Builder, bool bSkipEditorOnly)
        //
        // FBlake3 is the XCore-4a hash type; forward-declared in
        // <Hash/FBlake3.h>. The slot signature uses a forward-declared
        // FBlake3 reference; the .cpp call site MUST include the
        // FBlake3 header to dispatch.
        //
        // For Phase 4b.4a the slot is populated by every subclass with
        // a body that appends a stable subclass-identifying byte
        // sequence to the FBlake3 builder. The full SchemaHash
        // discipline lands at Phase 4b.6 (XReflectionRuntime) when the
        // canonical byte stream layout is finalised.
        // (The function-pointer storage at Phase 4b.4a uses void*; the
        // type-safe wrapper lives at Phase 4b.6 alongside the full
        // SchemaHash integration.)

        // ESlot::ConvertFromType (Rev 3 added per FIX-R2-HIGH-1) --
        //   EConvertFromTypeResult(*)(const FPropertyTag& Tag, FArchive& Ar,
        //       void* PropertyValue, FStruct* DefaultsStruct, const void* Defaults)
        //
        // Schema-migration hook called when XSerialization loads an
        // asset whose serialized property tag doesn't match the
        // runtime type (e.g., int32 -> int64, FName -> FString).
        // Defer-implement at Phase 4b.4a per XSerialization landing
        // (Layer 9 owns the migration paths).
        using FConvertFromTypeFn = EConvertFromTypeResult (*)(
            const ::XCore::Serialization::FPropertyTag& Tag,
            ::XCore::Serialization::FArchive& Ar,
            void* PropertyValue,
            FStruct* DefaultsStruct,
            const void* Defaults);

        XPACT_FORCEINLINE EConvertFromTypeResult ConvertFromType(
            const ::XCore::Serialization::FPropertyTag& Tag,
            ::XCore::Serialization::FArchive& Ar,
            void* PropertyValue,
            FStruct* DefaultsStruct = nullptr,
            const void* Defaults    = nullptr) const noexcept
        {
            XPACT_CHECK(DispatchTable != nullptr);
            if (!DispatchTable->HasSlot(ESlot::ConvertFromType))
            {
                return EConvertFromTypeResult::UseSerializeItem;
            }
            const auto Fn = DispatchTable->GetSlot<FConvertFromTypeFn>(ESlot::ConvertFromType);
            return Fn(Tag, Ar, PropertyValue, DefaultsStruct, Defaults);
        }

        // -------------------------------------------------------------
        // StaticClass -- the base FProperty's FFieldClass descriptor.
        //
        // Returns the runtime FFieldClass for the FProperty base type.
        // Used by the Cast<FProperty>(FField*) helper; every FProperty
        // subclass overrides this via the standard Field/Class hidden-
        // friend pattern (see the F{X}Property.h headers).
        //
        // The FFieldClass for the base FProperty itself is the
        // kFPropertyStaticClass instance defined in FProperty.cpp.
        // CastFlags == kFProperty (the parent bit) so any FProperty
        // subclass's CastFlags-AND-kFProperty test returns nonzero.
        // -------------------------------------------------------------
        static const FFieldClass* StaticClass() noexcept;
    };

    // ---------------------------------------------------------------------
    // ABI locks (Contract Rev 13.8 §11.2 + XPACT_FPROPERTY_LAYOUT_TAG).
    // ---------------------------------------------------------------------
    static_assert(sizeof(FProperty) == 104,
                  "FProperty ABI lock: must be exactly 104 bytes "
                  "(32 FField base + 72 FProperty body; XPACT_FPROPERTY_LAYOUT_TAG)");
    static_assert(alignof(FProperty) == 8,
                  "FProperty ABI lock: 8-byte alignment per §5.3 alignas(8)");

    // Member offsets locked per §11.2.
    static_assert(offsetof(FProperty, ArrayDim)                       == 32,
                  "FProperty ABI lock: ArrayDim at offset 32");
    static_assert(offsetof(FProperty, ElementSize)                    == 36,
                  "FProperty ABI lock: ElementSize at offset 36");
    static_assert(offsetof(FProperty, Offset)                         == 40,
                  "FProperty ABI lock: Offset at offset 40");
    static_assert(offsetof(FProperty, PropertyFlags)                  == 48,
                  "FProperty ABI lock: PropertyFlags at offset 48");
    static_assert(offsetof(FProperty, RepIndex)                       == 56,
                  "FProperty ABI lock: RepIndex at offset 56");
    static_assert(offsetof(FProperty, BlueprintReplicationCondition)  == 58,
                  "FProperty ABI lock: BlueprintReplicationCondition at offset 58");
    static_assert(offsetof(FProperty, RepNotifyFunc)                  == 64,
                  "FProperty ABI lock: RepNotifyFunc at offset 64");
    static_assert(offsetof(FProperty, BlueprintFlags)                 == 72,
                  "FProperty ABI lock: BlueprintFlags at offset 72");
    static_assert(offsetof(FProperty, EditFlags)                      == 76,
                  "FProperty ABI lock: EditFlags at offset 76");
    static_assert(offsetof(FProperty, PropertyLinkNext)               == 80,
                  "FProperty ABI lock: PropertyLinkNext at offset 80");
    static_assert(offsetof(FProperty, DestructorLinkNext)             == 88,
                  "FProperty ABI lock: DestructorLinkNext at offset 88");
    static_assert(offsetof(FProperty, DispatchTable)                  == 96,
                  "FProperty ABI lock: DispatchTable at offset 96");

    // Type traits.
    //
    // is_standard_layout_v is NOT asserted on FProperty (or any
    // FProperty subclass). Per [class]/7, a derived class with
    // non-static data members whose base class ALSO has non-static
    // data members is NOT standard-layout. FField has 4 data members;
    // FProperty adds 13 more. The class is therefore not standard-
    // layout under the strict C++ rule.
    //
    // The offsetof() usage below is `conditionally-supported` for
    // non-standard-layout types per [support.types]/4, but every
    // major compiler (MSVC, Clang, GCC) supports it unconditionally
    // when the layout is single-inheritance + POD-throughout. The
    // implementation discipline is: every FField subclass uses
    // single inheritance only, no virtual functions, only POD
    // members. That gives us a well-defined memory layout even
    // without formal standard-layout status.
    //
    // is_trivially_copyable_v AND is_trivially_destructible_v ARE
    // asserted -- those traits DO hold for FProperty because all
    // members are POD and the defaulted copy/move/destructor are
    // bitwise.
    static_assert(::std::is_trivially_copyable_v<FProperty>,
                  "FProperty must be trivially copyable (every member is POD)");
    static_assert(::std::is_trivially_destructible_v<FProperty>,
                  "FProperty must be trivially destructible (no per-instance teardown)");

    // ---------------------------------------------------------------------
    // The base "FProperty" class -- the root of the FProperty hierarchy.
    //
    // Every FProperty subclass's FFieldClass has SuperClass pointing at
    // this. CastFlags == EClassCastFlags::kFProperty so subclasses that
    // CastFlags-OR-include the parent bit can short-circuit IsA checks.
    //
    // The declaration is extern; the definition lives in FProperty.cpp.
    // ---------------------------------------------------------------------
    extern FFieldClass kFPropertyStaticClass;

    // ---------------------------------------------------------------------
    // GetFPropertyStaticClass -- the accessor that lazy-initialises the
    // base FProperty FFieldClass's Name slot to FName("FProperty") on
    // first call and returns the resolved descriptor.
    //
    // Mirrors the Phase 4b.3 GetFieldStaticClass() pattern; same lazy-
    // init posture and thread-safety guarantees via the C++ runtime
    // static-init lock.
    // ---------------------------------------------------------------------
    const FFieldClass& GetFPropertyStaticClass() noexcept;

} // namespace XCore::Reflect
