// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FWeakObjectProperty.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4b.
// Weak XObject pointer property.
// =====================================================================
//
// FWeakObjectProperty's value slot is an 8-byte FWeakObjectPtr value.
// The dispatch slots operate on FWeakObjectPtr-sized chunks (8 bytes
// at Phase 4b.4b; the System 5 full impl preserves the 8-byte size).
//
//   * GetValue / SetValue                -- copy 8 bytes (FWeakObjectPtr).
//   * CopySingleValue / CopyCompleteValue -- copy N * 8 bytes.
//   * InitializeValue                    -- zero N * 8 bytes (null weak).
//   * DestroyValue                       -- no-op (no owned resources).
//   * Identical                          -- bytewise compare.
//   * GetValueTypeHash                   -- hash the weak-ptr bits.
//   * ContainsObjectReference            -- FALSE (weak refs are NOT
//                                          GC roots; the slot does
//                                          NOT contribute reachability).
//   * ExportText / ImportText            -- stubs at Phase 4b.4b.
//   * SerializeItem / NetSerializeItem   -- nullptr (FArchive lands
//                                          at XSerialization Layer 9).
//   * AppendToSchemaHash / ConvertFromType -- nullptr.
//
// =====================================================================

#include "Reflection/FWeakObjectProperty.h"

#include "Hash/FXxh3.h"
#include "Reflection/EClassCastFlags.h"
#include "Reflection/FFakeVTable.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FName.h"
#include "Reflection/FProperty.h"

#include <cstring>
#include <new>

namespace XCore::Reflect
{

namespace
{
    void GetValueSlot(const void* Instance, ::int32 ElementIndex, void* OutValue) noexcept
    {
        const FWeakObjectPtr* Src =
            static_cast<const FWeakObjectPtr*>(Instance) + ElementIndex;
        ::std::memcpy(OutValue, Src, sizeof(FWeakObjectPtr));
    }

    void SetValueSlot(void* Instance, ::int32 ElementIndex, const void* InValue) noexcept
    {
        FWeakObjectPtr* Dest = static_cast<FWeakObjectPtr*>(Instance) + ElementIndex;
        ::std::memcpy(Dest, InValue, sizeof(FWeakObjectPtr));
    }

    void CopySingleValueSlot(void* Dest, const void* Src) noexcept
    {
        ::std::memcpy(Dest, Src, sizeof(FWeakObjectPtr));
    }

    void CopyCompleteValueSlot(void* Dest, const void* Src, ::int32 Count) noexcept
    {
        ::std::memcpy(Dest, Src, sizeof(FWeakObjectPtr) * static_cast<::size_t>(Count));
    }

    void InitializeValueSlot(void* Dest, ::int32 Count) noexcept
    {
        ::std::memset(Dest, 0, sizeof(FWeakObjectPtr) * static_cast<::size_t>(Count));
    }

    void DestroyValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
        // FWeakObjectPtr is POD; no destructor needed.
    }

    bool IdenticalSlot(const void* A, const void* B, ::uint32 /*PortFlags*/) noexcept
    {
        return ::std::memcmp(A, B, sizeof(FWeakObjectPtr)) == 0;
    }

    ::uint64 GetValueTypeHashSlot(const void* PropertyValue) noexcept
    {
        return ::XCore::Hash::FXxh3::Hash64(PropertyValue, sizeof(FWeakObjectPtr));
    }

    // -----------------------------------------------------------------
    // ContainsObjectReferenceSlot -- weak refs are NOT GC roots.
    //
    // The slot returns FALSE so the GC walker does NOT treat
    // FWeakObjectProperty slots as reachable-from-root pointers. The
    // weak-ref fixup phase (post-GC) still consults FWeakObjectProperty
    // slots to null-out references to collected XObjects, but that
    // fixup walk uses a different traversal (XCoreXObject System 5).
    // -----------------------------------------------------------------
    bool ContainsObjectReferenceSlot(
        ::XCore::TArray<const FStructProperty*>& /*EncounteredStructProps*/) noexcept
    {
        return false;
    }

    constexpr ::uint32 kWeakObjectCapabilities =
          CapabilityBit(ESlot::GetValue)
        | CapabilityBit(ESlot::SetValue)
        | CapabilityBit(ESlot::CopySingleValue)
        | CapabilityBit(ESlot::CopyCompleteValue)
        | CapabilityBit(ESlot::InitializeValue)
        | CapabilityBit(ESlot::DestroyValue)
        | CapabilityBit(ESlot::Identical)
        | CapabilityBit(ESlot::ContainsObjectReference)
        | CapabilityBit(ESlot::GetValueTypeHash);

} // anonymous

constinit const FFakeVTable kFWeakObjectPropertyFakeVTable{
    /* Capabilities    */ kWeakObjectCapabilities,
    /* _reservedHeader */ 0U,
    /* Slots           */ {
        (void(*)(void))&GetValueSlot,
        (void(*)(void))&SetValueSlot,
        (void(*)(void))&CopySingleValueSlot,
        (void(*)(void))&CopyCompleteValueSlot,
        (void(*)(void))&InitializeValueSlot,
        (void(*)(void))&DestroyValueSlot,
        (void(*)(void))&IdenticalSlot,
        nullptr,                                          // 7  SerializeItem
        nullptr,                                          // 8  NetSerializeItem
        (void(*)(void))&ContainsObjectReferenceSlot,      // 9  ContainsObjectReference
        nullptr,                                          // 10 ExportText
        nullptr,                                          // 11 ImportText
        (void(*)(void))&GetValueTypeHashSlot,
        nullptr,                                          // 13 AppendToSchemaHash
        nullptr,                                          // 14 ConvertFromType
    },
};

constinit FFieldClass kFWeakObjectPropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFWeakObjectProperty
                   | EClassCastFlags::kFObjectPropertyBase
                   | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FWeakObjectProperty::ConstructFn,
    /* FakeVTable */ &kFWeakObjectPropertyFakeVTable,
};

FWeakObjectProperty::FWeakObjectProperty(FFieldVariant InOwner, FName InName,
                                         FClass* InPropertyClass) noexcept
    : FProperty(&kFWeakObjectPropertyStaticClass, InOwner, InName)
    , PropertyClass(InPropertyClass)
{
    ElementSize = static_cast<::int32>(sizeof(FWeakObjectPtr));
}

void FWeakObjectProperty::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FWeakObjectProperty(Owner, Name);
}

const FFieldClass* FWeakObjectProperty::StaticClass() noexcept
{
    return &GetFWeakObjectPropertyStaticClass();
}

const FFieldClass& GetFWeakObjectPropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFWeakObjectPropertyStaticClass.Name = FName("FWeakObjectProperty");
        return true;
    }();
    (void)Init;
    return kFWeakObjectPropertyStaticClass;
}

} // namespace XCore::Reflect
