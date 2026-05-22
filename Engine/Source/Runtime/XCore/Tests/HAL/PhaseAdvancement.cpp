// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// PhaseAdvancement.cpp -- runtime test of __AdvanceInitPhase contract.
// =====================================================================
//
// XCore-4a Section 1.5 (fix C-2). The phase ladder global must:
//   * Initialise to PreStaticInit at static-storage-duration time.
//   * Advance monotonically: PreStaticInit -> PostStaticInit -> FrameZero.
//   * Abort cleanly on any retreat or skip.
//
// This test is runtime (not compile-only) because the abort behaviour
// is observable at runtime. The two non-abort transitions are checked
// directly; the abort cases are NOT exercised here (running them
// would terminate the test runner). Instead, an out-of-process death
// test fixture (e.g., GoogleTest's ASSERT_DEATH or a manual fork +
// exit-code check) is the right tool for the abort cases; that
// fixture is a follow-up Phase 1b task documented at the bottom of
// this file.
//
// Phase 1a coverage: the happy-path advance is verified; the abort
// branches are covered by code review + the static contract in
// __AdvanceInitPhase's body.
//
// =====================================================================

#include "HAL/XInitPhase.h"

#include <cstdlib>
#include <iostream>

int main()
{
    using ::XCore::HAL::EInitPhase;
    using ::XCore::HAL::EngineInitPhase;
    using ::XCore::HAL::__AdvanceInitPhase;

    // ----------- Test 1: initial state is PreStaticInit. -----------
    if (EngineInitPhase() != EInitPhase::PreStaticInit)
    {
        ::std::cerr << "FAIL: initial EngineInitPhase() must be PreStaticInit\n";
        return 1;
    }

    // ----------- Test 2: advance Pre -> Post. -----------
    __AdvanceInitPhase(EInitPhase::PostStaticInit);
    if (EngineInitPhase() != EInitPhase::PostStaticInit)
    {
        ::std::cerr << "FAIL: after advance to PostStaticInit, phase must be PostStaticInit\n";
        return 1;
    }

    // ----------- Test 3: advance Post -> FrameZero. -----------
    __AdvanceInitPhase(EInitPhase::FrameZero);
    if (EngineInitPhase() != EInitPhase::FrameZero)
    {
        ::std::cerr << "FAIL: after advance to FrameZero, phase must be FrameZero\n";
        return 1;
    }

    // ----------- TODO(Phase 1b): out-of-process abort tests. -----------
    //
    // The following cases each abort the process, so they cannot run
    // inline in this test. A follow-up fixture should fork() (POSIX)
    // / CreateProcess (Win64) a child that exercises one of these,
    // and assert the child exits with the abort-style exit code:
    //
    //   * Retreat: from FrameZero, call __AdvanceInitPhase(
    //     EInitPhase::PostStaticInit) -- must abort with the
    //     "illegal transition 2 -> 1" diagnostic.
    //   * Skip: from a fresh process (phase = PreStaticInit), call
    //     __AdvanceInitPhase(EInitPhase::FrameZero) directly -- must
    //     abort with the "illegal transition 0 -> 2" diagnostic.
    //   * Self-loop: from PostStaticInit, call
    //     __AdvanceInitPhase(EInitPhase::PostStaticInit) -- must
    //     abort with the "illegal transition 1 -> 1" diagnostic.
    //
    // Until the fixture lands, these branches are covered by code
    // review of __AdvanceInitPhase's body in XInitPhase.cpp.

    ::std::cout << "PhaseAdvancement: PASS\n";
    return 0;
}
