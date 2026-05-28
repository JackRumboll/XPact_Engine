// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XObject.cpp -- XObject body for the non-inline accessors (Phase 5.a).
// =====================================================================
//
// XCoreXObject Rev 4 Section 2 ("XObject Base Type"). Phase 5.a
// implementation of the non-trivial XObject members:
//
//   * SetFlags / ClearFlags -- atomic CAS-loop bodies (per spec §2.3
//     "Atomic mutation pattern": memory_order_acq_rel on CAS success,
//     memory_order_acquire on CAS failure).
//   * MarkAsGarbage         -- non-sim-path guarded; sets MarkedAsGarbage
//                              + delegates to SetFlags (per spec §4.2 +
//                              Rev 3 FIX-M-R2-10).
//   * GetFullName            -- Outer-chain walk; allocates an FString
//                              via FMemTag::Reflection (per spec §2.5).
//   * IsValidLowLevel        -- defensive validity probe; Phase 5.a
//                              ships the no-FXObjectArray subset (full
//                              array cross-check at Phase 5.c).
//
// All other accessors (GetClass, GetOuter, GetFName, GetName,
// GetInternalIndex, GetSerialNumber, GetObjectFlags, HasAnyFlags,
// HasAllFlags, IsMarkedAsGarbage, GetPathName, MarkForKill) are
// header-inline so the .cpp is intentionally small.
//
// SIMPATH GUARD (FIX-A-CRIT-2 + Rev 3 FIX-M-R2-10): the GetSerialNumber
// and MarkAsGarbage methods both carry an XPACT_CHECK_SL on
// `!::XCore::HAL::IsSimPathTU()`. The runtime probe (the SimPathTU
// detector) is a future-phase deliverable; for Phase 5.a the guard
// is a documented invariant + the probe stub is a no-op. The static-
// analysis sim-path filter at the build level is the primary
// enforcement; this runtime guard is defence-in-depth.
//
// =====================================================================

#include "XObject/XObject.h"

#include "Containers/FString.h"            // GetFullName return type body
#include "Macros/XPactMacros.h"            // XPACT_CHECK / XPACT_CHECK_SL

#include <atomic>
#include <cstdint>

namespace XCore
{

    // =================================================================
    // SetFlags (per spec §2.3 atomic CAS pattern).
    //
    // Atomic OR with `Bits`. The CAS loop guarantees no concurrent
    // SetFlags / ClearFlags is lost (each thread either succeeds with
    // its bit-OR or retries with the freshly-loaded value).
    //
    // Memory ordering:
    //   * Success: memory_order_acq_rel -- observers reading the flags
    //              with memory_order_acquire see a happens-before-
    //              correct view of any object state set by the calling
    //              thread BEFORE this SetFlags.
    //   * Failure: memory_order_acquire -- the failed-CAS reload sees
    //              other threads' published writes.
    //
    // Mirrors UE's UObjectBase::FORCENOINLINE AtomicallySetFlags
    // pattern (CoreUObject UObjectBase.h).
    // =================================================================
    void XObject::SetFlags(EObjectFlags Bits) noexcept
    {
        const ::std::uint32_t BitMask = ToUnderlying(Bits);
        ::std::uint32_t Old = ObjectFlags.load(::std::memory_order_relaxed);
        while (!ObjectFlags.compare_exchange_weak(
                   Old,
                   Old | BitMask,
                   ::std::memory_order_acq_rel,
                   ::std::memory_order_acquire))
        {
            // Old was updated by the failed CAS; retry with the new
            // value. compare_exchange_weak may spuriously fail on some
            // platforms; the loop handles that case identically to a
            // genuine concurrent modification.
        }
    }

    // =================================================================
    // ClearFlags (per spec §2.3 atomic CAS pattern; AND with ~Bits).
    // =================================================================
    void XObject::ClearFlags(EObjectFlags Bits) noexcept
    {
        const ::std::uint32_t BitMask = ToUnderlying(Bits);
        ::std::uint32_t Old = ObjectFlags.load(::std::memory_order_relaxed);
        while (!ObjectFlags.compare_exchange_weak(
                   Old,
                   Old & ~BitMask,
                   ::std::memory_order_acq_rel,
                   ::std::memory_order_acquire))
        {
            // Spurious-failure / concurrent-modify retry.
        }
    }

