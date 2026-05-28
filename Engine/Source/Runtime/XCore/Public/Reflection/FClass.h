// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FClass.h -- the 240-byte XObject-class descriptor (XCore-4b §7.3 +
// §11.3; FIX-R2-CRIT-1; XCoreXObject Rev 4 §2.4 + §11.2 Contract Rev
// 13.9 micro-bump).
// =====================================================================
//
// REV 13.9 ADDITION (XCoreXObject Phase 5.a' Contract prerequisite):
//
//   FClass grows by 16 bytes (224 -> 240) via TWO 8-byte appendages:
//
//     (a) FStruct base grows by 8 (112 -> 120) via the appended
//         `RefSchema` field (per FStruct.h SPEC DRIFT NOTICE update);
//         every FClass-specific offset therefore shifts by +8.
//
//     (b) FClass-specific itself grows by 8 (112 -> 120) via the
//         appended `LifecycleTable` pointer at FClass-specific offset
//         112 (FClass-absolute offset 232). The slot points at a
//         .rodata-resident FXObjectLifecycleTable emitted by XHT per
//         FClass; XCoreXObject's lifecycle dispatch (PostInitProperties,
//         BeginDestroy, FinishDestroy, AddReferencedObjects, Serialize,
//         PostLoad, PreSave) reads through this table instead of a
//         per-class virtual.
//
//   Total: FClass 224 -> 240 (+16 bytes net per XCoreXObject Rev 4
//   §11.2 cascade). The arithmetic correction over Rev 2's inconsistent
//   232 figure is preserved at the spec (Rev 3 / FIX-C-R2-1).
//
//   Rationale (per XCoreXObject Rev 4 spec FIX-A-CRIT-3 / O1 arbitration):
//   alternatives rejected per Prime Directive: parallel TMap lookup per
//   lifecycle dispatch = wrong perf (hash probe per call); inline 8-slot
//   table per FClass = 720 KB module footprint (8 fn ptrs * 10k classes).
//   The 8-byte pointer is the right trade.
//
// =====================================================================
//
// XCore-4b Rev 3, Section 7.3 ("FClass") + Section 11.3 layout row
// `FClass: 200 bytes (104 FStruct + 96 FClass-specific)`.
//
// SPEC DRIFT (Phase 4b.5 audit-corrected; root cause documented in
// FStruct.h SPEC DRIFT NOTICE): TArray = 24 bytes (not 16), so
// ObjectRefProperties + ClassReps + NetFields each consume 8 more
// bytes than the spec expected. Phase 5.a' Rev 13.9 micro-bump
// cascades a further +16 bytes net. Cascading totals:
//
//     FStruct base   120 (Phase 4b.5: 112; Phase 5.a' Rev 13.9: +8 RefSchema)
//     FClass-specific 120 (Phase 4b.5: 112; Phase 5.a' Rev 13.9: +8 LifecycleTable)
//        - +8 from ClassReps (24 vs 16)
//        - +8 from NetFields (24 vs 16)
//        - +8 from LifecycleTable (Rev 13.9 appendage at FClass-specific.112)
//     Total          240 (was spec-asserted 200; Phase 4b.5: 224; Rev 13.9: 240)
//
// FClass extends FStruct and is the runtime descriptor for XObject-
// derived classes (the C++ side; the C# transpilation produces matching
// FClass instances). UE's `UClass` (`Class.h:3893`) extends UStruct and
// carries ~80 fields; XPact's FClass extends FStruct and consolidates
// to the load-bearing subset.
//
// LAYOUT (Phase 4b.5 audit-corrected baseline 224, Phase 5.a' Rev 13.9
// micro-bump cascade to 240):
//
//   struct alignas(8) FClass : FStruct {
//       // FStruct base @ 0-119 (120 bytes; Phase 4b.5: 112; Rev 13.9: +8 RefSchema appended)
//       void                          (*ClassConstructorFn)(void*, FFieldVariant);  // 120 +8
//       void*                         (*ClassVTableHelperCtorCaller)(void*);        // 128 +8
//       mutable std::atomic<const FObject*> ClassDefaultObject;  // 136 +8 (lazy CDO; Phase 5.d const-qualified per Rev 3 FIX-M-R2-30 immutable-CDO discipline)
//       EClassFlags                   ClassFlags;                // 144 +8
//       EClassCastFlags               ClassCastFlags;            // 152 +8
//       const FClass*                 ClassWithin;               // 160 +8
//       int32                         FirstOwnedClassRep;        // 168 +4
//       int32                         ClassRepCount;             // 172 +4
//       TArray<FRepRecord>            ClassReps;                 // 176 +24 (TArray=24)
//       TArray<FField*>               NetFields;                 // 200 +24 (TArray=24)
//       FName                         ClassConfigName;           // 224 +8
//       const FXObjectLifecycleTable* LifecycleTable;            // 232 +8 (Rev 13.9 appended)
//   };
//
// sizeof(FClass) == 240.
//
// XPACT_FCLASS_LAYOUT_TAG (Rev 13.9 v6 per XCoreXObject Rev 4 §11.1):
//   "FClass-v6 (Contract Rev 13.9 extension via XCoreXObject Rev 3):
//    240 bytes = 120 FStruct base (with appended RefSchema@112) +
//    120 FClass-specific (with appended LifecycleTable@FClass-specific.112
//    = FClass-absolute.232). FClass-specific field layout (offsets
//    relative to FStruct end at FClass-absolute 120):
//    ClassConstructorFn@0, ClassVTableHelperCtorCaller@8,
//    ClassDefaultObject@16, ClassFlags@24, ClassCastFlags@32,
//    ClassWithin@40, FirstOwnedClassRep@48, ClassRepCount@52,
//    ClassReps@56 (24 byte TArray<FRepRecord>), NetFields@80 (24
//    byte TArray<FField*>), ClassConfigName@104, LifecycleTable@112
//    (Rev 3 appended; FClass-absolute offset 232)."
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

    // FXObjectLifecycleTable -- the .rodata-resident per-FClass
    // lifecycle dispatch table emitted by XHT for the Phase 5.a'
    // Contract Rev 13.9 micro-bump (XCoreXObject Rev 4 §2.4 +
    // §11.1 tag XPACT_XOBJECT_LIFECYCLE_TABLE_TAG). Full type ships
    // at XCoreXObject (System 5); Phase 5.a' forward-declares so
    // FClass can reference it as a pointer slot.
    //
    // The table is 72 bytes per FClass: 8-byte header (Capabilities
    // bitmask + _padHeader) + 8 slots * 8 bytes for the lifecycle
    // function-pointer dispatch (PostInitProperties, BeginDestroy,
    // IsReadyForFinishDestroy, FinishDestroy, Serialize,
    // AddReferencedObjects, PostLoad, PreSave). XCoreXObject's
    // dispatcher reads the Capabilities bitmask first to gate which
    // slots are populated; an unimplemented slot is the default no-op
    // (per FIX-A-HIGH-13 the Serialize slot signature is
    // void(*)(XObject*, FArchive&, const FArchiveContext*)).
    struct FXObjectLifecycleTable;

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
        //
        // PHASE 5.d (Rev 3 FIX-M-R2-30 / FIX-A-MED-30): the type IS
        // `std::atomic<const FObject*>` -- the `const` qualification
        // is the type-system enforcement of XCoreXObject Rev 4
        // §8.1.1's immutable-CDO discipline. The CDO is constructed
        // exactly once + read concurrently by editor / delta-
        // serialisation / replication reconciliation; immutability
        // eliminates the data-race surface that UE's mutable CDO
        // exposes. UE 5.6+ ships an opt-in `UE_WITH_IMMUTABLE_CDO`;
        // XPact's posture is "default + always".
        //
        // The stored type is `const FObject*` (FObject is the
        // forward-declared placeholder that resolves to XCore::XObject
        // at the XCoreXObject layer via reinterpret_cast at the CDO-
        // set site -- XCoreXObject's CDO management owns the
        // reconciliation). The byte layout is identical to the prior
        // `FObject*` form (the const qualifier does not affect the
        // pointer representation).

        mutable ::std::atomic<const FObject*> ClassDefaultObject;      // 128 +8

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

        // ---- Class config name (FClass-absolute offset 224; 8 bytes) ----
        //
        // The .ini section name (Engine.ini, Game.ini, etc.) populated
        // from XCLASS(config = ...). NAME_None means "not config-
        // loaded".
        //
        // Phase 5.a' Rev 13.9 cascade: offset shifts from 216 -> 224
        // because FStruct base grew by +8 (RefSchema appendage).

        FName              ClassConfigName;                             // 224 +8

        // ---- Lifecycle dispatch table (FClass-absolute offset 232;
        //      8 bytes; Rev 13.9 addition per XCoreXObject Rev 4 §2.4) ----
        //
        // Rev 13.9 addition (XCoreXObject Phase 5.a' Contract
        // prerequisite per XCoreXObject Rev 4 §2.4 / FIX-A-CRIT-3 /
        // O1 arbitration). Populated by XCoreXObject's FClass::Link
        // path for classes with non-trivial lifecycle hooks
        // (PostInitProperties, BeginDestroy, FinishDestroy,
        // AddReferencedObjects, Serialize, PostLoad, PreSave); nullptr
        // for classes that use only default lifecycle behavior (no
        // explicit hook implementations -- the dispatcher short-circuits
        // on nullptr and runs the default no-op path).
        //
        // The LifecycleTable points at a constinit
        // FXObjectLifecycleTable in the owning module's .rodata; XHT
        // emits the table at .gen.cpp time alongside the FClass
        // descriptor. XCoreXObject's lifecycle dispatch reads the
        // table's Capabilities bitmask first to gate which slots are
        // populated. The per-class table size is 72 bytes (8 header +
        // 8 slots * 8 bytes); the ~10k-classes-per-project budget puts
        // module-aggregate table footprint at ~720 KB, but the table
        // is in .rodata (shared, demand-paged), so the resident-set
        // cost is bounded by hot-class working set.
        //
        // FClass-specific offset 112 = FClass-absolute offset 232
        // (120 FStruct base + 112 within FClass-specific). The slot
        // is the load-bearing 8-byte append for Contract Rev 13.9
        // micro-bump per XCoreXObject Rev 4 §11.2 (FClass-specific
        // 112 -> 120 bytes; FClass total 224 -> 240 bytes; +16 net
        // when combined with FStruct.RefSchema appendage).
        //
        // Hot-reload safety: the LifecycleTable pointer is per-FClass
        // and per-module; an XHT-regenerated .gen.cpp publishes a new
        // table, and FClass::Link rewrites the slot during the
        // hot-reload cascade. The table itself is immutable .rodata.

        const FXObjectLifecycleTable* LifecycleTable = nullptr;        // 232 +8

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
            , ClassDefaultObject(static_cast<const FObject*>(nullptr))
            , ClassFlags(EClassFlags::CLASS_None)
            , ClassCastFlags(EClassCastFlags::kNone)
            , ClassWithin(nullptr)
            , FirstOwnedClassRep(0)
            , ClassRepCount(0)
            , ClassReps()
            , NetFields()
            , ClassConfigName()
            , LifecycleTable(nullptr)   // Rev 13.9: populated by
                                        // XCoreXObject FClass::Link
                                        // for classes with non-trivial
                                        // lifecycle hooks; nullptr
                                        // default for programmatic
                                        // ctor path.
        {
        }

        FClass(FName InName, const FStruct* InSuper,
               EClassFlags InClassFlags = EClassFlags::CLASS_None,
               EClassCastFlags InClassCastFlags = EClassCastFlags::kNone) noexcept
            : FStruct(InName, InSuper)
            , ClassConstructorFn(nullptr)
            , ClassVTableHelperCtorCaller(nullptr)
            , ClassDefaultObject(static_cast<const FObject*>(nullptr))
            , ClassFlags(InClassFlags)
            , ClassCastFlags(InClassCastFlags)
            , ClassWithin(nullptr)
            , FirstOwnedClassRep(0)
            , ClassRepCount(0)
            , ClassReps()
            , NetFields()
            , ClassConfigName()
            , LifecycleTable(nullptr)   // Rev 13.9: populated by
                                        // XCoreXObject FClass::Link
                                        // for classes with non-trivial
                                        // lifecycle hooks; nullptr
                                        // default for programmatic
                                        // ctor path.
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

        // Rev 13.9 accessor for the per-class lifecycle dispatch
        // table pointer. Returns nullptr for classes that use only
        // the default no-op lifecycle path; the caller (XCoreXObject's
        // lifecycle dispatcher) MUST check for nullptr before reading
        // any slot.
        [[nodiscard]] XPACT_FORCEINLINE const FXObjectLifecycleTable* GetLifecycleTable() const noexcept
        {
            return LifecycleTable;
        }

        // -------------------------------------------------------------
        // GetCDO -- access the lazy class default object.
        //
        // Returns the current CDO pointer (may be nullptr if not yet
        // populated). Per spec: lazy creation is the
        // XReflectionRuntime's / XCoreXObject CDO management's
        // responsibility (System 5+); Phase 4b.5 ships the accessor
        // only.
        //
        // PHASE 5.d (Rev 3 FIX-M-R2-30): returns `const FObject*` so
        // the immutable-CDO discipline is enforced at the type system
        // level. Callers that need to MUTATE the CDO (rare; only the
        // hot-reload cascade per spec §9.3) must use TryCAS-style
        // pointer swap via the atomic's compare_exchange surface
        // directly.
        // -------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE const FObject* GetCDO() const noexcept
        {
            return ClassDefaultObject.load(::std::memory_order_acquire);
        }

        // -------------------------------------------------------------
        // SetCDO -- atomic publish of the resolved CDO.
        //
        // The XCoreXObject CDO-management path (lazy first-construct
        // OR DrainPendingEagerCDOs) calls this after constructing the
        // singleton. Memory ordering is release so a subsequent
        // GetCDO observes a fully-constructed object.
        //
        // PHASE 5.d (Rev 3 FIX-M-R2-30): accepts `const FObject*` --
        // the type-system enforcement of immutable-CDO. The atomic
        // store is the only legitimate write to the slot after the
        // initial construction; hot-reload cascade publishes via
        // compare_exchange to atomically swap the old/new CDO pair.
        // -------------------------------------------------------------
        XPACT_FORCEINLINE void SetCDO(const FObject* InCDO) noexcept
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
    // ABI locks (Phase 4b.5 audit-corrected baseline 224 + Phase 5.a'
    // Contract Rev 13.9 micro-bump per XCoreXObject Rev 4 §11.2 / §11.3
    // adding +16 bytes net = 240 total).
    //
    // Phase 5.a' cascade:
    //   (a) FStruct base 112 -> 120 (RefSchema appended @ FStruct.112).
    //       Every FClass-specific offset therefore shifts +8 from the
    //       Phase 4b.5 baseline.
    //   (b) LifecycleTable appended @ FClass-specific.112 = FClass-
    //       absolute.232 (8 bytes; per FIX-A-CRIT-3 / O1 arbitration).
    //
    // Reference: XCoreXObject Rev 4 §11.3 XPACT_VERIFY_XOBJECT_LAYOUT
    // pin set authoritatively names these offsets; this header is the
    // C++ realization.
    // ---------------------------------------------------------------------
    static_assert(sizeof(FClass) == 240,
                  "FClass ABI lock (Contract Rev 13.9 micro-bump per "
                  "XCoreXObject Rev 4 §11.2 / FIX-C-R2-1): 240 bytes = "
                  "120 FStruct base (with appended RefSchema@112) + "
                  "120 FClass-specific (with appended LifecycleTable "
                  "@ FClass-specific.112 = FClass-absolute.232). +16 "
                  "bytes net over the Phase 4b.5 baseline (224).");
    static_assert(alignof(FClass) == 8,
                  "FClass ABI lock: 8-byte alignment per §7.3 alignas(8)");

    // Member offsets locked per the Rev 13.9 cascade (every FClass-
    // specific offset shifts +8 from the Phase 4b.5 baseline because
    // FStruct base grew by +8 via the appended RefSchema; ClassReps
    // + NetFields shift by an additional +8 each due to TArray = 24;
    // LifecycleTable is the new Rev 13.9 appendage at FClass-absolute
    // offset 232).
    static_assert(offsetof(FClass, ClassConstructorFn)           == 120,
                  "FClass ABI lock (Rev 13.9 cascade): ClassConstructorFn "
                  "at offset 120 (Phase 4b.5: 112; +8 shift from "
                  "FStruct base growing to 120 via Rev 13.9 RefSchema "
                  "appendage)");
    static_assert(offsetof(FClass, ClassVTableHelperCtorCaller)  == 128,
                  "FClass ABI lock (Rev 13.9 cascade): ClassVTableHelperCtorCaller at offset 128");
    static_assert(offsetof(FClass, ClassDefaultObject)           == 136,
                  "FClass ABI lock (Rev 13.9 cascade): ClassDefaultObject at offset 136");
    static_assert(offsetof(FClass, ClassFlags)                   == 144,
                  "FClass ABI lock (Rev 13.9 cascade): ClassFlags at offset 144");
    static_assert(offsetof(FClass, ClassCastFlags)               == 152,
                  "FClass ABI lock (Rev 13.9 cascade): ClassCastFlags at offset 152");
    static_assert(offsetof(FClass, ClassWithin)                  == 160,
                  "FClass ABI lock (Rev 13.9 cascade): ClassWithin at offset 160");
    static_assert(offsetof(FClass, FirstOwnedClassRep)           == 168,
                  "FClass ABI lock (Rev 13.9 cascade): FirstOwnedClassRep at offset 168");
    static_assert(offsetof(FClass, ClassRepCount)                == 172,
                  "FClass ABI lock (Rev 13.9 cascade): ClassRepCount at offset 172");
    static_assert(offsetof(FClass, ClassReps)                    == 176,
                  "FClass ABI lock (Rev 13.9 cascade): ClassReps at offset 176");
    static_assert(offsetof(FClass, NetFields)                    == 200,
                  "FClass ABI lock (Rev 13.9 cascade): NetFields at offset 200 "
                  "(Phase 4b.5: 192; +8 shift from FStruct base growth)");
    static_assert(offsetof(FClass, ClassConfigName)              == 224,
                  "FClass ABI lock (Rev 13.9 cascade): ClassConfigName at offset 224");

    // Phase 5.a' Rev 13.9 micro-bump pin per XCoreXObject Rev 4 §11.3
    // (FIX-H-R2-2): LifecycleTable slot at FClass-absolute offset 232
    // (= 120 FStruct base + 112 FClass-specific). Load-bearing for
    // per-class lifecycle dispatch (PostInitProperties, BeginDestroy,
    // FinishDestroy, AddReferencedObjects, Serialize, PostLoad,
    // PreSave) without per-call hash-probe overhead.
    static_assert(offsetof(FClass, LifecycleTable)              == 232,
                  "FClass ABI lock (Rev 13.9 per XCoreXObject Rev 4 "
                  "§11.3 / FIX-H-R2-2): LifecycleTable at FClass-"
                  "absolute offset 232 (= 120 FStruct base + 112 "
                  "FClass-specific offset). Appended after "
                  "ClassConfigName@224 for the per-class lifecycle "
                  "dispatch table per §2.4.");

} // namespace XCore::Reflect
