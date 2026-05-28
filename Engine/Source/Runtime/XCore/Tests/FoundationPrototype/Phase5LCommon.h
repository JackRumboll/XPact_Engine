// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// Phase5LCommon.h -- shared test-harness scaffolding for the Foundation
// Prototype acceptance gates (XCoreXObject Rev 4 §13.1 + §13.2; Phase
// 5.l harness).
// =====================================================================
//
// This header is the COMMON INFRASTRUCTURE for every Phase 5.l test TU
// under Engine/Source/Runtime/XCore/Tests/FoundationPrototype/. It is
// intentionally header-only so each test TU compiles standalone (the
// XBT per-fixture test pattern; same as every other XCore.Tests TU).
//
// Surfaces provided:
//
//   1. P5L_CHECK(condition, diagnostic)
//        -- a Check macro that increments the local failure counter and
//           emits the diagnostic to stderr on failure. Mirrors the
//           existing XCore.Tests Check pattern.
//
//   2. P5L_REPORT_PASS(test_name)
//        -- emits the PASS line + returns 0 if the local failure
//           counter is zero; otherwise emits the FAIL line + returns 1.
//           Use as `return P5L_REPORT_PASS("CriterionA.GCPauseBudget");`
//           at the bottom of main().
//
//   3. P5L_SKIP_AND_PASS(test_name, reason_category, reason_detail)
//        -- emits a structured SKIP line + returns 0 (so the test
//           runner ranks the file as PASSED). Use this for tests that
//           are LEGITIMATELY DEFERRED because the required runtime
//           system has not shipped (XIL2CPP, XLiveCoding, editor, Quest
//           3 hardware).
//
//   4. P5L_REQUIRES_QUEST3_HARDWARE_OR_SKIP(test_name, reason_detail)
//        -- conditional skip on non-Quest 3 builds. The test still
//           BUILDS on Win64 (CI catches build regressions) but the
//           heavy phase is gated. The macro emits a "SKIP (hardware-
//           required)" diagnostic on non-Quest 3 builds and returns
//           from main() with exit code 0.
//
//   5. P5L_BENCH_NS(loop_count, body) -> uint64_t nanoseconds elapsed
//        -- a wall-clock micro-benchmark helper. Uses
//           std::chrono::steady_clock (portable across Win64 / Quest 3
//           ARM64). Returns the elapsed nanoseconds across `loop_count`
//           iterations of `body`. The caller divides by loop_count to
//           get the per-iteration cost.
//
//   6. Phase 5.l synthetic XObject helpers (Phase5L::CreateAndBind /
//      Phase5L::ReleaseAndDeallocate) -- mirror the well-established
//      "AllocateRaw + ReserveSlot + placement-new + BindObject" pattern
//      from existing XObject tests. Used by every harness that needs
//      live allocator-allocated XObjects.
//
//   7. Phase 5.l shared reset helper (Phase5L::ResetAllForTests) --
//      resets every XObject-runtime singleton + initialises FMemory
//      + advances the init-phase to PostStaticInit. Use at test entry.
//
// JUDGEMENT CALL (Phase5LCommon.h placement). The harness header lives
// under Tests/FoundationPrototype/ rather than Public/XObject/ because:
//   * It is consumed ONLY by Phase 5.l test TUs (not by production
//     code or other test modules).
//   * Co-locating with the test TUs means the include path is `..` /
//     `Phase5LCommon.h` (or a public_include_paths entry in the test
//     module TOML) -- simpler than reserving a Public/Tests/ namespace.
//   * The header pattern matches what we ship for other test families;
//     see for example the locale_stub.cpp pattern in Phase 5.b tests.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "HAL/FMemory.h"
#include "HAL/XInitPhase.h"

#include "Reflection/FClass.h"
#include "Reflection/FName.h"

#include "XObject/CDOManagement.h"
#include "XObject/FXDeferredDestructionQueue.h"
#include "XObject/FXObjectAllocator.h"
#include "XObject/FXObjectArray.h"
#include "XObject/FXObjectCollector.h"
#include "XObject/FXObjectGCCardTable.h"
#include "XObject/FXObjectHotReloadCoordinator.h"
#include "XObject/FXObjectSatbQueue.h"
#include "XObject/FXSweepCandidateQueue.h"
#include "XObject/XObject.h"

#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <iostream>
#include <new>
#include <string_view>

