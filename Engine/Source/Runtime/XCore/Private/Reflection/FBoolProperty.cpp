// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FBoolProperty.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4a. bool +
// bitfield type.
// =====================================================================
//
// The FBoolProperty's dispatch slots are SPECIALISED (not shared with
// the numeric template) because:
//
//   * GetValue / SetValue must route through the SetBitFunc indirection
//     for the bitfield path.
//   * Identical needs to compare BOOL values, not byte/word values
//     (mismatched bitfield representations of `true` -- 0x01 vs 0xFF --
//     are still semantically identical).
//   * ExportText / ImportText emit "true" / "false" (NOT "1" / "0").
//
// All payload-dependent slots are non-template free functions; the
// FFakeVTable points at THESE functions, not template instantiations.
//
// =====================================================================

#include "Reflection/FBoolProperty.h"

#include "Containers/FString.h"
#include "Reflection/EClassCastFlags.h"
#include "Reflection/FFakeVTable.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FName.h"
#include "Reflection/FProperty.h"
#include "Hash/FXxh3.h"

#include <cstring>
#include <new>

namespace XCore::Reflect
{

namespace
{
    // -----------------------------------------------------------------
    // SetBitImpl_PlainBool -- write a whole byte (FieldSize=1,
    // ByteMask=0xFF).
    // -----------------------------------------------------------------
    void SetBitImpl_PlainBool(void* Instance, bool bValue,
                              ::uint8 /*ByteOffset*/,
                              ::uint8 /*ByteMask*/,
                              ::uint8 /*FieldMask*/) noexcept
    {
        *static_cast<::uint8*>(Instance) = bValue ? ::uint8(1) : ::uint8(0);
    }

    // -----------------------------------------------------------------
    // SetBitImpl_Bitfield -- write a single bit within a byte.
    //
    // The Instance pointer is the property's resolved-value-pointer
    // (Container + Offset). The bit lies at byte (Instance + ByteOffset)
    // masked by ByteMask.
    // -----------------------------------------------------------------
    void SetBitImpl_Bitfield(void* Instance, bool bValue,
                             ::uint8 ByteOffset,
                             ::uint8 ByteMask,
                             ::uint8 /*FieldMask*/) noexcept
    {
        ::uint8* Byte = static_cast<::uint8*>(Instance) + ByteOffset;
        if (bValue)
        {
            *Byte |= ByteMask;
        }
        else
        {
            *Byte &= static_cast<::uint8>(~ByteMask);
        }
    }

    // -----------------------------------------------------------------
    // Read the bool value through the (FieldSize, ByteOffset, ByteMask)
    // metadata. Instance is the resolved-value-pointer.
    // -----------------------------------------------------------------
    bool GetBoolValueImpl(const void* Instance, ::uint8 ByteOffset,
                          ::uint8 ByteMask) noexcept
    {
        const ::uint8* Byte = static_cast<const ::uint8*>(Instance) + ByteOffset;
        return (*Byte & ByteMask) != 0;
    }

    // -----------------------------------------------------------------
    // Dispatch-slot bodies.
    //
    // The slots receive the resolved VALUE pointer (NOT the owner
    // struct pointer). The FProperty wrapper has folded the
    // ContainerPtrToValuePtr arithmetic in.
    //
    // For FBoolProperty the slots are AGNOSTIC to the per-instance
    // metadata (FieldSize/ByteOffset/ByteMask). The metadata is
    // captured in the per-instance SetBitFunc + the property's own
    // metadata fields. The slot signatures take the value pointer
    // verbatim; the metadata is implicit in the SetBitFunc that the
    // dispatch wrapper used to write the value.
    //
    // GetValue / SetValue write a bool (1 byte; 0 or 1) into OutValue /
    // read from InValue. The Instance parameter is the resolved value
    // pointer. The slot does NOT consult the bitfield metadata -- it
    // operates on whatever byte the value-pointer points at, returning
    // it as a bool. For the bitfield case, callers should NOT use
    // GetValue / SetValue directly; they should use GetBoolValue /
    // SetBoolValue on the FBoolProperty subclass which routes through
    // the SetBitFunc.
    //
    // FOR PLAIN BOOL (the dominant case): the dispatch slot is the
    // identity copy (memcpy 1 byte).
    // -----------------------------------------------------------------

