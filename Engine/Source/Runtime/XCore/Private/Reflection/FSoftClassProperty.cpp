// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FSoftClassProperty.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4b.
// Soft class reference property (Rev 2 MVP add per FIX-3).
// =====================================================================
//
// FSoftClassProperty's value slot is an 8-byte FSoftObjectPath value
// (same payload shape as FSoftObjectProperty; the discriminator is the
// FFieldClass identity + the MetaClass bound).
//
// ContainsObjectReference returns FALSE (soft refs are NOT GC roots;
// the soft-resolved FClass loads on demand via the asset system).
//
// =====================================================================

#include "Reflection/FSoftClassProperty.h"

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
    }

    bool IdenticalSlot(const void* A, const void* B, ::uint32 /*PortFlags*/) noexcept
    {
        return ::std::memcmp(A, B, sizeof(FSoftObjectPath)) == 0;
    }

    ::uint64 GetValueTypeHashSlot(const void* PropertyValue) noexcept
    {
        return ::XCore::Hash::FXxh3::Hash64(PropertyValue, sizeof(FSoftObjectPath));
    }

    bool ContainsObjectReferenceSlot(
        ::XCore::TArray<const FStructProperty*>& /*EncounteredStructProps*/) noexcept
    {
        // Soft class refs are NOT GC roots.
        return false;
    }

    constexpr ::uint32 kSoftClassCapabilities =
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

constinit const FFakeVTable kFSoftClassPropertyFakeVTable{
    /* Capabilities    */ kSoftClassCapabilities,
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

constinit FFieldClass kFSoftClassPropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFSoftClassProperty
                   | EClassCastFlags::kFObjectPropertyBase
                   | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FSoftClassProperty::ConstructFn,
    /* FakeVTable */ &kFSoftClassPropertyFakeVTable,
};

FSoftClassProperty::FSoftClassProperty(FFieldVariant InOwner, FName InName,
                                       FClass* InMetaClass) noexcept
    : FProperty(&kFSoftClassPropertyStaticClass, InOwner, InName)
    , MetaClass(InMetaClass)
{
    ElementSize = static_cast<::int32>(sizeof(FSoftObjectPath));
}

void FSoftClassProperty::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FSoftClassProperty(Owner, Name);
}

const FFieldClass* FSoftClassProperty::StaticClass() noexcept
{
    return &GetFSoftClassPropertyStaticClass();
}

const FFieldClass& GetFSoftClassPropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFSoftClassPropertyStaticClass.Name = FName("FSoftClassProperty");
        return true;
    }();
    (void)Init;
    return kFSoftClassPropertyStaticClass;
}

} // namespace XCore::Reflect
