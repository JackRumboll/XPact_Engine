// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XObjectKey.Tests/EqualityHash.cpp -- equality + GetTypeHash
// (XCoreXObject Rev 4 §6.4 + Rev 1 HIGH-1).
// =====================================================================
//
// Spec §6.4: operator== is bytewise equality (via std::bit_cast<uint64>
// per Rev 1 HIGH-1 pattern). GetTypeHash produces a 64-bit XXH3 hash
// over the 8-byte handle. Two equal keys must produce the same hash
// (the load-bearing TMap invariant).
//
// =====================================================================

#include "XObject/XObject.h"
#include "XObject/XObjectKey.h"

#include <cstdint>
#include <iostream>

int main()
{
    using ::XCore::XObject;
    using ::XCore::XObjectKey;

    int FailureCount = 0;
    auto Check = [&](bool Cond, const char* Diagnostic)
    {
        if (!Cond)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++FailureCount;
        }
    };

    // -----------------------------------------------------------------
    // Equality on identical {Index, Serial}.
    // -----------------------------------------------------------------
    {
        XObject A;
        A.InternalIndex = 7;
        A.SerialNumber  = 3u;

        XObjectKey K1(&A);
        XObjectKey K2(&A);
        Check(K1 == K2, "K1 == K2 false (same XObject captures)");
        Check(!(K1 != K2), "K1 != K2 true");
    }

    // -----------------------------------------------------------------
    // Inequality on differing Index.
    // -----------------------------------------------------------------
    {
        XObject A; A.InternalIndex = 7;  A.SerialNumber = 3u;
        XObject B; B.InternalIndex = 8;  B.SerialNumber = 3u;

        XObjectKey K1(&A);
        XObjectKey K2(&B);
        Check(K1 != K2, "K1 != K2 false (different Index)");
        Check(!(K1 == K2), "K1 == K2 true (different Index)");
    }

    // -----------------------------------------------------------------
    // Inequality on differing Serial.
    // -----------------------------------------------------------------
    {
        XObject A; A.InternalIndex = 7;  A.SerialNumber = 3u;
        XObject B; B.InternalIndex = 7;  B.SerialNumber = 4u;

        XObjectKey K1(&A);
        XObjectKey K2(&B);
        Check(K1 != K2, "K1 != K2 false (different Serial)");
    }

    // -----------------------------------------------------------------
    // Two null keys are equal.
    // -----------------------------------------------------------------
    {
        XObjectKey K1;
        XObjectKey K2(nullptr);
        Check(K1 == K2,         "two nulls: K1 != K2");
        Check(K1 == nullptr,    "K1 == nullptr false");
        Check(K2 == nullptr,    "K2 == nullptr false");
    }

    // -----------------------------------------------------------------
    // Hash: equal keys produce equal hashes (the load-bearing TMap
    // invariant).
    // -----------------------------------------------------------------
    {
        XObject A; A.InternalIndex = 11; A.SerialNumber = 22u;
        XObjectKey K1(&A);
        XObjectKey K2(&A);
        const ::uint64 H1 = GetTypeHash(K1);
        const ::uint64 H2 = GetTypeHash(K2);
        Check(H1 == H2, "equal keys produced different hashes");
    }

    // -----------------------------------------------------------------
    // Hash: differing keys produce different hashes WITH HIGH
    // PROBABILITY (XXH3 has very low collision rate for 8-byte inputs;
    // we only assert non-equality, NOT a specific distribution).
    // -----------------------------------------------------------------
    {
        XObject A; A.InternalIndex = 11; A.SerialNumber = 22u;
        XObject B; B.InternalIndex = 11; B.SerialNumber = 23u;
        XObject C; C.InternalIndex = 12; C.SerialNumber = 22u;

        const ::uint64 HA = GetTypeHash(XObjectKey(&A));
        const ::uint64 HB = GetTypeHash(XObjectKey(&B));
        const ::uint64 HC = GetTypeHash(XObjectKey(&C));

        Check(HA != HB,
              "hashes for differing SerialNumber collided (extremely unlikely; "
              "may indicate hash function regression)");
        Check(HA != HC,
              "hashes for differing InternalIndex collided");
    }

    // -----------------------------------------------------------------
    // Hash: null key produces a well-defined value (typically 0's
    // XXH3-64 digest); we only assert it is REPEATABLE (same value
    // on successive calls; the determinism contract).
    // -----------------------------------------------------------------
    {
        const ::uint64 H1 = GetTypeHash(XObjectKey{});
        const ::uint64 H2 = GetTypeHash(XObjectKey{});
        Check(H1 == H2, "null-key hash not repeatable");
    }

    if (FailureCount == 0)
    {
        std::cout << "XObjectKey.EqualityHash: PASS\n";
        return 0;
    }
    std::cerr << "XObjectKey.EqualityHash: " << FailureCount << " FAIL(s)\n";
    return 1;
}
