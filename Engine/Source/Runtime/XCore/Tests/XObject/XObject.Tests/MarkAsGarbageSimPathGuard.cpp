// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XObject.Tests/MarkAsGarbageSimPathGuard.cpp -- documents the
// sim-path guard contract on XObject::MarkAsGarbage
// (XCoreXObject Rev 4 §4.2 + Rev 3 FIX-M-R2-10).
// =====================================================================
//
// CONTRACT (per spec §4.2 + the spec body at lines 327-333):
//
//   void XObject::MarkForKill() noexcept {
//       XPACT_CHECK_SL(!::XCore::HAL::IsSimPathTU(),
//           "MarkForKill is non-sim-path; the next-GC-sweep timing is non-deterministic");
//       SetFlags(uint32_t(EObjectFlags::MarkedAsGarbage));
//   }
//
// The XPACT_CHECK_SL guard fires in Debug / Development if the
// sim-path runtime probe (::XCore::HAL::IsSimPathTU) returns true.
// In Shipping the guard compiles to ((void)0); the static-analysis
// sim-path filter at the build level is the primary enforcement.
//
// PHASE 5.a SCOPE (deliberate deferral):
//
// The runtime SimPathTU probe (::XCore::HAL::IsSimPathTU) does NOT
// exist at Phase 5.a -- it ships in a future phase (the spec
// references the symbol but no XCore-4a/4b TU defines it). Until the
// probe lands, the guard at the MarkAsGarbage entry is a NO-OP and
// the contract is enforced ONLY by the static-analysis sim-path
// filter:
//
//   * Sim-path TUs are compiled with `sim_path = true` in their
//     XBT module .toml.
//   * The Sleef-style banned-symbol check (XBT.Toolchain.SleefFMACheck)
//     verifies the per-TU compile does NOT pull non-sim-path symbols.
//   * A sim-path TU calling MarkAsGarbage today is a build-time
//     diagnostic via the include-graph + symbol-table audit, not a
//     runtime check.
//
// This Phase 5.a test therefore VERIFIES THE NON-SIM-PATH HAPPY PATH
// (calling MarkAsGarbage from a normal TU works). The full sim-path
// negative test ships at the future phase that lands
// ::XCore::HAL::IsSimPathTU.
//
// Test outcome:
//   * Phase 5.a: PASS (the non-sim-path call is the only path
//     exercisable today).
//   * Phase 5.e+ (when SimPathTU probe ships): the test will be
//     extended to spawn a sim-path TU compile that calls
//     MarkAsGarbage and expects the XPACT_CHECK_SL to fire.
//
// =====================================================================

#include "XObject/EObjectFlags.h"
#include "XObject/XObject.h"

#include <iostream>

int main()
{
    // -----------------------------------------------------------------
    // Non-sim-path happy path: MarkAsGarbage sets MarkedAsGarbage.
    // -----------------------------------------------------------------
    ::XCore::XObject Obj;

    if (Obj.IsMarkedAsGarbage())
    {
        std::cerr << "FAIL: fresh XObject reports IsMarkedAsGarbage() == true\n";
        return 1;
    }

    Obj.MarkAsGarbage();

    if (!Obj.IsMarkedAsGarbage())
    {
        std::cerr << "FAIL: after MarkAsGarbage(), IsMarkedAsGarbage() == false\n";
        return 1;
    }
    if (!Obj.HasAnyFlags(::XCore::EObjectFlags::MarkedAsGarbage))
    {
        std::cerr << "FAIL: after MarkAsGarbage(), HasAnyFlags(MarkedAsGarbage) == false\n";
        return 1;
    }

    // -----------------------------------------------------------------
    // MarkForKill alias (per spec; the spec body uses both names).
    // -----------------------------------------------------------------
    ::XCore::XObject Obj2;
    Obj2.MarkForKill();
    if (!Obj2.IsMarkedAsGarbage())
    {
        std::cerr << "FAIL: MarkForKill() alias did not set MarkedAsGarbage\n";
        return 1;
    }

    // -----------------------------------------------------------------
    // Idempotency: a second MarkAsGarbage is a no-op (SetFlags is
    // idempotent via OR).
    // -----------------------------------------------------------------
    Obj.MarkAsGarbage();
    if (!Obj.IsMarkedAsGarbage())
    {
        std::cerr << "FAIL: double MarkAsGarbage() lost the flag\n";
        return 1;
    }

    std::cout << "XObject.MarkAsGarbageSimPathGuard: PASS\n"
              << "  NOTE: sim-path negative test deferred to the "
              << "future phase that lands ::XCore::HAL::IsSimPathTU "
              << "runtime probe (per spec §4.2 + Rev 3 FIX-M-R2-10).\n";
    return 0;
}
