// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FField.cpp -- FField + FFieldClass anchor definitions (§5.1 + §5.2).
// =====================================================================
//
// XCore-4b Rev 3, Section 5 ("FField + FProperty Design").
//
// This translation unit anchors two pieces of state:
//
//   1. The static `kFieldStaticClass` -- the FFieldClass instance for
//      the base FField type itself. Every FField subclass's FFieldClass
//      has SuperClass pointing at this (directly or transitively). The
//      base's SuperClass is nullptr.
//
//   2. The `FField::ConstructField` placement-new function. It is the
//      FConstructFn target stored in `kFieldStaticClass.Construct`,
//      placement-new's an FField with the given Owner+Name into caller-
//      provided storage.
//
// The base FField type has no per-FProperty-subclass dispatch surface,
// so `kFieldStaticClass.FakeVTable` is nullptr. No CastFlags bit is
// reserved for the base because IsA(FField) is true for every FField
// (handled by the SuperClass-chain walk in FFieldClass::IsChildOf and
// by callers that compare ClassPrivate directly against
// &kFieldStaticClass).
//
// =====================================================================

#include "Reflection/FField.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FFieldVariant.h"
#include "Reflection/FName.h"

#include <new>          // placement-new

namespace XCore::Reflect
{

// =====================================================================
// FField::ConstructField -- the FConstructFn target for kFieldStaticClass.
//
// Placement-new constructs an FField at OutStorage. The ClassPrivate
// slot is set to `&kFieldStaticClass` (the base "Field" descriptor).
// Next is left as nullptr -- the property-list linking happens at
// FClass::Link time (Phase 4b.5).
//
// Pre-condition: OutStorage points at a sizeof(FField)-byte buffer with
// at least 8-byte alignment. The function does NOT validate the input;
// callers (the registry / hot-reload bootstrap) own the discipline.
//
// Post-condition: A valid FField object exists at OutStorage with
// ClassPrivate == &kFieldStaticClass, Owner == InOwner, Name == InName,
// Next == nullptr.
// =====================================================================
void FField::ConstructField(FFieldVariant InOwner, FName InName, void* OutStorage) noexcept
{
    new (OutStorage) FField(&kFieldStaticClass, InOwner, InName);
}

// =====================================================================
// kFieldStaticClass -- the base "Field" FFieldClass descriptor.
//
// constinit-eligible: every initialiser is constexpr.
//   * FName("Field"): NOT constexpr (the FName(const char*) ctor
//     touches the intern table). To keep this object constinit-friendly
//     we initialise the Name field to NAME_None and populate the actual
//     "Field" interned name at the first call to FFieldClassInit() (lazy
//     once-only registration; see comment below).
//
//     RATIONALE for the lazy-init posture (Prime Directive compliance):
//     the alternative is to drop constinit and use a runtime function-
//     local-static FFieldClass. That would defeat the §5.2 design that
//     XHT-emitted `.gen.cpp` populates FFieldClass instances at
//     constinit. The lazy-init shim runs once on first access and is
//     wait-free thereafter; the discipline matches FCustomVersionRegistry's
//     function-local-static singleton pattern.
//
//     The Name field IS the FName("Field") handle once GetFieldStaticClass()
//     has been called at least once. Test code that touches
//     kFieldStaticClass.Name directly before any call to that initialiser
//     observes NAME_None (a clean uninitialised sentinel; the lazy-init
//     is detected by `Name.IsNone()`).
//
// Field-by-field initialisation:
//   Name        : NAME_None at constinit; populated to FName("Field")
//                 by FFieldClassInit() on first access.
//   Id          : 0 -- the base type has no cast-flag bit; the Id is the
//                 BLAKE3-derived per-class id. Phase 4b.3 ships the base
//                 with Id == 0 (sentinel "no class id"); Phase 4b.4
//                 assigns Id values matching the cast-flag bits for the
//                 28 FProperty subclasses per §5.5.
//   CastFlags   : EClassCastFlags::kNone (no dedicated bit for the base).
//   SuperClass  : nullptr (base of the hierarchy).
//   Construct   : &FField::ConstructField (the placement-new fn).
//   FakeVTable  : nullptr (no dispatch surface on the base type).
//
// The struct is qualified `inline constexpr` ... wait -- constexpr
// requires every member's ctor to be constexpr. FName::FName() IS
// constexpr (NAME_None default ctor). FConstructFn is a function-
// pointer literal (constexpr). The pointer to FField::ConstructField
// is an address-of-function expression -- constexpr-valid since C++17.
//
// But constexpr `inline const FFieldClass` at namespace scope with a
// non-constexpr ctor would fail. The actual init below uses the
// explicit constructor which IS constexpr, so the whole declaration is
// constexpr-eligible.
//
// We use `constinit const` instead of `inline constexpr` because the
// header `extern const FFieldClass kFieldStaticClass;` declares external
// linkage -- a single definition is needed in this TU.
// =====================================================================

// The mutable Name slot is populated lazily by FFieldClassInit() at the
// first call to GetFieldStaticClass() (see below). The constinit form
// stores NAME_None; the lazy-init promotes it to FName("Field").
//
// `constinit` (NOT `const`) so the Name slot is writable at runtime.
// The other slots are still effectively immutable -- the lazy-init
// modifies ONLY the Name slot.
constinit FFieldClass kFieldStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kNone,
    /* SuperClass */ nullptr,
    /* Construct  */ &FField::ConstructField,
    /* FakeVTable */ nullptr,
};

// =====================================================================
// Once-only late-init of the kFieldStaticClass.Name slot.
//
// FName(const char*) is not constexpr (it touches the FNamePool intern
// table). We can't populate Name at constinit, so we lazy-init on first
// access via a function-local-static initialiser.
//
// Threading: the function-local-static initialisation is guarded by the
// C++ runtime's static-init lock (`magic` field in Itanium ABI; tracked
// via InterlockedCompareExchange on MSVC). The first call interns
// "Field" once; subsequent calls see the cached FName.
//
// The lazy-init does NOT mutate any other FFieldClass slot.
// =====================================================================
const FFieldClass& GetFieldStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        // Intern "Field" and patch the Name slot. The patch is a
        // single 8-byte write under the static-init lock; subsequent
        // observers see the new value with the publish guaranteed by
        // the static-init memory ordering.
        kFieldStaticClass.Name = FName("Field");
        return true;
    }();
    (void)Init;
    return kFieldStaticClass;
}

} // namespace XCore::Reflect
