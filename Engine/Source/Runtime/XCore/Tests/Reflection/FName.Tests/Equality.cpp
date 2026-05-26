// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FName.Tests/Equality.cpp -- intern determinism + case-sensitivity.
// =====================================================================
//
// XCore-4b Rev 3 §4.5 ("Hash function + equality") + §4.10
// (divergence from UE: case-sensitive MVP).
//
// Acceptance gates:
//   * A2 -- same-string-twice returns same Index.
//   * A4 -- case-sensitive comparison ("Actor" != "actor").
//   * Equality + ordering of FName operators.
// =====================================================================

#include "Reflection/FName.h"
#include "HAL/FMemory.h"

#include <cstdio>

int main()
{
    ::XCore::HAL::FMemory::__Init();

    using ::XCore::Reflect::FName;

    // -----------------------------------------------------------------
    // A2: same string twice returns same Index.
    // -----------------------------------------------------------------
    {
        FName A("Actor");
        FName B("Actor");
        if (A.GetIndex() != B.GetIndex())
        {
            std::fprintf(stderr, "FAIL: FName(\"Actor\") second-call Index differs (A=%u, B=%u)\n",
                         A.GetIndex(), B.GetIndex());
            return 1;
        }
        if (!(A == B))
        {
            std::fprintf(stderr, "FAIL: A == B for same input\n");
            return 1;
        }
        if (A != B)
        {
            std::fprintf(stderr, "FAIL: !(A != B) for same input\n");
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // A4: case-sensitive comparison ("Actor" != "actor").
    // -----------------------------------------------------------------
    {
        FName Upper("Actor");
        FName Lower("actor");

        if (Upper == Lower)
        {
            std::fprintf(stderr, "FAIL: FName(\"Actor\") == FName(\"actor\") (case-sensitive MVP)\n");
            return 1;
        }
        if (Upper.GetIndex() == Lower.GetIndex())
        {
            std::fprintf(stderr, "FAIL: case-folded names produced identical Index\n");
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // Distinct strings produce distinct Indices.
    // -----------------------------------------------------------------
    {
        FName A("Alpha");
        FName B("Beta");
        if (A == B)
        {
            std::fprintf(stderr, "FAIL: distinct strings produced equal FNames\n");
            return 1;
        }
        if (A.GetIndex() == B.GetIndex())
        {
            std::fprintf(stderr, "FAIL: distinct strings produced equal Index\n");
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // Ordering: comparison operators (Index-then-SerialNumber).
    // -----------------------------------------------------------------
    {
        FName A("XYZ");
        FName B("XYZ");
        // Index equal, SerialNumber differs.
        FName A1 = FName::WithNumber(A, 1);
        FName A2 = FName::WithNumber(A, 2);

        if (!(A1 < A2))
        {
            std::fprintf(stderr, "FAIL: A_1 < A_2 not true (Serial 1 < Serial 2)\n");
            return 1;
        }
        if (A2 < A1)
        {
            std::fprintf(stderr, "FAIL: A_2 < A_1 returned true (should be false)\n");
            return 1;
        }
        if (A1 >= A2)
        {
            std::fprintf(stderr, "FAIL: A_1 >= A_2 returned true\n");
            return 1;
        }
        if (!(A1 <= A2))
        {
            std::fprintf(stderr, "FAIL: A_1 <= A_2 not true\n");
            return 1;
        }
        if (!(A2 > A1))
        {
            std::fprintf(stderr, "FAIL: A_2 > A_1 not true\n");
            return 1;
        }
        if (!(A == B))
        {
            std::fprintf(stderr, "FAIL: A == B (same string, no suffix)\n");
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // NAME_None equals default ctor.
    // -----------------------------------------------------------------
    {
        FName N;
        if (!N.IsNone())
        {
            std::fprintf(stderr, "FAIL: default ctor not IsNone()\n");
            return 1;
        }
        if (N != ::XCore::Reflect::NAME_None)
        {
            std::fprintf(stderr, "FAIL: default ctor != NAME_None\n");
            return 1;
        }
    }

    return 0;
}
