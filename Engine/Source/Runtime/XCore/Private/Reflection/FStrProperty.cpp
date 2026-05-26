// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FStrProperty.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4a. FString type.
// =====================================================================
//
// FString is NON-TRIVIALLY-COPYABLE; the dispatch slots use FString's
// typed operations. Critical correctness notes:
//
//   * CopySingleValue uses FString::operator= (NOT memcpy) so the
//     heap-allocated path doesn't double-free / leak.
//   * InitializeValue calls FString::FString() (placement-new) on each
//     element -- the bytes at Dest may be uninitialised (the slot is
//     called immediately after FProperty::ContainerPtrToValuePtr
//     resolves an uninitialised struct slot).
//   * DestroyValue calls FString::~FString() on each element.
//   * Identical uses FString::operator==.
//
// =====================================================================

#include "Reflection/FStrProperty.h"

#include "Containers/FString.h"
#include "Reflection/EClassCastFlags.h"
#include "Reflection/FFakeVTable.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FName.h"
#include "Reflection/FProperty.h"
#include "Hash/FXxh3.h"

#include <new>

namespace XCore::Reflect
{

namespace
{
    void GetValueSlot(const void* Instance, ::int32 ElementIndex, void* OutValue) noexcept
    {
        // Construct a copy of the FString into OutValue. The caller
        // owns OutValue; if OutValue points at an uninitialised
        // buffer, the caller MUST size it to sizeof(FString) bytes.
        //
        // The OutValue buffer is assumed to NOT hold a live FString
        // (the spec slot signature is "read into OutValue"; the
        // semantics mirror UE FProperty::GetValue which copies the
        // value-as-bytes into the caller's buffer).
        const ::XCore::FString* Src =
            static_cast<const ::XCore::FString*>(Instance) + ElementIndex;
        new (OutValue) ::XCore::FString(*Src);  // copy ctor
    }

    void SetValueSlot(void* Instance, ::int32 ElementIndex, const void* InValue) noexcept
    {
        // Assign through FString::operator=. The destination MUST
        // already hold a live FString (the slot is invoked on a
        // structurally-initialised owner; FProperty::SetValue
        // pre-condition).
        ::XCore::FString* Dest = static_cast<::XCore::FString*>(Instance) + ElementIndex;
        const ::XCore::FString* Src = static_cast<const ::XCore::FString*>(InValue);
        *Dest = *Src;
    }

    void CopySingleValueSlot(void* Dest, const void* Src) noexcept
    {
        // The Dest is assumed to already hold a live FString; this
        // is the operator= path (NOT placement-new).
        ::XCore::FString* DestStr = static_cast<::XCore::FString*>(Dest);
        const ::XCore::FString* SrcStr = static_cast<const ::XCore::FString*>(Src);
        *DestStr = *SrcStr;
    }

    void CopyCompleteValueSlot(void* Dest, const void* Src, ::int32 Count) noexcept
    {
        ::XCore::FString* DestArr = static_cast<::XCore::FString*>(Dest);
        const ::XCore::FString* SrcArr = static_cast<const ::XCore::FString*>(Src);
        for (::int32 i = 0; i < Count; ++i)
        {
            DestArr[i] = SrcArr[i];
        }
    }

    void InitializeValueSlot(void* Dest, ::int32 Count) noexcept
    {
        // Placement-new each FString. The Dest buffer is assumed to
        // be uninitialised storage of size Count * sizeof(FString).
        ::XCore::FString* Arr = static_cast<::XCore::FString*>(Dest);
        for (::int32 i = 0; i < Count; ++i)
        {
            new (&Arr[i]) ::XCore::FString();
        }
    }

    void DestroyValueSlot(void* Dest, ::int32 Count) noexcept
    {
        // Invoke ~FString() on each element. The slot does NOT free
        // the array storage itself; the caller (FStruct destructor /
        // FProperty walker) owns that.
        ::XCore::FString* Arr = static_cast<::XCore::FString*>(Dest);
        for (::int32 i = 0; i < Count; ++i)
        {
            Arr[i].~FString();
        }
    }

    bool IdenticalSlot(const void* A, const void* B, ::uint32 /*PortFlags*/) noexcept
    {
        const ::XCore::FString* LhsStr = static_cast<const ::XCore::FString*>(A);
        const ::XCore::FString* RhsStr = static_cast<const ::XCore::FString*>(B);
        return *LhsStr == *RhsStr;
    }

    ::uint64 GetValueTypeHashSlot(const void* PropertyValue) noexcept
    {
        const ::XCore::FString* StrVal =
            static_cast<const ::XCore::FString*>(PropertyValue);
        // Hash the byte payload (UTF-8 codepoint sequence).
        return ::XCore::Hash::FXxh3::Hash64(StrVal->ToUtf8Ptr(),
                                            static_cast<::SIZE_T>(StrVal->LenBytes()));
    }

    void ExportTextSlot(::XCore::FString& OutStr, const void* PropertyValue,
                        const void* /*DefaultValue*/, ::int32 /*PortFlags*/) noexcept
    {
        const ::XCore::FString* StrVal =
            static_cast<const ::XCore::FString*>(PropertyValue);
        // Append the value's bytes verbatim. (No escaping at Phase
        // 4b.4a; the full export-text path with escaping for embedded
        // quotes / newlines is XSerialization concern.)
        OutStr.Append(*StrVal);
    }

    const char* ImportTextSlot(const char* Buffer, void* PropertyValue,
                               ::int32 /*PortFlags*/) noexcept
    {
        if (Buffer == nullptr)
        {
            return Buffer;
        }
        // Walk to a delimiter (whitespace, NUL); consumed span is the
        // string's UTF-8 bytes.
        const char* End = Buffer;
        while (*End != '\0' && *End != ',' && *End != '\n' && *End != '\r')
        {
            ++End;
        }
        const ::int32 Len = static_cast<::int32>(End - Buffer);
        ::XCore::FString* Dest = static_cast<::XCore::FString*>(PropertyValue);
        *Dest = ::XCore::FString(Buffer, Len);
        return End;
    }

    constexpr ::uint32 kStrCapabilities =
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

constinit const FFakeVTable kFStrPropertyFakeVTable{
    /* Capabilities    */ kStrCapabilities,
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

constinit FFieldClass kFStrPropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFStrProperty | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FStrProperty::ConstructFn,
    /* FakeVTable */ &kFStrPropertyFakeVTable,
};

FStrProperty::FStrProperty(FFieldVariant InOwner, FName InName) noexcept
    : FProperty(&kFStrPropertyStaticClass, InOwner, InName)
{
    ElementSize = static_cast<::int32>(sizeof(::XCore::FString));
}

void FStrProperty::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FStrProperty(Owner, Name);
}

const FFieldClass* FStrProperty::StaticClass() noexcept
{
    return &GetFStrPropertyStaticClass();
}

const FFieldClass& GetFStrPropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFStrPropertyStaticClass.Name = FName("FStrProperty");
        return true;
    }();
    (void)Init;
    return kFStrPropertyStaticClass;
}

} // namespace XCore::Reflect
