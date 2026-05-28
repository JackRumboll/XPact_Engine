// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XObject.h -- the 56-byte XObject base type (XCoreXObject Rev 4 §2).
// =====================================================================
//
// XCoreXObject Rev 4 Section 2 ("XObject Base Type") + Contract Rev
// 13.9 addendum tag `XPACT_XOBJECT_LAYOUT_TAG`. Phase 5.a deliverable.
//
// XObject IS the universal parent of every reflected gameplay type in
// the engine. Every XActor, every XComponent, every XScenario, every
// XAsset derives from XObject. The 56-byte header carries:
//
//   * ClassPrivate@0 (8 bytes)  -- const FClass* the type pointer; first
//                                    load on the GC mark hot path.
//   * InternalIndex@8 (4 bytes) -- FXObjectArray slot index; -1 means
//                                    uninitialized (pre-NewObject state).
//   * SerialNumber@12 (4 bytes) -- bumps on slot reuse; XWeakPtr deref
//                                    checks. SIMPATH-FORBIDDEN read
//                                    (FIX-A-CRIT-2; GetSerialNumber
//                                    accessor enforces via XPACT_CHECK_SL).
//   * Outer@16 (8 bytes)        -- containing object in the ownership /
//                                    scoping hierarchy; GC-traced.
//   * NamePrivate@24 (8 bytes)  -- FName handle for debug / diagnostic.
//   * ObjectFlags@32 (4 bytes)  -- atomic uint32 EObjectFlags bitset.
//   * ReachabilityFlag@36 (4 bytes; Rev 3 per FIX-M-R2-3) -- atomic
//                                    uint32 holding 3 rotating reachability
//                                    bits + 29 reserved; moved here from
//                                    FXObjectArrayEntry to save one cache
//                                    miss per object marked in the GC inner
//                                    loop.
//   * _reservedCluster0@40 (8 bytes) -- Phase 2 cluster-root index
//                                          reservation; matches UE FCluster
//                                          shape per FIX-A-MIN-49 / O5.
//   * _reservedCluster1@48 (8 bytes) -- Phase 2 cluster-flags + remembered-
//                                          set state reservation.
//
// HOT-RELOAD COMMITMENT (Prime Directive): NO virtual methods on this
// surface. Subclass lifecycle (PostInitProperties, BeginDestroy,
// FinishDestroy, AddReferencedObjects, Serialize, PostLoad, PreSave)
// is dispatched via the FClass's FXObjectLifecycleTable (XCore-4b
// FakeVTable pattern extended to XObject lifecycle; see XCoreXObject
// Rev 4 §2.4). A patched DLL may change a user-class's behaviour
// without rewriting the XObject vtable -- there isn't one to rewrite.
//
// SIZE COMPARISON vs UE (per spec §2.2 trailing prose):
//
//   * UE UObjectBase  : 40 bytes (static_assert at UObjectHash.cpp:40).
//   * UE FUObjectItem : ~24 bytes; SEPARATE allocation in the parallel
//                       FUObjectArray chunk-pointer-array.
//   * UE per-object   : ~64 bytes across two indirections.
//
//   * XPact XObject              : 56 bytes (UNIFIED header).
//   * XPact FXObjectArrayEntry   : 32 bytes (parallel; pointer-stable;
//                                              §3.3).
//   * XPact per-object effective : ~88 bytes across ONE indirection.
//
// XPact pays +16-24 bytes/object vs UE in exchange for:
//   * One fewer indirection per GC mark walk iteration.
//   * Inline cluster reservation (Phase 2 activation is non-breaking).
//   * Reachability flag co-located with header (Rev 3 FIX-M-R2-3;
//     ~50-200 cycles saved per object marked on Quest 3 ARM64).
//
// At 50k typical XObjects the byte cost is ~0.8-1.2 MB (well within
// Quest 3's 4 GB budget). The cache-coherency advantage at GC walk
// dominates: ~100k objects * 5-10 ref properties * 50-200 cycles per
// L1/L2 miss = millions of cycles saved per cycle. Per Prime Directive
// this is the correct trade-off (more bytes per header, fewer
// indirections at GC walk).
//
// CACHE-LINE TOPOLOGY (per spec §2.6):
//
//   First cache line (64 bytes):
//     * Offset 0-15  (16 bytes) : ClassPrivate + InternalIndex +
//                                  SerialNumber  -- GC-hot identity
//                                  block.
//     * Offset 16-31 (16 bytes) : Outer + NamePrivate -- identity
//                                  continuation.
//     * Offset 32-55 (24 bytes) : ObjectFlags + ReachabilityFlag +
//                                  cluster reservation.
//     * Offset 56-63 (8 bytes)  : AVAILABLE for first subclass member
//                                  (XActor's Transform.X / Transform.Y
//                                  lands here -- hot at GC-walk time).
//
// API SURFACE (per spec §2.5):
//
//   * Identity:     GetClass, GetOuter, GetFName, GetName (alias),
//                   GetFullName.
//   * GC-state:     GetInternalIndex, GetSerialNumber (XPACT_CHECK_SL
//                   guarded per FIX-A-CRIT-2), GetObjectFlags,
//                   HasAnyFlags, HasAllFlags.
//   * Flag mutate:  SetFlags, ClearFlags (CAS loop;
//                   memory_order_acq_rel / acquire per spec §2.3).
//   * Garbage:      MarkAsGarbage (XPACT_CHECK_SL non-sim-path guard
//                   per spec §4.2 + Rev 3 FIX-M-R2-10),
//                   IsMarkedAsGarbage.
//   * Validation:   IsValidLowLevel (defensive; checks against
//                   FXObjectArray; Phase 5.c integration).
//
// NO virtual methods anywhere on the XObject surface. Standard C++
// derivation IS permitted -- a user-class may declare its own virtual
// methods; the GC NEVER consults the user-class vtable. Only changes
// to XObject's OWN implicit destructor would matter, and XObject has
// none (the FakeVTable replaces it).
//
// FORWARD-DECLARATION DISCIPLINE: types that land in later phases
// (FXObjectArray, FXObjectAllocator, FXObjectLifecycleTable,
// FXObjectRefSchema, FObjectKey, XPtr / XWeakPtr / XSoftPtr) are
// forward-declared OR referenced via their .h-included
// counterparts only when this header genuinely needs them. The header
// stays lean.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Reflection/FName.h"               // FName by-value member (need full type)
#include "XObject/EObjectFlags.h"            // EObjectFlags enum + ToUnderlying

