// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FClass.h -- the 200-byte XObject-class descriptor (XCore-4b §7.3 +
// §11.3; FIX-R2-CRIT-1).
// =====================================================================
//
// XCore-4b Rev 3, Section 7.3 ("FClass") + Section 11.3 layout row
// `FClass: 200 bytes (104 FStruct + 96 FClass-specific)`.
//
// SPEC DRIFT (Phase 4b.5 audit-corrected; root cause documented in
// FStruct.h SPEC DRIFT NOTICE): TArray = 24 bytes (not 16), so
// ObjectRefProperties + ClassReps + NetFields each consume 8 more
// bytes than the spec expected. Cascading totals:
//
//     FStruct base   112 (was spec-asserted 104)
//     FClass-specific 112 (was spec-asserted 96)
//        - +8 from ClassReps (24 vs 16)
//        - +8 from NetFields (24 vs 16)
//     Total          224 (was spec-asserted 200)
//
// FClass extends FStruct and is the runtime descriptor for XObject-
// derived classes (the C++ side; the C# transpilation produces matching
// FClass instances). UE's `UClass` (`Class.h:3893`) extends UStruct and
// carries ~80 fields; XPact's FClass extends FStruct and consolidates
// to the load-bearing subset.
//
// LAYOUT (Phase 4b.5 audit-corrected; spec said 200, actually 224):
//
//   struct alignas(8) FClass : FStruct {
//       // FStruct base @ 0-111 (112 bytes; was spec-asserted 104)
//       void                          (*ClassConstructorFn)(void*, FFieldVariant);  // 112 +8
//       void*                         (*ClassVTableHelperCtorCaller)(void*);        // 120 +8
//       mutable std::atomic<FObject*> ClassDefaultObject;        // 128 +8 (lazy CDO)
//       EClassFlags                   ClassFlags;                // 136 +8
//       EClassCastFlags               ClassCastFlags;            // 144 +8
//       const FClass*                 ClassWithin;               // 152 +8
//       int32                         FirstOwnedClassRep;        // 160 +4
//       int32                         ClassRepCount;             // 164 +4
//       TArray<FRepRecord>            ClassReps;                 // 168 +24 (TArray=24)
//       TArray<FField*>               NetFields;                 // 192 +24 (TArray=24)
//       FName                         ClassConfigName;           // 216 +8
//   };
//
// sizeof(FClass) == 224.
//
// XPACT_FCLASS_LAYOUT_TAG (Rev 3 §11.6):
//   "FClass-v3: 104 FStruct base + 96 FClass-specific = 200 bytes;
//    ClassReps is TArray<FRepRecord> (16 bytes); NetFields is
//    TArray<FField*> (16 bytes); ObjectRefProperties is dense TArray
//    on FStruct (16 bytes)"
//
// FIELD SUMMARY:
//
//   ClassConstructorFn          -- placement-new function-pointer for
//                                  instantiating XObject instances of
//                                  this class. The C++ ctor's address
//                                  populates this slot; hot-reload
//                                  patch DLLs put their patched ctor
//                                  address here.
//
//   ClassVTableHelperCtorCaller -- alternate ctor for editor-specific
//                                  paths (matches UE's
//                                  ClassVTableHelperCtorCaller).
//
//   ClassDefaultObject (CDO)    -- lazy-instantiated singleton of this
//                                  class used as the source of default
//                                  values when an instance is created.
//                                  std::atomic for publication-safety
//                                  on first-access.
//
//   ClassFlags                  -- EClassFlags bitmask (Abstract,
//                                  Native, Transient, etc.).
//
//   ClassCastFlags              -- EClassCastFlags bitmask. Allows
//                                  fast IsA on cast-flagged classes
//                                  (matches FFieldClass discipline at
//                                  the FProperty tier). For XObject
//                                  subclasses, the bits live in the
//                                  upper half of EClassCastFlags (the
//                                  lower bits 0-35 are FProperty
//                                  subclass and FObjectPropertyBase
//                                  parent gate bits).
//
//   ClassWithin                 -- required outer-class type
//                                  (mirroring UE's UClass.ClassWithin).
//                                  Some XObject subclasses are only
//                                  valid within specific outers
//                                  (e.g., XComponent within XActor);
//                                  nullptr means "any outer".
//
//   FirstOwnedClassRep / ClassRepCount  -- ClassReps slice owned by
//                                  THIS class (vs inherited from
//                                  SuperStruct). XHT pre-emits both at
//                                  constinit; FClass::Link verifies the
//                                  slice matches the runtime FProperty
//                                  chain (per FIX-R2-LOW-8).
//
//   ClassReps                   -- TArray<FRepRecord> for replication
//                                  acceleration. Indexed by RepIndex.
//                                  Per FIX-7: one entry per replicated
//                                  property * ArrayDim element.
//
//   NetFields                   -- TArray<FField*> of network-callable
//                                  functions / RPC entries (the
//                                  UFunction-equivalent set; FField
//                                  forward-declared at Phase 4b.5
//                                  because the post-MVP
//                                  FFunctionDescriptor class extends
//                                  FField).
//
//   ClassConfigName             -- the .ini section name (Engine.ini,
//                                  Game.ini, custom) populated from
//                                  XCLASS(config = ...).
//
// HOT-RELOAD SAFETY:
//
//   * No virtual methods (FStruct has none; FClass adds none).
//   * Standard-layout NOT asserted (FStruct + FClass both carry data
//     members; single inheritance with both base + derived non-
//     empty is not standard-layout).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Containers/TArray.h"
#include "Reflection/EClassCastFlags.h"
#include "Reflection/EClassFlags.h"
#include "Reflection/FFieldVariant.h"
#include "Reflection/FName.h"
#include "Reflection/FRepRecord.h"
#include "Reflection/FStruct.h"

