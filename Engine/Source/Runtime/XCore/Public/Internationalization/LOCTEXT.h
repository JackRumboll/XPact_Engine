// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// LOCTEXT.h -- the LOCTEXT / NSLOCTEXT / INVTEXT macros (Section 11.2).
// =====================================================================
//
// XCore-4a Rev 3, Section 11.2 (LOCTEXT macro) + UE Internationalization
// reference at `UnrealEngine/Engine/Source/Runtime/Core/Public/
// Internationalization/Internationalization.h:281` (LOCTEXT),
// `:286` (NSLOCTEXT), `:308` (INVTEXT).
//
// XPact's LOCTEXT differs from UE's in three load-bearing ways:
//
//   1. The string-literal types are UTF-8 `const char*` (NOT TCHAR).
//      Per locked decision 5 (UTF-8 throughout) the engine never
//      crosses a TCHAR boundary in user code.
//
//   2. The expansion is a constexpr-static-initialiser-friendly
//      `FText::FromLiteral` call (NOT UE's
//      `FText::AsLocalizable_Advanced_LocText` which constructs a
//      heap-allocated FTextKey). FromLiteral stores the rodata
//      pointers verbatim; the cost is exactly 3 pointer stores at
//      the call site, no allocation. The lazy-cache lookup happens
//      only at ResolveForCurrentLocale time.
//
//   3. INVTEXT uses a sentinel namespace "__Invariant" (NOT UE's
//      AsCultureInvariant which is a separate FText subtype). The
//      sentinel is recognised by FLocalizationManager::Lookup which
//      short-circuits the loctable search and uses the literal
//      directly. This keeps the FText layout uniform: every FText is
//      three rodata pointers + a cache, regardless of whether it is
//      culture-invariant or localised.
//
// USAGE (mirrors UE's pattern; the only difference is UTF-8 literals
// instead of TCHAR):
//
//   #define LOCTEXT_NAMESPACE "MySubsystem"
//   FText Greeting = LOCTEXT("Greeting", "Hello!");
//   FText Inv      = INVTEXT("Player1");
//   #undef LOCTEXT_NAMESPACE
//
// The user-facing `#define LOCTEXT_NAMESPACE` and `#undef` form is
// documented in Section 11.2 and matches UE's convention so user code
// migrated from UE compiles after a TCHAR -> const char* substitution.
//
// =====================================================================

#include "Internationalization/FText.h"

// ---------------------------------------------------------------------
// LOCTEXT -- the standard file-scope macro.
//
// Expands to FText::FromLiteral(LOCTEXT_NAMESPACE, InKey, InTextLiteral).
// The caller is responsible for #defining LOCTEXT_NAMESPACE to a
// string-literal at the top of the TU and #undef'ing it at the
// bottom (per Section 11.2 and UE precedent).
//
// Note: LOCTEXT_NAMESPACE is intentionally NOT predefined here. A TU
// that uses LOCTEXT without first #define'ing LOCTEXT_NAMESPACE gets
// a clear compiler diagnostic ("undeclared identifier
// LOCTEXT_NAMESPACE"); this is the correct shape because it forces
// the developer to make the namespace choice explicit.
// ---------------------------------------------------------------------
#define LOCTEXT(InKey, InTextLiteral) \
    ::XCore::Loc::FText::FromLiteral(LOCTEXT_NAMESPACE, InKey, InTextLiteral)

// ---------------------------------------------------------------------
// NSLOCTEXT -- the explicit-namespace form.
//
// Expands to FText::FromLiteral(InNamespace, InKey, InTextLiteral).
// Used when the caller does not want to set a file-scope
// LOCTEXT_NAMESPACE (e.g., a single-use FText in a one-off block,
// or a header file that should not pollute the consuming TU's
// LOCTEXT_NAMESPACE).
// ---------------------------------------------------------------------
#define NSLOCTEXT(InNamespace, InKey, InTextLiteral) \
    ::XCore::Loc::FText::FromLiteral(InNamespace, InKey, InTextLiteral)

// ---------------------------------------------------------------------
// INVTEXT -- culture-invariant text.
//
// Expands to FText::FromLiteral("__Invariant", "", InTextLiteral). The
// "__Invariant" namespace is recognised by FLocalizationManager::Lookup
// as a sentinel -- the lookup short-circuits and returns the literal
// fallback verbatim, never touching any loctable.
//
// Use cases per UE precedent (Internationalization.h:308):
//   * Pure display content that is never localised (e.g., a debug
//     "FPS: 60" overlay).
//   * Content that comes from runtime data, not from a loctable
//     (e.g., a player name pulled from a save file).
//
// For runtime-generated strings (not literals), use FString directly
// and convert to FText only at the display boundary if a localised
// surrounding string composes with it via FText::Format.
// ---------------------------------------------------------------------
#define INVTEXT(InTextLiteral) \
    ::XCore::Loc::FText::FromLiteral("__Invariant", "", InTextLiteral)