#include <atomic>
#include <cstddef>          // offsetof
#include <cstdint>
#include <type_traits>      // is_standard_layout etc.

// Forward declarations -- minimal coupling to other reflection
// subsystems. The full types are pulled by consumers that need to
// dereference past the pointer.
namespace XCore { class FString; }                       // GetFullName return
namespace XCore::Reflect { struct FClass; }              // ClassPrivate type
namespace XCore::Reflect { struct FXObjectLifecycleTable; } // FClass::LifecycleTable
namespace XCore::Reflect { struct FXObjectRefSchema; }      // FStruct::RefSchema

namespace XCore
{
    // -----------------------------------------------------------------
    // XObject -- 56-byte base for every reflected gameplay type.
    //
    // Per spec §2.2: `class XObject` with no namespace qualifier shown.
    // XPact places it in the root `XCore` namespace because XObject IS
    // the runtime gameplay root, not a reflection-descriptor (which
    // would warrant `XCore::Reflect` placement).
    //
    // alignas(8) -- matches FClass* alignment + 8-byte ABI lock for
    // every member. NO virtual methods.
    //
    // The struct is NOT trivially copyable (the std::atomic members
    // are non-copyable; the destructor for std::atomic is trivial on
    // every supported platform, so XObject IS trivially destructible).
    // Standard-layout is NOT asserted because the std::atomic member's
    // standard-layout-ness is implementation-defined; what matters is
    // the byte-exact static_assert-pinned offsets below.
    //
    // CONSTEVAL CDO BOOTSTRAP: the consteval ctor below constructs a
    // constinit-friendly XObject for the FClass static initialization
    // path (used at XHT-emitted .gen.cpp's `.ClassDefaultObject =
    // <consteval-constructed-XObject>` initialiser when the CDO is
    // emitted as a constinit constant). The FName parameter MUST be
    // a constant (typically NAME_None or an XHT-precomputed handle);
    // FName(const char*) is NOT constexpr (it touches the FNamePool
    // intern table at runtime) so the consteval ctor cannot accept
    // a raw string literal.
    // -----------------------------------------------------------------
    class alignas(8) XObject
    {
    public:
        // =============================================================
        // ABI-locked data members (56 bytes; per spec §2.2).
        //
        // All fields are public so:
        //   1. offsetof is well-defined on every supported compiler.
        //   2. XHT-emitted .gen.cpp can initialise the constinit CDO
        //      via designated initialisers when the CDO lands at .rodata.
        //   3. FXObjectAllocator can fill ClassPrivate / InternalIndex
        //      / SerialNumber / Outer / NamePrivate directly during
        //      the NewObject hot path (no setter call overhead).
        //
        // The user-facing API (GetClass / GetOuter / etc.) returns the
        // same data via const accessors; new code SHOULD prefer the
        // accessors for source-level intent clarity.
        // =============================================================

