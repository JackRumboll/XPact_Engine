// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FFieldClass.h -- FField subclass descriptor (XCore-4b §5.2).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.2 ("FFieldClass") + Section 11.2 layout row
// `FFieldClass: 48 bytes, Name@0, Id@8, CastFlags@16, SuperClass@24,
// Construct@32, FakeVTable@40`.
//
// FFieldClass is the runtime descriptor for an FField subclass. Every
// FField instance points at exactly one FFieldClass via its
// `ClassPrivate` field (§5.1). The FFieldClass carries:
//
//   * Name        -- the class's interned name (e.g. "FBoolProperty").
//                    FName handle; the bytes live in the FName pool.
//   * Id          -- a stable BLAKE3-derived class identifier. Locked at
//                    Stage B addendum time so patch DLLs can reference
//                    classes by Id without name-string compares.
//   * CastFlags   -- EClassCastFlags bitmask used for IsA acceleration.
//                    Each FProperty subclass owns one dedicated bit
//                    (§5.5); the base FField has CastFlags == kNone.
//   * SuperClass  -- the parent FFieldClass pointer (e.g.
//                    FBoolProperty's SuperClass is FProperty's class).
//                    nullptr for the base FField class.
//   * Construct   -- placement-new function pointer that constructs an
//                    instance of this subclass into caller-provided
//                    storage. Used by the registry to spawn descriptor
//                    objects without exposing subclass constructors
//                    through a vtable.
//   * FakeVTable  -- per-subclass FFakeVTable in `.rodata` (§5.4). Holds
//                    the 15-slot dispatch table replacing UE's virtual
//                    methods. Nullable for non-FProperty FField
//                    subclasses (the base FField has no dispatch
//                    surface). Forward-declared here; the full
//                    definition lands at Phase 4b.4.
//
// LAYOUT (locked at 48 bytes per Contract Rev 13.8 §11.2):
//
//   struct alignas(16) FFieldClass {
//       FName               Name;        //  0  +8  (8-byte handle)
//       uint64              Id;          //  8  +8  stable class id
//       EClassCastFlags     CastFlags;   // 16  +8  IsA acceleration bits
//       const FFieldClass*  SuperClass;  // 24  +8  parent class pointer
//       FConstructFn        Construct;   // 32  +8  placement-new fn ptr
//       const FFakeVTable*  FakeVTable;  // 40  +8  per-subclass dispatch
//   };
//
// alignas(16) per spec §5.2's `alignas(16) FFieldClass`. The 16-byte
// alignment is for placement-new-into-cache-line consistency with
// adjacent descriptors; the natural alignment of the struct's largest
// member (uint64 -> 8) would be enough for the LSB-tag invariant on
// pointers to FFieldClass instances (Phase 4b.4 will store these
// pointers in containers that benefit from 8-byte alignment), but the
// 16-byte form is the spec-locked choice (likely chosen so two
// FFieldClass instances pack into one 128-byte block contiguously).
//
// HOT-RELOAD SAFETY (§5.2):
//
//   * No virtual methods. The dispatch surface lives in the FakeVTable
//     pointer, NOT in a per-instance vtable. A patch DLL compiled
//     against the same XPACT_FFAKEVTABLE_LAYOUT_TAG produces a
//     binary-compatible FakeVTable.
//   * Standard-layout struct so offsetof is well-defined.
//   * Trivially copyable / destructible -- the registry stores
//     FFieldClass values verbatim and never instantiates per-class
//     teardown code.
//   * Constinit-friendly: every constructor is constexpr so XHT-emitted
//     `.gen.cpp` can populate FFieldClass instances at module init
//     via constinit.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Reflection/EClassCastFlags.h"
#include "Reflection/FFieldVariant.h"
#include "Reflection/FName.h"           // FName is a by-value member -- the
                                        // full struct body must be visible.

#include <cstddef>      // offsetof
#include <type_traits>  // is_trivially_copyable etc.

namespace XCore::Reflect
{
    // Forward declarations.
    //
    // FFakeVTable is the per-FProperty-subclass dispatch table that
    // lands at Phase 4b.4. The FFieldClass stores a `const FFakeVTable*`
    // member but never dereferences it through this header, so a
    // forward declaration is sufficient. Clients dispatching through
    // FakeVTable must include the full FFakeVTable.h header.
    struct FFakeVTable;

