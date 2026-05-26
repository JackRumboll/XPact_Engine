// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FBoolProperty.h -- reflected bool / bitfield property
// (XCore-4b §5.5 + §11.2).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.5 + Section 11.2 row `FBoolProperty: 120
// bytes (extends FProperty + FieldSize@104 + ByteOffset@105 +
// ByteMask@106 + FieldMask@107 + _pad@108 + SetBitFunc@112)`.
//
// FBoolProperty handles BOTH the plain `bool` case (FieldSize=1,
// ByteMask=0xFF, FieldMask=0xFF) AND the C bitfield case (e.g.,
// `uint32 bFlag : 1;`) where the bit lives inside a wider integer
// field at a specific offset and mask.
//
// PAYLOAD FIELDS (per §11.2):
//
//   FieldSize   (uint8 @ 104) -- byte size of the containing integer
//                                field. Valid: 1, 2, 4, 8.
//   ByteOffset  (uint8 @ 105) -- offset (in bytes) from the property's
//                                FProperty::Offset to the SPECIFIC byte
//                                containing the bit. For a `uint32`
//                                bitfield with the bit in byte 2, this
//                                is 2.
//   ByteMask    (uint8 @ 106) -- mask within the BYTE pointed at by
//                                (Offset + ByteOffset). E.g., bit 5
//                                of byte 2 -> ByteMask = 0x20.
//   FieldMask   (uint8 @ 107) -- mask within the FIELD (FieldSize bytes
//                                wide). For a 1-bit bitfield this is
//                                always 0xFF (the byte-mask is the
//                                source of truth for which bit; the
//                                field-mask masks-out neighbouring
//                                bits in the same field for the
//                                composite-write path).
//   _pad        (4 bytes @ 108) -- alignment pad to 8-byte boundary
//                                  before SetBitFunc.
//   SetBitFunc  (fn ptr @ 112) -- atomic bit-set callback. Mirrors
//                                UE's FBoolProperty::SetBoolValue
//                                indirection (UnrealType.h:2595);
//                                allows bitfield sets to go through
//                                a typed path that the optimiser can
//                                fold into a single x86 `or`/`and`/
//                                lock-prefix sequence.
//
// CONSTRUCTION:
//
// The default ctor / FConstructFn places a plain-bool FBoolProperty
// (FieldSize=1, ByteMask=0xFF, FieldMask=0xFF, SetBitFunc set to the
// plain-bool path). To declare a bitfield property, the caller
// populates the payload fields after construction (or uses the
// explicit ctor that takes the bitfield parameters).
//
// CastFlag bit: kFBoolProperty (0x01).
//
// =====================================================================