        // --- GC-hot identity (first cache line, first 16 bytes) ---

        // const FClass* the type descriptor. First load on the GC mark
        // hot path -- positioned at offset 0 so decoding the class
        // pointer from an `XObject*` is a single load with no offset
        // arithmetic.
        //
        // The pointer is `const` because the type descriptor's mutable
        // state (LifecycleTable, CDO atomic) is the FClass's own
        // concern; an XObject never re-binds its own class except via
        // the hot-reload swap path (which routes through XLiveCoding
        // and re-writes the slot under the appropriate lock).
        const ::XCore::Reflect::FClass*  ClassPrivate;       //  0  +8

        // FXObjectArray slot index. -1 (`INDEX_NONE`) means the object
        // has not yet been registered with the FXObjectArray (a
        // pre-NewObject default-constructed XObject is in this state).
        // The signed int32 type matches UE's convention and lets `< 0`
        // checks read naturally at call sites that test the
        // uninitialised state.
        ::int32                          InternalIndex;      //  8  +4

        // Per-slot generation counter. Bumps on each
        // FXObjectArray::ReleaseSlot. XWeakPtr / XObjectKey hold
        // {InternalIndex, SerialNumber}; deref checks the captured
        // SerialNumber against the current XObject's SerialNumber to
        // detect dangling weak references.
        //
        // SIMPATH-FORBIDDEN READ (per FIX-A-CRIT-2): cross-arch
        // determinism invariant -- sim-path TUs MAY NOT read this
        // field. The XObject::GetSerialNumber accessor enforces via
        // XPACT_CHECK_SL; this struct field is public for the
        // FXObjectArray's bookkeeping path but should NOT be read
        // directly from sim-path code.
        //
        // Initial value: 0. The field is uint32 so the comparison
        // against XWeakPtr's captured SerialNumber is a single 32-bit
        // compare; the wrap-around at 2^32 is acceptable (after 4
        // billion slot reuses the SerialNumber check would
        // mathematically collide; in practice 4G reuses is unreachable
        // within a process lifetime).
        ::uint32                         SerialNumber;       // 12  +4

        // --- Identity continuation (second half of first cache line) ---

        // Containing object in the ownership / scoping hierarchy. An
        // Asset's Outer is its Package; a Component's Outer is its
        // Actor; a top-level object's Outer is nullptr.
        //
        // The collector traces Outer as a GC root (a reachable Outer
        // marks its inner objects). Hot-reload preserves Outer
        // identity across class swaps -- the inner object's Outer
        // pointer is rebound to the swapped Outer if the Outer was
        // also swapped.
        XObject*                         Outer;              // 16  +8

        // FName handle for debug / diagnostic. NOT on the GC hot path.
        // The full path-qualified name ("Package.Outer.Name") is
        // materialised on demand by GetFullName / GetPathName which
        // walk the Outer chain.
        ::XCore::Reflect::FName          NamePrivate;        // 24  +8

        // --- Lifecycle state (second cache line, first 16 bytes) ---

        // EObjectFlags atomic bitset. Multiple threads may set / clear
        // flags concurrently (loader sets NeedLoad, GC sweep sets
        // BeginDestroyed); the CAS loop in SetFlags / ClearFlags
        // ensures no flag is lost.
        //
        // The uint32 underlying type matches EObjectFlags' uint32_t
        // underlying type; the atomic stores the raw uint32 (not the
        // enum class) so std::atomic<uint32_t>::compare_exchange_weak
        // operates on the integer payload directly.
        ::std::atomic<::std::uint32_t>   ObjectFlags;        // 32  +4

