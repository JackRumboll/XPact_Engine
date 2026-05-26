// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FEnumProperty.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4a.
// =====================================================================
//
// FEnumProperty's dispatch slots route to UnderlyingProp's typed slots
// for the integer read/write paths. The actual byte-level read/write
// is the same as the underlying integer FProperty subclass; the
// FEnumProperty wraps that behaviour with the FEnum descriptor for
// enumerator-name-aware ExportText / ImportText (when FEnum is
// available at Phase 4b.5).
//
// For Phase 4b.4a (FEnum not yet shipped), the slots use uint32-shaped
// memcpy operations as the SAFE FALLBACK -- this matches the most
// common enum class underlying type (int32) and ensures the dispatch
// path is structurally correct even before FEnum lands. Phase 4b.5
// will refine the slots to consult UnderlyingProp's typed paths.
//
// =====================================================================

#include "Reflection/FEnumProperty.h"

#include "Containers/FString.h"
#include "Hash/FXxh3.h"
#include "Reflection/EClassCastFlags.h"
#include "Reflection/FFakeVTable.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FName.h"
#include "Reflection/FProperty.h"

#include <charconv>
#include <cstring>
#include <new>

namespace XCore::Reflect
{

namespace
{
    // The Phase 4b.4a default underlying type for an enum class is
    // int32 (matching `enum class : int32`). The slots operate on
    // int32-sized chunks; Phase 4b.5 will refine to consult
    // UnderlyingProp dynamically.
    using DefaultEnumUnderlying = ::int32;

    void GetValueSlot(const void* Instance, ::int32 ElementIndex, void* OutValue) noexcept
    {
        const DefaultEnumUnderlying* Src =
            static_cast<const DefaultEnumUnderlying*>(Instance) + ElementIndex;
        ::std::memcpy(OutValue, Src, sizeof(DefaultEnumUnderlying));
    }

    void SetValueSlot(void* Instance, ::int32 ElementIndex, const void* InValue) noexcept
    {
        DefaultEnumUnderlying* Dest =
            static_cast<DefaultEnumUnderlying*>(Instance) + ElementIndex;
        ::std::memcpy(Dest, InValue, sizeof(DefaultEnumUnderlying));
    }

    void CopySingleValueSlot(void* Dest, const void* Src) noexcept
    {
        ::std::memcpy(Dest, Src, sizeof(DefaultEnumUnderlying));
    }

    void CopyCompleteValueSlot(void* Dest, const void* Src, ::int32 Count) noexcept
    {
        ::std::memcpy(Dest, Src, sizeof(DefaultEnumUnderlying) * static_cast<::size_t>(Count));
    }

    void InitializeValueSlot(void* Dest, ::int32 Count) noexcept
    {
        ::std::memset(Dest, 0, sizeof(DefaultEnumUnderlying) * static_cast<::size_t>(Count));
    }

    void DestroyValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
        // Trivial for integer underlying type.
    }

    bool IdenticalSlot(const void* A, const void* B, ::uint32 /*PortFlags*/) noexcept
    {
        return ::std::memcmp(A, B, sizeof(DefaultEnumUnderlying)) == 0;
    }

    ::uint64 GetValueTypeHashSlot(const void* PropertyValue) noexcept
    {
        return ::XCore::Hash::FXxh3::Hash64(PropertyValue, sizeof(DefaultEnumUnderlying));
    }

    void ExportTextSlot(::XCore::FString& OutStr, const void* PropertyValue,
                        const void* /*DefaultValue*/, ::int32 /*PortFlags*/) noexcept
    {
        // Phase 4b.4a: fall back to integer textual form. Phase 4b.5
        // will look up the enumerator name via the Enum descriptor.
        DefaultEnumUnderlying Value;
        ::std::memcpy(&Value, PropertyValue, sizeof(Value));

        char Buffer[32] = {};
        auto Result = ::std::to_chars(Buffer, Buffer + sizeof(Buffer), Value);
        if (Result.ec == ::std::errc())
        {
            OutStr.Append(Buffer, static_cast<::int32>(Result.ptr - Buffer));
        }
    }

    const char* ImportTextSlot(const char* Buffer, void* PropertyValue,
                               ::int32 /*PortFlags*/) noexcept
    {
        if (Buffer == nullptr)
        {
            return Buffer;
        }
        const char* End = Buffer;
        while (*End != '\0')
        {
            ++End;
        }
        DefaultEnumUnderlying Value{};
        auto Result = ::std::from_chars(Buffer, End, Value);
        if (Result.ec != ::std::errc())
        {
            return Buffer;
        }
        ::std::memcpy(PropertyValue, &Value, sizeof(Value));
        return Result.ptr;
    }

    constexpr ::uint32 kEnumCapabilities =
          CapabilityBit(ESlot::GetValue)
        | CapabilityBit(ESlot::SetValue)
        | CapabilityBit(ESlot::CopySingleValue)
        | CapabilityBit(ESlot::CopyCompleteValue)
        | CapabilityBit(ESlot::InitializeValue)
        | CapabilityBit(ESlot::DestroyValue)
        | CapabilityBit(ESlot::Identical)
        | CapabilityBit(ESlot::ExportText)
        | CapabilityBit(ESlot::ImportText)
        | CapabilityBit(ESlot::GetValueTypeHash);

} // anonymous

constinit const FFakeVTable kFEnumPropertyFakeVTable{
    /* Capabilities    */ kEnumCapabilities,
    /* _reservedHeader */ 0U,
    /* Slots           */ {
        (void(*)(void))&GetValueSlot,
        (void(*)(void))&SetValueSlot,
        (void(*)(void))&CopySingleValueSlot,
        (void(*)(void))&CopyCompleteValueSlot,
        (void(*)(void))&InitializeValueSlot,
        (void(*)(void))&DestroyValueSlot,
        (void(*)(void))&IdenticalSlot,
        nullptr, nullptr, nullptr,
        (void(*)(void))&ExportTextSlot,
        (void(*)(void))&ImportTextSlot,
        (void(*)(void))&GetValueTypeHashSlot,
        nullptr, nullptr,
    },
};

constinit FFieldClass kFEnumPropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFEnumProperty | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FEnumProperty::ConstructFn,
    /* FakeVTable */ &kFEnumPropertyFakeVTable,
};

FEnumProperty::FEnumProperty(FFieldVariant InOwner, FName InName,
                             FProperty* InUnderlyingProp, FEnum* InEnum) noexcept
    : FProperty(&kFEnumPropertyStaticClass, InOwner, InName)
    , UnderlyingProp(InUnderlyingProp)
    , Enum(InEnum)
{
    // Default ElementSize to int32; overridden by the caller if the
    // underlying type is different (the caller MUST set ElementSize
    // explicitly when populating UnderlyingProp for non-int32 enums).
    ElementSize = static_cast<::int32>(sizeof(::int32));
}

void FEnumProperty::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FEnumProperty(Owner, Name);
}

const FFieldClass* FEnumProperty::StaticClass() noexcept
{
    return &GetFEnumPropertyStaticClass();
}

const FFieldClass& GetFEnumPropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFEnumPropertyStaticClass.Name = FName("FEnumProperty");
        return true;
    }();
    (void)Init;
    return kFEnumPropertyStaticClass;
}

} // namespace XCore::Reflect
