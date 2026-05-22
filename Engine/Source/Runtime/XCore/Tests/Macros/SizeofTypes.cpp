// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// SizeofTypes.cpp -- compile-time ABI checks for XCoreTypes.h.
// =====================================================================
//
// Per XCore-4a Section 17.10 J5: "Links cleanly against the existing
// XReflectionRuntime stub (no symbol overlap)." This test exercises
// the integer typedefs declared in XCoreTypes.h via static_assert;
// the file is compiled standalone to verify XCoreTypes.h is
// self-contained (no transitive dependency that would prevent header-
// only use).
//
// Test strategy is compile-only (Section 13.4): static_assert lines
// fire at compile time; no runtime work. A passing build = a passing
// test.
//
// =====================================================================

#include "Macros/XCoreTypes.h"

#include <type_traits>  // is_signed_v / is_unsigned_v

namespace
{
    // Width asserts -- mirror the asserts inside XCoreTypes.h itself
    // so this TU's failure is attributable to the header.
    static_assert(sizeof(int8)   == 1, "int8 must be 1 byte");
    static_assert(sizeof(int16)  == 2, "int16 must be 2 bytes");
    static_assert(sizeof(int32)  == 4, "int32 must be 4 bytes");
    static_assert(sizeof(int64)  == 8, "int64 must be 8 bytes");
    static_assert(sizeof(uint8)  == 1, "uint8 must be 1 byte");
    static_assert(sizeof(uint16) == 2, "uint16 must be 2 bytes");
    static_assert(sizeof(uint32) == 4, "uint32 must be 4 bytes");
    static_assert(sizeof(uint64) == 8, "uint64 must be 8 bytes");

    // Signedness asserts -- the typedefs MUST be signed / unsigned
    // matching their name. A mistaken typedef to an unsigned variant
    // of int32 would silently shift the meaning of array indices.
    //
    // The trait check `is_signed_v` / `is_unsigned_v` is the
    // unambiguous spelling; arithmetic-based checks like
    // `0 - 1 > 0` fail for sub-int types because integral promotion
    // widens the operands to `int` before the operation, so the
    // wraparound never happens at the small-type's width.
    static_assert(::std::is_signed_v<int8>,    "int8 must be signed");
    static_assert(::std::is_signed_v<int16>,   "int16 must be signed");
    static_assert(::std::is_signed_v<int32>,   "int32 must be signed");
    static_assert(::std::is_signed_v<int64>,   "int64 must be signed");
    static_assert(::std::is_unsigned_v<uint8>,  "uint8 must be unsigned");
    static_assert(::std::is_unsigned_v<uint16>, "uint16 must be unsigned");
    static_assert(::std::is_unsigned_v<uint32>, "uint32 must be unsigned");
    static_assert(::std::is_unsigned_v<uint64>, "uint64 must be unsigned");

    // Pointer-sized aliases.
    static_assert(sizeof(SIZE_T)  == sizeof(void*), "SIZE_T must be pointer-sized");
    static_assert(sizeof(UPTRINT) == sizeof(void*), "UPTRINT must be pointer-sized");
    static_assert(sizeof(PTRINT)  == sizeof(void*), "PTRINT must be pointer-sized");

    // TCHAR / XCHAR are 1-byte UTF-8 chars; WIDECHAR is 2-byte UTF-16.
    static_assert(sizeof(TCHAR)    == 1, "TCHAR must be 1 byte (UTF-8)");
    static_assert(sizeof(XCHAR)    == 1, "XCHAR must be 1 byte");
    static_assert(sizeof(WIDECHAR) == 2, "WIDECHAR must be 2 bytes (UTF-16)");

    // INDEX_NONE is -1 and is int32.
    static_assert(INDEX_NONE == -1,          "INDEX_NONE must be -1");
    static_assert(sizeof(INDEX_NONE) == 4,   "INDEX_NONE must be int32");
} // anonymous

// Make this a linkable TU so the test runner can pick it up via the
// build system. The actual assertions are all static -- this main is
// purely a "the tests compiled" signal.
int main()
{
    return 0;
}