#include "Reflection/FProperty.h"

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // FBoolProperty -- 120-byte FProperty subclass for `bool` /
    // bitfield.
    // -----------------------------------------------------------------
    struct alignas(8) FBoolProperty : public FProperty
    {
        // ---- Per-subclass payload (offsets 104..119) ----

        ::uint8 FieldSize;        // 104 +1   1, 2, 4, or 8 (containing field width)
        ::uint8 ByteOffset;       // 105 +1   byte offset within the field
        ::uint8 ByteMask;         // 106 +1   bit mask within the byte
        ::uint8 FieldMask;        // 107 +1   bit mask within the field
        ::uint32 _padBoolPayload; // 108 +4   alignment pad to 8 bytes

        // SetBitFunc -- function pointer that writes a bool into the
        // property's slot. Signature:
        //
        //   void(*)(void* Instance, bool bValue, ::uint8 ByteOffset,
        //           ::uint8 ByteMask, ::uint8 FieldMask)
        //
        // The pointer is per-instance (NOT per-subclass) because the
        // bit-arithmetic depends on the specific FieldSize / ByteMask
        // combination for THIS property. The .cpp file provides:
        //
        //   * SetBitImpl_PlainBool  -- for FieldSize=1, ByteMask=0xFF
        //   * SetBitImpl_Bitfield   -- for everything else
        //
        // The constructor selects the right impl based on the bitfield
        // parameters.
        using FSetBitFn = void (*)(void* Instance, bool bValue,
                                   ::uint8 ByteOffset, ::uint8 ByteMask,
                                   ::uint8 FieldMask);

        FSetBitFn SetBitFunc;     // 112 +8

        // ---- Construction ----

        constexpr FBoolProperty() noexcept
            : FProperty()
            , FieldSize(1)
            , ByteOffset(0)
            , ByteMask(0xFFu)
            , FieldMask(0xFFu)
            , _padBoolPayload(0)
            , SetBitFunc(nullptr)
        {
        }

        // Plain-bool constructor. Sets FieldSize=1, ByteMask=0xFF,
        // FieldMask=0xFF, SetBitFunc -> plain-bool impl.
        FBoolProperty(FFieldVariant InOwner, FName InName) noexcept;

        // Bitfield constructor.
        //
        //   InFieldSize  : 1, 2, 4, or 8 (the containing field width)
        //   InByteOffset : byte offset within the field
        //   InByteMask   : mask within the byte (single bit; 0x01..0x80)
        //   InFieldMask  : mask within the field
        FBoolProperty(FFieldVariant InOwner, FName InName,
                      ::uint8 InFieldSize, ::uint8 InByteOffset,
                      ::uint8 InByteMask, ::uint8 InFieldMask) noexcept;

        // ---- FConstructFn target ----

        static void ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept;

        // ---- StaticClass hook for Cast<T> ----

        static const FFieldClass* StaticClass() noexcept;

        // ---- Per-subclass accessors ----

        [[nodiscard]] XPACT_FORCEINLINE ::uint8 GetFieldSize() const noexcept  { return FieldSize; }
        [[nodiscard]] XPACT_FORCEINLINE ::uint8 GetByteOffset() const noexcept { return ByteOffset; }
        [[nodiscard]] XPACT_FORCEINLINE ::uint8 GetByteMask() const noexcept   { return ByteMask; }
        [[nodiscard]] XPACT_FORCEINLINE ::uint8 GetFieldMask() const noexcept  { return FieldMask; }

        // True iff this is a single-bit bitfield (NOT a plain bool).
        [[nodiscard]] XPACT_FORCEINLINE bool IsBitfield() const noexcept
        {
            return ByteMask != 0xFFu;
        }

        // Get / set the bool value through the dispatch path.
        // These complement the base FProperty::GetValue / SetValue
        // wrappers; they take a typed bool argument and route through
        // SetBitFunc.
        //
        // Pre: Instance points at the OWNER STRUCT (FProperty's
        // ContainerPtr-to-value-ptr arithmetic is folded in here).
        [[nodiscard]] bool GetBoolValue(const void* Instance) const noexcept;
        void               SetBoolValue(void* Instance, bool bValue) const noexcept;
    };

    // ABI lock (per §11.2 layout table).
    static_assert(sizeof(FBoolProperty)  == 120,
                  "FBoolProperty ABI lock: 120 bytes "
                  "(104 FProperty + 1+1+1+1 metadata + 4 pad + 8 SetBitFunc)");
    static_assert(alignof(FBoolProperty) == 8,
                  "FBoolProperty ABI lock: 8-byte alignment");
    static_assert(offsetof(FBoolProperty, FieldSize)       == 104,
                  "FBoolProperty ABI lock: FieldSize @ offset 104");
    static_assert(offsetof(FBoolProperty, ByteOffset)      == 105,
                  "FBoolProperty ABI lock: ByteOffset @ offset 105");
    static_assert(offsetof(FBoolProperty, ByteMask)        == 106,
                  "FBoolProperty ABI lock: ByteMask @ offset 106");
    static_assert(offsetof(FBoolProperty, FieldMask)       == 107,
                  "FBoolProperty ABI lock: FieldMask @ offset 107");
    static_assert(offsetof(FBoolProperty, SetBitFunc)      == 112,
                  "FBoolProperty ABI lock: SetBitFunc @ offset 112");
    static_assert(::std::is_trivially_copyable_v<FBoolProperty>);
    static_assert(::std::is_trivially_destructible_v<FBoolProperty>);

    extern const FFakeVTable kFBoolPropertyFakeVTable;
    extern FFieldClass       kFBoolPropertyStaticClass;
    const FFieldClass& GetFBoolPropertyStaticClass() noexcept;

} // namespace XCore::Reflect
