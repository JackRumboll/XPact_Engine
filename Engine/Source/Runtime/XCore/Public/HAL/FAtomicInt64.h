// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FAtomicInt64.h -- typed 64-bit atomic wrapper.
// =====================================================================
//
// XCore-4a Rev 3, Section 8.1 (Threading Primitives) + fix M-4.
//
// Mirror of FAtomicInt32 with int64_t storage. See FAtomicInt32.h for
// the full memory_order-discipline rationale; this header replicates
// the same surface at 64-bit width.
//
// ABI: 8 bytes / 8-byte alignment. The C++ standard guarantees that
// std::atomic<int64_t> is the same size as int64_t when
// is_always_lock_free is true; all three XPact targets (Win64-x86_64,
// Linux-x86_64, Android-ARM64) are always-lock-free for 64-bit
// (CMPXCHG16B / LDXR/STXR or equivalent).
//
// =====================================================================

#include "Macros/XCoreTypes.h"

#include <atomic>

namespace XCore::HAL
{

class FAtomicInt64
{
public:
    constexpr FAtomicInt64() noexcept : v(0) {}
    constexpr explicit FAtomicInt64(::int64 Initial) noexcept : v(Initial) {}

    FAtomicInt64(const FAtomicInt64&)            = delete;
    FAtomicInt64& operator=(const FAtomicInt64&) = delete;
    FAtomicInt64(FAtomicInt64&&)                 = delete;
    FAtomicInt64& operator=(FAtomicInt64&&)      = delete;

    // ----- generic API: caller MUST pass memory_order (fix M-4) -----

    [[nodiscard]] ::int64 Load(::std::memory_order Order) const noexcept
    {
        return v.load(Order);
    }

    void Store(::int64 Value, ::std::memory_order Order) noexcept
    {
        v.store(Value, Order);
    }

    [[nodiscard]] ::int64 FetchAdd(::int64 Delta, ::std::memory_order Order) noexcept
    {
        return v.fetch_add(Delta, Order);
    }

    [[nodiscard]] ::int64 FetchSub(::int64 Delta, ::std::memory_order Order) noexcept
    {
        return v.fetch_sub(Delta, Order);
    }

    [[nodiscard]] ::int64 Exchange(::int64 Value, ::std::memory_order Order) noexcept
    {
        return v.exchange(Value, Order);
    }

    [[nodiscard]] bool CompareExchangeStrong(::int64& Expected, ::int64 Desired,
                                             ::std::memory_order Success,
                                             ::std::memory_order Failure) noexcept
    {
        return v.compare_exchange_strong(Expected, Desired, Success, Failure);
    }

    [[nodiscard]] bool CompareExchangeWeak(::int64& Expected, ::int64 Desired,
                                           ::std::memory_order Success,
                                           ::std::memory_order Failure) noexcept
    {
        return v.compare_exchange_weak(Expected, Desired, Success, Failure);
    }

    // ----- named convenience variants (fix M-4) -----

    [[nodiscard]] ::int64 LoadAcquire() const noexcept
    {
        return v.load(::std::memory_order_acquire);
    }

    [[nodiscard]] ::int64 LoadRelaxed() const noexcept
    {
        return v.load(::std::memory_order_relaxed);
    }

    void StoreRelease(::int64 Value) noexcept
    {
        v.store(Value, ::std::memory_order_release);
    }

    void StoreRelaxed(::int64 Value) noexcept
    {
        v.store(Value, ::std::memory_order_relaxed);
    }

    [[nodiscard]] ::int64 FetchAddAcquire(::int64 Delta) noexcept
    {
        return v.fetch_add(Delta, ::std::memory_order_acquire);
    }

    [[nodiscard]] ::int64 FetchAddRelease(::int64 Delta) noexcept
    {
        return v.fetch_add(Delta, ::std::memory_order_release);
    }

    [[nodiscard]] ::int64 FetchAddRelaxed(::int64 Delta) noexcept
    {
        return v.fetch_add(Delta, ::std::memory_order_relaxed);
    }

    [[nodiscard]] ::int64 FetchAddAcqRel(::int64 Delta) noexcept
    {
        return v.fetch_add(Delta, ::std::memory_order_acq_rel);
    }

    [[nodiscard]] ::int64 ExchangeAcqRel(::int64 Value) noexcept
    {
        return v.exchange(Value, ::std::memory_order_acq_rel);
    }

private:
    ::std::atomic<::int64> v;
};

static_assert(sizeof(FAtomicInt64)  == 8,
              "FAtomicInt64 ABI lock: 8 bytes (matches int64_t)");
static_assert(alignof(FAtomicInt64) == 8,
              "FAtomicInt64 ABI lock: 8-byte alignment (matches int64_t)");

} // namespace XCore::HAL
