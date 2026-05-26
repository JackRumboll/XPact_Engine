// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FStructProperty.cpp -- XCore-4b §5.5 + §11.2; Phase 4b.4b.
// Nested-struct property.
// =====================================================================
//
// FStructProperty's value slot holds the nested struct's bytes INLINE
// (not a pointer; sizeof(nested struct) bytes at FProperty::Offset).
// The dispatch slots use ElementSize -- which the constructor sets to
// 0 at Phase 4b.4b (the caller MUST populate ElementSize from
// Struct->Size at FClass::Link time when FStruct is available; Phase
// 4b.5 codegen owns this).
//
// CAPABILITY TRUTH-TABLE DISCIPLINE (XCore-4b Subagent A FIX-A1):
//
//   Prior Phase 4b.4b shape claimed every slot was supported while
//   the slot bodies were silently no-ops. The dispatch shape doesn't
//   carry per-instance FStruct context (the FFakeVTable signature is
//   per-class, not per-instance), so the slots cannot access the
//   nested Struct's FCppStructOps handlers at Phase 4b.4b.
//
//   FIX: every slot has its `kStructCapabilities` bit CLEARED at Phase
//   4b.4b. Well-behaved callers (probing HasSlot) skip dispatch
//   entirely; the FProperty wrapper returns the safe sentinel (false
//   for Identical/ContainsObjectReference; 0 for GetValueTypeHash;
//   no-op for GetValue/SetValue/Copy/Init/Destroy via the wrapper's
//   nullptr-fn guards).
//
//   Direct dispatch hits XPACT_CHECK(false) and crashes with the
//   Phase 4b.5 / XCoreXObject-deferred diagnostic. This is correct:
//   the slots are not yet usable, and a caller bypassing HasSlot is
//   a contract violation.
//
//   ContainsObjectReference is structurally important: at Phase 4b.5,
//   it will be rewired to consult Struct->ObjectRefProperties (FIX-13).
//   Until then it returns false (the safe sentinel) via the wrapper's
//   HasSlot=false fall-through, matching the
//   ContainsObjectReferenceMatrix test expectation.
//
// NOTE: The slot bodies CANNOT access the per-instance Struct pointer
// directly -- the FFakeVTable dispatch shape is (Container, ElementIndex,
// OutValue / InValue) with no FProperty* parameter. For Phase 4b.5 we
// will extend the FFakeVTable model (or use a side table) to carry the
// per-instance Struct context through dispatch.
//
// =====================================================================

#include "Reflection/FStructProperty.h"

