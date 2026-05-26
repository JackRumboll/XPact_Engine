// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FMulticastInlineDelegateProperty.cpp -- XCore-4b §5.5 + §11.2;
// Phase 4b.4b. Inline multicast delegate property
// (Rev 2 MVP add per FIX-3).
// =====================================================================
//
// FMulticastInlineDelegateProperty's value slot holds an
// FMulticastInlineDelegate by-value (inline list of subscriber
// {FObject*, FName} pairs). Phase 4b.4b treats the slot's bytes as
// opaque -- the runtime size is implementation-specific; full
// dispatch lands at Phase 4b.5.
//
// ContainsObjectReference returns TRUE (subscriber Targets are GC
// roots).
//
// =====================================================================

#include "Reflection/FMulticastInlineDelegateProperty.h"

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
    // FMulticastInlineDelegate runtime size is implementation-specific
    // and isn't fixed at Phase 4b.4b. The slot bodies are no-ops; the
    // load-bearing slot is ContainsObjectReference.
    //
    // TODO(Phase 4b.5): wire the slots to typed dispatch via
    // FMulticastInlineDelegate's value-type operations once the type
    // ships.

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
        // Subscriber list contains FObject* Target references.
        return true;
    }

    constexpr ::uint32 kMulticastInlineCapabilities =
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

constinit const FFakeVTable kFMulticastInlineDelegatePropertyFakeVTable{
    /* Capabilities    */ kMulticastInlineCapabilities,
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

constinit FFieldClass kFMulticastInlineDelegatePropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFMulticastInlineDelegateProperty
                   | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FMulticastInlineDelegateProperty::ConstructFn,
    /* FakeVTable */ &kFMulticastInlineDelegatePropertyFakeVTable,
};

FMulticastInlineDelegateProperty::FMulticastInlineDelegateProperty(
    FFieldVariant InOwner, FName InName,
    FFunctionDescriptor* InSignatureFunction) noexcept
    : FProperty(&kFMulticastInlineDelegatePropertyStaticClass, InOwner, InName)
    , SignatureFunction(InSignatureFunction)
{
    // ElementSize populated at FClass::Link time (Phase 4b.5).
}

void FMulticastInlineDelegateProperty::ConstructFn(
    FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FMulticastInlineDelegateProperty(Owner, Name);
}

const FFieldClass* FMulticastInlineDelegateProperty::StaticClass() noexcept
{
    return &GetFMulticastInlineDelegatePropertyStaticClass();
}

const FFieldClass& GetFMulticastInlineDelegatePropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFMulticastInlineDelegatePropertyStaticClass.Name =
            FName("FMulticastInlineDelegateProperty");
        return true;
    }();
    (void)Init;
    return kFMulticastInlineDelegatePropertyStaticClass;
}

} // namespace XCore::Reflect
