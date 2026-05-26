// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FMulticastInlineDelegateProperty.cpp -- XCore-4b §5.5 + §11.2;
// Phase 4b.4b. Inline multicast delegate property
// (Rev 2 MVP add per FIX-3).
// =====================================================================
//
// FMulticastInlineDelegateProperty's value slot holds an
// FMulticastInlineDelegate by-value (inline list of subscriber
// {FObject*, FName} pairs). Phase 4b.4b treats the slot's bytes as
// opaque -- the runtime size is implementation-specific; full
// dispatch lands at Phase 4b.5.
//
// CAPABILITY TRUTH-TABLE DISCIPLINE (XCore-4b Subagent A FIX-A1):
//
//   Prior Phase 4b.4b shape claimed every slot was supported while
//   the slot bodies were silently no-ops. FIX: the value-operation
//   slots have their `kMulticastInlineCapabilities` bit CLEARED.
//   Direct dispatch (bypassing HasSlot) hits XPACT_CHECK(false).
//
//   ContainsObjectReference IS implemented (returns TRUE; subscriber
//   Targets are GC roots). Its bit remains set.
//
// =====================================================================

#include "Reflection/FMulticastInlineDelegateProperty.h"

#include "Reflection/EClassCastFlags.h"
#include "Reflection/FFakeVTable.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FName.h"
#include "Reflection/FProperty.h"

#include "Macros/XPactMacros.h"   // XPACT_CHECK

#include <new>

namespace XCore::Reflect
{

namespace
{
    // -----------------------------------------------------------------
    // Unimplemented-slot bodies (FIX-A1). Capability bits CLEARED in
    // `kMulticastInlineCapabilities` below.
    //
    // TODO(Phase 4b.5): wire the slots to typed dispatch via
    // FMulticastInlineDelegate's value-type operations once the type
    // ships.
    // -----------------------------------------------------------------

    void GetValueSlot(const void* /*Instance*/, ::int32 /*ElementIndex*/, void* /*OutValue*/) noexcept
    {
        XPACT_CHECK(!"FMulticastInlineDelegateProperty::GetValue dispatch slot not yet "
                     "implemented; Phase 4b.5 will land typed delegate traversal. "
                     "Capability bit cleared in kMulticastInlineCapabilities.");
    }

    void SetValueSlot(void* /*Instance*/, ::int32 /*ElementIndex*/, const void* /*InValue*/) noexcept
    {
        XPACT_CHECK(!"FMulticastInlineDelegateProperty::SetValue dispatch slot not yet "
                     "implemented; Phase 4b.5 will land typed delegate traversal. "
                     "Capability bit cleared in kMulticastInlineCapabilities.");
    }

    void CopySingleValueSlot(void* /*Dest*/, const void* /*Src*/) noexcept
    {
        XPACT_CHECK(!"FMulticastInlineDelegateProperty::CopySingleValue dispatch slot not "
                     "yet implemented; Phase 4b.5 will deep-copy subscriber list. "
                     "Capability bit cleared in kMulticastInlineCapabilities.");
    }

    void CopyCompleteValueSlot(void* /*Dest*/, const void* /*Src*/, ::int32 /*Count*/) noexcept
    {
        XPACT_CHECK(!"FMulticastInlineDelegateProperty::CopyCompleteValue dispatch slot "
                     "not yet implemented; Phase 4b.5 will deep-copy subscriber list. "
                     "Capability bit cleared in kMulticastInlineCapabilities.");
    }

    void InitializeValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
        XPACT_CHECK(!"FMulticastInlineDelegateProperty::InitializeValue dispatch slot not "
                     "yet implemented; Phase 4b.5 will zero-init the delegate header. "
                     "Capability bit cleared in kMulticastInlineCapabilities.");
    }

    void DestroyValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
        XPACT_CHECK(!"FMulticastInlineDelegateProperty::DestroyValue dispatch slot not yet "
                     "implemented; Phase 4b.5 will release subscriber list storage. "
                     "Capability bit cleared in kMulticastInlineCapabilities.");
    }

    bool IdenticalSlot(const void* /*A*/, const void* /*B*/, ::uint32 /*PortFlags*/) noexcept
    {
        XPACT_CHECK(!"FMulticastInlineDelegateProperty::Identical dispatch slot not yet "
                     "implemented; Phase 4b.5 will subscriber-wise compare. "
                     "Capability bit cleared in kMulticastInlineCapabilities.");
        return false;
    }

    ::uint64 GetValueTypeHashSlot(const void* /*PropertyValue*/) noexcept
    {
        XPACT_CHECK(!"FMulticastInlineDelegateProperty::GetValueTypeHash dispatch slot "
                     "not yet implemented; multicast delegates are not hashable keys. "
                     "Capability bit cleared in kMulticastInlineCapabilities.");
        return 0;
    }

    // -----------------------------------------------------------------
    // ContainsObjectReferenceSlot -- LOAD-BEARING; capability bit kept set.
    //
    // Subscriber list contains FObject* Target references; the slot
    // returns TRUE so FStruct::ObjectRefProperties population (FIX-13)
    // includes this property at FClass::Link time.
    // -----------------------------------------------------------------
    bool ContainsObjectReferenceSlot(
        ::XCore::TArray<const FStructProperty*>& /*EncounteredStructProps*/) noexcept
    {
        return true;
    }

    // -----------------------------------------------------------------
    // kMulticastInlineCapabilities -- TRUTH TABLE per FIX-A1.
    //
    // SET: ContainsObjectReference (returns TRUE).
    // CLEARED: all value-op slots (Phase 4b.5 deferred).
    // -----------------------------------------------------------------
    constexpr ::uint32 kMulticastInlineCapabilities =
          CapabilityBit(ESlot::ContainsObjectReference);

} // anonymous

constinit const FFakeVTable kFMulticastInlineDelegatePropertyFakeVTable{
    /* Capabilities    */ kMulticastInlineCapabilities,
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

constinit FFieldClass kFMulticastInlineDelegatePropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFMulticastInlineDelegateProperty
                   | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FMulticastInlineDelegateProperty::ConstructFn,
    /* FakeVTable */ &kFMulticastInlineDelegatePropertyFakeVTable,
};

FMulticastInlineDelegateProperty::FMulticastInlineDelegateProperty(
    FFieldVariant InOwner, FName InName,
    FFunctionDescriptor* InSignatureFunction) noexcept
    : FProperty(&kFMulticastInlineDelegatePropertyStaticClass, InOwner, InName)
    , SignatureFunction(InSignatureFunction)
{
    // ElementSize populated at FClass::Link time (Phase 4b.5).
}

void FMulticastInlineDelegateProperty::ConstructFn(
    FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FMulticastInlineDelegateProperty(Owner, Name);
}

const FFieldClass* FMulticastInlineDelegateProperty::StaticClass() noexcept
{
    return &GetFMulticastInlineDelegatePropertyStaticClass();
}

const FFieldClass& GetFMulticastInlineDelegatePropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFMulticastInlineDelegatePropertyStaticClass.Name =
            FName("FMulticastInlineDelegateProperty");
        return true;
    }();
    (void)Init;
    return kFMulticastInlineDelegatePropertyStaticClass;
}

} // namespace XCore::Reflect
