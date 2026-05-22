// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// SleefSymbolVariants.cpp -- linked Sleef symbol variant verification.
// =====================================================================
//
// XCore-4a Rev 3 Section 17.3 C-extra:
//   "ULP tier verified at unit-test time: sqrt/exp/log use u10,
//    sin/cos/tan/atan2 use u35; the symbol-table dump asserts the
//    exact Sleef variant names are linked."
//
// The test exercises the link-side discipline.  In Phase 1e (Sleef
// vendored as the API-compatible shim that routes to libm under
// XPACT_SIMPATH_PROVISIONAL=1), the test verifies:
//
//   (a) Every required Sleef_*_u10 / Sleef_*_u35 entry is callable
//       from C++ via the extern "C" linkage in Sleef.h.  If any of
//       these symbols were missing from the linked archive, the test
//       executable would have failed to link before this code ever
//       ran.  The presence of this test as a compiled+linked binary
//       IS the test (a self-asserting "link-time gate").
//
//   (b) The function pointers (taken via address-of) point at
//       DISTINCT bodies -- not a single accidental aliased symbol.
//       This catches the regression where a future Sleef build
//       accidentally maps Sleef_sinf_u35 and Sleef_cosf_u35 to the
//       same body (e.g., a copy-paste error in the vendored .c
//       file).
//
// In Phase 1g (cross-arch CI runs the actual symbol-table dump) the
// SleefFMACheck.cs tool runs `llvm-objdump -t` over the linked
// archive and asserts:
//   * Every linked Sleef symbol carries an _u10 or _u35 suffix.
//   * NO _avx2 / _sse4 / _neon / _sve / _finz / _fma variants are
//     linked.
// This C++ test ships the runtime smoke check; the link-time scan is
// the load-bearing gate.
//
// =====================================================================

#include "Sleef.h"
#include "Macros/XCoreTypes.h"

#include <cstdio>

namespace
{
    // Address-of-function pointer collection.  If any of these is a
    // null pointer (theoretically impossible because the linker
    // would refuse to produce the executable), the test catches it.
    // If two distinct functions resolve to the same pointer, the
    // test catches the aliasing.
    struct FSleefSymbolEntry
    {
        const char* Name;
        const void* Address;
    };
}

int main()
{
    // ---------- Collect every required Sleef entry. ----------
    const FSleefSymbolEntry Entries[] = {
        // Single-precision u35 (trig).
        { "Sleef_sinf_u35",   reinterpret_cast<const void*>(&::Sleef_sinf_u35)   },
        { "Sleef_cosf_u35",   reinterpret_cast<const void*>(&::Sleef_cosf_u35)   },
        { "Sleef_tanf_u35",   reinterpret_cast<const void*>(&::Sleef_tanf_u35)   },
        // Single-precision u10 (transcendental).
        { "Sleef_atan2f_u10", reinterpret_cast<const void*>(&::Sleef_atan2f_u10) },
        { "Sleef_sqrtf_u10",  reinterpret_cast<const void*>(&::Sleef_sqrtf_u10)  },
        { "Sleef_powf_u10",   reinterpret_cast<const void*>(&::Sleef_powf_u10)   },
        { "Sleef_expf_u10",   reinterpret_cast<const void*>(&::Sleef_expf_u10)   },
        { "Sleef_logf_u10",   reinterpret_cast<const void*>(&::Sleef_logf_u10)   },
        // Double-precision u35.
        { "Sleef_sin_u35",    reinterpret_cast<const void*>(&::Sleef_sin_u35)    },
        { "Sleef_cos_u35",    reinterpret_cast<const void*>(&::Sleef_cos_u35)    },
        { "Sleef_tan_u35",    reinterpret_cast<const void*>(&::Sleef_tan_u35)    },
        // Double-precision u10.
        { "Sleef_atan2_u10",  reinterpret_cast<const void*>(&::Sleef_atan2_u10)  },
        { "Sleef_sqrt_u10",   reinterpret_cast<const void*>(&::Sleef_sqrt_u10)   },
        { "Sleef_pow_u10",    reinterpret_cast<const void*>(&::Sleef_pow_u10)    },
        { "Sleef_exp_u10",    reinterpret_cast<const void*>(&::Sleef_exp_u10)    },
        { "Sleef_log_u10",    reinterpret_cast<const void*>(&::Sleef_log_u10)    },
    };
    constexpr int N = sizeof(Entries) / sizeof(Entries[0]);

    int Failed = 0;

    // ---------- (a) Every entry is non-null. ----------
    for (int I = 0; I < N; ++I)
    {
        if (Entries[I].Address == nullptr)
        {
            std::fprintf(stderr,
                "FAIL: Sleef symbol '%s' resolved to nullptr.\n", Entries[I].Name);
            ++Failed;
        }
    }

    // ---------- (b) Every entry is distinct. ----------
    // Pairwise comparison is O(N^2 / 2) = ~120 comparisons; cheap.
    for (int I = 0; I < N; ++I)
    {
        for (int J = I + 1; J < N; ++J)
        {
            // Two entries are allowed to share a body ONLY if their
            // type signatures coincide -- which can happen for
            // single-precision-vs-double-precision pairs (no; they
            // differ in parameter type, body must differ).  Per the
            // Sleef-3.6 ABI every named entry is its own function body.
            if (Entries[I].Address == Entries[J].Address)
            {
                std::fprintf(stderr,
                    "FAIL: Sleef symbols '%s' and '%s' share the same address %p; aliasing forbidden.\n",
                    Entries[I].Name, Entries[J].Name, Entries[I].Address);
                ++Failed;
            }
        }
    }

    // ---------- (c) Smoke-call to confirm linkage at exec time. ----------
    // If any of these were stubs that crash on call, the test catches it.
    volatile float V1 = ::Sleef_sinf_u35  (0.5f);
    volatile float V2 = ::Sleef_cosf_u35  (0.5f);
    volatile float V3 = ::Sleef_tanf_u35  (0.5f);
    volatile float V4 = ::Sleef_atan2f_u10(0.5f, 0.5f);
    volatile float V5 = ::Sleef_sqrtf_u10 (0.5f);
    volatile float V6 = ::Sleef_powf_u10  (0.5f, 0.5f);
    volatile float V7 = ::Sleef_expf_u10  (0.5f);
    volatile float V8 = ::Sleef_logf_u10  (0.5f);
    (void)V1; (void)V2; (void)V3; (void)V4;
    (void)V5; (void)V6; (void)V7; (void)V8;

    if (Failed > 0)
    {
        std::fprintf(stderr, "SleefSymbolVariants: FAIL (%d issues)\n", Failed);
        return 1;
    }

    std::printf("SleefSymbolVariants: PASS (%d Sleef _u10/_u35 entries linked + distinct)\n", N);
    return 0;
}