#include "Hash/FXxh3.h"
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
    // Unimplemented-slot bodies (FIX-A1). Capability bits ALL CLEARED
    // in `kStructCapabilities` below; well-behaved callers see the
    // wrapper return sentinel values (false / 0 / no-op).
    //
    // TODO(Phase 4b.5): rewire dispatch to consult per-instance Struct
    // via FCppStructOps or an extended FFakeVTable model that carries
    // FStructProperty* through dispatch.
    // -----------------------------------------------------------------

    void GetValueSlot(const void* /*Instance*/, ::int32 /*ElementIndex*/, void* /*OutValue*/) noexcept
    {
        XPACT_CHECK(!"FStructProperty::GetValue dispatch slot not yet implemented; "
                     "Phase 4b.5 will delegate to nested Struct's FCppStructOps Copy. "
                     "Capability bit cleared in kStructCapabilities; HasSlot() returns false.");
    }

    void SetValueSlot(void* /*Instance*/, ::int32 /*ElementIndex*/, const void* /*InValue*/) noexcept
    {
        XPACT_CHECK(!"FStructProperty::SetValue dispatch slot not yet implemented; "
                     "Phase 4b.5 will delegate to nested Struct's FCppStructOps Copy. "
                     "Capability bit cleared in kStructCapabilities.");
    }

    void CopySingleValueSlot(void* /*Dest*/, const void* /*Src*/) noexcept
    {
        XPACT_CHECK(!"FStructProperty::CopySingleValue dispatch slot not yet implemented; "
                     "Phase 4b.5 will delegate to FCppStructOps Copy handler. "
                     "Capability bit cleared in kStructCapabilities.");
    }

    void CopyCompleteValueSlot(void* /*Dest*/, const void* /*Src*/, ::int32 /*Count*/) noexcept
    {
        XPACT_CHECK(!"FStructProperty::CopyCompleteValue dispatch slot not yet implemented; "
                     "Phase 4b.5 will delegate to FCppStructOps Copy handler. "
                     "Capability bit cleared in kStructCapabilities.");
    }

    void InitializeValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
        XPACT_CHECK(!"FStructProperty::InitializeValue dispatch slot not yet implemented; "
                     "Phase 4b.5 will delegate to FCppStructOps Construct handler. "
                     "Capability bit cleared in kStructCapabilities.");
    }

    void DestroyValueSlot(void* /*Dest*/, ::int32 /*Count*/) noexcept
    {
        XPACT_CHECK(!"FStructProperty::DestroyValue dispatch slot not yet implemented; "
                     "Phase 4b.5 will delegate to FCppStructOps Destruct handler. "
                     "Capability bit cleared in kStructCapabilities.");
    }

    bool IdenticalSlot(const void* /*A*/, const void* /*B*/, ::uint32 /*PortFlags*/) noexcept
    {
        XPACT_CHECK(!"FStructProperty::Identical dispatch slot not yet implemented; "
                     "Phase 4b.5 will delegate to FCppStructOps Identical handler. "
                     "Capability bit cleared in kStructCapabilities.");
        return false;
    }

    ::uint64 GetValueTypeHashSlot(const void* /*PropertyValue*/) noexcept
    {
        XPACT_CHECK(!"FStructProperty::GetValueTypeHash dispatch slot not yet implemented; "
                     "Phase 4b.5 will delegate to FCppStructOps GetTypeHash handler. "
                     "Capability bit cleared in kStructCapabilities.");
        return 0;
    }

    // -----------------------------------------------------------------
    // ContainsObjectReferenceSlot -- capability bit CLEARED at Phase 4b.4b.
    //
    // The slot CANNOT access the per-instance FStructProperty (and thus
    // the wrapped FStruct's ObjectRefProperties) under the signature-
    // only dispatch shape. At Phase 4b.5, dispatch will be rewired to
    // consult Struct->ObjectRefProperties (FIX-13) via either an
    // extended FFakeVTable model or a side-table walk at FClass::Link
    // time.
    //
    // Until then, the wrapper's HasSlot=false path returns false (safe
    // sentinel). This matches ContainsObjectReferenceMatrix test
    // expectation for FStructProperty.
    //
    // SAFETY: a struct containing object references that never appears
    // in FStruct::ObjectRefProperties WOULD cause missed GC roots. At
    // Phase 4b.4b no live XObject reflection exists, so no roots are
    // missed in practice; Phase 4b.5 wires the correct propagation
    // BEFORE the GC scan goes live.
    // -----------------------------------------------------------------
    bool ContainsObjectReferenceSlot(
        ::XCore::TArray<const FStructProperty*>& /*EncounteredStructProps*/) noexcept
    {
        XPACT_CHECK(!"FStructProperty::ContainsObjectReference dispatch slot not yet "
                     "implemented; Phase 4b.5 will delegate to Struct->ObjectRefProperties. "
                     "Capability bit cleared in kStructCapabilities.");
        return false;
    }

    // -----------------------------------------------------------------
    // kStructCapabilities -- TRUTH TABLE per FIX-A1.
    //
    // ALL bits CLEARED at Phase 4b.4b: every slot requires per-instance
    // Struct context the FFakeVTable dispatch shape doesn't carry.
    // Wrappers return safe sentinels (false / 0 / no-op).
    //
    // Phase 4b.5 will populate capability bits AFTER rewiring dispatch
    // to consult the per-instance FCppStructOps + ObjectRefProperties.
    // -----------------------------------------------------------------
    constexpr ::uint32 kStructCapabilities = 0U;

} // anonymous

constinit const FFakeVTable kFStructPropertyFakeVTable{
    /* Capabilities    */ kStructCapabilities,
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

constinit FFieldClass kFStructPropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFStructProperty
                   | EClassCastFlags::kFProperty,
    /* SuperClass */ &kFPropertyStaticClass,
    /* Construct  */ &FStructProperty::ConstructFn,
    /* FakeVTable */ &kFStructPropertyFakeVTable,
};

FStructProperty::FStructProperty(FFieldVariant InOwner, FName InName,
                                 FStruct* InStruct) noexcept
    : FProperty(&kFStructPropertyStaticClass, InOwner, InName)
    , Struct(InStruct)
{
    // ElementSize is NOT set here -- the FStruct's size isn't known
    // until Phase 4b.5 (FStruct ships). FClass::Link will populate
    // ElementSize from Struct->Size at link time. Phase 4b.4b leaves
    // it at the FProperty-base-ctor default of 0 (the "not yet linked"
    // sentinel).
}

void FStructProperty::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FStructProperty(Owner, Name);
}

const FFieldClass* FStructProperty::StaticClass() noexcept
{
    return &GetFStructPropertyStaticClass();
}

const FFieldClass& GetFStructPropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFStructPropertyStaticClass.Name = FName("FStructProperty");
        return true;
    }();
    (void)Init;
    return kFStructPropertyStaticClass;
}

} // namespace XCore::Reflect