    void GetValueSlot(const void* Instance, ::int32 ElementIndex, void* OutValue) noexcept
    {
        // For plain bool, the value IS the byte; for bitfield, the
        // caller MUST use the typed GetBoolValue path.
        const ::uint8* Src = static_cast<const ::uint8*>(Instance) + ElementIndex;
        ::std::memcpy(OutValue, Src, sizeof(::uint8));
    }

    void SetValueSlot(void* Instance, ::int32 ElementIndex, const void* InValue) noexcept
    {
        ::uint8* Dest = static_cast<::uint8*>(Instance) + ElementIndex;
        ::std::memcpy(Dest, InValue, sizeof(::uint8));
    }

    void CopySingleValueSlot(void* Dest, const void* Src) noexcept
    {
        ::std::memcpy(Dest, Src, sizeof(::uint8));
    }

    void CopyCompleteValueSlot(void* Dest, const void* Src, ::int32 Count) noexcept
    {
        ::std::memcpy(Dest, Src, sizeof(::uint8) * static_cast<::size_t>(Count));
    }

    void InitializeValueSlot(void* Dest, ::int32 Count) noexcept
    {
        ::std::memset(Dest, 0, sizeof(::uint8) * static_cast<::size_t>(Count));
    }

    void DestroyValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
        // Trivial.
    }

    bool IdenticalSlot(const void* A, const void* B, ::uint32 /*PortFlags*/) noexcept
    {
        // Bool equality: true iff both sides are non-zero OR both
        // sides are zero. (For non-bitfield bool the byte representation
        // is canonical: 0 or 1; for bitfield bool the masked byte may
        // be 0 or any nonzero value -- we want both to compare equal
        // if either has the bit set.)
        const ::uint8 LhsBool = (*static_cast<const ::uint8*>(A)) ? ::uint8(1) : ::uint8(0);
        const ::uint8 RhsBool = (*static_cast<const ::uint8*>(B)) ? ::uint8(1) : ::uint8(0);
        return LhsBool == RhsBool;
    }

    ::uint64 GetValueTypeHashSlot(const void* PropertyValue) noexcept
    {
        // Canonical-form hash: hash the canonical 0/1 representation
        // so two bitfield representations of `true` (0x01 vs 0xFF)
        // both produce the same hash.
        const ::uint8 BoolByte = (*static_cast<const ::uint8*>(PropertyValue))
                                    ? ::uint8(1) : ::uint8(0);
        return ::XCore::Hash::FXxh3::Hash64(&BoolByte, sizeof(BoolByte));
    }

    void ExportTextSlot(::XCore::FString& OutStr, const void* PropertyValue,
                        const void* /*DefaultValue*/, ::int32 /*PortFlags*/) noexcept
    {
        const bool bValue = (*static_cast<const ::uint8*>(PropertyValue)) != 0;
        if (bValue)
        {
            OutStr.Append("true", 4);
        }
        else
        {
            OutStr.Append("false", 5);
        }
    }

    const char* ImportTextSlot(const char* Buffer, void* PropertyValue,
                               ::int32 /*PortFlags*/) noexcept
    {
        if (Buffer == nullptr)
        {
            return Buffer;
        }

        // Accept "true" / "True" / "TRUE" + "1" -> true; "false" /
        // "False" / "FALSE" + "0" -> false. The compare is byte-wise
        // case-insensitive on the literal "true" / "false" prefixes.
        auto MatchPrefix = [](const char* B, const char* Lit, ::int32 LitLen) -> bool {
            for (::int32 i = 0; i < LitLen; ++i)
            {
                char Bc = B[i];
                if (Bc == '\0') return false;
                // Case-insensitive: tolower on ASCII letters only.
                if (Bc >= 'A' && Bc <= 'Z') Bc = static_cast<char>(Bc + ('a' - 'A'));
                if (Bc != Lit[i]) return false;
            }
            return true;
        };

        if (MatchPrefix(Buffer, "true", 4))
        {
            *static_cast<::uint8*>(PropertyValue) = 1;
            return Buffer + 4;
        }
        if (MatchPrefix(Buffer, "false", 5))
        {
            *static_cast<::uint8*>(PropertyValue) = 0;
            return Buffer + 5;
        }
        if (*Buffer == '1')
        {
            *static_cast<::uint8*>(PropertyValue) = 1;
            return Buffer + 1;
        }
        if (*Buffer == '0')
        {
            *static_cast<::uint8*>(PropertyValue) = 0;
            return Buffer + 1;
        }
        return Buffer;  // parse failure
    }

