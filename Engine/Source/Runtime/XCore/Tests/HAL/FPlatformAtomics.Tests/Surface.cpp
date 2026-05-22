// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FPlatformAtomics.Tests/Surface.cpp -- compile-only surface
// verification + std::atomic_ref signature compatibility check.
// =====================================================================
//
// XCore-4a Rev 3, Section 7 (Platform HAL) + Section 8.6 (Threading
// divergences row 3). Verifies that:
//   1. Each FPlatformAtomics::Interlocked* method has the right
//      signature.
//   2. The InterlockedCompareExchange signature is compatible with
//      std::atomic_ref<int32_t>::compare_exchange_strong as the
//      planned Phase 1b backing primitive.
//
// Phase 1a has NO .cpp bodies for FPlatformAtomics; this test
// compiles against `extern` declarations only.
//
// =====================================================================

#include "HAL/FPlatformAtomics.h"

#include <atomic>
#include <cstdint>
#include <type_traits>

namespace XCore::HAL::Tests::FPlatformAtomicsSurface
{

// ---------------------------------------------------------------------
// Compile-time signature verifications.
//
// We extract the function pointer TYPE via decltype on each overload's
// address, then assert that the type matches the expected signature
// shape. The decltype-of-overloaded-member trick disambiguates by
// requiring an explicit cast: the static_cast forms the function pointer
// only if the cast is valid (i.e., the method signature matches);
// otherwise the cast itself is the compile error.
//
// This catches any signature drift in FPlatformAtomics.h at the static_
// assert site, NOT silently in a header refactor.
// ---------------------------------------------------------------------

// ---------- int32_t overloads ----------

// InterlockedIncrement (int32_t)
[[maybe_unused]] constexpr auto kIncrementI32 =
    static_cast<int32_t (*)(int32_t volatile*) noexcept>(
        &FPlatformAtomics::InterlockedIncrement);

// InterlockedDecrement (int32_t)
[[maybe_unused]] constexpr auto kDecrementI32 =
    static_cast<int32_t (*)(int32_t volatile*) noexcept>(
        &FPlatformAtomics::InterlockedDecrement);

// InterlockedAdd (int32_t)
[[maybe_unused]] constexpr auto kAddI32 =
    static_cast<int32_t (*)(int32_t volatile*, int32_t) noexcept>(
        &FPlatformAtomics::InterlockedAdd);

// InterlockedExchange (int32_t)
[[maybe_unused]] constexpr auto kExchangeI32 =
    static_cast<int32_t (*)(int32_t volatile*, int32_t) noexcept>(
        &FPlatformAtomics::InterlockedExchange);

// InterlockedCompareExchange (int32_t)
[[maybe_unused]] constexpr auto kCASI32 =
    static_cast<int32_t (*)(int32_t volatile*, int32_t, int32_t) noexcept>(
        &FPlatformAtomics::InterlockedCompareExchange);

// AtomicRead (int32_t)
[[maybe_unused]] constexpr auto kAtomicReadI32 =
    static_cast<int32_t (*)(int32_t volatile const*) noexcept>(
        &FPlatformAtomics::AtomicRead);

// ---------- int64_t overloads ----------

// InterlockedIncrement (int64_t)
[[maybe_unused]] constexpr auto kIncrementI64 =
    static_cast<int64_t (*)(int64_t volatile*) noexcept>(
        &FPlatformAtomics::InterlockedIncrement);

// InterlockedDecrement (int64_t)
[[maybe_unused]] constexpr auto kDecrementI64 =
    static_cast<int64_t (*)(int64_t volatile*) noexcept>(
        &FPlatformAtomics::InterlockedDecrement);

// InterlockedAdd (int64_t)
[[maybe_unused]] constexpr auto kAddI64 =
    static_cast<int64_t (*)(int64_t volatile*, int64_t) noexcept>(
        &FPlatformAtomics::InterlockedAdd);

// InterlockedExchange (int64_t)
[[maybe_unused]] constexpr auto kExchangeI64 =
    static_cast<int64_t (*)(int64_t volatile*, int64_t) noexcept>(
        &FPlatformAtomics::InterlockedExchange);

// InterlockedCompareExchange (int64_t)
[[maybe_unused]] constexpr auto kCASI64 =
    static_cast<int64_t (*)(int64_t volatile*, int64_t, int64_t) noexcept>(
        &FPlatformAtomics::InterlockedCompareExchange);

// AtomicRead (int64_t)
[[maybe_unused]] constexpr auto kAtomicReadI64 =
    static_cast<int64_t (*)(int64_t volatile const*) noexcept>(
        &FPlatformAtomics::AtomicRead);

// ---------- pointer overload ----------

// InterlockedCompareExchangePointer (void*)
[[maybe_unused]] constexpr auto kCASPtr =
    static_cast<void* (*)(void* volatile*, void*, void*) noexcept>(
        &FPlatformAtomics::InterlockedCompareExchangePointer);

// ---------------------------------------------------------------------
// std::atomic_ref<int32_t> signature compatibility check.
//
// Per Section 7.6 divergence row 3 + Section 8.6 row 3, FPlatformAtomics
// is implemented on top of std::atomic_ref in Phase 1b. This test
// verifies that the compare-exchange signature matches what
// std::atomic_ref<int32_t>::compare_exchange_strong would consume.
//
// std::atomic_ref<int32_t>::compare_exchange_strong has signature:
//   bool compare_exchange_strong(int32_t& expected, int32_t desired,
//                                std::memory_order success_order,
//                                std::memory_order failure_order)
// noexcept
//
// FPlatformAtomics::InterlockedCompareExchange wraps this with the
// classic Win32 semantics:
//   - Comparand parameter: the value to compare *Dest against.
//   - Exchange parameter: the value to swap in if equal.
//   - Returns: the value that was at *Dest BEFORE the operation
//     (Comparand on success; the current *Dest value on failure).
//
// The wrapping is verifiable by the planned Phase 1b body:
//   int32_t InterlockedCompareExchange(int32_t volatile* Dest,
//                                       int32_t Exchange,
//                                       int32_t Comparand) noexcept
//   {
//       int32_t Expected = Comparand;
//       std::atomic_ref<int32_t>(*Dest).compare_exchange_strong(
//           Expected, Exchange,
//           std::memory_order_seq_cst, std::memory_order_seq_cst);
//       return Expected;  // updated to actual value if CAS failed
//   }
//
// Below we verify the signature shapes the wrapping would consume.
// ---------------------------------------------------------------------

namespace
{
    // Compile-only sanity: std::atomic_ref<int32_t> can be constructed
    // from int32_t&; its compare_exchange_strong returns bool and
    // takes (int32_t&, int32_t, memory_order, memory_order). If this
    // check ever fails, the Phase 1b backing strategy needs revisiting.

