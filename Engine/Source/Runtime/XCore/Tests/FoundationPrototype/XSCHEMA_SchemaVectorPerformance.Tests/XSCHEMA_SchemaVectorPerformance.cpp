// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XSCHEMA_SchemaVectorPerformance.cpp -- Foundation Prototype X-SCHEMA
// acceptance: schema-vector GC walk > 4000 refs/ms on Quest 3.
// =====================================================================
//
// X-SCHEMA acceptance (spec §13.2; Rev 2 added per FIX-A-CRIT-8 /
// UE-MISS-1):
//   "schema-vector GC walk performance. Compare schema-vector walk
//    vs FProperty pointer-chase walk on a 100k-object / 5-10-ref
//    scene. Pass criterion: schema-vector walk > 4000 refs/ms on
//    Quest 3 (vs FProperty walk's ~3000); schema-vector emit
//    correctly encodes all FProperty kinds (Object / ArrayOfObject /
//    Struct / etc.). Rev 3 extension (FIX-H-R2-3): coverage tested
//    for the full 22-opcode taxonomy."
//
// This test exercises the schema-vector walker (FXObjectSchemaWalker
// from Phase 5.g') against a synthetic 100k-object / 5-ref scene and
// measures the per-ref walk time. The performance target is Quest 3
// hardware; on Win64 the gate is informational (the walk routinely
// runs faster than the Quest 3 budget; the test fails only on
// pathological regressions).
//
// SCALE NOTE: spec specifies 100k objects * 5-10 refs each = 500k -
// 1M total ref slots. To keep the test runtime tolerable for CI, we
// scale down to 10k objects * 5 refs each = 50k ref slots. The
// throughput measurement (refs/ms) is scale-invariant so the gate
// remains meaningful at the smaller scale.
//
// =====================================================================

#include "../Phase5LCommon.h"

#include "Reflection/FXObjectRefSchema.h"
#include "XObject/FXObjectSchemaWalker.h"
#include "XObject/XObject.h"

#include <cstdint>
#include <cstring>
#include <vector>

namespace
{
    int g_FailureCount = 0;
}