#include <atomic>
#include <cstddef>      // offsetof

namespace XCore::Reflect
{
    // Forward declarations.

    // FField -- the polymorphism-free reflection anchor (Phase 4b.3).
    // NetFields stores pointers to FField subclasses (currently
    // FFunctionDescriptor post-MVP; Phase 4b.5 ships the storage).
    struct FField;

    // FProperty -- needed for FindPropertyByName which delegates to
    // FStruct's chain walk through SuperStruct.
    struct FProperty;

    // FObject -- the GC-managed object base (lands at XCoreXObject /
    // System 5). ClassDefaultObject stores a pointer; the type is
    // opaque to FClass.
    struct FObject;

    // -----------------------------------------------------------------
    // FClass -- 200-byte FStruct subclass for XObject-derived classes.
    //
    // Per spec §7.3: alignas(8). NO virtual methods.
    //
    // FClass inherits FStruct's deleted copy/move ops.
    // -----------------------------------------------------------------
    struct alignas(8) FClass : public FStruct
    {
        // ---- Class construction (offsets relative to FScript-end = +0 here) ----

        // Placement-new ctor for XObject instances of this class.
        // Signature mirrors UE's ClassConstructor: takes the instance
        // buffer + an FFieldVariant for the owner (LSB-tagged FStruct*
        // or FField* per the §5.1 invariant). The constructor body
        // populates the instance's polymorphic vtable + member init.
        //
        // Hot-reload: patch DLLs reassign this slot to a patched
        // constructor address. The FClass descriptor body remains
        // ABI-stable; only the targeted function changes.
        void               (*ClassConstructorFn)(
            void* InstancePtr, FFieldVariant Owner);                  // 112 +8

        // Alternate ctor for the editor-specific helper path that
        // mirrors UE's ClassVTableHelperCtorCaller. Used by the
        // Editor's CDO-aware construction. nullptr is valid (skips
        // the helper path).
        void*              (*ClassVTableHelperCtorCaller)(
            void* HelperPtr);                                          // 120 +8