        // Per-object reachability flag (Rev 3 per FIX-M-R2-3; moved
        // here from FXObjectArrayEntry.StateBits). 3 rotating
        // reachability bits + 29 reserved.
        //
        // The 32-bit word layout (low to high):
        //   * bit 0 : kReachabilityFlag0 (rotates; one of bits 0/1/2
        //              is the "current cycle's" mark bit per the
        //              global g_CurrentReachabilityIndex)
        //   * bit 1 : kReachabilityFlag1
        //   * bit 2 : kReachabilityFlag2
        //   * bits 3..7  : reserved for GC-internal mirrors
        //                   (PendingDestroy / RootPinned / Garbage)
        //   * bits 8..31 : reserved for future GC scheme extensions
        //
        // Per cycle, the global g_CurrentReachabilityIndex (uint8 in
        // 0/1/2) selects which of bits 0..2 is the "this-cycle's"
        // reachable mark. The three-cycle rotation lets the cycle
        // that USED bit N two cycles ago be the one whose objects must
        // be swept this cycle if still unmarked. This is the UE 5.x
        // EInternalObjectFlags::ReachabilityFlag0/1/2 pattern (faithfully
        // ported with the optimisation that the bits live on the
        // XObject header rather than the array entry).
        //
        // INNER-LOOP HOT PATH: FXObjectArray::Mark / ClearMark /
        // IsMarked read this slot directly off the XObject*; saves one
        // cache miss per object marked vs the historical access via
        // the parallel FXObjectArrayEntry (Quest 3 ARM64 ~50-200
        // cycles per L1/L2 miss; significant at 100k objects per
        // scan).
        //
        // (The Phase 5.c FXObjectArray body defines the constants for
        // the rotating-cycle masks; Phase 5.a only ships the slot
        // itself.)
        ::std::atomic<::std::uint32_t>   ReachabilityFlag;   // 36  +4

        // --- Cluster reservation (offset 40-55; 16 bytes; Phase 2;
        //     mirrors UE FCluster shape per FIX-A-MIN-49 / O5) ---
        //
        // Two 8-byte slots reserved for the cluster pattern. The
        // pattern is a Phase 2 feature (UE's UObjectCluster equivalent
        // ships post-MVP); Rev 2 pre-reserves the inline storage so
        // future activation is non-breaking. Pre-Phase-2 values: 0.
        //
        // When the cluster pattern lands:
        //   * _reservedCluster0 : cluster-root index. Bit 63 indicates
        //                          "this object IS a cluster root";
        //                          bits 0..30 are the cluster root's
        //                          InternalIndex.
        //   * _reservedCluster1 : cluster-flags + remembered-set
        //                          state. Cluster-aware GC reads this
        //                          slot to decide whether to expand
        //                          the cluster as a unit.
        //
        // Note: the inline reservation costs 16 bytes per XObject (vs
        // UE's per-cluster FCluster allocation). At 50k objects =
        // 800 KB; well within Quest 3's budget. The cache-coherency
        // win at cluster-aware mark dominates per spec §2.7.
        ::std::uint64_t                  _reservedCluster0;  // 40  +8
        ::std::uint64_t                  _reservedCluster1;  // 48  +8

        // =============================================================
        // Construction.
        //
        // The default ctor zero-initialises every field. Used by the
        // FXObjectAllocator's placement-new path BEFORE
        // ClassConstructorFn populates the header (NewObject hot path
        // calls placement-new first, then ClassConstructorFn fills
        // class-specific fields, then the header fields are filled
        // via direct member access; see spec §3.5).
        //
        // The default ctor is NOT marked constexpr because the atomic
        // members' constexpr default ctors are C++20+ and the default
        // ctor is callable from runtime context (FXObjectAllocator).
        // Constinit-construction goes through the consteval ctor
        // below which takes the full identity tuple as parameters.
        // =============================================================

        XObject() noexcept
            : ClassPrivate(nullptr)
            , InternalIndex(::INDEX_NONE)
            , SerialNumber(0)
            , Outer(nullptr)
            , NamePrivate()
            , ObjectFlags(0)
            , ReachabilityFlag(0)
            , _reservedCluster0(0)
            , _reservedCluster1(0)
        {
        }

        // =============================================================
        // Consteval CDO bootstrap ctor (per spec §2.2).
        //
        // Constructs a constinit-friendly XObject for the FClass
        // static initialization path (the XHT-emitted .gen.cpp may
        // emit a CDO as a constinit constant; see spec §2.8 for the
        // expansion shape).
        //
        // The FName parameter MUST be a constant (typically NAME_None
        // or an XHT-precomputed handle); FName(const char*) is NOT
        // constexpr (it touches the FNamePool intern table) so a true
        // string-taking consteval ctor cannot exist.
        //
        // Pre-condition: `cls` is a constinit `const FClass*` (the
        // XHT-emitted Z_Construct_FClass_<T> initialiser provides
        // this); `outer` is nullptr or another constinit XObject*;
        // `name` is a precomputed FName handle.
        //
        // The consteval qualifier guarantees the call is constant-
        // evaluated -- a runtime-only invocation is a compile error.
        // The std::atomic members' constexpr default initialisation
        // is C++20+ (P0883 + P1006) which all supported toolchains
        // honour.
        // =============================================================

