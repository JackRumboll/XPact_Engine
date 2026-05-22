// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XCoreTypes.h -- integer typedefs + size_t alias + INDEX_NONE.
// =====================================================================
//
// XCore-4a Rev 3, Section 13 (Header Macros) + cross-reference Section 1.2
// non-goals. This header is the lowest layer in the dependency graph
// (Section 3 step 1); every other XCore-4a header sits on top of it.
//
// Mirrors UE's CoreTypes.h shape (`int8`, `int16`, `int32`, `int64`,
// `uint8`, etc.) so existing XPact code that uses those aliases keeps
// compiling, but UTF-8-default: `TCHAR == char` (NOT `wchar_t`). UE's
// historical `TCHAR == wchar_t` is the source of the per-syscall
// transcoding tax that Section 11 of the spec eliminates (UTF-8
// throughout; UTF-16 only at the Win32 wide-char syscall boundary via
// the `FUTF16String` escape hatch in Section 11). For explicit
// narrow-char interop with Win32 ANSI APIs the `XCHAR` typedef is
// provided; sim-path code uses `TCHAR == char` exclusively.
//
// C# IL2CPP-friendly: every typedef resolves to a fixed-width
// `<cstdint>` type whose layout is identical across Win64-MSVC,
// Linux-Clang, Android-Clang-ARM64. Mirroring `int32_t` etc. directly
// (rather than `long` or `int`) makes the C# `[StructLayout(Sequential)]`
// marshalling rules trivial (`int32` <-> `System.Int32`).
//
// =====================================================================

#include <cstddef>
#include <cstdint>

// ---------------------------------------------------------------------
// Signed integer typedefs (Section 13 cross-reference; mirrors UE).
// ---------------------------------------------------------------------

using int8  = ::std::int8_t;
using int16 = ::std::int16_t;
using int32 = ::std::int32_t;
using int64 = ::std::int64_t;

// ---------------------------------------------------------------------
// Unsigned integer typedefs.
// ---------------------------------------------------------------------

using uint8  = ::std::uint8_t;
using uint16 = ::std::uint16_t;
using uint32 = ::std::uint32_t;
using uint64 = ::std::uint64_t;

// ---------------------------------------------------------------------
// Pointer-sized integral aliases. SIZE_T is the size_t alias UE code uses
// for byte counts and array indices; UPTRINT is the unsigned-integer
// pointer-bit-pattern type used at the boundary between containers and
// the allocator's raw VM addresses (Section 4).
// ---------------------------------------------------------------------

using SIZE_T  = ::std::size_t;
using PTRINT  = ::std::intptr_t;
using UPTRINT = ::std::uintptr_t;

// ---------------------------------------------------------------------
// Character typedefs (Section 11 cross-reference; UTF-8 throughout).
//
// TCHAR is `char` (NOT `wchar_t`). FString stores UTF-8 bytes; every
// FString character API operates over `char`. The Win32 wide-char
// escape hatch is `FUTF16String` (Section 11) which uses `char16_t`,
// NOT TCHAR.
//
// XCHAR is the explicit ANSI-narrow alias for Win32 *A-suffix APIs;
// kept as a separate name so a future re-targeting of TCHAR to a UTF-32
// path would not silently break the Win32 ANSI bridge.
// ---------------------------------------------------------------------

using TCHAR = char;
using XCHAR = char;
using WIDECHAR = char16_t;  // explicit-UTF-16 alias; sim-path-banned indirectly via FUTF16String

// ---------------------------------------------------------------------
// INDEX_NONE: the sentinel returned by container search-by-value APIs
// to indicate "not found". Mirrors UE; signed because callers test with
// `< 0` and a signed compare avoids the size_t-vs-(-1) compare warning.
// ---------------------------------------------------------------------

inline constexpr int32 INDEX_NONE = -1;

// ---------------------------------------------------------------------
// ABI locks. The sizeof asserts here are the load-bearing guarantees
// for downstream C#-interop: the IL2CPP layer assumes `int32 == 4
// bytes` and similar on every platform. Failing this assert is a build
// stop, never a runtime surprise.
// ---------------------------------------------------------------------

static_assert(sizeof(int8)   == 1, "int8 ABI lock: must be 1 byte");
static_assert(sizeof(int16)  == 2, "int16 ABI lock: must be 2 bytes");
static_assert(sizeof(int32)  == 4, "int32 ABI lock: must be 4 bytes");
static_assert(sizeof(int64)  == 8, "int64 ABI lock: must be 8 bytes");
static_assert(sizeof(uint8)  == 1, "uint8 ABI lock: must be 1 byte");
static_assert(sizeof(uint16) == 2, "uint16 ABI lock: must be 2 bytes");
static_assert(sizeof(uint32) == 4, "uint32 ABI lock: must be 4 bytes");
static_assert(sizeof(uint64) == 8, "uint64 ABI lock: must be 8 bytes");
static_assert(sizeof(SIZE_T)  == sizeof(void*), "SIZE_T ABI lock: must be pointer-sized");
static_assert(sizeof(UPTRINT) == sizeof(void*), "UPTRINT ABI lock: must be pointer-sized");
static_assert(sizeof(PTRINT)  == sizeof(void*), "PTRINT ABI lock: must be pointer-sized");
static_assert(sizeof(TCHAR)  == 1, "TCHAR ABI lock: UTF-8 char (1 byte)");
static_assert(sizeof(WIDECHAR) == 2, "WIDECHAR ABI lock: UTF-16 char (2 bytes)");