        // ---- Class default object (offset 120; 8 bytes) ----
        //
        // Lazy CDO singleton. nullptr at constinit; populated on
        // first construction request. The atomic protects against
        // double-initialisation in a multi-threaded module-init
        // (rare; CDO requests typically serialise through the
        // XReflectionRuntime at PostStaticInit but the atomic is
        // defensive).

        mutable ::std::atomic<FObject*> ClassDefaultObject;            // 128 +8

        // ---- Class flags + cast acceleration (offsets 128-143; 16 bytes) ----

        EClassFlags        ClassFlags;                                 // 136 +8
        EClassCastFlags    ClassCastFlags;                             // 144 +8

        // ---- ClassWithin (offset 152; 8 bytes) ----

        const FClass*      ClassWithin;                                // 152 +8

        // ---- Replication acceleration (offsets 160-167; 8 bytes) ----
        //
        // FirstOwnedClassRep + ClassRepCount slice ClassReps to the
        // entries owned by THIS class (vs inherited from SuperStruct).
        // Allows derived classes to identify their own additions
        // without walking the full ClassReps array.

        ::int32            FirstOwnedClassRep;                         // 160 +4
        ::int32            ClassRepCount;                              // 164 +4

        // ---- ClassReps (offset 168; 24 bytes; per FIX-7; TArray=24) ----
        //
        // TArray<FRepRecord> -- one entry per replicated property *
        // ArrayDim element. Indexed by RepIndex (the property's
        // per-class replication index; FIX-19 collapses NetIndex into
        // RepIndex via the array position). Pre-emitted as constinit
        // by XHT (FIX-R2-LOW-8); FClass::Link verifies the runtime
        // FProperty chain produces a matching count + per-entry
        // identity.

        ::XCore::TArray<FRepRecord> ClassReps;                         // 168 +24

        // ---- NetFields (offset 192; 24 bytes; TArray=24) ----
        //
        // TArray<FField*> -- network-callable functions / RPC entries
        // (the UFunction-equivalent set on UE's UClass). The entries
        // are FField subclasses (FFunctionDescriptor in the post-MVP
        // function-reflection tier); each carries an FName + signature
        // hash for dispatch resolution.

        ::XCore::TArray<FField*> NetFields;                             // 192 +24

        // ---- Class config name (offset 216; 8 bytes) ----
        //
        // The .ini section name (Engine.ini, Game.ini, etc.) populated
        // from XCLASS(config = ...). NAME_None means "not config-
        // loaded".

        FName              ClassConfigName;                             // 216 +8

        // -------------------------------------------------------------
        // Construction.
        //
        // FClass constructors delegate to FStruct's identity-taking
        // ctor. The default ctor zero-initialises every FClass-
        // specific slot. The explicit ctor populates identity +
        // construction + class flags.
        //
        // FClass inherits FStruct's deleted copy/move ops.
        // -------------------------------------------------------------

        FClass() noexcept
            : FStruct()
            , ClassConstructorFn(nullptr)
            , ClassVTableHelperCtorCaller(nullptr)
            , ClassDefaultObject(nullptr)
            , ClassFlags(EClassFlags::CLASS_None)
            , ClassCastFlags(EClassCastFlags::kNone)
            , ClassWithin(nullptr)
            , FirstOwnedClassRep(0)
            , ClassRepCount(0)
            , ClassReps()
            , NetFields()
            , ClassConfigName()
        {
        }

        FClass(FName InName, const FStruct* InSuper,
               EClassFlags InClassFlags = EClassFlags::CLASS_None,
               EClassCastFlags InClassCastFlags = EClassCastFlags::kNone) noexcept
            : FStruct(InName, InSuper)
            , ClassConstructorFn(nullptr)
            , ClassVTableHelperCtorCaller(nullptr)
            , ClassDefaultObject(nullptr)
            , ClassFlags(InClassFlags)
            , ClassCastFlags(InClassCastFlags)
            , ClassWithin(nullptr)
            , FirstOwnedClassRep(0)
            , ClassRepCount(0)
            , ClassReps()
            , NetFields()
            , ClassConfigName()
        {
        }

        // -------------------------------------------------------------
        // Accessors.
        // -------------------------------------------------------------