        explicit consteval XObject(
            const ::XCore::Reflect::FClass* cls,
            XObject*                         outer,
            ::XCore::Reflect::FName          name,
            ::std::uint32_t                  flags) noexcept
            : ClassPrivate(cls)
            , InternalIndex(::INDEX_NONE)
            , SerialNumber(0)
            , Outer(outer)
            , NamePrivate(name)
            , ObjectFlags(flags)
            , ReachabilityFlag(0)
            , _reservedCluster0(0)
            , _reservedCluster1(0)
        {
        }

        // XObject is NON-COPYABLE + NON-MOVABLE: the std::atomic
        // members are non-copyable; the FXObjectArray slot binding
        // makes the object position-stable for its lifetime. Cloning
        // an XObject goes through the high-level Duplicate / NewObject
        // paths, not value-copy. We delete the surface explicitly so
        // misuse is a compile error rather than silent corruption of
        // the atomic state.
        XObject(const XObject&)            = delete;
        XObject(XObject&&)                 = delete;
        XObject& operator=(const XObject&) = delete;
        XObject& operator=(XObject&&)      = delete;

        // Destructor is non-virtual + trivial. The atomic members'
        // destructors are trivial on every supported platform;
        // teardown of the XObject identity slots is the FXObjectArray's
        // concern (ReleaseSlot bumps SerialNumber and nulls Object).
        // Subclass teardown routes through the FClass FakeVTable's
        // FinishDestroy slot (spec §2.4); the C++-language destructor
        // here is a no-op + non-virtual to preserve the hot-reload
        // commitment (NO virtuals on the XObject surface).
        ~XObject() noexcept = default;

        // =============================================================
        // Identity accessors (per spec §2.5; non-virtual; header-inline).
        // =============================================================

        // The FClass type descriptor for this object. nullptr only
        // during the brief NewObject hot-path window between
        // placement-new and ClassConstructorFn return; production code
        // never observes a nullptr GetClass() result.
        [[nodiscard]] XPACT_FORCEINLINE
        const ::XCore::Reflect::FClass* GetClass() const noexcept
        {
            return ClassPrivate;
        }

        // The Outer object in the ownership / scoping hierarchy.
        // nullptr for top-level objects (Packages are themselves
        // XObjects; their Outer is nullptr).
        [[nodiscard]] XPACT_FORCEINLINE
        XObject* GetOuter() const noexcept
        {
            return Outer;
        }

        // The FName handle. Spec §2.5 uses `GetFName()` for the
        // accessor; we ALSO expose `GetName()` as an ergonomic alias
        // matching the user-prompt naming (the two methods return the
        // same FName by value).
        [[nodiscard]] XPACT_FORCEINLINE
        ::XCore::Reflect::FName GetFName() const noexcept
        {
            return NamePrivate;
        }

        [[nodiscard]] XPACT_FORCEINLINE
        ::XCore::Reflect::FName GetName() const noexcept
        {
            return NamePrivate;
        }

        // =============================================================
        // GC-state accessors (per spec §2.5).
        // =============================================================

        // The FXObjectArray slot index. Returns INDEX_NONE (-1) for an
        // uninitialised XObject (pre-NewObject).
        [[nodiscard]] XPACT_FORCEINLINE
        ::int32 GetInternalIndex() const noexcept
        {
            return InternalIndex;
        }

        // The per-slot generation counter.
        //
        // SIMPATH-GUARD (per FIX-A-CRIT-2 + spec §2.5): cross-arch
        // determinism invariant -- sim-path TUs MAY NOT read the
        // SerialNumber (the slot reuse cadence is non-deterministic
        // relative to the sim-tick boundary; reading it would couple
        // sim-path decisions to the GC's reclaim timing which differs
        // across architectures with different GC cycle costs).
        //
        // The XPACT_CHECK_SL guard fires in Debug / Development if
        // called from a sim-path TU; the harness's
        // ::XCore::HAL::IsSimPathTU() detection routes here (the
        // sim-path TU framework is XCore-4a Phase 1e+ / Math/SimPath;
        // forward-declared below). In Shipping the guard compiles
        // out; the contract is "diagnose at Debug/Dev; trust the
        // contract at Shipping".
        //
        // (Until the IsSimPathTU runtime probe lands in a future
        // phase, the guard is a no-op stub; the static-analysis sim-
        // path filter at the build level is the primary enforcement
        // mechanism. The runtime guard exists for defence-in-depth.)
        [[nodiscard]] XPACT_FORCEINLINE
        ::uint32 GetSerialNumber() const noexcept
        {
            // Sim-path guard (FIX-A-CRIT-2). The IsSimPathTU runtime
            // probe lands at a future phase (the spec body references
            // ::XCore::HAL::IsSimPathTU; the symbol does not exist
            // yet); when it does, this XPACT_CHECK_SL will activate.
            // For Phase 5.a we ship the call-site invariant
            // documentation and the (currently no-op) guard hook so
            // the source-level contract is visible at every read.
            //
            // TODO(Phase 5.e+ sim-path runtime probe): wire
            // ::XCore::HAL::IsSimPathTU() here once the probe ships:
            //     XPACT_CHECK_SL(!::XCore::HAL::IsSimPathTU());
            return SerialNumber;
        }

