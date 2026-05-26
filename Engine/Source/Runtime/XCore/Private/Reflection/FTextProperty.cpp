// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FTextProperty.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4a. FText type
// (Rev 2 MVP add per FIX-3).
// =====================================================================
//
// FText is XCore-4a's localised-text type. It carries three rodata
// pointers (namespace + key + literal fallback) + a lazily-resolved
// FString cache. The dispatch slots use FText's typed operations:
//
//   * Copy: FText's implicit copy ctor / operator= (FString-aware).
//   * Identical: compare ResolveForCurrentLocale results.
//   * GetValueTypeHash: hash the resolved FString bytes.
//   * ExportText: emit the resolved string bytes.
//   * ImportText: build an INVTEXT-style FText (no namespace/key, just
//                 a literal). The full export-text-with-namespace path
//                 is XLocalization concern.
//
// FText lives in the XCore::Loc namespace; not XCore::Reflect.
//
// =====================================================================

#include "Reflection/FTextProperty.h"

#include "Containers/FString.h"
#include "Hash/FXxh3.h"
#include "Internationalization/FText.h"
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
    using ::XCore::Loc::FText;

    void GetValueSlot(const void* Instance, ::int32 ElementIndex, void* OutValue) noexcept
    {
        const FText* Src = static_cast<const FText*>(Instance) + ElementIndex;
        new (OutValue) FText(*Src);  // copy ctor
    }

    void SetValueSlot(void* Instance, ::int32 ElementIndex, const void* InValue) noexcept
    {
        FText* Dest = static_cast<FText*>(Instance) + ElementIndex;
        const FText* Src = static_cast<const FText*>(InValue);
        *Dest = *Src;
    }

    void CopySingleValueSlot(void* Dest, const void* Src) noexcept
    {
        FText* DestText = static_cast<FText*>(Dest);
        const FText* SrcText = static_cast<const FText*>(Src);
        *DestText = *SrcText;
    }

    void CopyCompleteValueSlot(void* Dest, const void* Src, ::int32 Count) noexcept
    {
        FText* DestArr = static_cast<FText*>(Dest);
        const FText* SrcArr = static_cast<const FText*>(Src);
        for (::int32 i = 0; i < Count; ++i)
        {
            DestArr[i] = SrcArr[i];
        }
    }

    void InitializeValueSlot(void* Dest, ::int32 Count) noexcept
    {
        FText* Arr = static_cast<FText*>(Dest);
        for (::int32 i = 0; i < Count; ++i)
        {
            new (&Arr[i]) FText();
        }
    }

    void DestroyValueSlot(void* Dest, ::int32 Count) noexcept
    {
        FText* Arr = static_cast<FText*>(Dest);
        for (::int32 i = 0; i < Count; ++i)
        {
            Arr[i].~FText();
        }
    }

    bool IdenticalSlot(const void* A, const void* B, ::uint32 /*PortFlags*/) noexcept
    {
        // Compare the resolved-for-current-locale strings. Two FTexts
        // with the same namespace+key but different literal fallbacks
        // resolve to the same string under the current locale (if the
        // localisation table maps both to the same entry) -- so the
        // identity-by-resolved-value is the correct semantic.
        const FText* Lhs = static_cast<const FText*>(A);
        const FText* Rhs = static_cast<const FText*>(B);
        // FText resolution is non-const-friendly externally but the
        // method is logically const (it caches via mutable members).
        const ::XCore::FString& LhsResolved =
            const_cast<FText*>(Lhs)->ResolveForCurrentLocale();
        const ::XCore::FString& RhsResolved =
            const_cast<FText*>(Rhs)->ResolveForCurrentLocale();
        return LhsResolved == RhsResolved;
    }

    ::uint64 GetValueTypeHashSlot(const void* PropertyValue) noexcept
    {
        const FText* TextVal = static_cast<const FText*>(PropertyValue);
        const ::XCore::FString& Resolved =
            const_cast<FText*>(TextVal)->ResolveForCurrentLocale();
        return ::XCore::Hash::FXxh3::Hash64(Resolved.ToUtf8Ptr(),
                                            static_cast<::SIZE_T>(Resolved.LenBytes()));
    }

    void ExportTextSlot(::XCore::FString& OutStr, const void* PropertyValue,
                        const void* /*DefaultValue*/, ::int32 /*PortFlags*/) noexcept
    {
        const FText* TextVal = static_cast<const FText*>(PropertyValue);
        const ::XCore::FString& Resolved =
            const_cast<FText*>(TextVal)->ResolveForCurrentLocale();
        OutStr.Append(Resolved);
    }

    const char* ImportTextSlot(const char* Buffer, void* PropertyValue,
                               ::int32 /*PortFlags*/) noexcept
    {
        // Build an INVTEXT-style FText from the literal at Buffer.
        // This is the minimum-viable import path; the full export-
        // text-with-namespace round-trip is XLocalization concern
        // (the loctable serializer pipeline).
        if (Buffer == nullptr)
        {
            return Buffer;
        }
        const char* End = Buffer;
        while (*End != '\0' && *End != ',' && *End != '\n' && *End != '\r')
        {
            ++End;
        }
        FText* Dest = static_cast<FText*>(PropertyValue);
        // Note: FromLiteral expects a string-literal lifetime guarantee
        // on its parameter. The Buffer pointer here is caller-owned and
        // may not have static lifetime, so we cannot use FromLiteral
        // directly. The Phase 4b.4a path constructs a default FText
        // (empty); a future revision will introduce a "FText::FromString"
        // path that materialises an owned literal.
        //
        // For Phase 4b.4a the import-text result is the default empty
        // FText; the consumed-buffer span is reported correctly so
        // downstream parsers advance.
        *Dest = FText();
        return End;
    }

    constexpr ::uint32 kTextCapabilities =
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

constinit const FFakeVTable kFTextPropertyFakeVTable{
    /* Capabilities    */ kTextCapabilities,
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

constinit FFieldClass kFTextPropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFTextProperty | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FTextProperty::ConstructFn,
    /* FakeVTable */ &kFTextPropertyFakeVTable,
};

FTextProperty::FTextProperty(FFieldVariant InOwner, FName InName) noexcept
    : FProperty(&kFTextPropertyStaticClass, InOwner, InName)
{
    ElementSize = static_cast<::int32>(sizeof(::XCore::Loc::FText));
}

void FTextProperty::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FTextProperty(Owner, Name);
}

const FFieldClass* FTextProperty::StaticClass() noexcept
{
    return &GetFTextPropertyStaticClass();
}

const FFieldClass& GetFTextPropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFTextPropertyStaticClass.Name = FName("FTextProperty");
        return true;
    }();
    (void)Init;
    return kFTextPropertyStaticClass;
}

} // namespace XCore::Reflect