// ---------------------------------------------------------------------
// Failure-counter scaffolding (per-TU; matches every other test TU's
// pattern). Each test TU defines `int g_FailureCount = 0;` in an
// unnamed namespace at the top of its main TU; the P5L_CHECK macro
// expects that name in scope. The Phase5LCommon.h header does NOT
// itself define the counter (to keep the header header-only-includable
// across multiple TUs in the same .exe; only the consumer TU owns the
// counter).
// ---------------------------------------------------------------------

// P5L_CHECK -- the canonical test-assertion macro.
//
// MSVC /W4 /WX flags `if (!(Condition))` as C4127 ("conditional
// expression is constant") when the Condition is a constexpr value
// (e.g., sizeof(T), offsetof(T, M), trait-trait composition). The
// fix is the standard idiom for "I know this is constant; that is
// the point": suppress C4127 around the if-test ONLY for this
// statement. The suppression does NOT propagate past the macro
// expansion (per MSVC __pragma(warning(suppress: N)) semantics).
#if defined(_MSC_VER)
    #define P5L_CHECK(Condition, Diagnostic)                              \
        do                                                                \
        {                                                                 \
            __pragma(warning(suppress: 4127))                             \
            if (!(Condition))                                             \
            {                                                             \
                ::std::cerr << "FAIL: " << (Diagnostic) << "\n";          \
                ++g_FailureCount;                                         \
            }                                                             \
        } while (false)
#else
    #define P5L_CHECK(Condition, Diagnostic)                              \
        do                                                                \
        {                                                                 \
            if (!(Condition))                                             \
            {                                                             \
                ::std::cerr << "FAIL: " << (Diagnostic) << "\n";          \
                ++g_FailureCount;                                         \
            }                                                             \
        } while (false)
#endif

#define P5L_REPORT_PASS(TestName)                                         \
    ((g_FailureCount == 0)                                                \
        ? (::std::cout << (TestName) << ": PASS\n", 0)                    \
        : (::std::cerr << (TestName) << ": " << g_FailureCount            \
                       << " FAIL(s)\n", 1))

// SKIP for tests that have NO live execution body (the required runtime
// system has not shipped). Returns 0 so the test runner ranks the file
// as PASSED; the SKIP diagnostic is visible in the test output.
#define P5L_SKIP_AND_PASS(TestName, ReasonCategory, ReasonDetail)         \
    do                                                                    \
    {                                                                     \
        ::std::cout << (TestName)                                          \
                    << ": SKIP (" << (ReasonCategory) << ") -- "          \
                    << (ReasonDetail) << "\n";                            \
        return 0;                                                          \
    } while (false)

// Hardware-required gate. On non-Quest 3 builds the test BUILDS but
// short-circuits past the heavy phase with a SKIP diagnostic. On Quest
// 3 ARM64 the macro is a no-op and the test body runs.
//
// JUDGEMENT CALL (Phase 5.l). XPACT_PLATFORM_ANDROID is the closest
// proxy for "Quest 3 ARM64" because Quest 3 is an Android-derived OS
// running on ARM64. The Foundation Prototype acceptance scenes for
// (a) / (b) / (h) require a Quest 3 device specifically -- a generic
// Android emulator on x86_64 would not produce the right pause-budget
// numbers. The "Quest 3 hardware" gate is a property the build
// environment must surface; the macro below uses the platform proxy
// + a future runtime probe slot for the QuestSDK presence.
#define P5L_REQUIRES_QUEST3_HARDWARE_OR_SKIP(TestName, ReasonDetail)      \
    do                                                                    \
    {                                                                     \
        if (!XPACT_PLATFORM_ANDROID)                                      \
        {                                                                 \
            ::std::cout << (TestName)                                      \
                        << ": SKIP (hardware-required) -- "               \
                        << (ReasonDetail) << "\n";                        \
            return 0;                                                      \
        }                                                                 \
    } while (false)

// ---------------------------------------------------------------------
// Micro-benchmark helper.
//
// Returns nanoseconds elapsed across `LoopCount` iterations of the
// callable body `Body`. The body MUST be inlineable (a lambda or a
// trivial functor); the helper is a template so each call site
// monomorphises.
//
// Usage:
//   const std::uint64_t TotalNs = Phase5L::BenchNs(1'000'000,
//       [&]() noexcept { /* one iteration */ });
//   const double PerIterNs = double(TotalNs) / 1'000'000.0;
//
// JUDGEMENT CALL (Phase 5.l). std::chrono::steady_clock is the right
// portable choice over the XPact PlatformTime API because (a) it is
// guaranteed monotonic, (b) it has nanosecond-precision underlying
// representation on every supported platform (per cppreference; on
// Win64 the implementation routes through QueryPerformanceCounter;
// on POSIX through clock_gettime(CLOCK_MONOTONIC); on Android same).
// The XPact PlatformTime API is sim-path-flagged + provides only
// microsecond precision; not appropriate for a sub-100-ns micro-bench.
// ---------------------------------------------------------------------