    // -----------------------------------------------------------------
    // FConstructFn -- the placement-new function-pointer type stored in
    // FFieldClass::Construct.
    //
    // Signature: `void (*)(FFieldVariant Owner, FName Name, void* OutStorage)`
    //
    // Semantics: construct an instance of the subclass into the caller-
    // provided OutStorage buffer (which must be sizeof(Subclass) bytes
    // and properly aligned). Pre-Phase 4b.4 the only FField subclass is
    // FField itself; that class's Construct function placement-new's an
    // FField with the given Owner+Name. Phase 4b.4 introduces FProperty
    // subclasses whose Construct functions placement-new the corresponding
    // FProperty subclass.
    //
    // Why a free function rather than a method? Hot-reload safety: a
    // patched DLL's FFieldClass populates Construct with a function
    // pointer that lives in the patched DLL's .text section. The base
    // engine's FFieldClass remains binary-compatible because the
    // function-pointer slot's TYPE is stable; only the TARGET changes.
    // -----------------------------------------------------------------
    using FConstructFn = void (*)(FFieldVariant Owner, FName Name, void* OutStorage);

    // -----------------------------------------------------------------
    // FFieldClass -- the 48-byte FField subclass descriptor.
    //
    // Per spec §5.2: alignas(16). NO virtual methods (hot-reload safety).
    // -----------------------------------------------------------------
    struct alignas(16) FFieldClass
    {
        FName               Name;         //  0  +8   class name (e.g. "FBoolProperty")
        ::uint64            Id;           //  8  +8   stable BLAKE3-derived class id
        EClassCastFlags     CastFlags;    // 16  +8   IsA acceleration bitmask
        const FFieldClass*  SuperClass;   // 24  +8   parent class pointer; nullptr for base
        FConstructFn        Construct;    // 32  +8   placement-new fn ptr; nullptr for abstract
        const FFakeVTable*  FakeVTable;   // 40  +8   per-subclass dispatch table; nullable

        // -------------------------------------------------------------
        // Construction.
        //
        // All constructors are constexpr so FFieldClass instances can be
        // populated via constinit at module-init time (the XHT-emitted
        // `.gen.cpp` aggregates one FFieldClass per FField subclass and
        // populates them at PreStaticInit).
        // -------------------------------------------------------------

        // Default ctor: all fields zero-initialised. The zero state is
        // the "uninitialised" sentinel; an FField whose ClassPrivate
        // points at a default-constructed FFieldClass is structurally
        // broken (Name is NAME_None, no Construct fn). The default ctor
        // exists so constinit FFieldClass arrays can be allocated and
        // later populated by name; runtime code should not construct
        // FFieldClass instances this way.
        constexpr FFieldClass() noexcept
            : Name()
            , Id(0)
            , CastFlags(EClassCastFlags::kNone)
            , SuperClass(nullptr)
            , Construct(nullptr)
            , FakeVTable(nullptr)
        {
        }

        // Explicit constructor. Used by XHT-emitted `.gen.cpp` to
        // populate FFieldClass instances at constinit time. Every field
        // is provided; no implicit defaults so a future revision that
        // adds a field forces every existing constinit site to update
        // (preventing silent zero-init of new fields).
        //
        // Const-correctness: SuperClass and FakeVTable are pointers to
        // const because both are immutable post-registration. Construct
        // is a function pointer (no const qualifier; the targeted
        // function is itself not const-qualified).
        constexpr FFieldClass(FName               InName,
                              ::uint64            InId,
                              EClassCastFlags     InCastFlags,
                              const FFieldClass*  InSuperClass,
                              FConstructFn        InConstruct,
                              const FFakeVTable*  InFakeVTable) noexcept
            : Name(InName)
            , Id(InId)
            , CastFlags(InCastFlags)
            , SuperClass(InSuperClass)
            , Construct(InConstruct)
            , FakeVTable(InFakeVTable)
        {
        }

        // -------------------------------------------------------------
        // Accessors.
        //
        // Conventional Get-prefixed accessors mirror UE's FFieldClass
        // surface (`Field.h:136 GetFName`, `:143 GetId`, `:147 GetCastFlags`,
        // `:173 GetSuperClass`). All accessors are constexpr + noexcept
        // for hot-path usage.
        // -------------------------------------------------------------

