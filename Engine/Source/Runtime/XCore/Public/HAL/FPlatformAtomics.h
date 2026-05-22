// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FPlatformAtomics.h -- thin static-function atomic surface (UE-mirror).
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1 (Platform HAL) + Section 8.6 (Threading
// divergences). This is the C-style ABI primitive that the higher-level
// FAtomicInt32 / FAtomicInt64 / FAtomicPtr typed wrappers (Section 8.1;
// Phase 1c) are built on top of. Both surfaces exist:
//   - FPlatformAtomics (this file; Phase 1a abstract surface): the
//     low-level, UE-mirror, function-call surface taking `volatile T*`.
//     Used internally by FAtomicInt32/64/Ptr, by the allocator's free-
//     list bookkeeping (Section 4.2), and by the lock-free queue
//     implementations (Section 8.1).
//   - FAtomicInt32/64/Ptr (Section 8.1; Phase 1c): the user-facing
//     C++20 wrappers with EXPLICIT memory_order parameters per fix
//     M-4. Sim-path-incompatible (Section 8.3).
//
// Pattern reference: UE Core `GenericPlatformAtomics.h:25-200` defines
// `FGenericPlatformAtomics` as a static-only struct with all the
// Interlocked* methods. XPact's surface is the same shape but with
// std::atomic_ref-backed bodies (Section 7.6 row 3) instead of UE's
// platform-specific intrinsic per-method dispatch.
//
// std::atomic_ref backing (Phase 1b): each Phase 1b .cpp body is a
// thin wrapper over `std::atomic_ref<T>(*p).fetch_add(...)` etc. The
// memory order is seq_cst -- this is the historically-UE-compatible
// "Interlocked*" semantic (Win32 InterlockedIncrement is implicitly
// full barrier on x86_64; XPact mirrors it). Code that needs weaker
// orderings uses FAtomicInt32/64/Ptr in Section 8.1, NOT this surface.
//
// Sim-path discipline: the sim path is single-threaded; calling any
// FPlatformAtomics method from a sim-path TU is a compile error via
// [[deprecated]] applied in the sim-path overlay (Phase 1e). This
// surface is NOT part of the sim-path-safe API.
//
// Abstract surface only -- NO .cpp bodies in Phase 1a.
//
// =====================================================================

#include <cstdint>

namespace XCore::HAL
{

// ---------------------------------------------------------------------
// FPlatformAtomics -- low-level atomic operations surface.
//
// All methods are noexcept; all methods are static. The `volatile`
// qualifier on the pointer is mirror-of-UE; std::atomic_ref's
// constructor accepts both volatile and non-volatile lvalues. The
// volatile spelling is also documented anti-elision protection: the
// compiler is forbidden from caching the result of *p outside the
// atomic operation.
//
// Width coverage: int32_t + int64_t + void* (the three widths the
// allocator and lock-free queues need; Section 4.2, Section 8.1).
// int8/int16 are intentionally absent -- no XCore-4a hot path uses
// sub-32-bit atomics. Phase 1c's FAtomicInt32/64 cover the user-facing
// surface.
// ---------------------------------------------------------------------

class FPlatformAtomics
{
public:
    // -----------------------------------------------------------------
    // InterlockedIncrement -- atomic post-increment.
    // Returns the NEW value (post-increment), mirroring Win32
    // InterlockedIncrement semantics. UE's GenericPlatformAtomics.h:32
    // also returns NEW.
    //
    // Phase 1b: `return std::atomic_ref<int32_t>(*Value).fetch_add(1,
    //                  std::memory_order_seq_cst) + 1;`
    // -----------------------------------------------------------------
    static int32_t InterlockedIncrement(int32_t volatile* Value) noexcept;
    static int64_t InterlockedIncrement(int64_t volatile* Value) noexcept;

    // -----------------------------------------------------------------
    // InterlockedDecrement -- atomic post-decrement.
    // Returns the NEW value (post-decrement); mirrors Win32 + UE.
    //
    // Phase 1b: `return std::atomic_ref<int32_t>(*Value).fetch_sub(1,
    //                  std::memory_order_seq_cst) - 1;`
    // -----------------------------------------------------------------
    static int32_t InterlockedDecrement(int32_t volatile* Value) noexcept;
    static int64_t InterlockedDecrement(int64_t volatile* Value) noexcept;

    // -----------------------------------------------------------------
    // InterlockedAdd -- atomic add; returns the PREVIOUS value.
    //
    // Mirrors UE's GenericPlatformAtomics.h:85-93 which also returns
    // the previous value (`RetVal = *Value` before the CAS-fence).
    //
    // Phase 1b: `return std::atomic_ref<int32_t>(*Value).fetch_add(
    //                  Amount, std::memory_order_seq_cst);`
    // -----------------------------------------------------------------
    static int32_t InterlockedAdd(int32_t volatile* Value, int32_t Amount) noexcept;
    static int64_t InterlockedAdd(int64_t volatile* Value, int64_t Amount) noexcept;

    // -----------------------------------------------------------------
    // InterlockedExchange -- atomic swap; returns the PREVIOUS value.
    //
    // Mirrors UE's GenericPlatformAtomics.h:112-133 and Win32's
    // InterlockedExchange semantics.
    //
    // Phase 1b: `return std::atomic_ref<int32_t>(*Value).exchange(
    //                  Exchange, std::memory_order_seq_cst);`
    // -----------------------------------------------------------------
    static int32_t InterlockedExchange(int32_t volatile* Value, int32_t Exchange) noexcept;
    static int64_t InterlockedExchange(int64_t volatile* Value, int64_t Exchange) noexcept;

    // -----------------------------------------------------------------
    // InterlockedCompareExchange -- atomic compare-and-swap.
    //
    // If `*Dest == Comparand`, atomically writes `Exchange` and returns
    // `Comparand` (success). Otherwise returns the current `*Dest`
    // (failure; caller can compare to Comparand to detect).
    //
    // Mirrors UE's GenericPlatformAtomics.h:165-178 and Win32's
    // InterlockedCompareExchange. This is the load-bearing primitive
    // every other Interlocked* method can be built from.
    //
    // Phase 1b: uses std::atomic_ref::compare_exchange_strong with
    // memory_order_seq_cst on both success and failure.
    // -----------------------------------------------------------------
    static int32_t InterlockedCompareExchange(
        int32_t volatile* Dest, int32_t Exchange, int32_t Comparand) noexcept;
    static int64_t InterlockedCompareExchange(
        int64_t volatile* Dest, int64_t Exchange, int64_t Comparand) noexcept;

    // -----------------------------------------------------------------
    // InterlockedCompareExchangePointer -- pointer-width CAS.
    //
    // Used by the lock-free queues' head/tail-pointer publish protocol
    // (Section 8.1) and by the TLS-cache cross-thread reclaim path
    // (Section 4.2.5).
    //
    // Phase 1b: `return std::atomic_ref<void*>(*Dest).compare_exchange_
    //                  strong_explicit(Comparand, Exchange,
    //                  std::memory_order_seq_cst,
    //                  std::memory_order_seq_cst) ? Comparand : <prev>;`
    //
    // ABI consideration: on Win64 and Linux-x86_64 sizeof(void*) ==
    // sizeof(int64_t) == 8 so this method is internally equivalent to
    // the int64_t overload. On Android-ARM64 same: pointer = 8 bytes.
    // Keeping a typed pointer overload aids type safety at call sites
    // and lets the volatile qualifier propagate correctly.
    // -----------------------------------------------------------------
    static void* InterlockedCompareExchangePointer(
        void* volatile* Dest, void* Exchange, void* Comparand) noexcept;

    // -----------------------------------------------------------------
    // AtomicRead -- consistent atomic read of a 32/64-bit value.
    //
    // Returns the value at the read-instant; equivalent to a
    // memory_order_seq_cst load. Required because reading a 64-bit
    // value on a 32-bit architecture is NOT atomic by default (matters
    // less for XPact since we target 64-bit only, but the surface stays
    // consistent with UE).
    //
    // Pattern reference: UE Core's atomic-read pattern (used by the
    // Stat shard merger and lock-free queue ApproxCount methods).
    //
    // Phase 1b: `return std::atomic_ref<int32_t>(const_cast<int32_t&>(
    //                  *Value)).load(std::memory_order_seq_cst);`
    // -----------------------------------------------------------------
    static int32_t AtomicRead(int32_t volatile const* Value) noexcept;
    static int64_t AtomicRead(int64_t volatile const* Value) noexcept;
};

} // namespace XCore::HAL

// =====================================================================
// TODO(Phase 1b):
//   - Implement every FPlatformAtomics method as a thin std::atomic_ref
//     wrapper with memory_order_seq_cst (UE-mirror semantic). Phase 1b's
//     .cpp file is platform-independent because std::atomic_ref is the
//     standard library's portable implementation; only the .cpp lives
//     in `Private/HAL/FPlatformAtomics.cpp` (NOT under Win64/Linux/
//     Android subdirs).
// =====================================================================
