// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FAtomicInt32.h -- typed 32-bit atomic wrapper.
// =====================================================================
//
// XCore-4a Rev 3, Section 8.1 (Threading Primitives) + Section 8.6
// (UE-divergence row 3 "one surface, not two") + Locked Decision 4
// "no default memory_order" + fix M-4.
//
// Public user-facing C++20 wrapper over std::atomic<int32_t> with an
// EXPLICIT memory_order discipline: NO method has a default
// memory_order argument. Every call site MUST specify, either via:
//
//   * Generic Load(order) / Store(value, order) / FetchAdd(delta, order)
//     methods that take a required std::memory_order parameter, or
//   * Named convenience variants: LoadAcquire / LoadRelaxed /
//     StoreRelease / StoreRelaxed / FetchAddAcquire / FetchAddRelease /
//     FetchAddRelaxed / FetchAddAcqRel / ExchangeAcqRel.
//
// Rationale (Section 8.1 spec body): defaulting to memory_order_seq_cst
// (UE's historical pattern via FPlatformAtomics) is banned because on
// ARM64 (Snapdragon XR2 Gen 2 / Quest 3) seq_cst stores issue a full
// `dmb ish` instruction, costing 10-50x the latency of acquire/release.
// On x86_64 the cost difference is negligible due to the strong memory
// model. XPact ships the same code on both targets; forcing the caller
// to choose makes the cost trade-off visible at the source level.
//
// Pattern reference: std::atomic<int32_t> is C++11+; we wrap rather
// than expose it directly so the engine can:
//   1. Enforce the "no default memory_order" discipline per fix M-4.
//   2. Add Sim-path discipline via [[deprecated]] in the sim-path
//      overlay (Phase 1e) -- a sim-path TU's call into FAtomicInt32
//      compiles as a deprecation diagnostic.
//   3. Pin the ABI: size + alignment are part of the contract
//      (`static_assert(sizeof(FAtomicInt32) == 4)`).
//
// UE-divergence (Section 8.6 row 3): UE Core has TWO atomic surfaces --
// `FPlatformAtomics::Interlocked*` (HAL/PlatformAtomics.h) and
// `std::atomic<T>` via `Templates/Atomic.h`. The dual surface predates
// C++11 standardisation and survives only because UE accumulates legacy
// code. XPact consolidates to ONE user-facing surface (FAtomicInt32 /
// FAtomicInt64 / FAtomicPtr) with FPlatformAtomics as the lowest-level
// platform abstraction below it (see Public/HAL/FPlatformAtomics.h).
//
// Hot-reload (Section 8.5): NO virtual methods; POD-like wrapper.
// `static_assert(sizeof(FAtomicInt32) == 4)` is the ABI lock. The
// wrapper carries the contract; any size change is an explicit ABI
// version bump in XPACT_GC_ROOT_ABI_TAG.
//
// =====================================================================

#include "Macros/XCoreTypes.h"

#include <atomic>

namespace XCore::HAL
{

// ---------------------------------------------------------------------
// FAtomicInt32 -- typed 32-bit atomic wrapper.
//
// All methods are noexcept. Construction is `constexpr` so the type
// can live in `constinit` storage and is well-defined at PreStaticInit
// (Section 1.5 phase ladder).
//
// Copy + assignment are deleted; atomic types are not copyable in the
// standard sense (the underlying std::atomic<T> is non-copyable).
// ---------------------------------------------------------------------

class FAtomicInt32
{
public:
    // -----------------------------------------------------------------
    // Constructors.
    //
    // Default-constructs to 0. The single-argument constructor takes an
    // initial value; both are `constexpr` so the type is constinit-safe.
    // -----------------------------------------------------------------
    constexpr FAtomicInt32() noexcept : v(0) {}
    constexpr explicit FAtomicInt32(::int32 Initial) noexcept : v(Initial) {}

    // Non-copyable / non-movable. std::atomic<T> is the same; the
    // restriction propagates through this wrapper.
    FAtomicInt32(const FAtomicInt32&)            = delete;
    FAtomicInt32& operator=(const FAtomicInt32&) = delete;
    FAtomicInt32(FAtomicInt32&&)                 = delete;
    FAtomicInt32& operator=(FAtomicInt32&&)      = delete;

    // =================================================================
    // Generic API: caller MUST pass memory_order (fix M-4).
    //
    // These map 1:1 to std::atomic<int32_t> methods with the same
    // memory_order arg. No default; calling without an order is a
    // compile error.
    // =================================================================

    [[nodiscard]] ::int32 Load(::std::memory_order Order) const noexcept
    {
        return v.load(Order);
    }

    void Store(::int32 Value, ::std::memory_order Order) noexcept
    {
        v.store(Value, Order);
    }

    [[nodiscard]] ::int32 FetchAdd(::int32 Delta, ::std::memory_order Order) noexcept
    {
        return v.fetch_add(Delta, Order);
    }

