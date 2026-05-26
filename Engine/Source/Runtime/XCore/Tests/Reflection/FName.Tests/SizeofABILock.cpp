// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FName.Tests/SizeofABILock.cpp -- ABI lock verification at runtime.
// =====================================================================
//
// XCore-4b Rev 3 §4.1 ABI lock + Contract Rev 13.8 XPACT_FNAME_LAYOUT_TAG.
//
// Verifies sizeof / alignof / offsetof for FName, plus the FNameEntry
// header layout. These are also static_asserts in the public headers;
// this runtime test is the belt-and-braces gate that emits a CI artifact
// for the Section 13 acceptance criteria.
// =====================================================================

#include "Reflection/FName.h"
#include "Reflection/FNameEntry.h"

#include <cstddef>
#include <cstdio>

int main()
{
    using ::XCore::Reflect::FName;
    using ::XCore::Reflect::FNameEntry;
    using ::XCore::Reflect::FNameEntryHeader;

    // -----------------------------------------------------------------
    // FName 8-byte ABI lock.
    // -----------------------------------------------------------------
    if (sizeof(FName) != 8)
    {
        std::fprintf(stderr, "FAIL: sizeof(FName) = %zu (expected 8)\n", sizeof(FName));
        return 1;
    }
    if (alignof(FName) != 4)
    {
        std::fprintf(stderr, "FAIL: alignof(FName) = %zu (expected 4)\n", alignof(FName));
        return 1;
    }
    if (offsetof(FName, Index) != 0)
    {
        std::fprintf(stderr, "FAIL: offsetof(FName, Index) = %zu (expected 0)\n", offsetof(FName, Index));
        return 1;
    }
    if (offsetof(FName, SerialNumber) != 4)
    {
        std::fprintf(stderr, "FAIL: offsetof(FName, SerialNumber) = %zu (expected 4)\n",
                     offsetof(FName, SerialNumber));
        return 1;
    }

    // -----------------------------------------------------------------
    // FNameEntry header 6-byte ABI lock (per Rev 2 FIX-21 / Rev 3
    // FIX-R2-LOW-2: header dropped from 8 to 6 bytes; Reserved field
    // removed).
    // -----------------------------------------------------------------
    if (sizeof(FNameEntryHeader) != 6)
    {
        std::fprintf(stderr, "FAIL: sizeof(FNameEntryHeader) = %zu (expected 6)\n",
                     sizeof(FNameEntryHeader));
        return 1;
    }

    // -----------------------------------------------------------------
    // FNameEntry payload offset + 8-byte alignment.
    // -----------------------------------------------------------------
    if (offsetof(FNameEntry, Bytes) != 6)
    {
        std::fprintf(stderr, "FAIL: offsetof(FNameEntry, Bytes) = %zu (expected 6)\n",
                     offsetof(FNameEntry, Bytes));
        return 1;
    }
    if (alignof(FNameEntry) != 8)
    {
        std::fprintf(stderr, "FAIL: alignof(FNameEntry) = %zu (expected 8)\n", alignof(FNameEntry));
        return 1;
    }

    return 0;
}
