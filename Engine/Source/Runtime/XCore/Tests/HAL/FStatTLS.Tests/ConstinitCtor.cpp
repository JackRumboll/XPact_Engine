// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FStatTLS.Tests/ConstinitCtor.cpp -- XSTAT_INC from a PreStaticInit
// constinit constructor (Phase 1g fix MIN-3).
// =====================================================================
//
// XCore-4a Rev 3 Section 10.5 fix Rev 3 M1 "Hot-path lazy-init
// lifecycle":
//
//   "XSTAT_INC from a constinit constructor (PreStaticInit) on a fresh
//    thread does not abort and increments the slot correctly; the count
//    survives into PostStaticInit's first merger pass via the open-
//    addressing slot the merger walks regardless of perfect-hash state."
//
// The test instantiates a global object whose constructor calls
// XSTAT_INC during static-storage-duration initialisation. The
// constructor runs BEFORE main(). main() then runs
// FStatRegistry::MergeFromAllThreads + Snapshot and verifies the
// pre-main XSTAT_INC count survived.
//
// =====================================================================

#include "HAL/FStatTLS.h"
#include "HAL/FStatRegistry.h"
#include "HAL/FMemory.h"
#include "HAL/XInitPhase.h"
#include "Macros/XCoreTypes.h"

#include <cstdio>

namespace
{
    // XSTAT_DECL produces an inline constexpr FStatId with a stable
    // FNV-1a-64 hash known at compile time.
    XSTAT_DECL(EarlyStat, ConstinitGroup);

    struct FEarlyCounter
    {
        // Constructor runs at static-storage-duration init. The body
        // calls XSTAT_INC against EarlyStat; this exercises the
        // FStatTLS path before main() runs.
        FEarlyCounter() noexcept
        {
            // The Stat shard is constinit-zero by construction; the
            // open-addressing slot walk treats Hash=0 as empty.
            // EarlyStat_StatId.Hash is non-zero by FNV-1a-64 design.
            XSTAT_INC(EarlyStat);
            XSTAT_INC(EarlyStat);
            XSTAT_INC(EarlyStat);
        }
    };

    // The global; constructor runs before main().
    FEarlyCounter g_earlyCounter;
}

int main()
{
    // FMemory + InitPhase + (FStatRegistry init is implicit on first
    // merger call).
    ::XCore::HAL::FMemory::__Init();
    ::XCore::HAL::__AdvanceInitPhase(::XCore::HAL::EInitPhase::PostStaticInit);

    // Merge: the calling thread's shard contains the 3 XSTAT_INCs from
    // the global ctor. Merge folds them into the global tree.
    ::XCore::Stat::FStatRegistry::MergeFromAllThreads();

    ::XCore::Stat::FStatSnapshot Snap;
    ::XCore::Stat::FStatRegistry::Snapshot(Snap);
    const ::int64 Count = Snap.Lookup(EarlyStat_StatId.Hash);
    if (Count != 3)
    {
        std::fprintf(stderr,
            "FAIL: XSTAT_INC from constinit ctor count = %lld, expected 3. "
            "The pre-main XSTAT_INC writes did not survive the Merge.\n",
            static_cast<long long>(Count));
        return 1;
    }

    std::printf("PASS: XSTAT_INC from constinit ctor (PreStaticInit) survives Merge\n");
    return 0;
}
