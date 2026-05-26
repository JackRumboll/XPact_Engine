// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FArrayProperty.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4b.
// TArray<T> property.
// =====================================================================
//
// FArrayProperty's value slot holds a TArray<T> by-value (16 bytes
// per XCore-4a Rev 3 EBO TArray header layout).
//
// SLOT BEHAVIOUR:
//
//   * GetValue / SetValue                -- 16-byte memcpy of the TArray
//                                          header. Doesn't deep-copy the
//                                          element storage; the caller
//                                          gets a shallow-equivalent
//                                          handle (Phase 4b.5 will refine
//                                          to deep-copy via Inner).
//   * CopySingleValue                    -- 16-byte memcpy.
//   * CopyCompleteValue                  -- N * 16-byte memcpy.
//   * InitializeValue                    -- zero 16 bytes per element
//                                          (the TArray's "empty" sentinel).
//   * DestroyValue                       -- no-op at Phase 4b.4b (Phase
//                                          4b.5 will release element
//                                          storage via Inner->Destroy
//                                          loop).
//   * Identical                          -- no-op compare returning false
//                                          at Phase 4b.4b (Phase 4b.5
//                                          element-wise compare via Inner).
//   * GetValueTypeHash                   -- returns 0 at Phase 4b.4b.
//   * ContainsObjectReference            -- DELEGATES TO Inner. THIS IS
//                                          THE LOAD-BEARING SLOT: it is
//                                          what FStruct::ObjectRefProperties
//                                          population (FIX-13) walks at
//                                          FClass::Link time.
//   * ExportText / ImportText            -- nullptr at Phase 4b.4b.
//   * SerializeItem / NetSerializeItem   -- nullptr.
//   * AppendToSchemaHash / ConvertFromType -- nullptr.
//
// NOTE: TArray storage size is 16 bytes (XCore-4a §5.1 fix B-M2 EBO).
//
// =====================================================================

#include "Reflection/FArrayProperty.h"

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
    // TArray<T> header size per XCore-4a Rev 3: 16 bytes (Data + Num +
    // Max via EBO with the allocator).
    constexpr ::size_t kTArrayHeaderSize = 16;

    void GetValueSlot(const void* Instance, ::int32 ElementIndex, void* OutValue) noexcept
    {
        const ::uint8* Src =
            static_cast<const ::uint8*>(Instance) + ElementIndex * kTArrayHeaderSize;
        ::std::memcpy(OutValue, Src, kTArrayHeaderSize);
    }

    void SetValueSlot(void* Instance, ::int32 ElementIndex, const void* InValue) noexcept
    {
        ::uint8* Dest =
            static_cast<::uint8*>(Instance) + ElementIndex * kTArrayHeaderSize;
        ::std::memcpy(Dest, InValue, kTArrayHeaderSize);
    }

    void CopySingleValueSlot(void* Dest, const void* Src) noexcept
    {
        ::std::memcpy(Dest, Src, kTArrayHeaderSize);
    }

    void CopyCompleteValueSlot(void* Dest, const void* Src, ::int32 Count) noexcept
    {
        ::std::memcpy(Dest, Src, kTArrayHeaderSize * static_cast<::size_t>(Count));
    }

    void InitializeValueSlot(void* Dest, ::int32 Count) noexcept
    {
        // Zero the TArray header bytes (the "empty array" sentinel:
        // Data=nullptr, Num=0, Max=0).
        ::std::memset(Dest, 0, kTArrayHeaderSize * static_cast<::size_t>(Count));
    }

    void DestroyValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
        // Phase 4b.4b: no-op. The TArray header's heap pointer (if
        // any) is leaked under this slot at Phase 4b.4b. Phase 4b.5
        // will iterate elements via Inner->DestroyValue then free
        // the header's heap.
    }

    bool IdenticalSlot(const void* /*A*/, const void* /*B*/, ::uint32 /*PortFlags*/) noexcept
    {
        // Phase 4b.4b: structurally returns false; Phase 4b.5 will
        // element-wise compare via Inner->Identical.
        return false;
    }

    ::uint64 GetValueTypeHashSlot(const void* /*PropertyValue*/) noexcept
    {
        // Phase 4b.4b: TArrays are NOT keys in TMap; the wrapper returns
        // 0 (the "not hashable" sentinel) for unpopulated slots, but
        // the slot IS populated here -- returning 0 forces the caller
        // to not use TArray as a hash key at Phase 4b.4b. Phase 4b.5
        // will element-wise hash via Inner->GetValueTypeHash.
        return 0;
    }

    // -----------------------------------------------------------------
    // ContainsObjectReferenceSlot -- LOAD-BEARING; delegates to Inner.
    //
    // This is the slot that FStruct::ObjectRefProperties population
    // (FIX-13) walks at FClass::Link time. If Inner->ContainsObjectRef
    // is true (e.g., TArray<XObject*>), then GC must scan each element
    // for reachability roots.
    //
    // The slot dispatch shape is signature-only (no per-instance
    // FArrayProperty*); we cannot access Inner from inside the slot
    // generically.
    //
    // RESOLUTION (Phase 4b.4b): the slot returns true unconditionally
    // here, which is the CONSERVATIVE answer ("treat every TArray as
    // potentially-containing object references"). The FStruct walker
    // will then perform a finer-grained check via the FArrayProperty
    // accessor (Inner->ContainsObjectReference). The slot's true
    // return is the safe upper bound; the typed walker is the precise
    // arbiter.
    //
    // Phase 4b.5 may refine this with an FFakeVTable extension that
    // passes the FProperty* to slots needing per-instance context;
    // until then the conservative answer is correct (it never causes
    // missed GC roots; it may cause one extra typed call per
    // FArrayProperty per scan).
    // -----------------------------------------------------------------
    bool ContainsObjectReferenceSlot(
        ::XCore::TArray<const FStructProperty*>& /*EncounteredStructProps*/) noexcept
    {
        // Conservative: TArray slots may contain object references;
        // the typed walker will check Inner->ContainsObjectReference
        // to confirm.
        return true;
    }

    constexpr ::uint32 kArrayCapabilities =
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

constinit const FFakeVTable kFArrayPropertyFakeVTable{
    /* Capabilities    */ kArrayCapabilities,
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

constinit FFieldClass kFArrayPropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFArrayProperty
                   | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FArrayProperty::ConstructFn,
    /* FakeVTable */ &kFArrayPropertyFakeVTable,
};

FArrayProperty::FArrayProperty(FFieldVariant InOwner, FName InName,
                               FProperty* InInner) noexcept
    : FProperty(&kFArrayPropertyStaticClass, InOwner, InName)
    , Inner(InInner)
    , ArrayFlags(0)
    , _padArrayPayload(0)
{
    // ElementSize is the size of one TArray<T> header (16 bytes).
    ElementSize = static_cast<::int32>(kTArrayHeaderSize);
}

void FArrayProperty::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FArrayProperty(Owner, Name);
}

const FFieldClass* FArrayProperty::StaticClass() noexcept
{
    return &GetFArrayPropertyStaticClass();
}

const FFieldClass& GetFArrayPropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFArrayPropertyStaticClass.Name = FName("FArrayProperty");
        return true;
    }();
    (void)Init;
    return kFArrayPropertyStaticClass;
}

} // namespace XCore::Reflect
