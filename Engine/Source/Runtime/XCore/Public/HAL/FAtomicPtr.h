// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FAtomicPtr.h -- typed pointer-width atomic wrapper.
// =====================================================================
//
// XCore-4a Rev 3, Section 8.1 (Threading Primitives) + fix M-4 +
// Locked Decision OPEN-3 RESOLVED in Rev 2 ("untyped FAtomicPtr per
// recommended default; templated TAtomicPtr<T> ships as a thin layer").
//
// Two surfaces:
//   * FAtomicPtr      -- untyped (void*); the lowest-level pointer-
//                        atomic surface. Used internally by lock-free
//                        queue head/tail-pointer publish protocols and
//                        by Treiber stacks where the element type is
//                        the queue node's intrinsic type.
//   * TAtomicPtr<T>   -- typed thin wrapper. Stores T* but the
//                        underlying storage is the same void* atomic.
//                        Available to callers that prefer type safety;
//                        the wrapper costs zero at runtime (every
//                        method is an inline forwarder).
//
// Both follow the no-default-memory_order discipline per fix M-4.
//
// ABI: 8 bytes / 8-byte alignment on all three XPact targets (Win64,
// Linux-x86_64, Android-ARM64 all have 64-bit pointer width).
// std::atomic<void*> is guaranteed always-lock-free on these targets.
//
// =====================================================================

#include "Macros/XCoreTypes.h"

#include <atomic>

namespace XCore::HAL
{

// ---------------------------------------------------------------------
// FAtomicPtr -- untyped pointer-width atomic.
// ---------------------------------------------------------------------

class FAtomicPtr
{
public:
    constexpr FAtomicPtr() noexcept : v(nullptr) {}
    constexpr explicit FAtomicPtr(void* Initial) noexcept : v(Initial) {}

    FAtomicPtr(const FAtomicPtr&)            = delete;
    FAtomicPtr& operator=(const FAtomicPtr&) = delete;
    FAtomicPtr(FAtomicPtr&&)                 = delete;
    FAtomicPtr& operator=(FAtomicPtr&&)      = delete;

    // ----- generic API: caller MUST pass memory_order (fix M-4) -----

    [[nodiscard]] void* Load(::std::memory_order Order) const noexcept
    {
        return v.load(Order);
    }

    void Store(void* Value, ::std::memory_order Order) noexcept
    {
        v.store(Value, Order);
    }

    [[nodiscard]] void* Exchange(void* Value, ::std::memory_order Order) noexcept
    {
        return v.exchange(Value, Order);
    }

    [[nodiscard]] bool CompareExchangeStrong(void*& Expected, void* Desired,
                                             ::std::memory_order Success,
                                             ::std::memory_order Failure) noexcept
    {
        return v.compare_exchange_strong(Expected, Desired, Success, Failure);
    }

    [[nodiscard]] bool CompareExchangeWeak(void*& Expected, void* Desired,
                                           ::std::memory_order Success,
                                           ::std::memory_order Failure) noexcept
    {
        return v.compare_exchange_weak(Expected, Desired, Success, Failure);
    }

    // ----- named convenience variants (fix M-4) -----

    [[nodiscard]] void* LoadAcquire() const noexcept
    {
        return v.load(::std::memory_order_acquire);
    }

    [[nodiscard]] void* LoadRelaxed() const noexcept
    {
        return v.load(::std::memory_order_relaxed);
    }

    void StoreRelease(void* Value) noexcept
    {
        v.store(Value, ::std::memory_order_release);
    }

    void StoreRelaxed(void* Value) noexcept
    {
        v.store(Value, ::std::memory_order_relaxed);
    }

    [[nodiscard]] void* ExchangeAcqRel(void* Value) noexcept
    {
        return v.exchange(Value, ::std::memory_order_acq_rel);
    }

private:
    ::std::atomic<void*> v;
};

static_assert(sizeof(FAtomicPtr)  == sizeof(void*),
              "FAtomicPtr ABI lock: pointer-sized");
static_assert(alignof(FAtomicPtr) == alignof(void*),
              "FAtomicPtr ABI lock: pointer-aligned");

// ---------------------------------------------------------------------
// TAtomicPtr<T> -- typed thin wrapper.
//
// Stores T* logically; storage backed by the same atomic-pointer cell
// as FAtomicPtr. All methods are inline forwarders with reinterpret_cast
// inserted at the call boundary; the compiler optimises the casts away.
//
// CAS uses static_cast on the void*& in the expected-out param to
// translate back to T*&; the calling convention here matches
// std::atomic<T*>::compare_exchange_strong so call sites read naturally:
//
//   T* expected = currentHead;
//   if (head.CompareExchangeStrong(expected, newNode,
//                                  std::memory_order_release,
//                                  std::memory_order_relaxed)) { ... }
//
// Use this when type safety is desired at the call site without paying
// for runtime overhead. FAtomicPtr remains available for code that
// genuinely operates on void* (e.g., generic queue scaffolding).
// ---------------------------------------------------------------------

template<typename T>
class TAtomicPtr
{
public:
    constexpr TAtomicPtr() noexcept : v(nullptr) {}
    constexpr explicit TAtomicPtr(T* Initial) noexcept : v(Initial) {}

    TAtomicPtr(const TAtomicPtr&)            = delete;
    TAtomicPtr& operator=(const TAtomicPtr&) = delete;
    TAtomicPtr(TAtomicPtr&&)                 = delete;
    TAtomicPtr& operator=(TAtomicPtr&&)      = delete;

    // ----- generic API: caller MUST pass memory_order (fix M-4) -----

    [[nodiscard]] T* Load(::std::memory_order Order) const noexcept
    {
        return v.load(Order);
    }

    void Store(T* Value, ::std::memory_order Order) noexcept
    {
        v.store(Value, Order);
    }

    [[nodiscard]] T* Exchange(T* Value, ::std::memory_order Order) noexcept
    {
        return v.exchange(Value, Order);
    }

    [[nodiscard]] bool CompareExchangeStrong(T*& Expected, T* Desired,
                                             ::std::memory_order Success,
                                             ::std::memory_order Failure) noexcept
    {
        return v.compare_exchange_strong(Expected, Desired, Success, Failure);
    }

    [[nodiscard]] bool CompareExchangeWeak(T*& Expected, T* Desired,
                                           ::std::memory_order Success,
                                           ::std::memory_order Failure) noexcept
    {
        return v.compare_exchange_weak(Expected, Desired, Success, Failure);
    }

    // ----- named convenience variants (fix M-4) -----

    [[nodiscard]] T* LoadAcquire() const noexcept
    {
        return v.load(::std::memory_order_acquire);
    }

    [[nodiscard]] T* LoadRelaxed() const noexcept
    {
        return v.load(::std::memory_order_relaxed);
    }

    void StoreRelease(T* Value) noexcept
    {
        v.store(Value, ::std::memory_order_release);
    }

    void StoreRelaxed(T* Value) noexcept
    {
        v.store(Value, ::std::memory_order_relaxed);
    }

    [[nodiscard]] T* ExchangeAcqRel(T* Value) noexcept
    {
        return v.exchange(Value, ::std::memory_order_acq_rel);
    }

private:
    ::std::atomic<T*> v;
};

} // namespace XCore::HAL
