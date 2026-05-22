// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FString.Tests/Format.cpp -- placeholder for Phase 1e Format/FormatFixed.
// =====================================================================
//
// Per Subagent A's dispatch, the Format/FormatFixed templates land in
// Phase 1e (after the math + Sleef vendoring) because they require
// either:
//   (a) MSVC 19.30+ / libstdc++ 13+ / libc++ 16+ with std::format, OR
//   (b) the fmt vendoring (TODO Phase 2).
//
// For Phase 1d, the public surface in FString.h does NOT declare
// the Format/FormatFixed templates (the spec text in the header
// banner notes the intent + the Phase 1e completion). Tests gated on
// Format are therefore SKIPPED at this milestone with a `return 0`
// success exit. When Phase 1e lands the templates, this file is
// replaced by the real test body (compile-time-bad format-string
// negative cases + sim-path FormatFixed correctness).
//
// =====================================================================

#include "Containers/FString.h"

#include <cstdio>

int main()
{
    // Format/FormatFixed are not yet a public surface on FString.
    // This test passes as a placeholder; replace with real tests when
    // Phase 1e lands the templates.
    std::printf("PHASE1D_SKIP: FString::Format / FormatFixed deferred to Phase 1e.\n");
    return 0;
}
