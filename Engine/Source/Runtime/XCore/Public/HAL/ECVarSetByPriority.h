// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// ECVarSetByPriority.h -- five-level SetByPriority cascade (Section 9.1).
// =====================================================================
//
// XCore-4a Rev 3, Section 1.3 locked decision 10 + Section 9.1.
//
// Per locked decision 10:
//   "CVar SetByPriority has 5 levels (not UE's 16). The cascade is
//    SetByDefault < SetByConfig < SetByCommandline < SetByCode <
//    SetByConsole. Covers every realistic case at a fraction of the
//    debug surface."
//
// A weaker setter cannot overwrite a value already set by a stronger
// one. Ties resolve by recency (the same-priority later setter wins).
// In Dev builds each CVar carries a "SetBy history" (last 4 entries)
// so the question "where did this CVar value come from?" is answered
// by `cvar_history <name>` -- the history surface is consulted at
// `IConsoleVariable::GetLastSetBy()` and is Debug+Dev-only.
//
// UE divergence: UE's EConsoleVariableFlags packs the SetBy enum into
// the same uint32 as the ECVF_* feature flags (HAL/IConsoleManager.h:63
// EConsoleVariableFlags). XPact splits ECVarSetByPriority off into a
// dedicated uint8 enum so the priority cascade is independent of the
// feature-flag bitmask -- the two concerns are orthogonal and packing
// them into one type is the source of UE's "where did this come from?"
// debug friction. See Section 9.6 first row.
//
// ABI: uint8-backed (1 byte). 5 values fit in 3 bits with room for
// future additions; the spec wording for Phase 2 is "if a sixth level
// is ever needed, MINOR ABI bump replacing the trailing reserved slots
// in IConsoleVariable, not a separate enum widening."
//
// =====================================================================

#include "Macros/XCoreTypes.h"

namespace XCore::Misc
{

    // -----------------------------------------------------------------
    // ECVarSetByPriority -- the five-level cascade.
    //
    // Weakest first; strongest last. The numeric value IS the priority
    // (higher number = higher priority); IConsoleVariable's setter
    // body compares the current SetBy against the incoming SetBy and
    // ignores the call if the current is greater.
    //
    // The names mirror the spec wording (Section 9.1 spec body uses
    // ECVarSetBy::Default etc.; the dispatch instructions use
    // ECVarSetByPriority::Default etc.). The dispatch wording is the
    // more recent direction so we adopt it; an alias
    // `using ECVarSetBy = ECVarSetByPriority;` is provided below
    // so the spec-body wording continues to parse at .gen.cpp sites
    // and any future docs reconciliation can collapse to either name.
    // -----------------------------------------------------------------
    enum class ECVarSetByPriority : ::uint8
    {
        Default     = 0,   // weakest -- the registration-time default
        Config      = 1,   // .ini / config-file load
        Commandline = 2,   // CLI arg at process launch
        Code        = 3,   // SetInt/SetFloat/SetString from C++ code
        Console     = 4,   // strongest -- in-game console / debug HUD
    };

    static_assert(sizeof(ECVarSetByPriority) == 1, "ECVarSetByPriority ABI lock: 1 byte");

    // Alias matching the spec body wording (Section 9.1). The two
    // identifiers refer to the same enum; pick either at the call site.
    using ECVarSetBy = ECVarSetByPriority;

} // namespace XCore::Misc
