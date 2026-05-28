// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// SimPathRejects/CVarNotSimPathSafe.cpp -- expect-fail TU: reading a
// non-SimPathSafe CVar from sim-path code is a build error.
// =====================================================================
//
// XCore-4a Rev 3 Section 9.x: CVars carry a SimPathSafe flag in
// ECVarFlags. Sim-path TUs may only read CVars with SimPathSafe set;
// reading a non-SimPathSafe CVar via the typed handle in a sim-path
// TU should produce a [[deprecated]] / static_assert build error.
//
// EXPECT-FAIL CONTRACT (see Math/SimPathRejects/StdSqrt.cpp). The
// sim-path overlay header decorates a TConsoleVariableHandle<T>::Get
// call against a non-SimPathSafe CVar with the deprecation marker;
// flipping this TU's sim-path attribute exercises the gate.
//
// =====================================================================

#include "HAL/FAutoConsoleVariable.h"
#include "HAL/TConsoleVariableHandle.h"
#include "HAL/ECVarFlags.h"
#include "Macros/XCoreTypes.h"

namespace
{
    // Declare a non-SimPathSafe CVar; the default flags do not include
    // SimPathSafe.
    //
    // FAutoConsoleVariable + ECVarFlags live in `XCore::Misc` (the
    // canonical namespace per FAutoConsoleVariable.h and ECVarFlags.h);
    // the test file's header includes are under `HAL/` for the path
    // taxonomy, but the symbols themselves are not in `XCore::HAL`.
    ::XCore::Misc::FAutoConsoleVariable<::int32> g_nonSimPathCVar(
        "test.NonSimPath", 0,
        "Test: not sim-path-safe",
        ::XCore::Misc::ECVarFlags::Default);
}

int main()
{
    auto Handle = g_nonSimPathCVar.GetHandle();
    // The next call would be a deprecation error in a sim-path TU
    // because Handle.Get() on a non-SimPathSafe CVar is banned.
    volatile ::int32 V = Handle.Get();
    (void)V;
    return 0;
}
