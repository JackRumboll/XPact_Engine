// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XWeakPtr.Tests/Equality.cpp -- bytewise operator== / operator!=
// (XCoreXObject Rev 4 §6.2 + Rev 1 HIGH-1).
// =====================================================================
//
// XWeakPtr equality compares the CAPTURED {InternalIndex, SerialNumber}
// tuple. Two weak-ptrs are equal iff they were captured from the same
// slot at the same SerialNumber.
//
// =====================================================================

#include "XObject/XObject.h"
#include "XObject/XWeakPtr.h"

#include <cstdint>
#include <iostream>

int main()
{
    using ::XCore::XObject;
    using ::XCore::XWeakPtr;

    int FailureCount = 0;
    auto Check = [&](bool Cond, const char* Diagnostic)
    {
        if (!Cond)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++FailureCount;
        }
    };

    // Equal captures.
    {
        XObject A; A.InternalIndex = 10; A.SerialNumber = 20u;
        XWeakPtr<XObject> W1(&A);
        XWeakPtr<XObject> W2(&A);
        Check(W1 == W2, "equal captures: == false");
        Check(!(W1 != W2), "equal captures: != true");
    }

    // Differing Index.
    {
        XObject A; A.InternalIndex = 10; A.SerialNumber = 20u;
        XObject B; B.InternalIndex = 11; B.SerialNumber = 20u;
        XWeakPtr<XObject> W1(&A);
        XWeakPtr<XObject> W2(&B);
        Check(W1 != W2, "differing Index: == returned true");
    }

    // Differing Serial.
    {
        XObject A; A.InternalIndex = 10; A.SerialNumber = 20u;
        XObject B; B.InternalIndex = 10; B.SerialNumber = 21u;
        XWeakPtr<XObject> W1(&A);
        XWeakPtr<XObject> W2(&B);
        Check(W1 != W2, "differing Serial: == returned true");
    }

    // Nullptr comparison.
    {
        XWeakPtr<XObject> W;
        Check(W == nullptr, "null: W == nullptr false");
        Check(!(W != nullptr), "null: W != nullptr true");

        XObject A; A.InternalIndex = 1; A.SerialNumber = 1u;
        XWeakPtr<XObject> W2(&A);
        Check(W2 != nullptr, "non-null: W2 != nullptr false");
    }

    if (FailureCount == 0)
    {
        std::cout << "XWeakPtr.Equality: PASS\n";
        return 0;
    }
    std::cerr << "XWeakPtr.Equality: " << FailureCount << " FAIL(s)\n";
    return 1;
}