        [[nodiscard]] XPACT_FORCEINLINE constexpr FName GetFName() const noexcept
        {
            return Name;
        }

        [[nodiscard]] XPACT_FORCEINLINE constexpr ::uint64 GetId() const noexcept
        {
            return Id;
        }

        [[nodiscard]] XPACT_FORCEINLINE constexpr EClassCastFlags GetCastFlags() const noexcept
        {
            return CastFlags;
        }

        [[nodiscard]] XPACT_FORCEINLINE constexpr const FFieldClass* GetSuperClass() const noexcept
        {
            return SuperClass;
        }

        [[nodiscard]] XPACT_FORCEINLINE constexpr FConstructFn GetConstructFn() const noexcept
        {
            return Construct;
        }

        [[nodiscard]] XPACT_FORCEINLINE constexpr const FFakeVTable* GetFakeVTable() const noexcept
        {
            return FakeVTable;
        }

        // -------------------------------------------------------------
        // Cast-flag predicates.
        //
        // HasAnyCastFlags / HasAllCastFlags wrap the namespace-level
        // helpers from EClassCastFlags.h with this-binding so call sites
        // can write `Class->HasAnyCastFlags(X)` rather than
        // `HasAnyCastFlags(Class->CastFlags, X)`. Mirrors UE's
        // `FFieldClass::HasAnyCastFlags` (`Field.h:151`).
        // -------------------------------------------------------------

        [[nodiscard]] XPACT_FORCEINLINE constexpr bool HasAnyCastFlags(EClassCastFlags FlagsToCheck) const noexcept
        {
            return ::XCore::Reflect::HasAnyCastFlags(CastFlags, FlagsToCheck);
        }

        [[nodiscard]] XPACT_FORCEINLINE constexpr bool HasAllCastFlags(EClassCastFlags FlagsToCheck) const noexcept
        {
            return ::XCore::Reflect::HasAllCastFlags(CastFlags, FlagsToCheck);
        }

        // -------------------------------------------------------------
        // IsChildOf -- walk the SuperClass chain, with a Phase 4b.4a
        // CastFlags-AND fast path for the common FProperty hierarchy
        // case.
        //
        // FAST PATH (Phase 4b.4a):
        //
        // Every FProperty subclass's CastFlags includes the kFProperty
        // parent gate bit (see EClassCastFlags.h) OR'd with its own
        // dedicated subclass bit. If OtherClass has any CastFlags bit
        // set, the question "am I a child of OtherClass?" reduces to
        // "do my CastFlags include all of OtherClass's CastFlags?".
        //
        // The bitmask test is one AND + one compare (single-cycle on
        // every supported CPU). For OtherClass == FProperty's base
        // class (kFProperty parent gate bit), every FProperty subclass
        // passes the test trivially.
        //
        // SLOW PATH:
        //
        // If OtherClass has no CastFlags (the base FField class itself
        // or any future non-FProperty FField subclass without a cast
        // bit), fall back to the SuperClass chain walk. The chain has
        // bounded depth (per spec §13 gate C1: typical <= 3, max <= 10
        // for any FProperty subclass).
        //
        // The fast-path-or-slow-path branch is structural: a single
        // compare on OtherClass->CastFlags resolves which path to take.
        // The fast path's worst case (kNone CastFlags) falls through
        // to the slow path; the slow path's worst case (deep
        // hierarchy) is bounded by the spec invariant.
        // -------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool IsChildOf(const FFieldClass* OtherClass) const noexcept
        {
            // nullptr is the "no class" sentinel; nothing is a child of
            // it.
            if (OtherClass == nullptr)
            {
                return false;
            }

            // FAST PATH: if OtherClass has any CastFlags bits set,
            // every class with those bits in its own CastFlags is a
            // child of OtherClass. The discipline (Phase 4b.4a) is
            // that every FProperty subclass's CastFlags OR-includes
            // kFProperty | <own bit>; a class with FProperty's own
            // CastFlags == kFProperty matches via this AND.
            //
            // Self-test: a class IS a child of itself; the fast path
            // honours this because the class's own CastFlags include
            // its own bits.
            if (OtherClass->CastFlags != EClassCastFlags::kNone)
            {
                return ::XCore::Reflect::HasAllCastFlags(CastFlags, OtherClass->CastFlags);
            }

            // SLOW PATH: walk SuperClass chain.
            for (const FFieldClass* Walker = this; Walker != nullptr; Walker = Walker->SuperClass)
            {
                if (Walker == OtherClass)
                {
                    return true;
                }
            }
            return false;
        }
    };