    // Pattern reference: this mirrors UE Core's GenericPlatformAtomics.h
    // CAS-loop primitive at lines 32-93 (InterlockedIncrement/Add via
    // CAS), but uses std::atomic_ref instead of platform intrinsics.

    // Verify that std::atomic_ref<int32_t> exists and that the
    // compare_exchange_strong overload taking (T&, T, memory_order,
    // memory_order) is available for the Phase 1b implementation. The
    // member is const-qualified on std::atomic_ref (it's an atomic
    // accessor over an external object, so the ref itself doesn't
    // mutate).
    //
    // We extract the member-function pointer through a static_cast
    // which disambiguates the overload set; the cast fails to compile
    // if the overload shape doesn't match.
    [[maybe_unused]] constexpr auto kAtomicRefCAS =
        static_cast<bool (std::atomic_ref<int32_t>::*)(
                        int32_t&, int32_t,
                        std::memory_order,
                        std::memory_order) const noexcept>(
            &std::atomic_ref<int32_t>::compare_exchange_strong);

    static_assert(std::is_trivially_destructible_v<std::atomic_ref<int32_t>>,
                  "std::atomic_ref<int32_t> must be trivially destructible "
                  "for the Phase 1b backing strategy");
}

// ---------------------------------------------------------------------
// noexcept verifications.
// ---------------------------------------------------------------------

static_assert(noexcept(FPlatformAtomics::InterlockedIncrement(
                          static_cast<int32_t volatile*>(nullptr))),
              "FPlatformAtomics::InterlockedIncrement(int32_t) must be noexcept");

static_assert(noexcept(FPlatformAtomics::InterlockedCompareExchange(
                          static_cast<int32_t volatile*>(nullptr), 0, 0)),
              "FPlatformAtomics::InterlockedCompareExchange(int32_t) must be noexcept");

static_assert(noexcept(FPlatformAtomics::InterlockedCompareExchangePointer(
                          static_cast<void* volatile*>(nullptr), nullptr, nullptr)),
              "FPlatformAtomics::InterlockedCompareExchangePointer must be noexcept");

} // namespace XCore::HAL::Tests::FPlatformAtomicsSurface
