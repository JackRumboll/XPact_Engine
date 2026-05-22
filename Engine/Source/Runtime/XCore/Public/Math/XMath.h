// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XMath.h -- math namespace dispatcher (Section 6.1 + locked decision 4).
// =====================================================================
//
// XCore-4a Rev 3, Section 6.1 namespace alias block (line 549-553).
//
// This header is the dispatcher. It includes EITHER XMathFast.h OR
// XSimMath.h based on the XPACT_SIMPATH macro, and then aliases the
// namespace XCore::Math to either XCore::FastMath or XCore::SimMath.
//
// User code includes this header instead of the two specific halves;
// the dispatcher does the right thing per TU.
//
// XPACT_SIMPATH IS SET BY XBT (Contract Rev 13.7 Section 4.1):
//   XBT sets XPACT_SIMPATH=1 on TUs in modules whose .Build.toml
//   declares `sim_path = true`. Other TUs (the renderer, the UI, the
//   audio system, the editor, the asset pipeline) leave it undefined
//   or set to 0.
//
//   XSimPathMathOverrides.h (Phase 1 stub) is the canonical place that
//   sets XPACT_SIMPATH=1 for sim-path TUs; including it auto-routes
//   downstream XMath.h consumers to XSimMath.
//
// USAGE EXAMPLE:
//
//   // In any TU:
//   #include "Math/XMath.h"
//
//   void SomeFunction() {
//       float Mag = XCore::Math::Sqrt(LengthSq);   // routes to FastMath::Sqrt
//                                                   // or SimMath::Sqrt depending
//                                                   // on the TU's XPACT_SIMPATH flag.
//       float T = XCore::Math::Clamp(X, 0.0f, 1.0f);  // scalar util; same in both.
//   }
//
// =====================================================================

#include "Macros/XCoreTypes.h"

// ---------------------------------------------------------------------
// XPACT_SIMPATH dispatch.
//
// The two halves of the dispatch each pull in every math type header.
// Including one or the other gives the consuming TU:
//   - the math types (FVector, FQuat, FMatrix, ...; same in both halves)
//   - the SCALAR utilities (XCore::Math::Lerp, Clamp, ...; same in both)
//   - the FRandomStream (same in both)
//   - the TRANSCENDENTAL forwarders (different: FastMath uses libm
//     freely; SimMath routes through Sleef and poisons libm)
//   - in SimMath only: FRotator is [[deprecated]].
// ---------------------------------------------------------------------

#if defined(XPACT_SIMPATH) && XPACT_SIMPATH
    #include "Math/XSimMath.h"
    namespace XCore { namespace Math = ::XCore::SimMath; }
#else
    #include "Math/XMathFast.h"
    namespace XCore { namespace Math = ::XCore::FastMath; }
#endif
