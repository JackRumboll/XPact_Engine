// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FNameForwardDecl.cpp -- ABI lock for the forward-declared FName.
// =====================================================================
//
// XCore-4a Section 1.2 + 11.8 (fix C-1). FName is forward-declared by
// XCore-4a so containers can compile a TMap<FName, V> without taking
// a circular dependency on XCore-4b. The 8-byte handle layout is the
// load-bearing ABI lock between the two systems.
//
// Per XCore-4b Rev 3 §4.1, the COMPLETE FName struct (with the public
// API: ctors, ToString, comparison, etc.) lives in Reflection/FName.h
// and supersedes the prior XCore-4a in-place declaration. This test
// pulls in Reflection/FName.h to verify the layout invariants the
// XCore-4b implementation must honor.
//
// This test asserts:
//   * sizeof(FName) == 8
//   * alignof(FName) == 4
//   * The two field offsets are 0 and 4 (Index first, SerialNumber
//     second; both uint32).
//
// Field offset checks are stronger than just sizeof: a future
// "optimization" that re-ordered the fields would still pass sizeof
// + alignof but would silently flip the meaning of the bytes on the
// XCore-4b side. The offset check pins the wire-format contract.
//
// =====================================================================

#include "Macros/XCoreFwd.h"
#include "Reflection/FName.h"

#include <cstddef>     // offsetof
#include <type_traits>

namespace
{
    using ::XCore::Reflect::FName;

    // Section 11.8 ABI lock -- migrated from the spec body inline.
    static_assert(sizeof(FName)  == 8, "FName ABI lock: must be 8 bytes");
    static_assert(alignof(FName) == 4, "FName ABI lock: 4-byte alignment");

    // Field-offset ABI lock. offsetof requires a standard-layout type;
    // FName is trivially-standard-layout because it has only two
    // public uint32_t members and no virtuals / inheritance.
    // `offsetof` from <cstddef> is the portable spelling (works on
    // MSVC + Clang + GCC); `__builtin_offsetof` is the underlying
    // builtin but only Clang/GCC expose it as a named keyword.
    static_assert(offsetof(FName, Index)        == 0,
                  "FName.Index must be at offset 0");
    static_assert(offsetof(FName, SerialNumber) == 4,
                  "FName.SerialNumber must be at offset 4");

    // Field-type ABI lock. The members must be uint32 specifically
    // (not int32, not size_t); the FName interning table indexes a
    // 4-billion-entry slot table, and the serial number is a 4-billion
    // recycle counter. Changing either to size_t would inflate the
    // handle to 16 bytes on 64-bit targets.
    static_assert(sizeof(FName::Index)        == 4, "FName.Index must be 4 bytes (uint32)");
    static_assert(sizeof(FName::SerialNumber) == 4, "FName.SerialNumber must be 4 bytes (uint32)");

    // The type is trivially copyable so it can be passed by value
    // through extern "C" boundaries without ABI surprises.
    static_assert(::std::is_trivially_copyable_v<FName>,    "FName must be trivially copyable");
    static_assert(::std::is_trivially_destructible_v<FName>, "FName must be trivially destructible");
    static_assert(::std::is_standard_layout_v<FName>,        "FName must be standard layout");
} // anonymous

int main()
{
    return 0;
}
