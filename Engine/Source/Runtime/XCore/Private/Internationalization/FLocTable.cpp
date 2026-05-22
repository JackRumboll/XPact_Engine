// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FLocTable.cpp -- loctable-format anchor TU.
// =====================================================================
//
// XCore-4a Rev 3 Section 18 OPEN-5.
//
// The loctable v1 binary format constants live in
// Public/Internationalization/FLocTable.h (inline constexpr
// definitions; no .cpp body required).
//
// The byte-level helpers live in
// Private/Internationalization/LocTableBytewise.h (inline functions;
// included by both FLocTable.cpp and FLocTableLoader.cpp).
//
// The file I/O surface (LoadFromFile / SaveToFile) and the
// payload-verification logic live in FLocTableLoader.cpp.
//
// This TU is intentionally minimal -- it exists so that:
//   * FLocTable.h's inline-constexpr constants have a TU that
//     instantiates them in the link map (the inline constexpr should
//     dedupe automatically, but ODR rules are clearest when there is
//     at least one TU that includes the header in a non-template
//     context).
//   * The Bytewise header-private helpers have at least one TU that
//     uses them, giving XBT's dead-code-stripping pass a clean
//     reference.
//
// =====================================================================

#include "Internationalization/FLocTable.h"
#include "LocTableBytewise.h"

namespace XCore::Loc
{
    // -----------------------------------------------------------------
    // Anchor symbol -- a no-op function that XBT can name in its link
    // graph as the TU's contribution. The function is never called;
    // its existence forces the TU to be included in the final link.
    //
    // Phase 1f: the TU is small enough that XBT's linker would keep
    // it anyway (constexpr constants in FLocTable.h are used by the
    // loader TU); the anchor is documentation, not a functional
    // requirement. Phase 2 may revisit if the build-system layout
    // changes.
    // -----------------------------------------------------------------
    void __LocTableAnchor() noexcept
    {
        // Reference the Bytewise helpers so a SIMD-loop-vectorizer
        // pass cannot DCE the entire TU. The compiler can still
        // constant-fold these calls; the symbol references survive in
        // the .obj.
        volatile ::uint8 ScratchBuf[8] = { 0, 0, 0, 0, 0, 0, 0, 0 };
        Bytewise::WriteU16Le(const_cast<::uint8*>(ScratchBuf), kLoctableVersion);
        Bytewise::WriteU32Le(const_cast<::uint8*>(ScratchBuf), kLoctableMagic);
    }
} // namespace XCore::Loc
