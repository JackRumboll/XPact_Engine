// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FMapProperty.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4b.
// TMap<K, V> property.
// =====================================================================
//
// FMapProperty's value slot holds a TMap<K, V> by-value. The dispatch
// slots operate on TMap header bytes (XCore-4a SwissTable backing).
//
// CAPABILITY TRUTH-TABLE DISCIPLINE (XCore-4b Subagent A FIX-A1):
//
//   Prior Phase 4b.4b shape claimed every slot was supported in
//   `kMapCapabilities` while the slot bodies were silently no-ops --
//   a Prime Directive violation ("stub-with-fake-success"). Callers
//   probing HasSlot(ESlot::GetValue) saw true, dispatched, and got
//   nothing back.
//
//   FIX: the value-operation slots that genuinely don't implement
//   their operation at Phase 4b.4b have their `kMapCapabilities` bit
//   CLEARED. Well-behaved callers (probing HasSlot before dispatch)
//   now skip the slot via the FFakeVTable wrapper's nullptr-return
//   on HasSlot=false. Ill-behaved callers that bypass HasSlot and
//   dispatch directly hit XPACT_CHECK(false) in the slot body and
//   crash loudly with a documented diagnostic.
//
//   ContainsObjectReference IS implemented (returns TRUE
//   conservatively); its bit remains set. The typed walker checks
//   KeyProp/ValueProp for the precise GC scan answer; the slot's
//   conservative true is the safe upper bound.
//
// All entry-walking slots (Identical, GetValueTypeHash, full Copy)
// require iterating entries via KeyProp + ValueProp; those slots are
// Phase 4b.5 work when TMap iteration + per-instance dispatch context
// are available.
//
// =====================================================================

