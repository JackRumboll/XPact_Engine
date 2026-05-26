// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FMapProperty.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4b.
// TMap<K, V> property.
// =====================================================================
//
// FMapProperty's value slot holds a TMap<K, V> by-value. The dispatch
// slots operate on TMap header bytes (XCore-4a SwissTable backing).
//
// All entry-walking slots (Identical, GetValueTypeHash, full Copy)
// require iterating entries via KeyProp + ValueProp; those slots are
// no-ops at Phase 4b.4b and re-wired at Phase 4b.5 when TMap iteration
// + per-instance dispatch context are available.
//
// ContainsObjectReference returns TRUE conservatively (the typed walker
// will check KeyProp + ValueProp ContainsObjectReference for the
// precise answer).
//
// =====================================================================

#include "Reflection/FMapProperty.h"

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
    // TMap<K, V> header size per XCore-4a §5.1 SwissTable backing.
    // The actual TMap layout is approximately 24-32 bytes depending on
    // alignment + EBO; for Phase 4b.4b we treat it as opaque and the
    // ElementSize is left at 0 (FClass::Link populates from TMap's
    // sizeof at Phase 4b.5).
    //
    // For the bytewise dispatch slots, we use a placeholder size of
    // 32 bytes (the upper bound of TMap header). The slots are NEVER
    // legitimately invoked at Phase 4b.4b (no FClass::Link path
    // populates ElementSize correctly until Phase 4b.5).

    void GetValueSlot(const void* /*Instance*/, ::int32 /*ElementIndex*/, void* /*OutValue*/) noexcept
    {
        // Phase 4b.4b: no-op.
    }

    void SetValueSlot(void* /*Instance*/, ::int32 /*ElementIndex*/, const void* /*InValue*/) noexcept
    {
        // Phase 4b.4b: no-op.
    }

    void CopySingleValueSlot(void* /*Dest*/, const void* /*Src*/) noexcept
    {
        // Phase 4b.4b: no-op.
    }

    void CopyCompleteValueSlot(void* /*Dest*/, const void* /*Src*/, ::int32 /*Count*/) noexcept
    {
        // Phase 4b.4b: no-op.
    }

    void InitializeValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
        // Phase 4b.4b: no-op.
    }

    void DestroyValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
        // Phase 4b.4b: no-op.
    }

    bool IdenticalSlot(const void* /*A*/, const void* /*B*/, ::uint32 /*PortFlags*/) noexcept
    {
        // Phase 4b.4b: returns "not identical".
        return false;
    }

    ::uint64 GetValueTypeHashSlot(const void* /*PropertyValue*/) noexcept
    {
        // Phase 4b.4b: TMaps are NOT keys in other maps (returns 0).
        return 0;
    }

    bool ContainsObjectReferenceSlot(
        ::XCore::TArray<const FStructProperty*>& /*EncounteredStructProps*/) noexcept
    {
        // Conservative: TMap slots may contain object references via
        // either KeyProp or ValueProp; the typed walker will check
        // both for the precise answer.
        return true;
    }

    constexpr ::uint32 kMapCapabilities =
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

constinit const FFakeVTable kFMapPropertyFakeVTable{
    /* Capabilities    */ kMapCapabilities,
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

constinit FFieldClass kFMapPropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFMapProperty
                   | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FMapProperty::ConstructFn,
    /* FakeVTable */ &kFMapPropertyFakeVTable,
};

FMapProperty::FMapProperty(FFieldVariant InOwner, FName InName,
                           FProperty* InKeyProp, FProperty* InValueProp) noexcept
    : FProperty(&kFMapPropertyStaticClass, InOwner, InName)
    , KeyProp(InKeyProp)
    , ValueProp(InValueProp)
    , MapFlags(0)
    , _padMapPayload(0)
{
    // ElementSize is NOT set here -- TMap header size is implementation-
    // dependent and isn't known until FClass::Link populates from
    // sizeof(TMap<K,V>) at Phase 4b.5.
}

void FMapProperty::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FMapProperty(Owner, Name);
}

const FFieldClass* FMapProperty::StaticClass() noexcept
{
    return &GetFMapPropertyStaticClass();
}

const FFieldClass& GetFMapPropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFMapPropertyStaticClass.Name = FName("FMapProperty");
        return true;
    }();
    (void)Init;
    return kFMapPropertyStaticClass;
}

} // namespace XCore::Reflect
