// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FCustomVersion.Tests/XBTTransitiveDirtyPlaceholder.cpp
// =====================================================================
//
// XCore-4b Rev 3, Section 13 Acceptance gates C5, C6, C7 -- SCAFFOLDING.
//
// These three gates depend on XBT (System 1) integration that is
// scheduled for Phase 4b.7, not Phase 4b.2:
//
//   C5. Transitive-dirty SchemaHash: modifying a parent class's
//       XPROPERTY declared shape without rebuilding child classes
//       flags XCore4b002 build error from XBT's transitive-dirty graph.
//       --> XBT-side test; cannot run as a runtime unit test.
//
//   C6. SchemaHash truncation-collision diagnostic: a synthesised pair
//       of types whose BLAKE3-lower-64 hashes collide produces a clean
//       XCore4b003 link-time error.
//       --> XHT/XBT link-time test; cannot run as a runtime unit test.
//
//   C7. ClassReps FRepRecord shape: requires FClass + FRepRecord
//       definitions (Phase 4b.5).
//       --> Cannot run until Phase 4b.5 ships.
//
// Phase 4b.2 ships the RUNTIME state (FCustomVersionRegistry + the
// SchemaHash computation function) that those gates will hook into.
// This file documents the scaffold and exits PASS as a marker -- the
// test runner records it as "ran" so the gate list shows a deliberate
// placeholder rather than a missing gate.
//
// When Phase 4b.7 ships, this file will be replaced with:
//
//   * C5: an XBT-driven integration test that mutates a parent class's
//     XPROPERTY declared shape (in a fixture project) and verifies the
//     build fails with XCore4b002. Runs under the XBT.Tests harness,
//     not this XCore.Tests harness.
//
//   * C6: an XHT-side test that synthesises two types whose declared
//     shapes hash to the same lower-64-bit BLAKE3 value (via XHT's
//     hash-collision-finder mode) and asserts the link error.
//
//   * C7: lives in FClass.Tests/ClassReps.cpp.
//
// =====================================================================

#include "Reflection/SchemaHash.h"
#include "Reflection/FCustomVersionRegistry.h"

#include <iostream>

int main()
{
    // We still touch the production APIs so this file is not "dead
    // compile-only" -- a future regression that makes SchemaHash or
    // the registry non-link-compatible will be caught here.
    using ::XCore::Reflect::ComputeSchemaHashImpl;
    using ::XCore::Reflect::FCustomVersionRegistry;

    const ::uint64 H = ComputeSchemaHashImpl(nullptr, 0);
    (void)H;
    (void)FCustomVersionRegistry::Get();

    std::cout << "FCustomVersion.XBTTransitiveDirtyPlaceholder: PASS "
                 "(scaffold; gates C5/C6/C7 deferred to Phase 4b.7+)\n";
    return 0;
}