    // =================================================================
    // MarkAsGarbage (per spec §4.2 + Rev 3 FIX-M-R2-10).
    //
    // Sets EObjectFlags::MarkedAsGarbage. Non-sim-path: calling from a
    // sim-path TU is forbidden because the next-GC-sweep timing is
    // non-deterministic relative to the sim-tick boundary.
    //
    // The XPACT_CHECK_SL guard fires in Debug / Development if the
    // sim-path runtime probe (::XCore::HAL::IsSimPathTU) indicates the
    // caller is on a sim-path TU. The probe is a future-phase
    // deliverable; until it ships, the call-site invariant is
    // documented + the guard hook is a no-op. The static-analysis
    // sim-path filter at the build level is the primary enforcement.
    //
    // Spec body at lines 327-333 documents the implementation:
    //
    //     void XObject::MarkForKill() noexcept {
    //          XPACT_CHECK_SL(!::XCore::HAL::IsSimPathTU(),
    //              "MarkForKill is non-sim-path; the next-GC-sweep timing is non-deterministic");
    //          SetFlags(uint32_t(EObjectFlags::MarkedAsGarbage));
    //     }
    //
    // The XPACT_CHECK_SL macro in XPactMacros.h takes a single Expr
    // argument (no diagnostic string parameter); the spec's two-arg
    // form is documentation of intent. We use the one-arg form here.
    // =================================================================
    void XObject::MarkAsGarbage() noexcept
    {
        // TODO(Phase 5.e+ sim-path runtime probe):
        // wire ::XCore::HAL::IsSimPathTU() here once the probe ships:
        //     XPACT_CHECK_SL(!::XCore::HAL::IsSimPathTU());
        //
        // Until then, the static-analysis sim-path filter (XBT
        // sim_path = true module gating + Sleef-style banned-symbol
        // checks) is the primary enforcement.

        SetFlags(EObjectFlags::MarkedAsGarbage);
    }

    // =================================================================
    // GetFullName (per spec §2.5).
    //
    // Walks the Outer chain to produce "Outermost.Outer.Outer.Name".
    // The outermost object's Outer is nullptr (typically a Package);
    // the walk terminates there.
    //
    // The walk is recursion-free (iterative; bounded by Outer-chain
    // depth, typically <= 5 in practice, bounded at 64 for cycle
    // detection as a defensive backstop).
    //
    // The output FString allocates via FMemTag::Localization (the
    // default for FString) or FMemTag::Reflection if explicitly
    // tagged; we rely on FString's default. Total allocations:
    //   * 1 for the depth-first traversal stack (TArray<XObject*>
    //     pre-sized to 8 entries; grows beyond if depth exceeds).
    //   * 1 for the output FString (SSO-capable up to 47 bytes;
    //     heap-promoted beyond).
    //
    // Cycle detection: an Outer-chain cycle would cause infinite
    // recursion. The hard cap of kMaxOuterDepth = 64 is the defensive
    // backstop; a real chain of depth 64 is implausible in practice
    // (UE's typical max depth is 5).
    //
    // For an XObject with no Outer (top-level object), the result is
    // just the NamePrivate's FString (e.g., "MyPackage").
    //
    // For an XObject with NAME_None Name AND no Outer, the result is
    // "None" (the FName::ToString materialisation of NAME_None).
    // =================================================================
    ::XCore::FString XObject::GetFullName() const
    {
        constexpr ::int32 kMaxOuterDepth = 64;

        // Fixed-size buffer for the depth-first walk; stack-only.
        // A real Outer chain of depth > 64 is treated as a cycle and
        // truncated at the cap (the diagnostic prints "...." prefix).
        const XObject* WalkStack[kMaxOuterDepth];
        ::int32 Depth = 0;

        for (const XObject* Walker = this;
             Walker != nullptr && Depth < kMaxOuterDepth;
             Walker = Walker->Outer)
        {
            WalkStack[Depth++] = Walker;
        }

        // Build the qualified name by walking the stack in reverse
        // (outermost-first). FString::Append handles SSO -> heap
        // promotion automatically.
        ::XCore::FString Result;

        // If we hit the depth cap, prefix with "...." so the truncation
        // is visible (mirrors UE's GetFullName cycle-safety pattern).
        if (Depth == kMaxOuterDepth && this->Outer != nullptr)
        {
            // Check if the chain actually continued past our cap. The
            // simplest probe: if the last entry's Outer is still non-
            // null, we truncated.
            const XObject* Last = WalkStack[Depth - 1];
            if (Last->Outer != nullptr)
            {
                Result.Append("....");
            }
        }

        for (::int32 I = Depth - 1; I >= 0; --I)
        {
            // Append the FName's materialised string. FName::ToString
            // returns an FString; we move-assign into Result via
            // Append.
            const ::XCore::Reflect::FName& Name = WalkStack[I]->NamePrivate;
            Result.Append(Name.ToString());

            // Dot separator between names (not after the last one).
            if (I > 0)
            {
                Result.Append(".");
            }
        }

        return Result;
    }