        [[nodiscard]] XPACT_FORCEINLINE EClassFlags GetClassFlags() const noexcept
        {
            return ClassFlags;
        }

        [[nodiscard]] XPACT_FORCEINLINE EClassCastFlags GetClassCastFlags() const noexcept
        {
            return ClassCastFlags;
        }

        [[nodiscard]] XPACT_FORCEINLINE const FClass* GetClassWithin() const noexcept
        {
            return ClassWithin;
        }

        [[nodiscard]] XPACT_FORCEINLINE FName GetClassConfigName() const noexcept
        {
            return ClassConfigName;
        }

        [[nodiscard]] XPACT_FORCEINLINE const ::XCore::TArray<FRepRecord>&
            GetClassReps() const noexcept
        {
            return ClassReps;
        }

        [[nodiscard]] XPACT_FORCEINLINE const ::XCore::TArray<FField*>&
            GetNetFields() const noexcept
        {
            return NetFields;
        }

        [[nodiscard]] XPACT_FORCEINLINE ::int32 GetFirstOwnedClassRep() const noexcept
        {
            return FirstOwnedClassRep;
        }

        [[nodiscard]] XPACT_FORCEINLINE ::int32 GetClassRepCount() const noexcept
        {
            return ClassRepCount;
        }

        // -------------------------------------------------------------
        // GetCDO -- access the lazy class default object.
        //
        // Returns the current CDO pointer (may be nullptr if not yet
        // populated). Per spec: lazy creation is the
        // XReflectionRuntime's responsibility (System 5+); Phase 4b.5
        // ships the accessor only.
        // -------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE FObject* GetCDO() const noexcept
        {
            return ClassDefaultObject.load(::std::memory_order_acquire);
        }

        // -------------------------------------------------------------
        // SetCDO -- atomic publish of the resolved CDO.
        //
        // The XReflectionRuntime's CDO-init path calls this after
        // constructing the singleton. Memory ordering is release so
        // a subsequent GetCDO observes a fully-constructed object.
        // -------------------------------------------------------------
        XPACT_FORCEINLINE void SetCDO(FObject* InCDO) noexcept
        {
            ClassDefaultObject.store(InCDO, ::std::memory_order_release);
        }

        // -------------------------------------------------------------
        // IsChildOf -- overload for FClass with CastFlags fast path.
        //
        // Two-step:
        //
        //   1. CastFlags fast path: if `Other` has any
        //      EClassCastFlags bits set, the question reduces to
        //      `(this->ClassCastFlags & Other->ClassCastFlags) ==
        //      Other->ClassCastFlags`. Single AND + compare.
        //
        //   2. Slow path: SuperStruct chain walk via the FStruct
        //      base's IsChildOf.
        //
        // The fast path requires Other to carry a non-empty
        // ClassCastFlags. Classes without dedicated cast bits (the
        // base case) fall through to the slow path.
        //
        // The IsChildOf<FStruct*> overload remains available via the
        // FStruct base; this overload extends the contract with the
        // FClass-specific fast path.
        // -------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE bool IsChildOf(const FClass* Other) const noexcept
        {
            if (Other == nullptr)
            {
                return false;
            }

            // CastFlags fast path.
            if (Other->ClassCastFlags != EClassCastFlags::kNone)
            {
                return HasAllCastFlags(ClassCastFlags, Other->ClassCastFlags);
            }

            // Slow path: chain walk via FStruct base.
            return FStruct::IsChildOf(static_cast<const FStruct*>(Other));
        }

        // Disambiguating overload: when called with the FStruct-base
        // type, dispatch to FStruct::IsChildOf (avoids the FClass
        // CastFlags shortcut for non-FClass FStruct targets like
        // FScriptStruct).
        [[nodiscard]] XPACT_FORCEINLINE bool IsChildOf(const FStruct* Other) const noexcept
        {
            return FStruct::IsChildOf(Other);
        }

