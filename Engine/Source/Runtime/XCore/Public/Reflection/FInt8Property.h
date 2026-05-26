// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FInt8Property.h -- reflected int8 property (XCore-4b §5.5 + §11.2).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.5 ("FProperty subclass family") +
// Section 11.2 layout table: `FInt8Property: 104 bytes (no subclass
// payload)`.
//
// FInt8Property is the FProperty subclass for `int8`-typed reflected
// fields. There is no per-subclass payload -- the value is stored
// directly in the owner struct's int8-sized slot at FProperty::Offset.
// The dispatch slots are the shared numeric implementations from
// Detail::*Impl<int8>.
//
// CastFlag bit: EClassCastFlags::kFInt8Property (0x04 = bit 2).
// Cast lineage: FInt8Property -> FProperty -> FField.
//
// =====================================================================

#include "Reflection/FProperty.h"

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // FInt8Property -- 104-byte FProperty subclass for `int8`.
    // -----------------------------------------------------------------
    struct alignas(8) FInt8Property : public FProperty
    {
        // ---- Construction ----

        // Default ctor: structurally-uninitialised. Use for storage
        // allocation followed by placement-new via ConstructFn.
        constexpr FInt8Property() noexcept = default;

        // Explicit ctor. Delegates to FProperty(InClass, InOwner,
        // InName) which sets the FField base + DispatchTable.
        FInt8Property(FFieldVariant InOwner, FName InName) noexcept;

        // ---- FConstructFn target ----

        // Placement-new constructs an FInt8Property at OutStorage.
        // The ClassPrivate slot is set to &kFInt8PropertyStaticClass.
        static void ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept;

        // ---- StaticClass hook for Cast<T> ----

        static const FFieldClass* StaticClass() noexcept;
    };

    // ABI lock.
    static_assert(sizeof(FInt8Property)  == 104,
                  "FInt8Property ABI lock: 104 bytes (no payload beyond FProperty)");
    static_assert(alignof(FInt8Property) == 8,
                  "FInt8Property ABI lock: 8-byte alignment");
    // is_standard_layout_v NOT asserted; FField has data members and
    // FProperty extends it with more data members, which precludes
    // formal standard-layout status. offsetof remains well-defined in
    // practice on every supported compiler.
    static_assert(::std::is_trivially_copyable_v<FInt8Property>,
                  "FInt8Property must be trivially copyable");
    static_assert(::std::is_trivially_destructible_v<FInt8Property>,
                  "FInt8Property must be trivially destructible");

    // -----------------------------------------------------------------
    // The FFieldClass + FFakeVTable for FInt8Property.
    // Definitions live in FInt8Property.cpp.
    // -----------------------------------------------------------------
    extern const FFakeVTable kFInt8PropertyFakeVTable;
    extern FFieldClass       kFInt8PropertyStaticClass;

    const FFieldClass& GetFInt8PropertyStaticClass() noexcept;

} // namespace XCore::Reflect
