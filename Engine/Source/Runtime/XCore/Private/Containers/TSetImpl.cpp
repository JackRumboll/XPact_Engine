// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// TSetImpl.cpp -- common SwissTable shared state.
// =====================================================================
//
// XCore-4a Rev 3, Section 5.1 (TSet + TMap shared backbone) +
// dependency-graph step 9.
//
// TSet and TMap are header-template-resident: every type-specific
// hash + equal + element-construction path is instantiated at the
// caller's translation unit. The only shared state across all
// instantiations is the empty-group sentinel array (kEmptyGroupBytes
// in Containers/TSet.h), and that one is `inline constexpr` so the
// linker handles dedup without a .cpp definition.
//
// However, the SwissTable header file `Containers/TSet.h` declares the
// `XCore::Detail::EmptyGroup()` accessor as a constexpr inline
// function; this would normally have header-only ODR semantics. To
// guarantee the symbol is present in at least one TU (some toolchains
// emit weak-linkage symbols only on-demand), we anchor one no-op
// reference to it here.
//
// This .cpp also serves as the natural home for any future common
// helpers (e.g., a TSet-shared probe-stat counter if performance
// telemetry lands).
//
// =====================================================================

#include "Containers/TSet.h"
#include "Containers/TMap.h"
#include "Containers/TPair.h"

namespace XCore::Detail
{
    namespace
    {
        // Anchor reference -- force at least one TU to materialize the
        // EmptyGroup() symbol. The volatile read defeats compilers that
        // would otherwise eliminate the unused-value statement.
        [[maybe_unused]] static const ::uint8* AnchorEmptyGroup()
        {
            const ::uint8* P = EmptyGroup();
            // Read the first byte to anchor the symbol. The result is
            // ignored.
            [[maybe_unused]] volatile ::uint8 V = *P;
            (void)V;
            return P;
        }

        // Materialize the anchor at static-init time.
        [[maybe_unused]] static const ::uint8* AnchorPtr = AnchorEmptyGroup();
    }
}
