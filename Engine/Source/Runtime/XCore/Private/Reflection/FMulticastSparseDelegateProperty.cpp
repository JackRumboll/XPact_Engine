// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FMulticastSparseDelegateProperty.cpp -- XCore-4b §5.5 + §11.2;
// Phase 4b.4b. Sparse multicast delegate property
// (Rev 2 MVP add per FIX-3).
// =====================================================================
//
// FMulticastSparseDelegateProperty's value slot holds an
// FMulticastSparseDelegate by-value (sparse subscriber map).
//
// ContainsObjectReference returns TRUE (when the sparse map has
// entries, subscriber Targets are GC roots; conservative under
// reflection-time analysis).
//
// =====================================================================

#include "Reflection/FMulticastSparseDelegateProperty.h"

#include "Reflection/EClassCastFlags.h"
#include "Reflection/FFakeVTable.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FName.h"
#include "Reflection/FProperty.h"

#include <new>

namespace XCore::Reflect
{

namespace
{
    // Slot bodies: no-ops at Phase 4b.4b; Phase 4b.5 wires typed
    // dispatch via FMulticastSparseDelegate's operations.

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
        return true;
    }

    constexpr ::uint32 kMulticastSparseCapabilities =
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

constinit const FFakeVTable kFMulticastSparseDelegatePropertyFakeVTable{
    /* Capabilities    */ kMulticastSparseCapabilities,
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

constinit FFieldClass kFMulticastSparseDelegatePropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFMulticastSparseDelegateProperty
                   | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FMulticastSparseDelegateProperty::ConstructFn,
    /* FakeVTable */ &kFMulticastSparseDelegatePropertyFakeVTable,
};

FMulticastSparseDelegateProperty::FMulticastSparseDelegateProperty(
    FFieldVariant InOwner, FName InName,
    FFunctionDescriptor* InSignatureFunction) noexcept
    : FProperty(&kFMulticastSparseDelegatePropertyStaticClass, InOwner, InName)
    , SignatureFunction(InSignatureFunction)
{
    // ElementSize populated at FClass::Link time (Phase 4b.5).
}

void FMulticastSparseDelegateProperty::ConstructFn(
    FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FMulticastSparseDelegateProperty(Owner, Name);
}

const FFieldClass* FMulticastSparseDelegateProperty::StaticClass() noexcept
{
    return &GetFMulticastSparseDelegatePropertyStaticClass();
}

const FFieldClass& GetFMulticastSparseDelegatePropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFMulticastSparseDelegatePropertyStaticClass.Name =
            FName("FMulticastSparseDelegateProperty");
        return true;
    }();
    (void)Init;
    return kFMulticastSparseDelegatePropertyStaticClass;
}

} // namespace XCore::Reflect
