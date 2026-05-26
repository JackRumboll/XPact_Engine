// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FDelegateProperty.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4b.
// Single-cast delegate property.
// =====================================================================
//
// FDelegateProperty's value slot holds an FScriptDelegate by-value
// (16 bytes: FObject* Target + FName FunctionName).
//
// ContainsObjectReference returns TRUE (the Target field IS a GC root).
//
// =====================================================================

#include "Reflection/FDelegateProperty.h"

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
    // FScriptDelegate slot size: FObject* Target (8) + FName Function
    // Name (8) = 16 bytes total. The slot bodies operate on the 16-byte
    // FScriptDelegate value.
    constexpr ::size_t kScriptDelegateSize = 16;

    void GetValueSlot(const void* Instance, ::int32 ElementIndex, void* OutValue) noexcept
    {
        const ::uint8* Src =
            static_cast<const ::uint8*>(Instance) + ElementIndex * kScriptDelegateSize;
        ::std::memcpy(OutValue, Src, kScriptDelegateSize);
    }

    void SetValueSlot(void* Instance, ::int32 ElementIndex, const void* InValue) noexcept
    {
        ::uint8* Dest =
            static_cast<::uint8*>(Instance) + ElementIndex * kScriptDelegateSize;
        ::std::memcpy(Dest, InValue, kScriptDelegateSize);
    }

    void CopySingleValueSlot(void* Dest, const void* Src) noexcept
    {
        ::std::memcpy(Dest, Src, kScriptDelegateSize);
    }

    void CopyCompleteValueSlot(void* Dest, const void* Src, ::int32 Count) noexcept
    {
        ::std::memcpy(Dest, Src, kScriptDelegateSize * static_cast<::size_t>(Count));
    }

    void InitializeValueSlot(void* Dest, ::int32 Count) noexcept
    {
        ::std::memset(Dest, 0, kScriptDelegateSize * static_cast<::size_t>(Count));
    }

    void DestroyValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
        // FScriptDelegate is POD; no destructor needed.
    }

    bool IdenticalSlot(const void* A, const void* B, ::uint32 /*PortFlags*/) noexcept
    {
        return ::std::memcmp(A, B, kScriptDelegateSize) == 0;
    }

    ::uint64 GetValueTypeHashSlot(const void* PropertyValue) noexcept
    {
        return ::XCore::Hash::FXxh3::Hash64(PropertyValue, kScriptDelegateSize);
    }

    bool ContainsObjectReferenceSlot(
        ::XCore::TArray<const FStructProperty*>& /*EncounteredStructProps*/) noexcept
    {
        // FScriptDelegate's Target field is an FObject* (GC root).
        return true;
    }

    constexpr ::uint32 kDelegateCapabilities =
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

constinit const FFakeVTable kFDelegatePropertyFakeVTable{
    /* Capabilities    */ kDelegateCapabilities,
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

constinit FFieldClass kFDelegatePropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFDelegateProperty
                   | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FDelegateProperty::ConstructFn,
    /* FakeVTable */ &kFDelegatePropertyFakeVTable,
};

FDelegateProperty::FDelegateProperty(FFieldVariant InOwner, FName InName,
                                     FFunctionDescriptor* InSignatureFunction) noexcept
    : FProperty(&kFDelegatePropertyStaticClass, InOwner, InName)
    , SignatureFunction(InSignatureFunction)
{
    ElementSize = static_cast<::int32>(kScriptDelegateSize);
}

void FDelegateProperty::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FDelegateProperty(Owner, Name);
}

const FFieldClass* FDelegateProperty::StaticClass() noexcept
{
    return &GetFDelegatePropertyStaticClass();
}

const FFieldClass& GetFDelegatePropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFDelegatePropertyStaticClass.Name = FName("FDelegateProperty");
        return true;
    }();
    (void)Init;
    return kFDelegatePropertyStaticClass;
}

} // namespace XCore::Reflect