    // ---------------------------------------------------------------------
    // ABI locks (per Contract Rev 13.8 §11.2). The static_asserts here ARE
    // the ABI contract. Any layout change breaks every FField subclass
    // descriptor in the engine.
    // ---------------------------------------------------------------------
    static_assert(sizeof(FFieldClass)  == 48,
                  "FFieldClass ABI lock: must be exactly 48 bytes "
                  "(Name 8 + Id 8 + CastFlags 8 + SuperClass 8 + "
                  "Construct 8 + FakeVTable 8). See XCore-4b §5.2.");
    static_assert(alignof(FFieldClass) == 16,
                  "FFieldClass ABI lock: 16-byte alignment per §5.2");

    // Member offsets locked to the bytes the Stage B addendum names.
    static_assert(offsetof(FFieldClass, Name)       ==  0,
                  "FFieldClass ABI lock: Name at offset 0");
    static_assert(offsetof(FFieldClass, Id)         ==  8,
                  "FFieldClass ABI lock: Id at offset 8");
    static_assert(offsetof(FFieldClass, CastFlags)  == 16,
                  "FFieldClass ABI lock: CastFlags at offset 16");
    static_assert(offsetof(FFieldClass, SuperClass) == 24,
                  "FFieldClass ABI lock: SuperClass at offset 24");
    static_assert(offsetof(FFieldClass, Construct)  == 32,
                  "FFieldClass ABI lock: Construct at offset 32");
    static_assert(offsetof(FFieldClass, FakeVTable) == 40,
                  "FFieldClass ABI lock: FakeVTable at offset 40");

    // Type traits: FFieldClass is a POD-style aggregate.
    static_assert(::std::is_standard_layout_v<FFieldClass>,
                  "FFieldClass must be standard layout (so offsetof is well-defined)");
    static_assert(::std::is_trivially_copyable_v<FFieldClass>,
                  "FFieldClass must be trivially copyable (no virtual methods)");
    static_assert(::std::is_trivially_destructible_v<FFieldClass>,
                  "FFieldClass must be trivially destructible (no per-instance teardown)");

    // ---------------------------------------------------------------------
    // The base "Field" class -- the root of the FFieldClass hierarchy.
    //
    // Every FField subclass's FFieldClass has SuperClass pointing at
    // this (directly or transitively); the base itself has SuperClass
    // == nullptr. CastFlags is kNone (no dedicated cast bit; IsA of
    // FField the base type is true for every FField, handled by the
    // hierarchy walk).
    //
    // Construct points at FField::ConstructField (defined in FField.cpp);
    // FakeVTable is nullptr (the base FField has no dispatch surface --
    // dispatch is per-FProperty-subclass at Phase 4b.4+).
    //
    // NOT const-qualified: the Name slot is populated lazily at first
    // call to `GetFieldStaticClass()` (the FName("Field") constructor
    // touches the intern table at runtime, which is not constexpr).
    // All other slots are effectively immutable post-constinit; only
    // the Name slot is patched. Callers that need the resolved
    // FName("Field") MUST go through `GetFieldStaticClass()` rather
    // than reading kFieldStaticClass.Name directly.
    //
    // The declaration is `extern`; the definition lives in FField.cpp.
    // ---------------------------------------------------------------------
    extern FFieldClass kFieldStaticClass;

    // ---------------------------------------------------------------------
    // GetFieldStaticClass -- the accessor that lazy-initialises the base
    // "Field" FFieldClass's Name slot to FName("Field") on first call
    // and returns the resolved descriptor.
    //
    // Phase 4b.6's XReflectionRuntime registry will replace this lazy-
    // init with an eager pass at PostStaticInit. Until then, callers
    // routed through this accessor observe a fully-populated descriptor.
    //
    // Thread-safe via the C++ runtime's static-init lock (Itanium ABI
    // `__cxa_guard_acquire` / MSVC equivalent). The lazy-init runs once
    // and subsequent observers see the cached FName.
    // ---------------------------------------------------------------------
    const FFieldClass& GetFieldStaticClass() noexcept;

} // namespace XCore::Reflect
