// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XErrorTypes.h -- error-enum declarations (Rev 3 fix m1).
// =====================================================================
//
// XCore-4a Rev 3, Section 13.1 (fix Rev 3 m1).
//
// Every Result<T, E> error type used in the XCore-4a public surface
// is declared here so the alias declaration is the single source of
// truth. Each enum is a uint8_t for compact Result<T, E> storage
// (std::expected's discriminator + 1-byte error is 2 bytes on most
// ABIs). Sub-systems may add their own *_Error enums following the
// same pattern.
//
// Cross-references (Section 13.1 quotation):
//   FBoundsError       <- Section 5.1   TArray::At
//   FParseError        <- Section 11.1  FString::ToInt32 / ToInt64 / ToFloat / ToDouble
//   FStringError       <- Section 11.1  FString::CodepointAt
//   FDateRangeError    <- Section 7.5   FDateTime::FromUnixTimestamp / FromUnixMicros / ParseIso8601
//
// The enums live in namespace XCore (the user-facing namespace);
// the spec's Section 13 wording places them at "the XCore-4a public
// surface" without further nesting. Section 7.5's FDateTime sits in
// namespace XCore::HAL but takes FDateRangeError by name through a
// using-alias; XCore::FDateRangeError is the canonical declaration.
//
// =====================================================================

#include "Macros/XCoreTypes.h"

namespace XCore
{
    // -----------------------------------------------------------------
    // FParseError -- numeric / textual parse failures.
    //
    // FString::ToInt32 / ToInt64 / ToFloat / ToDouble return
    // Result<T, FParseError>. The four variants cover the realistic
    // failure modes; sub-systems that want richer parsing diagnostics
    // can layer their own *_Error enum on top.
    // -----------------------------------------------------------------
    enum class FParseError : ::uint8
    {
        Empty,        // input string was empty
        Malformed,    // syntactic error (e.g., non-digit in a numeric parse)
        Overflow,     // numeric value exceeds destination type's max
        Underflow,    // numeric value below destination type's min
    };

    // -----------------------------------------------------------------
    // FBoundsError -- container-accessor index errors.
    //
    // TArray::At returns Result<reference_wrapper<T>, FBoundsError>;
    // the C# `list[i]` lowering binds this via IL2CPP (Section 5.1
    // fix A-MIN3 promoted to MAJOR). The three variants distinguish
    // the realistic OOB failure modes so the C# IndexOutOfRangeException
    // message can be specific.
    // -----------------------------------------------------------------
    enum class FBoundsError : ::uint8
    {
        IndexNegative,    // negative index passed to a non-negative-indexed accessor
        IndexTooLarge,    // index >= Num()
        EmptyContainer,   // accessor on an empty container (e.g., Front() on empty TArray)
    };

    // -----------------------------------------------------------------
    // FStringError -- UTF-8 / codepoint-access errors.
    //
    // FString::CodepointAt returns Result<char32_t, FStringError>;
    // sim-path code is required to use CodepointAt rather than
    // operator[] (per Section 11.1 / locked decision 3). The three
    // variants distinguish the failure modes a sim-path TU may
    // legitimately see (malformed UTF-8 in a wire-format input,
    // out-of-range codepoint index, or an empty-string accessor
    // misuse).
    // -----------------------------------------------------------------
    enum class FStringError : ::uint8
    {
        InvalidUtf8,        // malformed UTF-8 byte sequence
        IndexOutOfRange,    // codepoint or byte index outside the string
        EmptyString,        // operation on an empty string (e.g., First() on "")
    };

    // -----------------------------------------------------------------
    // FDateRangeError -- date/time arithmetic failures (Section 7.5).
    //
    // FDateTime::FromUnixTimestamp / FromUnixMicros / ParseIso8601
    // all return Result<FDateTime, FDateRangeError>. The three
    // variants cover the realistic failure modes of the Howard-
    // Hinnant proleptic-Gregorian algorithm (year-bounds + invalid-
    // input).
    // -----------------------------------------------------------------
    enum class FDateRangeError : ::uint8
    {
        OverflowYear,           // arithmetic produced a year > the proleptic-Gregorian upper bound
        UnderflowYear,          // arithmetic produced a year < the proleptic-Gregorian lower bound
        InvalidUnixTimestamp,   // FromUnixTimestamp / FromUnixMicros input outside int64 range
    };

    // -----------------------------------------------------------------
    // ABI locks. All four enums are uint8-backed so Result<T, E> can
    // pack discriminator + error into 2 bytes (std::expected's
    // canonical layout when both T and E are trivially destructible
    // and the engine value is small).
    // -----------------------------------------------------------------
    static_assert(sizeof(FParseError)      == 1, "FParseError ABI lock: uint8 underlying");
    static_assert(sizeof(FBoundsError)     == 1, "FBoundsError ABI lock: uint8 underlying");
    static_assert(sizeof(FStringError)     == 1, "FStringError ABI lock: uint8 underlying");
    static_assert(sizeof(FDateRangeError)  == 1, "FDateRangeError ABI lock: uint8 underlying");

} // namespace XCore
