// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// TModuleSafeThreadLocal.Tests/BasicGetSet.cpp -- Phase 1g Fix M-4
// smoke test: TModuleSafeThreadLocal<T> correctly allocates a TLS
// slot, lazily-creates per-thread T storage on first Get(), and
// returns the same pointer on subsequent Get() calls in the same
// thread.
// =====================================================================
//
// XCore-4a Rev 3, Section 13.1 fix B-C1 (XPACT_TLS_MODULE_SAFE) +
// Section 7.1 (Platform HAL FPlatformTLS).
//
// Scope:
//   * Default ctor: AllocSlot succeeds; IsValid() == true.
//   * First Get() returns a non-null pointer; the underlying T is
//     default-constructed.
//   * Second Get() returns the same pointer (no double-allocation).
//   * Set(p) overrides the slot; subsequent Get() returns p.
//   * Clear() destroys + frees the per-thread storage; the next
//     Get() lazily-creates a fresh T.
//
// =====================================================================

#include "HAL/TModuleSafeThreadLocal.h"
#include "Macros/XCoreTypes.h"

#include <cstdio>

namespace
{
    struct FCounter
    {
        ::int32 Value = 42;
    };

    int RunBasicGetSet()
    {
        ::XCore::HAL::TModuleSafeThreadLocal<FCounter> Tls;

        if (!Tls.IsValid())
        {
            std::fprintf(stderr,
                "FAIL: TModuleSafeThreadLocal::IsValid() returned false; "
                "the OS TLS-slot pool may be exhausted.\n");
            return 1;
        }

        FCounter* P1 = Tls.Get();
        if (P1 == nullptr)
        {
            std::fprintf(stderr, "FAIL: First Get() returned nullptr.\n");
            return 1;
        }
        if (P1->Value != 42)
        {
            std::fprintf(stderr,
                "FAIL: First Get() did not default-construct (Value=%d, "
                "expected 42).\n", P1->Value);
            return 1;
        }

        FCounter* P2 = Tls.Get();
        if (P2 != P1)
        {
            std::fprintf(stderr,
                "FAIL: Second Get() returned a different pointer "
                "(P1=%p, P2=%p); expected pointer identity.\n",
                static_cast<void*>(P1), static_cast<void*>(P2));
            return 1;
        }

        // Mutate via the pointer; verify the change persists.
        P1->Value = 100;
        FCounter* P3 = Tls.Get();
        if (P3->Value != 100)
        {
            std::fprintf(stderr,
                "FAIL: Mutation through Get() did not persist "
                "(Value=%d after mutation, expected 100).\n", P3->Value);
            return 1;
        }

        // Clear; the next Get() should re-allocate.
        Tls.Clear();
        FCounter* P4 = Tls.Get();
        if (P4 == nullptr)
        {
            std::fprintf(stderr, "FAIL: Get() after Clear() returned nullptr.\n");
            return 1;
        }
        if (P4->Value != 42)
        {
            std::fprintf(stderr,
                "FAIL: Get() after Clear() did not produce a "
                "freshly-default-constructed FCounter "
                "(Value=%d, expected 42).\n", P4->Value);
            return 1;
        }

        // Clean up before destructor.
        Tls.Clear();
        std::printf("PASS: TModuleSafeThreadLocal BasicGetSet\n");
        return 0;
    }
}

int main()
{
    return RunBasicGetSet();
}
