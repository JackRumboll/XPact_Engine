// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FInterfaceProperty.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4b.
// Typed-interface reference property.
// =====================================================================
//
// FInterfaceProperty's value slot is a single 8-byte pointer
// (`FObject*` fulfilling the named interface).
//
// ContainsObjectReference returns TRUE -- the wrapped FObject* IS a
// GC root via this property.
//
// =====================================================================

#include "Reflection/FInterfaceProperty.h"

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
        const FObject* const* Src =
            static_cast<const FObject* const*>(Instance) + ElementIndex;
        ::std::memcpy(OutValue, Src, sizeof(FObject*));
    }

    void SetValueSlot(void* Instance, ::int32 ElementIndex, const void* InValue) noexcept
    {
        FObject** Dest = static_cast<FObject**>(Instance) + ElementIndex;
        ::std::memcpy(Dest, InValue, sizeof(FObject*));
    }

    void CopySingleValueSlot(void* Dest, const void* Src) noexcept
    {
        ::std::memcpy(Dest, Src, sizeof(FObject*));
    }

    void CopyCompleteValueSlot(void* Dest, const void* Src, ::int32 Count) noexcept
    {
        ::std::memcpy(Dest, Src, sizeof(FObject*) * static_cast<::size_t>(Count));
    }

    void InitializeValueSlot(void* Dest, ::int32 Count) noexcept
    {
        ::std::memset(Dest, 0, sizeof(FObject*) * static_cast<::size_t>(Count));
    }

    void DestroyValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
    }

    bool IdenticalSlot(const void* A, const void* B, ::uint32 /*PortFlags*/) noexcept
    {
        return *static_cast<const FObject* const*>(A)
            == *static_cast<const FObject* const*>(B);
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
        // The wrapped FObject* is a GC root.
        return true;
    }

    constexpr ::uint32 kInterfaceCapabilities =
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

constinit const FFakeVTable kFInterfacePropertyFakeVTable{
    /* Capabilities    */ kInterfaceCapabilities,
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

constinit FFieldClass kFInterfacePropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFInterfaceProperty
                   | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FInterfaceProperty::ConstructFn,
    /* FakeVTable */ &kFInterfacePropertyFakeVTable,
};

FInterfaceProperty::FInterfaceProperty(FFieldVariant InOwner, FName InName,
                                       FInterface* InInterfaceClass) noexcept
    : FProperty(&kFInterfacePropertyStaticClass, InOwner, InName)
    , InterfaceClass(InInterfaceClass)
{
    ElementSize = static_cast<::int32>(sizeof(FObject*));
}

void FInterfaceProperty::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FInterfaceProperty(Owner, Name);
}

const FFieldClass* FInterfaceProperty::StaticClass() noexcept
{
    return &GetFInterfacePropertyStaticClass();
}

const FFieldClass& GetFInterfacePropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFInterfacePropertyStaticClass.Name = FName("FInterfaceProperty");
        return true;
    }();
    (void)Init;
    return kFInterfacePropertyStaticClass;
}

} // namespace XCore::Reflect
