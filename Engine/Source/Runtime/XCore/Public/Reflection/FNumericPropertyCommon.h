// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FNumericPropertyCommon.h -- header-only shared dispatch-slot
// implementations for numeric FProperty subclasses (XCore-4b §5.4 +
// §11.2; Phase 4b.4a).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.4 ("FProperty FakeVTable dispatch") +
// Section 5.5 ("FProperty subclass family").
//
// Every numeric primitive FProperty (FInt8Property, FInt16Property,
// FIntProperty, FInt64Property, FUInt16Property, FUInt32Property,
// FUInt64Property, FFloatProperty, FDoubleProperty, FByteProperty)
// shares the same 11-slot dispatch shape:
//
//   * GetValue / SetValue              (slots 0, 1)
//   * CopySingleValue /
//     CopyCompleteValue                (slots 2, 3)
//   * InitializeValue / DestroyValue   (slots 4, 5)
//   * Identical                        (slot  6)
//   * ExportText / ImportText          (slots 10, 11)
//   * GetValueTypeHash                 (slot  12)
//
// Slots NOT populated for primitive numerics (Phase 4b.4a):
//
//   * SerializeItem / NetSerializeItem  (slots 7, 8)  -- need FArchive
//     (XSerialization Layer 9; post-XCore-4b).
//   * ContainsObjectReference (slot 9)  -- primitive numerics never
//     contain XObject references; the FProperty dispatch wrapper
//     short-circuits to `false` when the slot is null.
//   * AppendToSchemaHash (slot 13)      -- deferred to Phase 4b.6
//     (XReflectionRuntime + canonical byte stream).
//   * ConvertFromType (slot 14)         -- XSerialization owns legacy
//     migration paths.
//
// HEADER-ONLY:
//
// The shared per-T templates live in this header because every
// FProperty subclass's FFakeVTable is a constinit instance in
// `.rodata`. Each subclass instantiates the template with its
// underlying numeric type; the linker deduplicates identical
// instantiations across translation units via COMDAT.
//
// CONSTINIT DISCIPLINE:
//
// The FFakeVTable for each numeric subclass is declared in the
// corresponding F<X>Property.cpp via direct brace-init with C-style
// function-pointer casts:
//
//     constinit const FFakeVTable F<X>PropertyFakeVTable{
//         /* Capabilities    */ Detail::kNumericCapabilities,
//         /* _reservedHeader */ 0U,
//         /* Slots           */ {
//             (void(*)(void))&Detail::GetValueImpl<T>,
//             ...
//         },
//     };
//
// The C-style cast `(void(*)(void))&Fn` IS a function-pointer-to-
// function-pointer reinterpret_cast, which is technically not a
// constant expression per [expr.const]/5.10. In practice every major
// compiler (MSVC 19.30+, Clang 13+, GCC 11+) accepts this at constinit
// scope because the storage layout doesn't depend on the cast's
// outcome -- the bytes stored ARE the function's address verbatim.
// The spec §5.4 example uses the same pattern.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Containers/FString.h"
#include "Hash/FXxh3.h"
#include "Reflection/FFakeVTable.h"

#include <charconv>     // std::from_chars / std::to_chars
#include <cstring>      // memcpy / memcmp
#include <type_traits>

namespace XCore::Reflect::Detail
{
    // -----------------------------------------------------------------
    // GetValueImpl<T> -- ESlot::GetValue body.
    //
    // The FProperty dispatch wrapper has ALREADY computed the value
    // pointer via ContainerPtrToValuePtr() and passes IT as
    // `Instance`. For ArrayDim > 1, the wrapper passes the BASE value
    // pointer plus the ElementIndex; the slot adds ElementIndex *
    // sizeof(T) to read the element.
    // -----------------------------------------------------------------
    template <typename T>
    inline void GetValueImpl(const void* Instance, ::int32 ElementIndex, void* OutValue) noexcept
    {
        const T* Src = static_cast<const T*>(Instance) + ElementIndex;
        ::std::memcpy(OutValue, Src, sizeof(T));
    }

    // -----------------------------------------------------------------
    // SetValueImpl<T> -- ESlot::SetValue body.
    // -----------------------------------------------------------------
    template <typename T>
    inline void SetValueImpl(void* Instance, ::int32 ElementIndex, const void* InValue) noexcept
    {
        T* Dest = static_cast<T*>(Instance) + ElementIndex;
        ::std::memcpy(Dest, InValue, sizeof(T));
    }

    // -----------------------------------------------------------------
    // CopySingleValueImpl<T> -- ESlot::CopySingleValue body.
    // -----------------------------------------------------------------
    template <typename T>
    inline void CopySingleValueImpl(void* Dest, const void* Src) noexcept
    {
        ::std::memcpy(Dest, Src, sizeof(T));
    }

