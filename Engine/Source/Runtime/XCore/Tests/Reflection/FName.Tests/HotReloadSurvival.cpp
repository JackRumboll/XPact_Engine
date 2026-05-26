// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FName.Tests/HotReloadSurvival.cpp -- placeholder for acceptance gate A6.
// =====================================================================
//
// XCore-4b Rev 3 §4.7 ("Hot-reload across DLL patch") + acceptance gate
// A6 + Rev 2 FIX-24 explicit test case ("a patched DLL creating a
// same-bytes FName as an existing one resolves to the existing Index").
//
// Per dispatch instructions: "tested via System 5 integration" since
// genuine DLL hot-reload requires the XLiveCoding harness which lives
// in a downstream system. THIS file is a placeholder that exercises the
// SUB-property the intern-table guarantees on its own: the intern-table
// is the single source of truth, so a SECOND construction of the same
// bytes (simulating a hot-patched DLL re-interning the same string)
// resolves to the existing Index without allocating a duplicate entry.
//
// The full DLL-reload scenario (load + unload + re-load) requires
// System 5's hot-reload harness and lands at that acceptance gate.
// =====================================================================

#include "Reflection/FName.h"
#include "HAL/FMemory.h"

#include <cstdio>

int main()
{
    ::XCore::HAL::FMemory::__Init();

    using ::XCore::Reflect::FName;

    // -----------------------------------------------------------------
    // Single-process duplicate-construction test: a "post-patch"
    // FName creation with the same bytes resolves to the existing Index.
    //
    // This is the in-process slice of A6. The full A6 (cross-DLL-load
    // survival) requires the XLiveCoding harness; this test verifies the
    // intern-table single-source-of-truth invariant the full test
    // relies on.
    // -----------------------------------------------------------------
    {
        // "Pre-patch": create the FName.
        FName PrePatch("HotReload_Survival_Target");
        if (PrePatch.IsNone())
        {
            std::fprintf(stderr, "FAIL: PrePatch FName is NAME_None\n");
            return 1;
        }

        const ::uint32 OriginalIndex = PrePatch.GetIndex();

        // Simulate many other FName constructions in between (the
        // intern-table should retain the original entry).
        for (int I = 0; I < 100; ++I)
        {
            char Buf[64];
            const int Len = std::snprintf(Buf, sizeof(Buf), "Filler_%d", I);
            FName Filler(Buf, Len);
            (void)Filler;
        }

        // "Post-patch": re-construct with the same bytes.
        FName PostPatch("HotReload_Survival_Target");
        if (PostPatch.GetIndex() != OriginalIndex)
        {
            std::fprintf(stderr,
                         "FAIL: post-patch FName Index differs (Original=%u, Post=%u)\n",
                         OriginalIndex, PostPatch.GetIndex());
            return 1;
        }
        if (PrePatch != PostPatch)
        {
            std::fprintf(stderr, "FAIL: pre/post-patch FName comparison unequal\n");
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // Full A6 ("a downstream-DLL hot-reload preserves Index across the
    // patch") requires the XLiveCoding harness in System 5 and lands at
    // that acceptance gate. Document the gap explicitly so a future
    // dependency audit sees the deliberate deferral.
    // -----------------------------------------------------------------
    std::fprintf(stdout,
                 "INFO: in-process intern-table single-source-of-truth invariant verified;\n"
                 "      full DLL-reload acceptance (A6) deferred to System 5 (XLiveCoding) harness.\n");

    return 0;
}