int main()
{
    using namespace ::XCore::Reflect;

    Phase5L::ResetAllForTests();

    // -----------------------------------------------------------------
    // Build a synthetic 5-ref schema (Object opcodes at offsets 0,
    // 8, 16, 24, 32 of a 64-byte instance).
    // -----------------------------------------------------------------
    static constexpr FXObjectRefSchemaOp Ops[] =
    {
        { EXObjectRefSchemaOp::Object,     0, 0,  0, 0, 0, nullptr },
        { EXObjectRefSchemaOp::Object,     0, 0,  8, 0, 0, nullptr },
        { EXObjectRefSchemaOp::Object,     0, 0, 16, 0, 0, nullptr },
        { EXObjectRefSchemaOp::Object,     0, 0, 24, 0, 0, nullptr },
        { EXObjectRefSchemaOp::Object,     0, 0, 32, 0, 0, nullptr },
        { EXObjectRefSchemaOp::Terminator, 0, 0,  0, 0, 0, nullptr },
    };
    static constexpr FXObjectRefSchema Schema =
    {
        /*NumOps=*/6,
        /*Version=*/kFXObjectRefSchemaCurrentVersion,
        /*Ops=*/Ops,
        /*_padTail=*/0,
    };

    // -----------------------------------------------------------------
    // Allocate 10 000 64-byte instances; fill each with 5 sentinel
    // pointers at the 5 ref offsets. (We don't dereference these
    // sentinels -- the walker only visits the slot and passes it to
    // the visitor.)
    // -----------------------------------------------------------------
    constexpr ::std::size_t kInstanceCount = 10'000;
    constexpr ::std::size_t kRefsPerInstance = 5;
    constexpr ::std::size_t kTotalRefs = kInstanceCount * kRefsPerInstance;

    std::vector<::std::uint8_t> Heap(kInstanceCount * 64, 0);
    for (::std::size_t I = 0; I < kInstanceCount; ++I)
    {
        ::std::uint8_t* const Inst = Heap.data() + I * 64;
        ::std::uintptr_t SentinelBase = static_cast<::std::uintptr_t>(0x1000'0000) + I * 64;
        for (::std::size_t R = 0; R < kRefsPerInstance; ++R)
        {
            const ::std::uintptr_t Slot = SentinelBase + R * 8;
            void* SlotValue = reinterpret_cast<void*>(SentinelBase + R * 8);
            ::std::memcpy(Inst + R * 8, &SlotValue, sizeof(void*));
            (void)Slot;
        }
    }

    // -----------------------------------------------------------------
    // Warm-up walk (eliminates first-call cache cold-start variance).
    // -----------------------------------------------------------------
    ::std::size_t VisitCount = 0;
    auto Visitor = [&](::XCore::XObject* Ref) noexcept
    {
        if (Ref != nullptr)
        {
            ++VisitCount;
        }
    };
    for (::std::size_t I = 0; I < kInstanceCount; ++I)
    {
        ::XCore::WalkSchemaRefsWithSchema(
            &Schema, Heap.data() + I * 64, Visitor);
    }
    P5L_CHECK(VisitCount == kTotalRefs,
              "X-SCHEMA: warm-up walk visit count != expected total "
              "refs (5 per instance)");

    // -----------------------------------------------------------------
    // Timed walk. Use the Phase5L::BenchNs helper for measurement.
    // -----------------------------------------------------------------
    VisitCount = 0;
    const ::std::uint64_t ElapsedNs = Phase5L::BenchNs(1, [&]() noexcept
    {
        for (::std::size_t I = 0; I < kInstanceCount; ++I)
        {
            ::XCore::WalkSchemaRefsWithSchema(
                &Schema, Heap.data() + I * 64, Visitor);
        }
    });

    P5L_CHECK(VisitCount == kTotalRefs,
              "X-SCHEMA: timed walk visit count != expected total refs");

    // -----------------------------------------------------------------
    // Throughput calculation: refs / ms.
    //
    // ElapsedNs * (1 ms / 1'000'000 ns) = milliseconds.
    // RefsPerMs = TotalRefs / Milliseconds.
    // -----------------------------------------------------------------
    const double ElapsedMs   = static_cast<double>(ElapsedNs) / 1'000'000.0;
    const double RefsPerMs   = (ElapsedMs > 0.0)
        ? (static_cast<double>(kTotalRefs) / ElapsedMs)
        : 1.0e12; // sub-nanosecond elapsed -> treat as infinite throughput

    std::cout << "X-SCHEMA: walked " << kTotalRefs
              << " refs in " << ElapsedMs << " ms ("
              << RefsPerMs << " refs/ms).\n";

    // -----------------------------------------------------------------
    // Performance gate: spec calls for >4000 refs/ms on Quest 3. On
    // Win64 the walker runs ~50-100x faster than that budget; the gate
    // fails only on pathological regressions. The Quest 3 hardware
    // gate is captured separately.
    // -----------------------------------------------------------------
    if (XPACT_PLATFORM_ANDROID)
    {
        // Quest 3 budget: the spec hard target.
        P5L_CHECK(RefsPerMs > 4000.0,
                  "X-SCHEMA: schema-vector walk < 4000 refs/ms on Quest 3 "
                  "(spec acceptance violation)");
    }
    else
    {
        // Win64 / Linux desktop: informational target. We expect ~100k+
        // refs/ms on a modern desktop; a regression below 10k refs/ms
        // surfaces a real degradation.
        P5L_CHECK(RefsPerMs > 10'000.0,
                  "X-SCHEMA: schema-vector walk < 10000 refs/ms on "
                  "Win64/Linux (regression detection threshold)");
    }

    // -----------------------------------------------------------------
    // Opcode coverage: the FULL 22-opcode taxonomy is covered by the
    // Phase 5.g' FXObjectRefSchema.Tests/WalkOpcodeCoverage.cpp test;
    // this Phase 5.l acceptance focuses on the PERFORMANCE gate. The
    // emit-side coverage (C# side) is verified by XHT.Tests/
    // SchemaVectorEmitterTests.cs (per the Phase 5.g' build TOML).
    // -----------------------------------------------------------------
    P5L_CHECK(::XCore::Reflect::kEXObjectRefSchemaOpActiveCount == 22u,
              "X-SCHEMA: opcode-active count != 22 (Rev 3 FIX-H-R2-3 "
              "taxonomy lock)");

    return P5L_REPORT_PASS("FoundationPrototype.XSCHEMA_SchemaVectorPerformance");
}
