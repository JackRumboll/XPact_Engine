// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FSetProperty.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4b.
// TSet<T> property.
// =====================================================================
//
// FSetProperty's value slot holds a TSet<T> by-value. Same dispatch
// shape as FArrayProperty / FMapProperty: per-instance ElementSize is
// populated at FClass::Link time (Phase 4b.5).
//
// CAPABILITY TRUTH-TABLE DISCIPLINE (XCore-4b Subagent A FIX-A1):
//
//   Prior Phase 4b.4b shape claimed every slot was supported in
//   `kSetCapabilities` while the slot bodies were silently no-ops --
//   a Prime Directive violation ("stub-with-fake-success"). Callers
//   probing HasSlot(ESlot::GetValue) saw true, dispatched, and got
//   nothing back.
//
//   FIX: the value-operation slots that genuinely don't implement
//   their operation at Phase 4b.4b have their `kSetCapabilities` bit
//   CLEARED. Well-behaved callers (probing HasSlot before dispatch)
//   now skip the slot via the FFakeVTable wrapper's nullptr-return
//   on HasSlot=false. Ill-behaved callers that bypass HasSlot and
//   dispatch directly hit XPACT_CHECK(false) and crash loudly.
//
//   ContainsObjectReference IS implemented (returns TRUE
//   conservatively); its bit remains set. The typed walker checks
//   ElementProp for the precise GC scan answer.
//
// =====================================================================

#include "Reflection/FSetProperty.h"

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
    // `kSetCapabilities` below; well-behaved callers skip via HasSlot().
    // Direct dispatch hits XPACT_CHECK(false).
    // -----------------------------------------------------------------

    void GetValueSlot(const void* /*Instance*/, ::int32 /*ElementIndex*/, void* /*OutValue*/) noexcept
    {
        XPACT_CHECK(!"FSetProperty::GetValue dispatch slot not yet implemented; "
                     "Phase 4b.5/XCoreXObject will land typed container traversal. "
                     "Capability bit cleared in kSetCapabilities; HasSlot() returns false.");
    }

    void SetValueSlot(void* /*Instance*/, ::int32 /*ElementIndex*/, const void* /*InValue*/) noexcept
    {
        XPACT_CHECK(!"FSetProperty::SetValue dispatch slot not yet implemented; "
                     "Phase 4b.5/XCoreXObject will land typed container traversal. "
                     "Capability bit cleared in kSetCapabilities; HasSlot() returns false.");
    }

    void CopySingleValueSlot(void* /*Dest*/, const void* /*Src*/) noexcept
    {
        XPACT_CHECK(!"FSetProperty::CopySingleValue dispatch slot not yet implemented; "
                     "Phase 4b.5 will deep-copy via ElementProp iteration. "
                     "Capability bit cleared in kSetCapabilities.");
    }

    void CopyCompleteValueSlot(void* /*Dest*/, const void* /*Src*/, ::int32 /*Count*/) noexcept
    {
        XPACT_CHECK(!"FSetProperty::CopyCompleteValue dispatch slot not yet implemented; "
                     "Phase 4b.5 will deep-copy via ElementProp iteration. "
                     "Capability bit cleared in kSetCapabilities.");
    }

    void InitializeValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
        XPACT_CHECK(!"FSetProperty::InitializeValue dispatch slot not yet implemented; "
                     "Phase 4b.5 will zero-init the TSet header. "
                     "Capability bit cleared in kSetCapabilities.");
    }

    void DestroyValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
        XPACT_CHECK(!"FSetProperty::DestroyValue dispatch slot not yet implemented; "
                     "Phase 4b.5 will release TSet heap storage via ElementProp. "
                     "Capability bit cleared in kSetCapabilities.");
    }

    bool IdenticalSlot(const void* /*A*/, const void* /*B*/, ::uint32 /*PortFlags*/) noexcept
    {
        XPACT_CHECK(!"FSetProperty::Identical dispatch slot not yet implemented; "
                     "Phase 4b.5 will entry-wise compare via ElementProp. "
                     "Capability bit cleared in kSetCapabilities.");
        return false;
    }

    ::uint64 GetValueTypeHashSlot(const void* /*PropertyValue*/) noexcept
    {
        XPACT_CHECK(!"FSetProperty::GetValueTypeHash dispatch slot not yet implemented; "
                     "TSet is not a hashable key type. "
                     "Capability bit cleared in kSetCapabilities.");
        return 0;
    }

    // -----------------------------------------------------------------
    // ContainsObjectReferenceSlot -- LOAD-BEARING; capability bit kept set.
    //
    // Conservative TRUE: TSet slots may contain object references via
    // ElementProp. The typed walker probes ElementProp for the precise
    // answer at FClass::Link time.
    // -----------------------------------------------------------------
    bool ContainsObjectReferenceSlot(
        ::XCore::TArray<const FStructProperty*>& /*EncounteredStructProps*/) noexcept
    {
        return true;
    }

    // -----------------------------------------------------------------
    // kSetCapabilities -- TRUTH TABLE per FIX-A1.
    //
    // SET: ContainsObjectReference (returns conservative TRUE).
    // CLEARED: all value-op slots (Phase 4b.5 deferred).
    // -----------------------------------------------------------------
    constexpr ::uint32 kSetCapabilities =
          CapabilityBit(ESlot::ContainsObjectReference);

} // anonymous

constinit const FFakeVTable kFSetPropertyFakeVTable{
    /* Capabilities    */ kSetCapabilities,
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

constinit FFieldClass kFSetPropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFSetProperty
                   | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FSetProperty::ConstructFn,
    /* FakeVTable */ &kFSetPropertyFakeVTable,
};

FSetProperty::FSetProperty(FFieldVariant InOwner, FName InName,
                           FProperty* InElementProp) noexcept
    : FProperty(&kFSetPropertyStaticClass, InOwner, InName)
    , ElementProp(InElementProp)
{
    // ElementSize populated at FClass::Link time when TSet's sizeof is
    // resolvable (Phase 4b.5).
}

void FSetProperty::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FSetProperty(Owner, Name);
}

const FFieldClass* FSetProperty::StaticClass() noexcept
{
    return &GetFSetPropertyStaticClass();
}

const FFieldClass& GetFSetPropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFSetPropertyStaticClass.Name = FName("FSetProperty");
        return true;
    }();
    (void)Init;
    return kFSetPropertyStaticClass;
}

} // namespace XCore::Reflect