namespace Phase5L
{
    template <typename Body>
    XPACT_FORCEINLINE ::std::uint64_t BenchNs(::std::size_t LoopCount, Body&& Fn) noexcept
    {
        const auto Start = ::std::chrono::steady_clock::now();
        for (::std::size_t I = 0; I < LoopCount; ++I)
        {
            Fn();
        }
        const auto End = ::std::chrono::steady_clock::now();
        return static_cast<::std::uint64_t>(
            ::std::chrono::duration_cast<::std::chrono::nanoseconds>(End - Start).count());
    }

    // -----------------------------------------------------------------
    // ResetAllForTests -- reset every XObject runtime singleton + bring
    // FMemory online + advance to PostStaticInit (the phase NewObject
    // requires).
    //
    // Call this at the top of every main() in the Phase 5.l harness.
    // Repeated calls are idempotent.
    // -----------------------------------------------------------------
    inline void ResetAllForTests() noexcept
    {
        ::XCore::HAL::FMemory::__Init();

        // Advance init-phase to PostStaticInit if we're not already
        // there. This is the gate NewObject's XPACT_CHECK looks for
        // (per Phase 5.d X-INIT acceptance).
        if (::XCore::HAL::EngineInitPhase() < ::XCore::HAL::EInitPhase::PostStaticInit)
        {
            ::XCore::HAL::__AdvanceInitPhase(::XCore::HAL::EInitPhase::PostStaticInit);
        }

        ::XCore::FXObjectArray::Get().__ResetForTests();
        ::XCore::FXObjectAllocator::Get().__ResetForTests();
        ::XCore::FXObjectGCCardTable::Get().__ResetForTests();
        ::XCore::FXSweepCandidateQueue::Get().__ResetForTests();
        ::XCore::FXDeferredDestructionQueue::Get().__ResetForTests();
        ::XCore::FXObjectCollector::Get().__ResetForTests();
        ::XCore::FXObjectHotReloadCoordinator::Get().__ResetForTests();
    }

    // -----------------------------------------------------------------
    // CreateAndBind -- allocate one XObject via the canonical hot path.
    //
    // Mirrors the well-established
    //   AllocateRaw -> ReserveSlot -> placement-new -> BindObject
    // pattern from Phase 5.h / 5.i tests. The caller supplies a
    // pre-registered FClass*. Returns a populated XObject* whose
    // ClassPrivate / InternalIndex / SerialNumber are filled.
    //
    // ALWAYS sized to sizeof(XObject) -- the harness tests use the
    // XObject base type directly (the SubClass user-defined extensions
    // ride into existence via XHT-emitted code, which Phase 5.l does
    // not exercise).
    // -----------------------------------------------------------------
    inline ::XCore::XObject* CreateAndBind(
        const ::XCore::Reflect::FClass* Class) noexcept
    {
        void* const Cell = ::XCore::FXObjectAllocator::Get().AllocateRaw(
            sizeof(::XCore::XObject), alignof(::XCore::XObject), Class);
        if (Cell == nullptr)
        {
            return nullptr;
        }

        ::XCore::XObject* const Obj = ::new (Cell) ::XCore::XObject();
        Obj->ClassPrivate = Class;

        ::uint32 Serial = 0;
        const ::int32 Idx = ::XCore::FXObjectArray::Get().ReserveSlot(&Serial);
        Obj->InternalIndex = Idx;
        Obj->SerialNumber  = Serial;
        ::XCore::FXObjectArray::Get().BindObject(Idx, Obj);

        return Obj;
    }

    // -----------------------------------------------------------------
    // ReleaseAndDeallocate -- the inverse of CreateAndBind.
    //
    // FreeEntry the slot first (bumps SerialNumber for weak-ptr deref),
    // then Deallocate the cell so the allocator's free-list reclaims
    // the storage.
    // -----------------------------------------------------------------
    inline void ReleaseAndDeallocate(::XCore::XObject* Obj) noexcept
    {
        if (Obj == nullptr)
        {
            return;
        }
        const ::int32 Idx = Obj->InternalIndex;
        if (Idx > 0)
        {
            ::XCore::FXObjectArray::Get().FreeEntry(Idx);
        }
        ::XCore::FXObjectAllocator::Get().Deallocate(static_cast<void*>(Obj));
    }
} // namespace Phase5L