        // -------------------------------------------------------------
        // FindPropertyByName -- hierarchical version of FStruct's
        // local-only FindPropertyByName.
        //
        // Walks this class's ChildProperties first; on miss, walks
        // SuperStruct->FindPropertyByName recursively. Returns the
        // first match (most-derived owns the property).
        //
        // Body in FClass.cpp.
        // -------------------------------------------------------------
        [[nodiscard]] FProperty* FindPropertyByName(FName PropertyName) const noexcept;

        // -------------------------------------------------------------
        // Link -- FStruct::Link + ClassReps population per FIX-7.
        //
        // Steps:
        //   1. FStruct::Link (rebuilds PropertyLink chains +
        //      ObjectRefProperties dense array).
        //   2. Walk the property chain; for each FProperty carrying
        //      CPF_Net, emit ArrayDim FRepRecord entries into
        //      ClassReps (one per array element per FIX-7).
        //   3. Populate FirstOwnedClassRep + ClassRepCount based on
        //      the SuperStruct's existing ClassReps count.
        //
        // Idempotent: clears + rebuilds.
        //
        // Body in FClass.cpp.
        // -------------------------------------------------------------
        void Link() noexcept;
    };

    // ---------------------------------------------------------------------
    // ABI locks (Phase 4b.5 audit-corrected; see SPEC DRIFT notice above).
    // ---------------------------------------------------------------------
    static_assert(sizeof(FClass) == 224,
                  "FClass ABI lock (audit-corrected): 224 bytes "
                  "(112 FStruct base + 112 FClass-specific). Spec Rev 3 §7.3 "
                  "declared 200 with FStruct=104 + ClassReps/NetFields TArray=16; "
                  "actuals: FStruct=112, TArray=24. See FStruct.h SPEC DRIFT.");
    static_assert(alignof(FClass) == 8,
                  "FClass ABI lock: 8-byte alignment per §7.3 alignas(8)");

    // Member offsets locked per the audit-corrected layout (every
    // FClass member shifts by +8 vs spec because FStruct base grew
    // by 8; ClassReps + NetFields shift by an additional +8 each due
    // to TArray = 24).
    static_assert(offsetof(FClass, ClassConstructorFn)           == 112,
                  "FClass ABI lock (audit-corrected): ClassConstructorFn at "
                  "offset 112 (spec said 104; +8 shift from FStruct=112)");
    static_assert(offsetof(FClass, ClassVTableHelperCtorCaller)  == 120,
                  "FClass ABI lock (audit-corrected): ClassVTableHelperCtorCaller at offset 120");
    static_assert(offsetof(FClass, ClassDefaultObject)           == 128,
                  "FClass ABI lock (audit-corrected): ClassDefaultObject at offset 128");
    static_assert(offsetof(FClass, ClassFlags)                   == 136,
                  "FClass ABI lock (audit-corrected): ClassFlags at offset 136");
    static_assert(offsetof(FClass, ClassCastFlags)               == 144,
                  "FClass ABI lock (audit-corrected): ClassCastFlags at offset 144");
    static_assert(offsetof(FClass, ClassWithin)                  == 152,
                  "FClass ABI lock (audit-corrected): ClassWithin at offset 152");
    static_assert(offsetof(FClass, FirstOwnedClassRep)           == 160,
                  "FClass ABI lock (audit-corrected): FirstOwnedClassRep at offset 160");
    static_assert(offsetof(FClass, ClassRepCount)                == 164,
                  "FClass ABI lock (audit-corrected): ClassRepCount at offset 164");
    static_assert(offsetof(FClass, ClassReps)                    == 168,
                  "FClass ABI lock (audit-corrected): ClassReps at offset 168");
    static_assert(offsetof(FClass, NetFields)                    == 192,
                  "FClass ABI lock (audit-corrected): NetFields at offset 192 "
                  "(spec said 176; +16 shift from TArray=24 cascade)");
    static_assert(offsetof(FClass, ClassConfigName)              == 216,
                  "FClass ABI lock (audit-corrected): ClassConfigName at offset 216");

} // namespace XCore::Reflect