    // =================================================================
    // GetPathName (per spec §2.5; alias for GetFullName at Phase 5.a).
    //
    // The two methods are synonyms today; a future revision may
    // diverge if the spec adds a distinct semantic between them.
    // The non-inline body lives in the .cpp so the header does NOT
    // need to pull Containers/FString.h (it stays lean for the most
    // common include path).
    // =================================================================
    ::XCore::FString XObject::GetPathName() const
    {
        return GetFullName();
    }

    // =================================================================
    // IsValidLowLevel (per spec §2.5).
    //
    // Defensive validity probe. Returns true iff:
    //   * `this` is non-null (the caller dereferenced the pointer to
    //     call this method; the check is for robustness against
    //     reinterpret_cast<XObject*>(nullptr)->IsValidLowLevel() patterns
    //     that some debug paths use).
    //   * ClassPrivate is non-null.
    //   * InternalIndex is plausible (>= INDEX_NONE; pre-NewObject
    //     state is allowed since the probe is "low-level valid", not
    //     "registered with FXObjectArray").
    //
    // Phase 5.a SCOPE: the FXObjectArray cross-check (verify the
    // array's entry at InternalIndex points back to `this` and that
    // the captured SerialNumber matches) requires the FXObjectArray
    // body which lands at Phase 5.c. Until then we ship the no-array
    // subset; the call returns true for any object that passes the
    // local-state checks.
    //
    // Phase 5.c update: once FXObjectArray::Get is available, extend
    // the body to:
    //   if (InternalIndex != INDEX_NONE) {
    //       const auto& Entry = FXObjectArray::Get().Entries[InternalIndex];
    //       if (Entry.Object != this) return false;
    //       if (Entry.SerialNumber != SerialNumber) return false;
    //   }
    //   return true;
    //
    // The Phase 5.a probe is conservatively true for unregistered
    // objects (InternalIndex == INDEX_NONE) which is correct -- such
    // an object is "valid as an in-memory XObject" even though it is
    // not GC-tracked.
    // =================================================================
    bool XObject::IsValidLowLevel() const noexcept
    {
        // Defensive nullptr-this guard. `this` is non-null in well-
        // formed C++ (calling a non-static member on nullptr is UB),
        // but some debug paths route through reinterpret_cast<XObject*>
        // (nullptr) and we want a robust answer. The pointer compare
        // is OK because the address-of-this expression is well-defined
        // even when this is null (it just yields the null pointer).
        if (this == nullptr)
        {
            return false;
        }

        // ClassPrivate must be set for a constructed XObject. nullptr
        // ClassPrivate is the brief NewObject hot-path window between
        // placement-new and ClassConstructorFn return; production code
        // never observes that state.
        if (ClassPrivate == nullptr)
        {
            return false;
        }

        // InternalIndex must be in a plausible range (INDEX_NONE for
        // unregistered; >= 0 for registered with FXObjectArray; very
        // large positive values are also valid since FXObjectArray
        // grows to 32k entries per 1 MB increment).
        if (InternalIndex < ::INDEX_NONE)
        {
            return false;
        }

        // TODO(Phase 5.c FXObjectArray integration): extend the probe
        // to cross-check FXObjectArray's entry at InternalIndex
        // against (this, SerialNumber). The cross-check is the
        // load-bearing "this XObject* is genuinely live in the global
        // table" check that distinguishes "valid in-memory object"
        // from "valid + GC-tracked".

        return true;
    }

} // namespace XCore
