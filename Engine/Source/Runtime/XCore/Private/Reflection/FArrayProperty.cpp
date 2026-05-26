// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FArrayProperty.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4b.
// TArray<T> property.
// =====================================================================
//
// FArrayProperty's value slot holds a TArray<T> by-value. TArray header
// is 24 bytes on MSVC (XCore-4a Phase 4b.5 verified: 8 m_data + 4 m_num
// + 4 m_capacity + 2 FMemTag + 6 pad).
//
// CAPABILITY TRUTH-TABLE DISCIPLINE (XCore-4b Subagent A FIX-A1):
//
//   Prior Phase 4b.4b shape claimed every slot was supported in
//   `kArrayCapabilities`, but the slot bodies performed shallow
//   TArray-header memcpy WITHOUT deep-copying element storage. The
//   shallow copy produces two TArray handles aliasing the same heap
//   buffer -- destruction via one frees the buffer the other still
//   references. This is the Prime Directive's "no silent corruption"
//   class of bug.
//
//   FIX: every value-operation slot has its `kArrayCapabilities` bit
//   CLEARED at Phase 4b.4b. The shallow-memcpy implementations are
//   replaced with XPACT_CHECK(false) bodies so direct dispatch crashes
//   loudly with a documented diagnostic. Well-behaved callers (probing
//   HasSlot) see false and the FProperty wrapper returns safe
//   sentinels.
//
//   ContainsObjectReference IS structurally important and remains SET:
//   FStruct::ObjectRefProperties population (FIX-13) at FClass::Link
//   time walks this slot. The slot returns conservative TRUE; the
//   typed walker checks Inner->ContainsObjectReference for the
//   precise GC answer.
//
//   Phase 4b.5 will rewire dispatch to perform proper deep-copy via
//   the Inner FProperty's slot table.
//
// =====================================================================

#include "Reflection/FArrayProperty.h"

#include "Reflection/EClassCastFlags.h"
#include "Reflection/FFakeVTable.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FName.h"
#include "Reflection/FProperty.h"

#include "Macros/XPactMacros.h"   // XPACT_CHECK

#include <cstring>
#include <new>

namespace XCore::Reflect
{

namespace
{
    // -----------------------------------------------------------------
    // Unimplemented-slot bodies (FIX-A1). Capability bits CLEARED in
    // `kArrayCapabilities` below; well-behaved callers skip via HasSlot
    // and the FProperty wrapper returns safe sentinels.
    //
    // TODO(Phase 4b.5): rewire to perform deep-copy / element-wise
    // operations via Inner->GetValue / Inner->SetValue / etc.
    // -----------------------------------------------------------------

    void GetValueSlot(const void* /*Instance*/, ::int32 /*ElementIndex*/, void* /*OutValue*/) noexcept
    {
        XPACT_CHECK(!"FArrayProperty::GetValue dispatch slot not yet implemented; "
                     "the prior shallow-memcpy implementation aliases element storage "
                     "(double-free risk). Phase 4b.5 will deep-copy via Inner. "
                     "Capability bit cleared in kArrayCapabilities; HasSlot() returns false.");
    }

    void SetValueSlot(void* /*Instance*/, ::int32 /*ElementIndex*/, const void* /*InValue*/) noexcept
    {
        XPACT_CHECK(!"FArrayProperty::SetValue dispatch slot not yet implemented; "
                     "shallow-memcpy aliases element storage. Phase 4b.5 deep-copies. "
                     "Capability bit cleared in kArrayCapabilities.");
    }

    void CopySingleValueSlot(void* /*Dest*/, const void* /*Src*/) noexcept
    {
        XPACT_CHECK(!"FArrayProperty::CopySingleValue dispatch slot not yet implemented; "
                     "shallow-memcpy aliases element storage. Phase 4b.5 deep-copies. "
                     "Capability bit cleared in kArrayCapabilities.");
    }

    void CopyCompleteValueSlot(void* /*Dest*/, const void* /*Src*/, ::int32 /*Count*/) noexcept
    {
        XPACT_CHECK(!"FArrayProperty::CopyCompleteValue dispatch slot not yet implemented; "
                     "shallow-memcpy aliases element storage. Phase 4b.5 deep-copies. "
                     "Capability bit cleared in kArrayCapabilities.");
    }

    void InitializeValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
        XPACT_CHECK(!"FArrayProperty::InitializeValue dispatch slot not yet implemented; "
                     "header-zero-fill is correct for empty TArray sentinel but the "
                     "underlying TArray ctor (FMemTag attribution etc.) is bypassed. "
                     "Phase 4b.5 will invoke TArray's typed default-ctor. "
                     "Capability bit cleared in kArrayCapabilities.");
    }

    void DestroyValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
        XPACT_CHECK(!"FArrayProperty::DestroyValue dispatch slot not yet implemented; "
                     "the prior no-op leaks the TArray's heap buffer. Phase 4b.5 will "
                     "iterate elements via Inner->DestroyValue then free the header heap. "
                     "Capability bit cleared in kArrayCapabilities.");
    }

    bool IdenticalSlot(const void* /*A*/, const void* /*B*/, ::uint32 /*PortFlags*/) noexcept
    {
        XPACT_CHECK(!"FArrayProperty::Identical dispatch slot not yet implemented; "
                     "prior unconditional-false is incorrect for two equal TArrays. "
                     "Phase 4b.5 will element-wise compare via Inner->Identical. "
                     "Capability bit cleared in kArrayCapabilities.");
        return false;
    }

    ::uint64 GetValueTypeHashSlot(const void* /*PropertyValue*/) noexcept
    {
        XPACT_CHECK(!"FArrayProperty::GetValueTypeHash dispatch slot not yet implemented; "
                     "Phase 4b.5 will element-wise hash via Inner->GetValueTypeHash. "
                     "Capability bit cleared in kArrayCapabilities.");
        return 0;
    }

    // -----------------------------------------------------------------
    // ContainsObjectReferenceSlot -- LOAD-BEARING; capability bit kept set.
    //
    // FStruct::ObjectRefProperties population (FIX-13) at FClass::Link
    // time consults this slot. Returns conservative TRUE: TArray slots
    // may contain object references via Inner. The typed walker then
    // checks Inner->ContainsObjectReference for the precise answer.
    // The slot's TRUE return is the safe upper bound; never causes
    // missed GC roots.
    // -----------------------------------------------------------------
    bool ContainsObjectReferenceSlot(
        ::XCore::TArray<const FStructProperty*>& /*EncounteredStructProps*/) noexcept
    {
        return true;
    }

    // -----------------------------------------------------------------
    // kArrayCapabilities -- TRUTH TABLE per FIX-A1.
    //
    // SET: ContainsObjectReference (returns conservative TRUE).
    // CLEARED: all value-op slots (Phase 4b.5 deferred for Inner-based
    //          deep-copy / element-wise dispatch).
    // -----------------------------------------------------------------
    constexpr ::uint32 kArrayCapabilities =
          CapabilityBit(ESlot::ContainsObjectReference);

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
    // ElementSize is the size of one TArray<T> header. Phase 4b.5
    // verified TArray = 24 bytes on MSVC due to XCore-4a's
    // DefaultAllocator carrying a 2-byte FMemTag for memory attribution
    // (no padding hole to fold into via XPACT_NO_UNIQUE_ADDRESS).
    ElementSize = 24;
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
