// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FProperty.cpp -- FProperty base + kFPropertyStaticClass anchor
// (XCore-4b §5.3 + §5.4 + §11.2).
// =====================================================================
//
// XCore-4b Rev 3, Section 5 ("FField + FProperty Design").
//
// This translation unit anchors:
//
//   1. The static `kFPropertyStaticClass` -- the FFieldClass instance
//      for the base FProperty type itself. Every FProperty subclass's
//      FFieldClass has SuperClass pointing at this (directly).
//
//   2. The static `kFPropertyFakeVTable` -- the base FProperty's
//      FFakeVTable in `.rodata`. The base FProperty has NO populated
//      dispatch slots (it is not constructible as a concrete type;
//      every reflectable property is a subclass). The Capabilities
//      mask is 0 and every Slots[] entry is nullptr. The FakeVTable
//      exists so the FFieldClass's FakeVTable pointer is non-null
//      (the discipline that ClassPrivate->FakeVTable is always non-
//      null for any FProperty-derived FFieldClass).
//
//   3. The FProperty::StaticClass() body returning
//      &kFPropertyStaticClass.
//
//   4. The GetFPropertyStaticClass() lazy-init accessor.
//
// The base FProperty is conceptually ABSTRACT -- it has no
// ConstructFn target and no populated dispatch slots. A direct
// construction of FProperty via the FFieldClass.Construct slot would
// produce a structurally-broken FProperty (DispatchTable would point
// at the empty FakeVTable; calling any dispatch wrapper would hit a
// nullptr slot). The Construct slot is set to nullptr; XHT codegen
// never targets it.
//
// =====================================================================

#include "Reflection/FProperty.h"

#include "Reflection/EClassCastFlags.h"
#include "Reflection/FFakeVTable.h"
#include "Reflection/FField.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FName.h"

namespace XCore::Reflect
{

// =====================================================================
// kFPropertyFakeVTable -- the empty FFakeVTable for the abstract base
// FProperty type.
//
// Lives in `.rodata` (constinit const). Capabilities == 0 (no slots
// populated); every Slots[] entry is nullptr.
//
// The base FProperty is conceptually abstract; the empty table exists
// so the FFieldClass.FakeVTable slot is non-null (the engine-wide
// discipline). Subclasses provide their own populated tables.
// =====================================================================
constinit const FFakeVTable kFPropertyFakeVTable{
    /* Capabilities    */ 0U,
    /* _reservedHeader */ 0U,
    /* Slots           */ {
        nullptr, nullptr, nullptr, nullptr, nullptr,
        nullptr, nullptr, nullptr, nullptr, nullptr,
        nullptr, nullptr, nullptr, nullptr, nullptr,
    },
};

// =====================================================================
// kFPropertyStaticClass -- the base "FProperty" FFieldClass descriptor.
//
// constinit: the FFieldClass constructor is constexpr, every member is
// constexpr-initialisable. The Name slot is left as NAME_None at
// constinit and populated lazily to FName("FProperty") by the
// GetFPropertyStaticClass() accessor (mirroring the Phase 4b.3
// kFieldStaticClass pattern).
//
// Field-by-field initialisation:
//   Name        : NAME_None at constinit; populated to FName("FProperty")
//                 by GetFPropertyStaticClass() on first access.
//   Id          : 0 -- the base FProperty has no cast-flag-derived Id;
//                 subclass FFieldClasses use a stable BLAKE3-derived Id
//                 in Phase 4b.6+. Sentinel 0 is the "no id" marker.
//   CastFlags   : kFProperty (the parent gate bit). Every subclass's
//                 CastFlags OR-includes this bit; the IsA fast path
//                 against the base FProperty reduces to a single AND.
//   SuperClass  : &kFieldStaticClass (the FField base; from Phase
//                 4b.3).
//   Construct   : nullptr -- the base is conceptually abstract.
//                 Subclasses provide their own ConstructFn target.
//   FakeVTable  : &kFPropertyFakeVTable (the empty table; the discipline
//                 that ClassPrivate->FakeVTable is non-null).
// =====================================================================
constinit FFieldClass kFPropertyStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kFProperty,
    /* SuperClass */ &kFieldStaticClass,
    /* Construct  */ nullptr,
    /* FakeVTable */ &kFPropertyFakeVTable,
};

// =====================================================================
// FProperty::StaticClass -- the static accessor used by Cast<T>(FField*).
//
// Returns &kFPropertyStaticClass. The function form (rather than a
// public static data member) ensures the descriptor's Name slot is
// lazy-populated on first use; calling StaticClass() always returns
// a FFieldClass with a resolved Name.
// =====================================================================
const FFieldClass* FProperty::StaticClass() noexcept
{
    return &GetFPropertyStaticClass();
}

// =====================================================================
// GetFPropertyStaticClass -- once-only late-init of the Name slot.
//
// FName(const char*) is not constexpr; we cannot populate Name at
// constinit. The lazy-init runs once on first call (under the C++
// runtime's static-init lock) and subsequent observers see the
// cached FName.
//
// Mirrors GetFieldStaticClass() in Phase 4b.3.
// =====================================================================
const FFieldClass& GetFPropertyStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFPropertyStaticClass.Name = FName("FProperty");
        return true;
    }();
    (void)Init;
    return kFPropertyStaticClass;
}

} // namespace XCore::Reflect
