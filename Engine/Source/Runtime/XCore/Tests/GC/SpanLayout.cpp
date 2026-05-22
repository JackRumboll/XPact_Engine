// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// SpanLayout.cpp -- XGCRootSpan / XGCRootKind ABI lock test.
// =====================================================================
//
// XCore-4a Section 5.4 fix M-12 (XGCRootSpan = 32 bytes) + fix Rev 3 m3
// (XGCRootKind = 1 byte, uint8 underlying).
//
// The collector mark-time iteration walks spans at 32-byte strides;
// any drift here fails XCore-4b's mark phase silently. The static
// asserts below pin the layout at the XCore-4a edge so the contract
// is enforced before XCore-4b sees the struct.
//
// =====================================================================

#include "GC/XGCDeclarations.h"

#include <cstddef>      // offsetof
#include <type_traits>

namespace
{
    using ::XGC::XGCRootKind;
    using ::XGC::XGCRootSpan;

    // Kind enum lock (fix Rev 3 m3): uint8 underlying.
    static_assert(sizeof(XGCRootKind) == 1, "XGCRootKind must be 1 byte");
    static_assert(static_cast<int>(XGCRootKind::Strong)       == 0, "XGCRootKind::Strong = 0");
    static_assert(static_cast<int>(XGCRootKind::Weak)         == 1, "XGCRootKind::Weak = 1");
    static_assert(static_cast<int>(XGCRootKind::Conservative) == 2, "XGCRootKind::Conservative = 2");

    // Span layout lock (fix M-12): 32 bytes.
    static_assert(sizeof(XGCRootSpan)  == 32, "XGCRootSpan must be 32 bytes (fix M-12)");
    static_assert(alignof(XGCRootSpan) >= 8,  "XGCRootSpan must be 8-byte aligned");

    // Field-offset locks. The collector reads the struct at the byte
    // offsets below; any drift would land at a different field.
    // Portable `offsetof` from <cstddef>; on MSVC the macro
    // internally uses __builtin_offsetof, but the named-keyword form
    // is not exposed.
    //
    // Note the head-pad-before-flags layout (see XGCDeclarations.h
    // comment for the spec divergence rationale): `flags` is at
    // offset 28 (naturally 4-byte aligned), not 25 as the spec body
    // literal placement suggests. The four collector-load-bearing
    // fields (base/stride/count/kind) are at the spec-locked offsets
    // 0/8/16/24; only the reserved `flags` shifted from 25 to 28 to
    // satisfy uint32 alignment.
    static_assert(offsetof(XGCRootSpan, base)     ==  0, "base must be at offset 0");
    static_assert(offsetof(XGCRootSpan, stride)   ==  8, "stride must be at offset 8");
    static_assert(offsetof(XGCRootSpan, count)    == 16, "count must be at offset 16");
    static_assert(offsetof(XGCRootSpan, kind)     == 24, "kind must be at offset 24");
    static_assert(offsetof(XGCRootSpan, _padHead) == 25, "_padHead must be at offset 25");
    static_assert(offsetof(XGCRootSpan, flags)    == 28, "flags must be at offset 28 (naturally 4-byte aligned)");

    // The struct is trivially copyable so the collector can memcpy
    // its state safely if needed (e.g., to a forensic snapshot).
    static_assert(::std::is_trivially_copyable_v<XGCRootSpan>,
                  "XGCRootSpan must be trivially copyable");
    static_assert(::std::is_standard_layout_v<XGCRootSpan>,
                  "XGCRootSpan must be standard layout (for offsetof + extern \"C\" passing)");
} // anonymous

int main()
{
    return 0;
}
