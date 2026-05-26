// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FObjectProperty.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4b.
// Strong XObject pointer property.
// =====================================================================
//
// FObjectProperty's value slot is a single 8-byte pointer (`FObject*`).
// The dispatch slots operate on pointer-sized chunks:
//
//   * GetValue / SetValue                -- copy 8 bytes (the pointer).
//   * CopySingleValue / CopyCompleteValue -- copy N * 8 bytes.
//   * InitializeValue                    -- zero N * 8 bytes (nullptr).
//   * DestroyValue                       -- no-op (pointer is not owned).
//   * Identical                          -- pointer equality.
//   * GetValueTypeHash                   -- hash the pointer bits.
//   * ContainsObjectReference            -- ALWAYS true (the slot IS
//                                          an XObject reference).
//   * ExportText / ImportText            -- stubs at Phase 4b.4b
//                                          (need FObject::GetName /
//                                          FName-resolved class lookup;
//                                          full path lands at System 5).
//   * SerializeItem / NetSerializeItem   -- nullptr (FArchive lands
//                                          at XSerialization Layer 9).
//   * AppendToSchemaHash / ConvertFromType -- nullptr at Phase 4b.4b.
//
// =====================================================================

#include "Reflection/FObjectProperty.h"

#include "Containers/FString.h"
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
    // -----------------------------------------------------------------
    // GetValueSlot -- copy the 8-byte pointer at Instance + ElementIndex
    // into OutValue.
    // -----------------------------------------------------------------
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
        // Initialise to nullptr.
        ::std::memset(Dest, 0, sizeof(FObject*) * static_cast<::size_t>(Count));
    }

    void DestroyValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
        // Pointer is NOT owned by the FObjectProperty -- GC owns the
        // pointed-at FObject lifetime. The destroy path is a no-op
        // (the dropped reference is observed by the next GC walk).
    }

    bool IdenticalSlot(const void* A, const void* B, ::uint32 /*PortFlags*/) noexcept
    {
        // Pointer equality. Two FObjectProperty slots are Identical iff
        // they point at the same FObject (or both are nullptr).
        return *static_cast<const FObject* const*>(A)
            == *static_cast<const FObject* const*>(B);
    }

    ::uint64 GetValueTypeHashSlot(const void* PropertyValue) noexcept
    {
        // Hash the pointer bits. Two FObjectProperty slots pointing at
        // the same FObject hash identically.
        const void* Ptr = nullptr;
        ::std::memcpy(&Ptr, PropertyValue, sizeof(Ptr));
        return ::XCore::Hash::FXxh3::Hash64(&Ptr, sizeof(Ptr));
    }

    // -----------------------------------------------------------------
    // ContainsObjectReferenceSlot -- declares that THIS property's
    // value IS an XObject reference (FIX-13).
    //
    // GC scan reachability: every FObjectProperty's slot must be
    // walked. The EncounteredStructProps argument is unused here (only
    // FStructProperty / FArrayProperty / FMapProperty / FSetProperty
    // need it to track recursion into nested struct definitions).
    // -----------------------------------------------------------------
    bool ContainsObjectReferenceSlot(
        ::XCore::TArray<const FStructProperty*>& /*EncounteredStructProps*/) noexcept
    {
        return true;
    }

    constexpr ::uint32 kObjectCapabilities =
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

constinit const FFakeVTable kFObjectPropertyFakeVTable{
    /* Capabilities    */ kObjectCapabilities,
    /* _reservedHeader */ 0U,
    /* Slots           */ {
        (void(*)(void))&GetValueSlot,
        (void(*)(void))&SetValueSlot,
        (void(*)(void))&CopySingleValueSlot,
        (void(*)(void))&CopyCompleteValueSlot,
        (void(*)(void))&InitializeValueSlot,
        (void(*)(void))&DestroyValueSlot,
        (void(*)(void))&IdenticalSlot,
        nullptr,                                          // 7  SerializeItem
        nullptr,                                          // 8  NetSerializeItem
        (void(*)(void))&ContainsObjectReferenceSlot,      // 9  ContainsObjectReference
        nullptr,                                          // 10 ExportText
        nullptr,                                          // 11 ImportText
        (void(*)(void))&GetValueTypeHashSlot,
        nullptr,                                          // 13 AppendToSchemaHash
        nullptr,                                          // 14 ConvertFromType
    },
};

constinit FFieldClass kFObjectPropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFObjectProperty
                   | EClassCastFlags::kFObjectPropertyBase
                   | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FObjectProperty::ConstructFn,
    /* FakeVTable */ &kFObjectPropertyFakeVTable,
};

FObjectProperty::FObjectProperty(FFieldVariant InOwner, FName InName,
                                 FClass* InPropertyClass) noexcept
    : FProperty(&kFObjectPropertyStaticClass, InOwner, InName)
    , PropertyClass(InPropertyClass)
{
    // Element is a pointer (8 bytes).
    ElementSize = static_cast<::int32>(sizeof(FObject*));
}

void FObjectProperty::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FObjectProperty(Owner, Name);
}

const FFieldClass* FObjectProperty::StaticClass() noexcept
{
    return &GetFObjectPropertyStaticClass();
}

const FFieldClass& GetFObjectPropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFObjectPropertyStaticClass.Name = FName("FObjectProperty");
        return true;
    }();
    (void)Init;
    return kFObjectPropertyStaticClass;
}

} // namespace XCore::Reflect