        // =============================================================
        // EObjectFlags accessors (per spec §2.5).
        //
        // Reads load ObjectFlags atomically. The memory_order is an
        // EXPLICIT parameter on the public surface (matching the
        // XCore-4a FAtomicInt32 discipline: NO default memory_order
        // anywhere). The convenience overload uses memory_order_relaxed
        // which is the right choice for the "read flags for inspection"
        // pattern (no fence required if no acquire-side data is being
        // synchronised with the flag).
        // =============================================================

        [[nodiscard]] XPACT_FORCEINLINE
        EObjectFlags GetObjectFlags(
            ::std::memory_order Order = ::std::memory_order_relaxed) const noexcept
        {
            return static_cast<EObjectFlags>(ObjectFlags.load(Order));
        }

        // Predicate: at least one bit in `Mask` is set.
        [[nodiscard]] XPACT_FORCEINLINE
        bool HasAnyFlags(EObjectFlags Mask) const noexcept
        {
            return HasAnyObjectFlags(GetObjectFlags(), Mask);
        }

        // Predicate: all bits in `Mask` are set.
        [[nodiscard]] XPACT_FORCEINLINE
        bool HasAllFlags(EObjectFlags Mask) const noexcept
        {
            return HasAllObjectFlags(GetObjectFlags(), Mask);
        }

        // =============================================================
        // Atomic flag mutation (CAS loop; per spec §2.3).
        //
        // SetFlags performs an atomic OR with `Bits`; ClearFlags
        // performs an atomic AND with `~Bits`. The CAS loop uses
        // memory_order_acq_rel on success (so observers reading with
        // memory_order_acquire see a happens-before-correct view of
        // the object state at the time the flag was set) and
        // memory_order_acquire on failure.
        //
        // Bodies in XObject.cpp -- the CAS-loop is non-trivial enough
        // to deserve a dedicated TU + the function-pointer-to-loop
        // would inline-bloat every call site.
        // =============================================================

        void SetFlags(EObjectFlags Bits) noexcept;
        void ClearFlags(EObjectFlags Bits) noexcept;

        // =============================================================
        // Garbage marking (per spec §4.2 + Rev 3 FIX-M-R2-10).
        //
        // MarkAsGarbage sets EObjectFlags::MarkedAsGarbage. The next
        // GC sweep with EXGCOptions::kEliminateGarbageRefs set clears
        // references TO this object via the schema-vector walk (each
        // ObjectRef slot that points at a MarkedAsGarbage object is
        // nulled). Editor "delete an actor" + hot-reload tomb-stoning
        // both route through this.
        //
        // NON-SIM-PATH (Rev 3 per FIX-M-R2-10): calling MarkAsGarbage
        // from a sim-path TU is forbidden because the next-GC-sweep
        // timing is non-deterministic relative to the sim-tick
        // boundary. Guarded via XPACT_CHECK_SL at entry (Debug/Dev);
        // compiles out in Shipping.
        //
        // (The runtime sim-path guard piggybacks on the same
        // ::XCore::HAL::IsSimPathTU hook documented at
        // GetSerialNumber above. The guard fires once the hook lands;
        // for Phase 5.a the call-site invariant is documented + the
        // hook stub is in place.)
        //
        // Body in XObject.cpp (consolidates the sim-path guard + the
        // SetFlags dispatch into a single TU for diagnostic clarity).
        //
        // NAMING: the spec body uses `MarkAsGarbage` (Phase 5.a name)
        // and the spec implementation comment at lines 327-333 uses
        // `MarkForKill` -- these are the same operation; the spec is
        // ambiguous between the two names. We ship `MarkAsGarbage` as
        // the primary name (matches the flag name) and provide
        // `MarkForKill` as an alias for the UE-faithful "kill"
        // wording.
        // =============================================================