#include "Reflection/FMapProperty.h"

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
    // Unimplemented-slot bodies. The corresponding capability bit is
    // CLEARED in `kMapCapabilities` below; well-behaved callers skip
    // via HasSlot() and never reach these bodies. Direct dispatch
    // (bypassing HasSlot) hits XPACT_CHECK(false) with a documented
    // diagnostic string -- the call is a contract violation.
    //
    // The diagnostic strings are baked into the failed-expression text
    // so XPACT_CHECK's stringizing produces the message at crash time.
    // -----------------------------------------------------------------

    void GetValueSlot(const void* /*Instance*/, ::int32 /*ElementIndex*/, void* /*OutValue*/) noexcept
    {
        XPACT_CHECK(!"FMapProperty::GetValue dispatch slot not yet implemented; "
                     "Phase 4b.5/XCoreXObject will land typed container traversal. "
                     "Capability bit cleared in kMapCapabilities; HasSlot() returns false.");
    }

    void SetValueSlot(void* /*Instance*/, ::int32 /*ElementIndex*/, const void* /*InValue*/) noexcept
    {
        XPACT_CHECK(!"FMapProperty::SetValue dispatch slot not yet implemented; "
                     "Phase 4b.5/XCoreXObject will land typed container traversal. "
                     "Capability bit cleared in kMapCapabilities; HasSlot() returns false.");
    }

    void CopySingleValueSlot(void* /*Dest*/, const void* /*Src*/) noexcept
    {
        XPACT_CHECK(!"FMapProperty::CopySingleValue dispatch slot not yet implemented; "
                     "Phase 4b.5 will deep-copy via KeyProp/ValueProp iteration. "
                     "Capability bit cleared in kMapCapabilities.");
    }

    void CopyCompleteValueSlot(void* /*Dest*/, const void* /*Src*/, ::int32 /*Count*/) noexcept
    {
        XPACT_CHECK(!"FMapProperty::CopyCompleteValue dispatch slot not yet implemented; "
                     "Phase 4b.5 will deep-copy via KeyProp/ValueProp iteration. "
                     "Capability bit cleared in kMapCapabilities.");
    }

    void InitializeValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
        XPACT_CHECK(!"FMapProperty::InitializeValue dispatch slot not yet implemented; "
                     "Phase 4b.5 will zero-init the TMap header. "
                     "Capability bit cleared in kMapCapabilities.");
    }

    void DestroyValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
        XPACT_CHECK(!"FMapProperty::DestroyValue dispatch slot not yet implemented; "
                     "Phase 4b.5 will release TMap heap storage via KeyProp/ValueProp. "
                     "Capability bit cleared in kMapCapabilities.");
    }

    bool IdenticalSlot(const void* /*A*/, const void* /*B*/, ::uint32 /*PortFlags*/) noexcept
    {
        XPACT_CHECK(!"FMapProperty::Identical dispatch slot not yet implemented; "
                     "Phase 4b.5 will entry-wise compare via KeyProp/ValueProp. "
                     "Capability bit cleared in kMapCapabilities.");
        return false;
    }

    ::uint64 GetValueTypeHashSlot(const void* /*PropertyValue*/) noexcept
    {
        XPACT_CHECK(!"FMapProperty::GetValueTypeHash dispatch slot not yet implemented; "
                     "TMap is not a hashable key type. "
                     "Capability bit cleared in kMapCapabilities.");
        return 0;
    }

    // -----------------------------------------------------------------
    // ContainsObjectReferenceSlot -- LOAD-BEARING; capability bit kept set.
    //
    // Conservative TRUE: TMap slots may contain object references via
    // either KeyProp or ValueProp. The typed walker probes both for the
    // precise answer at FClass::Link time (FStruct::ObjectRefProperties
    // population per FIX-13). The slot's conservative TRUE is the safe
    // upper bound; never causes missed GC roots.
    // -----------------------------------------------------------------
    bool ContainsObjectReferenceSlot(
        ::XCore::TArray<const FStructProperty*>& /*EncounteredStructProps*/) noexcept
    {
        return true;
    }

    // -----------------------------------------------------------------
    // kMapCapabilities -- TRUTH TABLE per FIX-A1.
    //
    // Bit set => slot IS implemented and returns correct results.
    // Bit cleared => slot is unsupported at Phase 4b.4b; the slot
    // pointer remains populated but the body is XPACT_CHECK(false).
    // Well-behaved callers probe HasSlot() and skip dispatch entirely.
    //
    // Currently SET (slot works):
    //   * ContainsObjectReference (returns conservative TRUE).
    //
    // Currently CLEARED (Phase 4b.5 / XCoreXObject deferred):
    //   * GetValue, SetValue, CopySingleValue, CopyCompleteValue,
    //     InitializeValue, DestroyValue, Identical, GetValueTypeHash.
    // -----------------------------------------------------------------
    constexpr ::uint32 kMapCapabilities =
          CapabilityBit(ESlot::ContainsObjectReference);

} // anonymous

constinit const FFakeVTable kFMapPropertyFakeVTable{
    /* Capabilities    */ kMapCapabilities,
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

constinit FFieldClass kFMapPropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFMapProperty
                   | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FMapProperty::ConstructFn,
    /* FakeVTable */ &kFMapPropertyFakeVTable,
};

FMapProperty::FMapProperty(FFieldVariant InOwner, FName InName,
                           FProperty* InKeyProp, FProperty* InValueProp) noexcept
    : FProperty(&kFMapPropertyStaticClass, InOwner, InName)
    , KeyProp(InKeyProp)
    , ValueProp(InValueProp)
    , MapFlags(0)
    , _padMapPayload(0)
{
    // ElementSize is NOT set here -- TMap header size is implementation-
    // dependent and isn't known until FClass::Link populates from
    // sizeof(TMap<K,V>) at Phase 4b.5.
}

void FMapProperty::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FMapProperty(Owner, Name);
}

const FFieldClass* FMapProperty::StaticClass() noexcept
{
    return &GetFMapPropertyStaticClass();
}

const FFieldClass& GetFMapPropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFMapPropertyStaticClass.Name = FName("FMapProperty");
        return true;
    }();
    (void)Init;
    return kFMapPropertyStaticClass;
}

} // namespace XCore::Reflect
