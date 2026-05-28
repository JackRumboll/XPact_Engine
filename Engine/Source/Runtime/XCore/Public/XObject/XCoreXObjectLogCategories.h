// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XCoreXObjectLogCategories.h -- producer-side XLog category names per
// spec §10.11 + Rev 2 FIX-A-MED-31 (Phase 5.k).
// =====================================================================
//
// Spec §10.11 names three XCoreXObject XLog categories:
//   * XCoreXObject.GC          -- cycle start/end, mark phase progress,
//                                  sweep counts.
//   * XCoreXObject.Allocator   -- pool growth, slab allocations,
//                                  scenario-boundary releases.
//   * XCoreXObject.HotReload   -- quiesce begin/end, cascade apply,
//                                  class replacement counts.
//
// The XLog System ships in Layer 1.5+ (post-XCoreXObject in build
// order). Phase 5.k provides the PRODUCER-side category-name surface
// so XCoreXObject code can refer to category identifiers in a stable
// way that XLog can plug into when it lands.
//
// PHASE 5.k STUB POSTURE:
//
// Until XLog ships, the XPACT_DECLARE_LOG_CATEGORY macro is a no-op
// placeholder. The category-name FNames are still constructed
// (function-local-static accessors mirroring XInsightsEvents.h) so
// downstream code can:
//   * Reference the category FName as an identifier (e.g., for
//     XInsights category alignment).
//   * Use the same FName at both the producer (this header) and the
//     receiver (XLog, when it ships).
//
// When XLog ships, this header should be updated to forward the
// XPACT_DECLARE_LOG_CATEGORY macro to XLog's real declaration body
// (typically: declare an FLogCategory instance + a destructor that
// unregisters from the global category registry). The category FNames
// remain stable; the X_LOG macro at every call site continues to
// compile against the same names.
//
// FORWARD-COMPATIBLE NAMING (Prime Directive):
//
// The macro is named XPACT_DECLARE_LOG_CATEGORY so it carries the same
// shape UE's DECLARE_LOG_CATEGORY_EXTERN uses, and so the eventual
// XLog migration is a header-only swap (no call-site rename). UE's
// macro takes a CategoryName + DefaultVerbosity + CompileTimeVerbosity;
// the XPact form takes just the CategoryName for Phase 5.k (the
// verbosity parameters are forward commitment for the XLog ship).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Reflection/FName.h"

// =====================================================================
// XPACT_DECLARE_LOG_CATEGORY -- placeholder declaration macro.
//
// Until XLog ships, the macro emits NOTHING at the declaration site.
// The category-name function-local-static FName accessors below
// provide the stable identifier surface.
//
// Use:
//   XPACT_DECLARE_LOG_CATEGORY(XCoreXObject_GC);
//
// In a future revision, XLog's real macro will look something like:
//   #define XPACT_DECLARE_LOG_CATEGORY(Name) \
//       extern ::XCore::HAL::FLogCategory g_LogCategory_##Name
//
// The two forms have IDENTICAL declarations from the consumer's
// perspective; the Phase 5.k consumer simply observes the no-op
// expansion, while a post-XLog consumer observes the FLogCategory
// extern declaration.
// =====================================================================
#ifndef XPACT_DECLARE_LOG_CATEGORY
    #define XPACT_DECLARE_LOG_CATEGORY(CategoryName) \
        /* Phase 5.k stub: no-op. XLog ships in System 1.5+. */ \
        static_assert(true, "XPACT_DECLARE_LOG_CATEGORY placeholder; XLog ships in System 1.5+.")
#endif

namespace XCore::HAL::LogCategories
{
    // =================================================================
    // Producer-side category declarations.
    //
    // At Phase 5.k these expand to nothing (the macro is the
    // placeholder). When XLog ships these will expand to real
    // FLogCategory externs that the X_LOG macro indirects through.
    //
    // The FOUR categories below cover Phase 5's XCoreXObject surface.
    // The fourth ("Telemetry") is the XInsights-bridge log category --
    // events emitted via XInsightsBridge::Emit can OPTIONALLY also
    // produce an XLog line per the spec §10.11 trailing prose ("X_LOG
    // emits a parallel line for Dev-mode debug-watch visibility").
    // =================================================================

    XPACT_DECLARE_LOG_CATEGORY(XCoreXObject_GC);
    XPACT_DECLARE_LOG_CATEGORY(XCoreXObject_Allocator);
    XPACT_DECLARE_LOG_CATEGORY(XCoreXObject_HotReload);
    XPACT_DECLARE_LOG_CATEGORY(XCoreXObject_Telemetry);

    // =================================================================
    // Category-name FName accessors -- producer-side identifier
    // surface. Match the names XLog will use as receiver-side keys.
    //
    // The function-local-static pattern matches XInsightsEvents.h.
    // The same FName values are used for the XLog category and the
    // XInsights category so a unified telemetry consumer can correlate
    // log lines with structured emit events by FName.GetIndex equality.
    // =================================================================

    [[nodiscard]] inline const ::XCore::Reflect::FName& GetGCCategoryName() noexcept
    {
        static const ::XCore::Reflect::FName Name("XCoreXObject.GC");
        return Name;
    }

    [[nodiscard]] inline const ::XCore::Reflect::FName& GetAllocatorCategoryName() noexcept
    {
        static const ::XCore::Reflect::FName Name("XCoreXObject.Allocator");
        return Name;
    }

    [[nodiscard]] inline const ::XCore::Reflect::FName& GetHotReloadCategoryName() noexcept
    {
        static const ::XCore::Reflect::FName Name("XCoreXObject.HotReload");
        return Name;
    }

    [[nodiscard]] inline const ::XCore::Reflect::FName& GetTelemetryCategoryName() noexcept
    {
        static const ::XCore::Reflect::FName Name("XCoreXObject.Telemetry");
        return Name;
    }

} // namespace XCore::HAL::LogCategories