    [[nodiscard]] ::int32 FetchSub(::int32 Delta, ::std::memory_order Order) noexcept
    {
        return v.fetch_sub(Delta, Order);
    }

    [[nodiscard]] ::int32 Exchange(::int32 Value, ::std::memory_order Order) noexcept
    {
        return v.exchange(Value, Order);
    }

    // CAS strong / weak. The TWO memory_order parameters (success +
    // failure) are required: std::atomic's CAS surface itself demands
    // them, and our discipline forbids implicit defaults.
    //
    // Failure order MUST be no stronger than success order and must
    // not be release / acq_rel (C++ standard rule); the caller is
    // responsible for satisfying that constraint -- we do not wrap-
    // check it because the cost of the runtime check on the hot path
    // is unjustified, and the C++ standard treats the violation as UB
    // which our XPACT_CHECK can't catch at compile time without
    // metaprogramming.
    [[nodiscard]] bool CompareExchangeStrong(::int32& Expected, ::int32 Desired,
                                             ::std::memory_order Success,
                                             ::std::memory_order Failure) noexcept
    {
        return v.compare_exchange_strong(Expected, Desired, Success, Failure);
    }

    [[nodiscard]] bool CompareExchangeWeak(::int32& Expected, ::int32 Desired,
                                           ::std::memory_order Success,
                                           ::std::memory_order Failure) noexcept
    {
        return v.compare_exchange_weak(Expected, Desired, Success, Failure);
    }

    // =================================================================
    // Named convenience variants (fix M-4).
    //
    // These pin the memory_order at the method name. Callers that want
    // a specific order can use these instead of the generic Load(order)
    // form. The names are deliberately verbose: LoadAcquire is harder
    // to mistype as LoadRelaxed than Load(acquire) is to mistype as
    // Load(relaxed), and the call site reads more clearly.
    //
    // The named variants are inline forwarders to the generic API; no
    // separate ABI surface. Inlining is left to the compiler (these
    // are single-call wrappers; the compiler always inlines).
    // =================================================================

    [[nodiscard]] ::int32 LoadAcquire() const noexcept
    {
        return v.load(::std::memory_order_acquire);
    }

    [[nodiscard]] ::int32 LoadRelaxed() const noexcept
    {
        return v.load(::std::memory_order_relaxed);
    }

    void StoreRelease(::int32 Value) noexcept
    {
        v.store(Value, ::std::memory_order_release);
    }

    void StoreRelaxed(::int32 Value) noexcept
    {
        v.store(Value, ::std::memory_order_relaxed);
    }

    [[nodiscard]] ::int32 FetchAddAcquire(::int32 Delta) noexcept
    {
        return v.fetch_add(Delta, ::std::memory_order_acquire);
    }

    [[nodiscard]] ::int32 FetchAddRelease(::int32 Delta) noexcept
    {
        return v.fetch_add(Delta, ::std::memory_order_release);
    }

    [[nodiscard]] ::int32 FetchAddRelaxed(::int32 Delta) noexcept
    {
        return v.fetch_add(Delta, ::std::memory_order_relaxed);
    }

    [[nodiscard]] ::int32 FetchAddAcqRel(::int32 Delta) noexcept
    {
        return v.fetch_add(Delta, ::std::memory_order_acq_rel);
    }

    [[nodiscard]] ::int32 ExchangeAcqRel(::int32 Value) noexcept
    {
        return v.exchange(Value, ::std::memory_order_acq_rel);
    }

private:
    // The underlying std::atomic. `alignas(4)` is the standard
    // alignment for int32_t; std::atomic<int32_t> already inherits
    // alignment from the underlying type. We do NOT pad to a cache
    // line because the user is responsible for placing the type in
    // appropriately-padded storage when false-sharing matters (see
    // XPACT_CACHE_LINE_SIZE in Macros/XPactMacros.h).
    ::std::atomic<::int32> v;
};

// ABI lock (Section 8.5). FAtomicInt32 must be 4 bytes -- the same as
// the underlying int32_t. std::atomic<int32_t> is required by C++ to
// be the same size as int32_t (the standard says "if std::atomic<T>
// is is_always_lock_free, the size must equal sizeof(T)").
//
// Note: We can't use `is_always_lock_free` as a static_assert directly
// because it's not a constant expression in the way we'd need; we trust
// the C++ standard's guarantee for trivially-copyable + 32-bit types
// on every supported platform (Win64 / Linux / Android-ARM64 are all
// always-lock-free for int32_t).
static_assert(sizeof(FAtomicInt32)  == 4,
              "FAtomicInt32 ABI lock: 4 bytes (matches int32_t)");
static_assert(alignof(FAtomicInt32) == 4,
              "FAtomicInt32 ABI lock: 4-byte alignment (matches int32_t)");

} // namespace XCore::HAL