    constexpr ::uint32 kBoolCapabilities =
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

constinit const FFakeVTable kFBoolPropertyFakeVTable{
    /* Capabilities    */ kBoolCapabilities,
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

constinit FFieldClass kFBoolPropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFBoolProperty | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FBoolProperty::ConstructFn,
    /* FakeVTable */ &kFBoolPropertyFakeVTable,
};

// =====================================================================
// FBoolProperty plain-bool constructor.
//
// FieldSize=1, ByteOffset=0, ByteMask=0xFF, FieldMask=0xFF; SetBitFunc
// points at the plain-bool path.
// =====================================================================
FBoolProperty::FBoolProperty(FFieldVariant InOwner, FName InName) noexcept
    : FProperty(&kFBoolPropertyStaticClass, InOwner, InName)
    , FieldSize(1)
    , ByteOffset(0)
    , ByteMask(0xFFu)
    , FieldMask(0xFFu)
    , _padBoolPayload(0)
    , SetBitFunc(&SetBitImpl_PlainBool)
{
    ElementSize = static_cast<::int32>(sizeof(::uint8));
}

// =====================================================================
// FBoolProperty bitfield constructor.
//
// Selects SetBitImpl_PlainBool vs SetBitImpl_Bitfield based on whether
// the ByteMask is 0xFF (plain) or any other value (bitfield).
// =====================================================================
FBoolProperty::FBoolProperty(FFieldVariant InOwner, FName InName,
                             ::uint8 InFieldSize, ::uint8 InByteOffset,
                             ::uint8 InByteMask, ::uint8 InFieldMask) noexcept
    : FProperty(&kFBoolPropertyStaticClass, InOwner, InName)
    , FieldSize(InFieldSize)
    , ByteOffset(InByteOffset)
    , ByteMask(InByteMask)
    , FieldMask(InFieldMask)
    , _padBoolPayload(0)
    , SetBitFunc(InByteMask == 0xFFu ? &SetBitImpl_PlainBool : &SetBitImpl_Bitfield)
{
    XPACT_CHECK(InFieldSize == 1 || InFieldSize == 2 ||
                InFieldSize == 4 || InFieldSize == 8);
    ElementSize = static_cast<::int32>(InFieldSize);
}

void FBoolProperty::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FBoolProperty(Owner, Name);  // plain-bool default
}

const FFieldClass* FBoolProperty::StaticClass() noexcept
{
    return &GetFBoolPropertyStaticClass();
}

const FFieldClass& GetFBoolPropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFBoolPropertyStaticClass.Name = FName("FBoolProperty");
        return true;
    }();
    (void)Init;
    return kFBoolPropertyStaticClass;
}

// =====================================================================
// FBoolProperty::GetBoolValue / SetBoolValue -- typed accessors that
// route through the bitfield-aware path.
// =====================================================================
bool FBoolProperty::GetBoolValue(const void* Instance) const noexcept
{
    XPACT_CHECK(Instance != nullptr);
    const void* ValuePtr = ContainerPtrToValuePtr(Instance);
    return GetBoolValueImpl(ValuePtr, ByteOffset, ByteMask);
}

void FBoolProperty::SetBoolValue(void* Instance, bool bValue) const noexcept
{
    XPACT_CHECK(Instance != nullptr);
    XPACT_CHECK(SetBitFunc != nullptr);
    void* ValuePtr = ContainerPtrToValuePtr(Instance);
    SetBitFunc(ValuePtr, bValue, ByteOffset, ByteMask, FieldMask);
}

} // namespace XCore::Reflect
