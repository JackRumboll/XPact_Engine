// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FSetProperty.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4b.
// TSet<T> property.
// =====================================================================
//
// FSetProperty's value slot holds a TSet<T> by-value. Same dispatch
// shape as FArrayProperty / FMapProperty: per-instance ElementSize is
// populated at FClass::Link time (Phase 4b.5); slots are no-ops at
// Phase 4b.4b except for ContainsObjectReference which returns true
// conservatively.
//
// =====================================================================

#include "Reflection/FSetProperty.h"

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
    void GetValueSlot(const void* /*Instance*/, ::int32 /*ElementIndex*/, void* /*OutValue*/) noexcept
    {
    }

    void SetValueSlot(void* /*Instance*/, ::int32 /*ElementIndex*/, const void* /*InValue*/) noexcept
    {
    }

    void CopySingleValueSlot(void* /*Dest*/, const void* /*Src*/) noexcept
    {
    }

    void CopyCompleteValueSlot(void* /*Dest*/, const void* /*Src*/, ::int32 /*Count*/) noexcept
    {
    }

    void InitializeValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
    }

    void DestroyValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
    }

    bool IdenticalSlot(const void* /*A*/, const void* /*B*/, ::uint32 /*PortFlags*/) noexcept
    {
        return false;
    }

    ::uint64 GetValueTypeHashSlot(const void* /*PropertyValue*/) noexcept
    {
        return 0;
    }

    bool ContainsObjectReferenceSlot(
        ::XCore::TArray<const FStructProperty*>& /*EncounteredStructProps*/) noexcept
    {
        // Conservative: TSet slots may contain object references via
        // ElementProp; the typed walker will check for the precise answer.
        return true;
    }

    constexpr ::uint32 kSetCapabilities =
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

constinit const FFakeVTable kFSetPropertyFakeVTable{
    /* Capabilities    */ kSetCapabilities,
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

constinit FFieldClass kFSetPropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFSetProperty
                   | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FSetProperty::ConstructFn,
    /* FakeVTable */ &kFSetPropertyFakeVTable,
};

FSetProperty::FSetProperty(FFieldVariant InOwner, FName InName,
                           FProperty* InElementProp) noexcept
    : FProperty(&kFSetPropertyStaticClass, InOwner, InName)
    , ElementProp(InElementProp)
{
    // ElementSize populated at FClass::Link time when TSet's sizeof is
    // resolvable (Phase 4b.5).
}

void FSetProperty::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FSetProperty(Owner, Name);
}

const FFieldClass* FSetProperty::StaticClass() noexcept
{
    return &GetFSetPropertyStaticClass();
}

const FFieldClass& GetFSetPropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFSetPropertyStaticClass.Name = FName("FSetProperty");
        return true;
    }();
    (void)Init;
    return kFSetPropertyStaticClass;
}

} // namespace XCore::Reflect
