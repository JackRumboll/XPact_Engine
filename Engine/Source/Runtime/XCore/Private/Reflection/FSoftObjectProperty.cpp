// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FSoftObjectProperty.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4b.
// Soft asset reference property (Rev 2 MVP add per FIX-3).
// =====================================================================
//
// FSoftObjectProperty's value slot is an 8-byte FSoftObjectPath value
// (placeholder type; System 5 full impl preserves 8-byte size).
//
//   * GetValue / SetValue                -- copy 8 bytes (FSoftObjectPath).
//   * CopySingleValue / CopyCompleteValue -- copy N * 8 bytes.
//   * InitializeValue                    -- zero N * 8 bytes (null path).
//   * DestroyValue                       -- no-op.
//   * Identical                          -- bytewise compare.
//   * GetValueTypeHash                   -- hash the path bits.
//   * ContainsObjectReference            -- FALSE (soft refs are NOT
//                                          GC roots; the asset cache
//                                          owns the loaded asset's
//                                          lifetime).
//   * ExportText / ImportText            -- stubs at Phase 4b.4b.
//   * SerializeItem / NetSerializeItem   -- nullptr.
//   * AppendToSchemaHash / ConvertFromType -- nullptr.
//
// =====================================================================

#include "Reflection/FSoftObjectProperty.h"

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
        const FSoftObjectPath* Src =
            static_cast<const FSoftObjectPath*>(Instance) + ElementIndex;
        ::std::memcpy(OutValue, Src, sizeof(FSoftObjectPath));
    }

    void SetValueSlot(void* Instance, ::int32 ElementIndex, const void* InValue) noexcept
    {
        FSoftObjectPath* Dest = static_cast<FSoftObjectPath*>(Instance) + ElementIndex;
        ::std::memcpy(Dest, InValue, sizeof(FSoftObjectPath));
    }

    void CopySingleValueSlot(void* Dest, const void* Src) noexcept
    {
        ::std::memcpy(Dest, Src, sizeof(FSoftObjectPath));
    }

    void CopyCompleteValueSlot(void* Dest, const void* Src, ::int32 Count) noexcept
    {
        ::std::memcpy(Dest, Src, sizeof(FSoftObjectPath) * static_cast<::size_t>(Count));
    }

    void InitializeValueSlot(void* Dest, ::int32 Count) noexcept
    {
        ::std::memset(Dest, 0, sizeof(FSoftObjectPath) * static_cast<::size_t>(Count));
    }

    void DestroyValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
        // FSoftObjectPath placeholder is POD; no destructor needed.
        // System 5 full impl preserves trivially-destructible semantics
        // (the path bytes themselves are owned by the asset system,
        // not the property slot).
    }

    bool IdenticalSlot(const void* A, const void* B, ::uint32 /*PortFlags*/) noexcept
    {
        return ::std::memcmp(A, B, sizeof(FSoftObjectPath)) == 0;
    }

    ::uint64 GetValueTypeHashSlot(const void* PropertyValue) noexcept
    {
        return ::XCore::Hash::FXxh3::Hash64(PropertyValue, sizeof(FSoftObjectPath));
    }

    // -----------------------------------------------------------------
    // ContainsObjectReferenceSlot -- soft refs are NOT GC roots.
    //
    // Soft asset references resolve on demand via the asset system; the
    // GC walker does NOT treat FSoftObjectProperty slots as reachable
    // pointers. Returns false.
    // -----------------------------------------------------------------
    bool ContainsObjectReferenceSlot(
        ::XCore::TArray<const FStructProperty*>& /*EncounteredStructProps*/) noexcept
    {
        return false;
    }

    constexpr ::uint32 kSoftObjectCapabilities =
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

constinit const FFakeVTable kFSoftObjectPropertyFakeVTable{
    /* Capabilities    */ kSoftObjectCapabilities,
    /* _reservedHeader */ 0U,
    /* Slots           */ {
        (void(*)(void))&GetValueSlot,
        (void(*)(void))&SetValueSlot,
        (void(*)(void))&CopySingleValueSlot,
        (void(*)(void))&CopyCompleteValueSlot,
        (void(*)(void))&InitializeValueSlot,
        (void(*)(void))&DestroyValueSlot,
        (void(*)(void))&IdenticalSlot,
        nullptr, nullptr,
        (void(*)(void))&ContainsObjectReferenceSlot,
        nullptr, nullptr,
        (void(*)(void))&GetValueTypeHashSlot,
        nullptr, nullptr,
    },
};

constinit FFieldClass kFSoftObjectPropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFSoftObjectProperty
                   | EClassCastFlags::kFObjectPropertyBase
                   | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FSoftObjectProperty::ConstructFn,
    /* FakeVTable */ &kFSoftObjectPropertyFakeVTable,
};

FSoftObjectProperty::FSoftObjectProperty(FFieldVariant InOwner, FName InName,
                                         FClass* InPropertyClass) noexcept
    : FProperty(&kFSoftObjectPropertyStaticClass, InOwner, InName)
    , PropertyClass(InPropertyClass)
{
    ElementSize = static_cast<::int32>(sizeof(FSoftObjectPath));
}

void FSoftObjectProperty::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FSoftObjectProperty(Owner, Name);
}

const FFieldClass* FSoftObjectProperty::StaticClass() noexcept
{
    return &GetFSoftObjectPropertyStaticClass();
}

const FFieldClass& GetFSoftObjectPropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFSoftObjectPropertyStaticClass.Name = FName("FSoftObjectProperty");
        return true;
    }();
    (void)Init;
    return kFSoftObjectPropertyStaticClass;
}

} // namespace XCore::Reflect