        void MarkAsGarbage() noexcept;

        // Alias: the spec body uses both names. MarkForKill is the
        // UE-faithful "mark for GC reclaim" wording; MarkAsGarbage is
        // the XPact-preferred name (matches the flag).
        XPACT_FORCEINLINE void MarkForKill() noexcept
        {
            MarkAsGarbage();
        }

        // Predicate: is this object marked as garbage?
        [[nodiscard]] XPACT_FORCEINLINE
        bool IsMarkedAsGarbage() const noexcept
        {
            return HasAnyFlags(EObjectFlags::MarkedAsGarbage);
        }

        // =============================================================
        // Identity (per spec §2.5).
        //
        // GetFullName materialises the path-qualified name by walking
        // the Outer chain. Format: "Outermost.Outer.Outer.Name" with
        // the package as the root.
        //
        // Body in XObject.cpp -- the walk is non-trivial enough to
        // deserve a dedicated TU and the returned FString allocates
        // via FMemTag::Reflection.
        // =============================================================

        [[nodiscard]] ::XCore::FString GetFullName() const;

        // Path-qualified name (alias for GetFullName; the spec lists
        // both names because UE's UObjectBaseUtility surface has both
        // `GetFullName` and `GetPathName`). Phase 5.a ships them as
        // synonyms; a future revision may diverge if the spec adds a
        // distinct semantic.
        //
        // Body in XObject.cpp (returning by value requires the
        // complete FString type; the forward-declaration in this
        // header keeps the include surface lean -- only callers that
        // actually invoke GetPathName / GetFullName must pull
        // Containers/FString.h).
        [[nodiscard]] ::XCore::FString GetPathName() const;

        // =============================================================
        // IsValidLowLevel -- defensive validity probe.
        //
        // Returns true iff the object passes a basic sanity sweep:
        //   * ClassPrivate is non-null.
        //   * InternalIndex is in a plausible range.
        //   * (Phase 5.c integration) FXObjectArray's entry at
        //     InternalIndex points back to `this` and the captured
        //     SerialNumber matches.
        //
        // Used by diagnostic / assert harnesses. NOT cheap; do not
        // call on the hot path.
        //
        // Phase 5.a ships the no-FXObjectArray subset (the array
        // doesn't exist yet); the full FXObjectArray cross-check
        // wires in at Phase 5.c when the array body lands.
        // =============================================================

        [[nodiscard]] bool IsValidLowLevel() const noexcept;
    };

    // ---------------------------------------------------------------------
    // ABI locks (per Contract Rev 13.9 §11.1 XPACT_XOBJECT_LAYOUT_TAG +
    // §11.3 XPACT_VERIFY_XOBJECT_LAYOUT macro pin set + spec §2.2
    // ABI-lock prose at lines 355-365).
    //
    // The static_asserts here ARE the ABI contract. Any byte-layout
    // change breaks:
    //   * Every GC-walk loop that reads ClassPrivate at offset 0.
    //   * Every XWeakPtr deref that reads SerialNumber at offset 12.
    //   * Every FXObjectAllocator NewObject path that fills the header.
    //   * Every XHT-emitted .gen.cpp constinit XObject CDO.
    // ---------------------------------------------------------------------

    static_assert(sizeof(XObject) == 56,
                  "XObject ABI lock (Contract Rev 13.9 / "
                  "XPACT_XOBJECT_LAYOUT_TAG): 56 bytes per XCoreXObject "
                  "Rev 4 §2.2. The Rev 2 cluster reservation expanded "
                  "from 8 to 16 bytes (FIX-A-MIN-49 / O5) reabsorbing "
                  "the dropped remote-id reservation; net sizeof "
                  "unchanged.");
    static_assert(alignof(XObject) == 8,
                  "XObject alignment ABI lock: 8-byte aligned (matches "
                  "FClass* alignment) per spec §2.2.");

