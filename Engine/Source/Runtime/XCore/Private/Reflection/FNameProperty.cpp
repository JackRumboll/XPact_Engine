// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FNameProperty.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4a. FName type.
// =====================================================================
//
// FName is an 8-byte interned-name handle (XCore-4b §4). The dispatch
// slots are SPECIALISED for FName:
//
//   * Identical compares Index + SerialNumber via FName operator==
//     (case-sensitive bytewise per §4.5).
//   * GetValueTypeHash uses FName's own GetTypeHash (FXxh3-64 over
//     the 8-byte handle).
//   * ExportText emits the FName's interned bytes + optional `_N`
//     suffix via FName::ToString.
//   * ImportText parses the input (recognising `_N` suffix per §4.4)
//     and interns the bytes into a new FName.
//
// =====================================================================

#include "Reflection/FNameProperty.h"

#include "Containers/FString.h"
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
        const FName* Src = static_cast<const FName*>(Instance) + ElementIndex;
        ::std::memcpy(OutValue, Src, sizeof(FName));
    }

    void SetValueSlot(void* Instance, ::int32 ElementIndex, const void* InValue) noexcept
    {
        FName* Dest = static_cast<FName*>(Instance) + ElementIndex;
        ::std::memcpy(Dest, InValue, sizeof(FName));
    }

    void CopySingleValueSlot(void* Dest, const void* Src) noexcept
    {
        ::std::memcpy(Dest, Src, sizeof(FName));
    }

    void CopyCompleteValueSlot(void* Dest, const void* Src, ::int32 Count) noexcept
    {
        ::std::memcpy(Dest, Src, sizeof(FName) * static_cast<::size_t>(Count));
    }

    void InitializeValueSlot(void* Dest, ::int32 Count) noexcept
    {
        // Value-initialise to NAME_None (Index=0, SerialNumber=0).
        ::std::memset(Dest, 0, sizeof(FName) * static_cast<::size_t>(Count));
    }

    void DestroyValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
        // FName is trivially destructible (the intern-table entries
        // are owned by FNamePool, not the FName handle).
    }

    bool IdenticalSlot(const void* A, const void* B, ::uint32 /*PortFlags*/) noexcept
    {
        const FName* LhsName = static_cast<const FName*>(A);
        const FName* RhsName = static_cast<const FName*>(B);
        return *LhsName == *RhsName;
    }

    ::uint64 GetValueTypeHashSlot(const void* PropertyValue) noexcept
    {
        const FName* NameVal = static_cast<const FName*>(PropertyValue);
        return GetTypeHash(*NameVal);
    }

    void ExportTextSlot(::XCore::FString& OutStr, const void* PropertyValue,
                        const void* /*DefaultValue*/, ::int32 /*PortFlags*/) noexcept
    {
        const FName* NameVal = static_cast<const FName*>(PropertyValue);
        NameVal->AppendString(OutStr);
    }

    const char* ImportTextSlot(const char* Buffer, void* PropertyValue,
                               ::int32 /*PortFlags*/) noexcept
    {
        if (Buffer == nullptr)
        {
            return Buffer;
        }
        // Walk to a delimiter (whitespace, comma, NUL); the consumed
        // span is the name's UTF-8 bytes.
        const char* End = Buffer;
        while (*End != '\0' && *End != ' ' && *End != '\t' &&
               *End != ',' && *End != '\n' && *End != '\r')
        {
            ++End;
        }
        const ::int32 Len = static_cast<::int32>(End - Buffer);
        if (Len <= 0)
        {
            return Buffer;
        }
        FName ParsedName(Buffer, Len);
        ::std::memcpy(PropertyValue, &ParsedName, sizeof(FName));
        return End;
    }

    constexpr ::uint32 kNameCapabilities =
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

constinit const FFakeVTable kFNamePropertyFakeVTable{
    /* Capabilities    */ kNameCapabilities,
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

constinit FFieldClass kFNamePropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFNameProperty | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FNameProperty::ConstructFn,
    /* FakeVTable */ &kFNamePropertyFakeVTable,
};

FNameProperty::FNameProperty(FFieldVariant InOwner, FName InName) noexcept
    : FProperty(&kFNamePropertyStaticClass, InOwner, InName)
{
    ElementSize = static_cast<::int32>(sizeof(FName));
}

void FNameProperty::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FNameProperty(Owner, Name);
}

const FFieldClass* FNameProperty::StaticClass() noexcept
{
    return &GetFNamePropertyStaticClass();
}

const FFieldClass& GetFNamePropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFNamePropertyStaticClass.Name = FName("FNameProperty");
        return true;
    }();
    (void)Init;
    return kFNamePropertyStaticClass;
}

} // namespace XCore::Reflect
