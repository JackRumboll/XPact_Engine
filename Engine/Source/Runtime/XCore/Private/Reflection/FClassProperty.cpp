// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FClassProperty.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4b.
// FClass* (metaclass) reference property.
// =====================================================================
//
// FClassProperty's value slot is a single 8-byte pointer (`FClass*`).
// Same dispatch shape as FObjectProperty (the value IS a pointer to a
// GC-managed singleton):
//
//   * GetValue / SetValue                -- copy 8 bytes (pointer).
//   * CopySingleValue / CopyCompleteValue -- copy N * 8 bytes.
//   * InitializeValue                    -- zero N * 8 bytes.
//   * DestroyValue                       -- no-op (pointer not owned).
//   * Identical                          -- pointer equality.
//   * GetValueTypeHash                   -- hash pointer bits.
//   * ContainsObjectReference            -- TRUE (FClass is XObject-
//                                          derived at System 5; the
//                                          GC walker must trace).
//   * ExportText / ImportText            -- stubs at Phase 4b.4b.
//   * SerializeItem / NetSerializeItem   -- nullptr.
//   * AppendToSchemaHash / ConvertFromType -- nullptr.
//
// =====================================================================

#include "Reflection/FClassProperty.h"

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
        const FClass* const* Src =
            static_cast<const FClass* const*>(Instance) + ElementIndex;
        ::std::memcpy(OutValue, Src, sizeof(FClass*));
    }

    void SetValueSlot(void* Instance, ::int32 ElementIndex, const void* InValue) noexcept
    {
        FClass** Dest = static_cast<FClass**>(Instance) + ElementIndex;
        ::std::memcpy(Dest, InValue, sizeof(FClass*));
    }

    void CopySingleValueSlot(void* Dest, const void* Src) noexcept
    {
        ::std::memcpy(Dest, Src, sizeof(FClass*));
    }

    void CopyCompleteValueSlot(void* Dest, const void* Src, ::int32 Count) noexcept
    {
        ::std::memcpy(Dest, Src, sizeof(FClass*) * static_cast<::size_t>(Count));
    }

    void InitializeValueSlot(void* Dest, ::int32 Count) noexcept
    {
        ::std::memset(Dest, 0, sizeof(FClass*) * static_cast<::size_t>(Count));
    }

    void DestroyValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
        // FClass singletons are owned by the reflection system; the
        // FClassProperty slot does NOT own the pointed-at FClass.
    }

    bool IdenticalSlot(const void* A, const void* B, ::uint32 /*PortFlags*/) noexcept
    {
        return *static_cast<const FClass* const*>(A)
            == *static_cast<const FClass* const*>(B);
    }

    ::uint64 GetValueTypeHashSlot(const void* PropertyValue) noexcept
    {
        const void* Ptr = nullptr;
        ::std::memcpy(&Ptr, PropertyValue, sizeof(Ptr));
        return ::XCore::Hash::FXxh3::Hash64(&Ptr, sizeof(Ptr));
    }

    bool ContainsObjectReferenceSlot(
        ::XCore::TArray<const FStructProperty*>& /*EncounteredStructProps*/) noexcept
    {
        // FClass instances are XObject-derived at System 5; a slot
        // holding an FClass* IS an object reference for GC purposes.
        return true;
    }

    constexpr ::uint32 kClassCapabilities =
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

constinit const FFakeVTable kFClassPropertyFakeVTable{
    /* Capabilities    */ kClassCapabilities,
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

constinit FFieldClass kFClassPropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFClassProperty
                   | EClassCastFlags::kFObjectPropertyBase
                   | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FClassProperty::ConstructFn,
    /* FakeVTable */ &kFClassPropertyFakeVTable,
};

FClassProperty::FClassProperty(FFieldVariant InOwner, FName InName,
                               FClass* InMetaClass) noexcept
    : FProperty(&kFClassPropertyStaticClass, InOwner, InName)
    , MetaClass(InMetaClass)
{
    ElementSize = static_cast<::int32>(sizeof(FClass*));
}

void FClassProperty::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FClassProperty(Owner, Name);
}

const FFieldClass* FClassProperty::StaticClass() noexcept
{
    return &GetFClassPropertyStaticClass();
}

const FFieldClass& GetFClassPropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFClassPropertyStaticClass.Name = FName("FClassProperty");
        return true;
    }();
    (void)Init;
    return kFClassPropertyStaticClass;
}

} // namespace XCore::Reflect