    // Per-member offset locks (per spec §2.2 + §11.3 XPACT_VERIFY_
    // XOBJECT_LAYOUT macro).
    static_assert(offsetof(XObject, ClassPrivate)      ==  0,
                  "XObject ABI lock: ClassPrivate at offset 0 (GC mark "
                  "hot path; first load from XObject*).");
    static_assert(offsetof(XObject, InternalIndex)     ==  8,
                  "XObject ABI lock: InternalIndex at offset 8 "
                  "(precedes Outer because GC walk needs the index "
                  "before the Outer chain).");
    static_assert(offsetof(XObject, SerialNumber)      == 12,
                  "XObject ABI lock: SerialNumber at offset 12 (same "
                  "cache line as InternalIndex for XWeakPtr deref).");
    static_assert(offsetof(XObject, Outer)             == 16,
                  "XObject ABI lock: Outer at offset 16 (second half "
                  "of first cache line).");
    static_assert(offsetof(XObject, NamePrivate)       == 24,
                  "XObject ABI lock: NamePrivate at offset 24 "
                  "(8-byte FName handle; debug/diagnostic, not on the "
                  "GC hot path).");
    static_assert(offsetof(XObject, ObjectFlags)       == 32,
                  "XObject ABI lock: ObjectFlags at offset 32 (atomic "
                  "uint32 EObjectFlags bitset).");
    static_assert(offsetof(XObject, ReachabilityFlag)  == 36,
                  "XObject ABI lock: ReachabilityFlag at offset 36 "
                  "(Rev 3 per FIX-M-R2-3; consumes former "
                  "_padObjectFlags slot; saves one cache miss per "
                  "object marked vs the historical FXObjectArrayEntry "
                  "access).");
    static_assert(offsetof(XObject, _reservedCluster0) == 40,
                  "XObject ABI lock: _reservedCluster0 at offset 40 "
                  "(Phase 2 cluster-root index reservation).");
    static_assert(offsetof(XObject, _reservedCluster1) == 48,
                  "XObject ABI lock: _reservedCluster1 at offset 48 "
                  "(Phase 2 cluster-flags + remembered-set state "
                  "reservation).");

    // Field-size locks. The fixed-width integer fields MUST be the
    // documented size (avoid platform-dependent int sizing).
    static_assert(sizeof(XObject::ClassPrivate)      == 8,
                  "XObject ABI lock: ClassPrivate is 8-byte pointer.");
    static_assert(sizeof(XObject::InternalIndex)     == 4,
                  "XObject ABI lock: InternalIndex is int32 (4 bytes).");
    static_assert(sizeof(XObject::SerialNumber)      == 4,
                  "XObject ABI lock: SerialNumber is uint32 (4 bytes).");
    static_assert(sizeof(XObject::Outer)             == 8,
                  "XObject ABI lock: Outer is 8-byte pointer.");
    static_assert(sizeof(XObject::NamePrivate)       == 8,
                  "XObject ABI lock: NamePrivate is 8-byte FName handle.");
    static_assert(sizeof(XObject::ObjectFlags)       == 4,
                  "XObject ABI lock: ObjectFlags is atomic uint32 "
                  "(4 bytes; std::atomic<uint32_t>::is_always_lock_free "
                  "holds on every supported platform).");
    static_assert(sizeof(XObject::ReachabilityFlag)  == 4,
                  "XObject ABI lock: ReachabilityFlag is atomic uint32.");
    static_assert(sizeof(XObject::_reservedCluster0) == 8,
                  "XObject ABI lock: _reservedCluster0 is uint64 (8 bytes).");
    static_assert(sizeof(XObject::_reservedCluster1) == 8,
                  "XObject ABI lock: _reservedCluster1 is uint64 (8 bytes).");

    // Trait locks. XObject MUST be trivially destructible (so
    // FXObjectAllocator slab teardown does not need a virtual dispatch
    // for the C++-language destructor; subclass FinishDestroy routes
    // through the FClass FakeVTable). std::atomic<uint32_t>::~atomic()
    // is trivial on every supported platform.
    //
    // XObject is NOT trivially copyable (std::atomic members are
    // non-copyable). NOT trivially default-constructible (the default
    // ctor body explicitly initialises every field; the atomic members
    // could be constexpr-default-constructed under C++20, but the
    // explicit body is the documented contract).
    //
    // is_polymorphic IS asserted FALSE -- the load-bearing hot-reload
    // invariant. A subclass declaring its own virtual methods is OK
    // (the user-class vtable lives in the subclass; the GC does not
    // consult it), but XObject itself MUST be non-polymorphic.
    static_assert(::std::is_trivially_destructible_v<XObject>,
                  "XObject must be trivially destructible (FXObjectAllocator "
                  "slab teardown depends on this; subclass FinishDestroy "
                  "routes through the FClass FakeVTable).");
    static_assert(!::std::is_polymorphic_v<XObject>,
                  "XObject must NOT be polymorphic (hot-reload commitment + "
                  "FakeVTable dispatch pattern). Subclasses MAY declare "
                  "their own virtual methods; the GC does not consult the "
                  "user-class vtable.");

} // namespace XCore