    // -----------------------------------------------------------------
    // CopyCompleteValueImpl<T> -- ESlot::CopyCompleteValue body.
    // -----------------------------------------------------------------
    template <typename T>
    inline void CopyCompleteValueImpl(void* Dest, const void* Src, ::int32 Count) noexcept
    {
        XPACT_CHECK(Count >= 0);
        ::std::memcpy(Dest, Src, sizeof(T) * static_cast<::size_t>(Count));
    }

    // -----------------------------------------------------------------
    // InitializeValueImpl<T> -- ESlot::InitializeValue body.
    // -----------------------------------------------------------------
    template <typename T>
    inline void InitializeValueImpl(void* Dest, ::int32 Count) noexcept
    {
        XPACT_CHECK(Count >= 0);
        ::std::memset(Dest, 0, sizeof(T) * static_cast<::size_t>(Count));
    }

    // -----------------------------------------------------------------
    // DestroyValueImpl<T> -- ESlot::DestroyValue body.
    // No-op for arithmetic types (trivially destructible).
    // -----------------------------------------------------------------
    template <typename T>
    inline void DestroyValueImpl(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
        // Trivially destructible; nothing to do.
    }

    // -----------------------------------------------------------------
    // IdenticalImpl<T> -- ESlot::Identical body.
    //
    // For floating-point types we use BYTEWISE comparison (NOT
    // operator==) so the result is consistent across IEEE-754 NaN
    // values. UE's FFloatProperty / FDoubleProperty use the same
    // bytewise pattern.
    // -----------------------------------------------------------------
    template <typename T>
    inline bool IdenticalImpl(const void* A, const void* B, ::uint32 /*PortFlags*/) noexcept
    {
        return ::std::memcmp(A, B, sizeof(T)) == 0;
    }

    // -----------------------------------------------------------------
    // GetValueTypeHashImpl<T> -- ESlot::GetValueTypeHash body.
    //
    // FXxh3-64 over the underlying T bytes.
    // -----------------------------------------------------------------
    template <typename T>
    inline ::uint64 GetValueTypeHashImpl(const void* PropertyValue) noexcept
    {
        return ::XCore::Hash::FXxh3::Hash64(PropertyValue, sizeof(T));
    }

    // -----------------------------------------------------------------
    // ExportTextImpl<T> -- ESlot::ExportText body.
    //
    // Formats the underlying T into OutStr via std::to_chars (locale-
    // neutral). The OutStr is APPENDED to.
    // -----------------------------------------------------------------
    template <typename T>
    inline void ExportTextImpl(
        ::XCore::FString& OutStr,
        const void*       PropertyValue,
        const void*       /*DefaultValue*/,
        ::int32           /*PortFlags*/) noexcept
    {
        T Value;
        ::std::memcpy(&Value, PropertyValue, sizeof(T));

        char Buffer[64] = {};
        auto Result = ::std::to_chars(Buffer, Buffer + sizeof(Buffer), Value);
        if (Result.ec == ::std::errc())
        {
            const ::int32 Len = static_cast<::int32>(Result.ptr - Buffer);
            OutStr.Append(Buffer, Len);
        }
    }

    // -----------------------------------------------------------------
    // ImportTextImpl<T> -- ESlot::ImportText body.
    //
    // Parses a textual numeric from a NUL-terminated buffer via
    // std::from_chars. Returns the advanced buffer pointer; on parse
    // failure, returns Buffer unchanged.
    // -----------------------------------------------------------------
    template <typename T>
    inline const char* ImportTextImpl(
        const char* Buffer,
        void*       PropertyValue,
        ::int32     /*PortFlags*/) noexcept
    {
        if (Buffer == nullptr)
        {
            return Buffer;
        }

        // Walk to the buffer's NUL terminator. Callers SHOULD pass a
        // NUL-terminated input; the slot does not accept an
        // unterminated byte buffer at Phase 4b.4a.
        const char* End = Buffer;
        while (*End != '\0')
        {
            ++End;
        }

        T Value{};
        auto Result = ::std::from_chars(Buffer, End, Value);
        if (Result.ec != ::std::errc())
        {
            return Buffer;
        }
        ::std::memcpy(PropertyValue, &Value, sizeof(T));
        return Result.ptr;
    }

    // -----------------------------------------------------------------
    // kNumericCapabilities -- the Capabilities bitmask for the shared
    // numeric subclass shape.
    //
    // Slots populated (bits): 0, 1, 2, 3, 4, 5, 6, 10, 11, 12.
    // Slots empty (bits):     7 SerializeItem, 8 NetSerializeItem,
    //                         9 ContainsObjectReference,
    //                         13 AppendToSchemaHash,
    //                         14 ConvertFromType.
    //
    // Computed at compile time; the constexpr fold is well-defined
    // because CapabilityBit is constexpr.
    // -----------------------------------------------------------------
    inline constexpr ::uint32 kNumericCapabilities =
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

} // namespace XCore::Reflect::Detail
