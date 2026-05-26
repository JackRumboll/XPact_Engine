// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FString.h -- UTF-8 native string class (Section 11.1).
// =====================================================================
//
// XCore-4a Rev 3, Section 11.1 (Public API -- FString) +
// Section 11.1.3 (Sim-path safety) + Section 11.1.4 (Discriminator
// encapsulation) + Section 11.1.5 (XCSharpString IL2CPP handle) +
// Section 1.3 locked decisions 3 (LenBytes / LenCodepoints) and 4
// (XMathFast vs XSimMath -- not in scope here but informs format
// locale handling).
//
// FString is XPact's UTF-8 string type. It supersedes UE's
// FString = TArray<TCHAR> entirely:
//   * UTF-8 throughout (UE was UTF-16 wide-char).
//   * 64-byte single-cache-line struct (UE was a 16-byte heap pointer).
//   * 47-byte SSO inline buffer for short strings.
//   * No operator[] (deliberate per spec; force the user between
//     ByteAt() / CodepointAt()).
//   * No Len() (locked decision 3; LenBytes vs LenCodepoints only).
//   * Result<T, FParseError>-returning parsers (no exceptions).
//
// LAYOUT (locked at sizeof == 64, alignof == 16 by static_assert at
// the bottom of this file; the static_assert IS the ABI contract).
//
// Heap mode (high bit of byte 63 == 0):
//   bytes 0-7   m_data       (char*) UTF-8 buffer pointer (not null-terminated)
//   bytes 8-15  m_byteLen    (int64_t) byte length
//   bytes 16-23 m_byteCap    (int64_t) byte capacity
//   bytes 24-62 reserved     (cached codepoint count, etc.; zero-filled)
//   byte  63    discriminator (high bit = 0; low 7 reserved)
//
// SSO mode (high bit of byte 63 == 1):
//   bytes 0-46  inline UTF-8 bytes (47 usable)
//   bytes 47-62 reserved (zero-filled at construction)
//   byte  63    discriminator (high bit = 1; low 7 bits = byte length 0..47)
//
// The two modes are an exclusive XOR over the same 64-byte storage;
// there is NO "shadow" overlap (the Rev 2 framing was a documentation
// bug, re-derived cleanly in Rev 3 fix M2). IsInSso() is a PRIVATE
// helper used only inside this class (and by the IL2CPP transpiler
// per Section 11.1.5); user code never observes the discriminator.
//
// SIM-PATH SAFETY (Section 11.1.3 table; per-method documentation in
// the method banners below):
//
//   Sim-path-safe (deterministic byte-level UTF-8):
//     LenBytes, LenCodepoints, ByteAt, CodepointAt, Codepoints(),
//     Substr, Append, Replace, Split, operator+ / operator+=,
//     Trim / TrimStart / TrimEnd (ASCII whitespace only),
//     StartsWith, EndsWith,
//     IndexOfByte / IndexOfCodepoint / IndexOf,
//     LastIndexOfByte / LastIndexOfCodepoint,
//     Equals, operator== / operator!=,
//     ToInt32 / ToInt64 / ToFloat / ToDouble (via std::from_chars; C locale),
//     FromInt32 / FromInt64 / FromFloat / FromDouble (via std::to_chars; C locale),
//     FormatFixed (std::format_to_n with fixed C locale),
//     ToUtf8Cstr / ToUtf8Ptr.
//
//   NOT sim-path-safe:
//     Format (locale-dependent format specifiers admit divergence;
//             the sim-path overlay header XSimPathMathOverrides.h
//             attaches [[deprecated]] for sim-path TUs).
//
// =====================================================================

#include "Macros/XCoreTypes.h"        // int32, SIZE_T, INDEX_NONE
#include "Macros/XPactMacros.h"       // XPACT_FORCEINLINE, XPACT_CHECK
#include "Macros/XErrorTypes.h"       // FParseError, FStringError
#include "Macros/XResult.h"           // Result<T, E>, Unexpected
#include "Macros/XCoreFwd.h"          // FString forward decl (already there)
#include "HAL/FMemory.h"              // FMemory::MallocOrAbort / Free
#include "HAL/FMemTag.h"              // FMemTag::Localization (FString fallback) / Container
#include "HAL/FormatString.h"         // XCore::HAL::FormatString<Args...> alias (Section 11.1 fix Rev 3 M4)

#include <cstddef>                    // std::byte
#include <cstring>                    // std::memcpy, std::memmove, std::strlen
#include <format>                     // std::vformat / std::make_format_args / std::format_to_n
#include <string>                     // std::string (return-type of std::vformat)
#include <type_traits>                // std::is_same_v
#include <utility>                    // std::forward

// Forward declare TArray<FString> for Split's return type. The full
// TArray template definition is intentionally NOT included here;
// per Section 5.1 fix C-3 ("the public TArray header takes only
// const char* in its diagnostic paths; it NEVER includes
// FString.h"), the cycle resolution puts the FString-returning
// surface ON the FString side. Consumers that actually call Split
// must include Containers/TArray.h alongside Containers/FString.h.
namespace XCore
{
    template<typename T, typename AllocatorT> class TArray;
    class DefaultAllocator;
}

namespace XCore
{

// -------------------------------------------------------------------
// Forward declaration of XCSharpString so FString can name it as a
// friend (per Section 11.1.5 / Section 11.1.4 IsInSso encapsulation;
// the IL2CPP transpiler accesses IsInSso to lay out interned
// literals).
// -------------------------------------------------------------------
struct XCSharpString;

// =====================================================================
// FString -- UTF-8 native string class.
// =====================================================================

class alignas(16) FString
{
public:
    // -------------------------------------------------------------
    // Lifetime.
    // -------------------------------------------------------------

    // Default ctor: zero-length SSO string.
    XPACT_FORCEINLINE FString() noexcept
    {
        InitEmpty();
    }

    // Construct from null-terminated UTF-8 C-string.
    // The length is computed via std::strlen; the input is assumed
    // to be valid UTF-8 (validity-checking is a debug-only concern;
    // malformed input survives but CodepointAt may return
    // FStringError::InvalidUtf8).
    explicit FString(const char* Utf8) noexcept;

    // Construct from UTF-8 byte buffer + explicit byte length.
    // Bytes are copied verbatim; ByteLen may be zero.
    FString(const char* Utf8, ::int32 ByteLen) noexcept;

    // Copy ctor: deep copy.
    FString(const FString& Other);

    // Move ctor: transfer ownership; leave Other in valid empty state.
    FString(FString&& Other) noexcept;

    // Destructor: frees heap buffer if in heap mode.
    ~FString() noexcept;

    // Copy assign.
    FString& operator=(const FString& Other);

    // Move assign.
    FString& operator=(FString&& Other) noexcept;

    // Assign from null-terminated C-string.
    FString& operator=(const char* Utf8) noexcept;

    // -------------------------------------------------------------
    // Length (locked decision 3: NO Len(); two explicit names).
    // -------------------------------------------------------------

    // O(1) byte count. Sim-path-safe.
    [[nodiscard]] XPACT_FORCEINLINE ::int32 LenBytes() const noexcept
    {
        return IsInSso()
            ? static_cast<::int32>(GetSsoLen())
            : static_cast<::int32>(GetHeap().m_byteLen);
    }

    // Codepoint count. O(n) on first call; cached for subsequent
    // calls. Cache is invalidated on any mutation. Sim-path-safe
    // (pure UTF-8 byte walk; no locale or platform divergence).
    [[nodiscard]] ::int32 LenCodepoints() const noexcept;

    // O(1) emptiness check.
    [[nodiscard]] XPACT_FORCEINLINE bool IsEmpty() const noexcept
    {
        return LenBytes() == 0;
    }

    // -------------------------------------------------------------
    // Element access (Section 11.1; no operator[] by design).
    // -------------------------------------------------------------

    // O(1) byte access. Caller discipline (Debug-only bounds check).
    // Sim-path-safe.
    [[nodiscard]] XPACT_FORCEINLINE char ByteAt(::int32 ByteIdx) const noexcept
    {
        XPACT_CHECK(ByteIdx >= 0 && ByteIdx < LenBytes());
        return Data()[ByteIdx];
    }

    // O(n) codepoint access. Returns Result so malformed UTF-8 and
    // OOB don't silently corrupt. Sim-path-safe.
    [[nodiscard]] Result<char32_t, FStringError> CodepointAt(::int32 CpIdx) const noexcept;

    // -------------------------------------------------------------
    // Iteration over codepoints (range-for support).
    //
    // Usage:
    //   for (char32_t Cp : MyString) { ... }
    //
    // FCodepointIter is the only public iterator; ByteAt remains
    // the byte-level access form. The iterator returns
    // U+FFFD (REPLACEMENT CHARACTER) on malformed UTF-8 sequences
    // rather than aborting -- the Robustness Principle ("be liberal
    // in what you accept") applies to display surfaces; sim-path
    // TUs that need to detect malformed input should use
    // CodepointAt explicitly.
    // -------------------------------------------------------------

    class FCodepointIter
    {
    public:
        constexpr FCodepointIter() noexcept = default;

        FCodepointIter(const char* Data, ::int32 ByteOffset, ::int32 ByteEnd) noexcept
            : m_data(Data), m_byteOffset(ByteOffset), m_byteEnd(ByteEnd) {}

        // Dereference: decode codepoint at current byte offset.
        // Returns U+FFFD on malformed sequence.
        [[nodiscard]] char32_t operator*() const noexcept;

        // Advance to next codepoint. Steps by 1..4 bytes depending
        // on the lead byte; steps by 1 on malformed sequence to
        // guarantee progress.
        FCodepointIter& operator++() noexcept;

        // Equality: two iterators are equal iff they point at the
        // same byte offset of the same underlying buffer.
        [[nodiscard]] XPACT_FORCEINLINE bool operator==(const FCodepointIter& Other) const noexcept
        {
            return m_data == Other.m_data && m_byteOffset == Other.m_byteOffset;
        }

        [[nodiscard]] XPACT_FORCEINLINE bool operator!=(const FCodepointIter& Other) const noexcept
        {
            return !(*this == Other);
        }

    private:
        const char* m_data       = nullptr;
        ::int32     m_byteOffset = 0;
        ::int32     m_byteEnd    = 0;
    };

    // Codepoint iterator -- spec wording "FCodepointIter Codepoints()".
    [[nodiscard]] XPACT_FORCEINLINE FCodepointIter Codepoints() const noexcept
    {
        return begin();
    }

    // Range-for begin / end -- returns FCodepointIter so
    // `for (char32_t Cp : MyString)` works.
    [[nodiscard]] XPACT_FORCEINLINE FCodepointIter begin() const noexcept
    {
        return FCodepointIter(Data(), 0, LenBytes());
    }

    [[nodiscard]] XPACT_FORCEINLINE FCodepointIter end() const noexcept
    {
        const ::int32 N = LenBytes();
        return FCodepointIter(Data(), N, N);
    }

    // -------------------------------------------------------------
    // Substring (codepoint-counted).
    //
    // Returns the slice [StartCp .. StartCp + LenCp). Out-of-range
    // arguments produce an empty string (graceful degradation; the
    // sim-path-strict CodepointAt is for surfaces that need an
    // error). Sim-path-safe.
    // -------------------------------------------------------------
    [[nodiscard]] FString Substr(::int32 StartCp, ::int32 LenCp) const;

    // -------------------------------------------------------------
    // Append + concatenation (Section 11.1 fix M-6, B-M4).
    //
    // All Append paths grow the buffer (with SSO -> heap promotion
    // when needed) and copy the source bytes verbatim. The cached
    // codepoint count is invalidated on any mutation.
    //
    // Sim-path-safe.
    // -------------------------------------------------------------

    FString& Append(const FString& Other);
    FString& Append(const char* Utf8);
    FString& Append(const char* Utf8, ::int32 ByteLen);

    [[nodiscard]] FString  operator+ (const FString& Other) const;
    FString& operator+=(const FString& Other);

    // -------------------------------------------------------------
    // Mutation / search (Section 11.1 fix M-6).
    // -------------------------------------------------------------

    // Return a new string with every Needle replaced by Replacement.
    // Byte-level replacement (works correctly on UTF-8 because
    // valid UTF-8 sequences never overlap each other at byte level).
    // Sim-path-safe.
    [[nodiscard]] FString Replace(const FString& Needle, const FString& Replacement) const;

    // Split on Delimiter. Returns a TArray<FString>. Empty segments
    // are preserved (so "a,,b" splits to ["a", "", "b"]). Caller
    // must #include "Containers/TArray.h" to use the return type.
    // Sim-path-safe.
    [[nodiscard]] TArray<FString, DefaultAllocator> Split(const FString& Delimiter) const;

    // Trim ASCII whitespace (space, tab, CR, LF) from both ends /
    // start / end. Non-ASCII whitespace passes through. Sim-path-safe.
    [[nodiscard]] FString Trim()      const;
    [[nodiscard]] FString TrimStart() const;
    [[nodiscard]] FString TrimEnd()   const;

    // -------------------------------------------------------------
    // UE-parity surface additions (Rev 3 Round 2 audit FIX-R2-MED-NEW-3).
    //
    // ASCII-only case conversion and padding helpers. Unicode-aware
    // case conversion (locale-correct upper / lower / title) is
    // deferred to the ICU integration layer (post-Phase 1d); these
    // ASCII variants are locale-independent and sim-path-safe.
    //
    // The case-conversion methods are sim-path-safe because they
    // touch only bytes in 'A'..'Z' / 'a'..'z' and operate by
    // direct byte arithmetic (no locale-dependent ICU calls). Bytes
    // outside that range (UTF-8 continuation bytes, other code-
    // points) pass through unchanged.
    // -------------------------------------------------------------

    // ToUpper / ToLower -- ASCII case conversion.
    // UE-equivalent: UnrealString.h.inl:1332 ToUpper() (UE uses
    // FChar::ToUpper which is locale-aware; XPact's variant is
    // explicitly ASCII-only per the locked locale-independence
    // contract). UE-Unicode-correct variants land in a future ICU
    // integration layer (XLocalization).
    [[nodiscard]] FString ToUpper() const;
    [[nodiscard]] FString ToLower() const;

    // LeftPad / RightPad -- pad to a target byte length with PadChar.
    //
    // Note: byte length, not codepoint length. Multi-byte UTF-8 input
    // strings already past TargetLength bytes are returned unchanged.
    // PadChar MUST be an ASCII byte (high bit clear); a non-ASCII pad
    // byte would produce malformed UTF-8 at the seam.
    [[nodiscard]] FString LeftPad (::int32 TargetLength, char PadChar = ' ') const;
    [[nodiscard]] FString RightPad(::int32 TargetLength, char PadChar = ' ') const;

    // Reverse -- byte-level reversal.
    //
    // Reverses the BYTES of the string. For pure-ASCII strings this
    // is the visually-expected reversal. For UTF-8 strings carrying
    // multi-byte codepoints, byte-level reversal CORRUPTS the
    // sequence (e.g., a 3-byte CJK character reverses into 3 invalid
    // bytes). The codepoint-correct reversal that walks codepoints
    // forward and emits them in reverse is a future addition; this
    // ASCII-correct variant matches the byte-level semantics UE's
    // FString::Reverse provides (UE FString is UCS-2 / UTF-16 internal,
    // so its reverse is also code-unit-level).
    [[nodiscard]] FString Reverse() const;

    // RemoveFromStart / RemoveFromEnd -- conditional prefix/suffix removal.
    //
    // Returns a new FString with the prefix (suffix) removed if it
    // matches; returns *this unchanged if it does not. Byte-exact
    // match per the StartsWith / EndsWith contract.
    //
    // The Prefix / Suffix parameter is taken as const FString& rather
    // than the requested FStringView because XCore-4a does not ship
    // FStringView (deferred to Phase 1e); the FString form is the
    // closest semantic equivalent.
    [[nodiscard]] FString RemoveFromStart(const FString& Prefix) const;
    [[nodiscard]] FString RemoveFromEnd  (const FString& Suffix) const;

    // JoinBy -- inverse of Split.
    //
    // Concatenates every element of Parts with Separator between them.
    // Empty Parts yields the empty string; single-element Parts yields
    // the element unchanged. UE-equivalent: FString::Join (UE name).
    //
    // The TArray parameter is const& and we DO NOT include TArray.h
    // here (avoiding the cycle per §5.1 fix C-3); the declaration uses
    // a forward declaration. Callers must include both FString.h and
    // TArray.h.
    [[nodiscard]] static FString JoinBy(const TArray<FString, DefaultAllocator>& Parts,
                                        const FString& Separator);

    // -------------------------------------------------------------
    // Affix tests (Section 11.1).
    // -------------------------------------------------------------

    [[nodiscard]] bool StartsWith(const FString& Needle) const noexcept;
    [[nodiscard]] bool EndsWith  (const FString& Needle) const noexcept;

    // -------------------------------------------------------------
    // Search (Section 11.1 fix M-6).
    // -------------------------------------------------------------

    // Byte-level search. Returns INDEX_NONE if not found.
    [[nodiscard]] ::int32 IndexOfByte(char Needle) const noexcept;
    [[nodiscard]] ::int32 IndexOfByte(char Needle, ::int32 StartByteIdx) const noexcept;

    // Codepoint-level search. O(n) walk; returns codepoint INDEX
    // (not byte offset). Returns INDEX_NONE if not found.
    [[nodiscard]] ::int32 IndexOfCodepoint(char32_t Needle) const noexcept;

    // Substring search via Boyer-Moore-Horspool. Returns the BYTE
    // OFFSET of the first occurrence (since FString is UTF-8 and
    // BMH operates on bytes; a codepoint-counted variant could
    // ship in a future revision). Returns INDEX_NONE if not found.
    [[nodiscard]] ::int32 IndexOf(const FString& Needle) const noexcept;

    // Last-occurrence variants.
    [[nodiscard]] ::int32 LastIndexOfByte        (char     Needle) const noexcept;
    [[nodiscard]] ::int32 LastIndexOfByte        (const FString& Needle) const noexcept;
    [[nodiscard]] ::int32 LastIndexOfCodepoint   (char32_t Needle) const noexcept;

    // Compatibility alias: the byte single-char form is named
    // LastIndexOf in some legacy spots; the new explicit name is
    // LastIndexOfByte (per Section 11.1 table). The byte form keeps
    // a thin forwarder so older call sites compile during the
    // migration window.
    [[nodiscard]] XPACT_FORCEINLINE ::int32 LastIndexOf(char Needle) const noexcept
    {
        return LastIndexOfByte(Needle);
    }

    // -------------------------------------------------------------
    // Equality (Section 11.1).
    //
    // Byte-exact comparison. Two strings compare equal iff their
    // byte sequences are bit-identical. Encoding-equivalent but
    // byte-distinct strings (e.g., NFC vs NFD Unicode normalisation
    // forms) are NOT considered equal -- Unicode-normalised
    // comparison is a future layer that builds on ICU (Section
    // 11.1.5).
    //
    // operator== is the default form (spec-compliant: a defaulted
    // operator== with `= default` would not be byte-level because
    // it would member-wise compare the storage; we provide an
    // explicit body that does the byte-exact compare).
    // -------------------------------------------------------------

    [[nodiscard]] bool Equals(const FString& Other) const noexcept;

    [[nodiscard]] XPACT_FORCEINLINE bool operator==(const FString& Other) const noexcept
    {
        return Equals(Other);
    }

    [[nodiscard]] XPACT_FORCEINLINE bool operator!=(const FString& Other) const noexcept
    {
        return !Equals(Other);
    }

    // -------------------------------------------------------------
    // Parse (Section 11.1 fix M-6).
    //
    // Uses std::from_chars with C locale (locale-independent by
    // standard). Sim-path-safe. Returns Result so caller is forced
    // to handle Empty / Malformed / Overflow / Underflow.
    // -------------------------------------------------------------

    [[nodiscard]] Result<::int32,  FParseError> ToInt32()  const noexcept;
    [[nodiscard]] Result<::int64,  FParseError> ToInt64()  const noexcept;
    [[nodiscard]] Result<float,    FParseError> ToFloat()  const noexcept;
    [[nodiscard]] Result<double,   FParseError> ToDouble() const noexcept;

    // -------------------------------------------------------------
    // Construction from primitive (Section 11.1).
    //
    // Uses std::to_chars with C locale; bit-exact across platforms.
    // Sim-path-safe.
    // -------------------------------------------------------------

    [[nodiscard]] static FString FromInt32 (::int32  Value);
    [[nodiscard]] static FString FromInt64 (::int64  Value);
    [[nodiscard]] static FString FromFloat (float    Value);
    [[nodiscard]] static FString FromDouble(double   Value);

    // -------------------------------------------------------------
    // Format -- compile-time-checked format-string facility
    // (Section 11.1 fix Rev 3 M4).
    //
    //   * Format      -- locale-defaulted; NOT sim-path-safe (the
    //                    {:L} grouping specifier admits divergence
    //                    across glibc / MSVC / Bionic locales).
    //                    Sim-path TUs see a [[deprecated]] decoration
    //                    via the sim-path overlay header.
    //   * FormatFixed -- C-locale-fixed; sim-path-safe; bit-exact
    //                    across platforms for {:f}, {:e}, {:g}, etc.
    //
    // Both templates are declared in the header so compile-time-bad
    // format strings fail at the call site (the FormatString<Args...>
    // parameter is std::format_string<Args...> which performs the
    // compile-time check). The bodies dispatch via std::vformat /
    // std::format_to_n to non-template helpers in FStringFormat.cpp
    // for the actual implementation.
    // -------------------------------------------------------------

    template<typename... Args>
    [[nodiscard]] static FString Format(::XCore::HAL::FormatString<Args...> Fmt, Args&&... Vs)
    {
        // std::vformat performs the locale-dependent formatting
        // (uses the thread-local locale; can be set via
        // std::locale::global). Returns std::string with UTF-8 bytes
        // when the source format string was UTF-8.
        ::std::string Out = ::std::vformat(
            Fmt.get(),
            ::std::make_format_args(Vs...));
        return FString(Out.data(), static_cast<::int32>(Out.size()));
    }

    template<typename... Args>
    [[nodiscard]] static FString FormatFixed(::XCore::HAL::FormatString<Args...> Fmt, Args&&... Vs) noexcept
    {
        // FormatFixed uses std::format_to_n with the imbued C locale
        // for sim-path bit-exactness. Implementation strategy:
        //
        //   1. Pre-flight: format to a small stack buffer with
        //      format_to_n to learn the required size.
        //   2. If the result fits in the stack buffer, return.
        //   3. Otherwise allocate via FMemory and re-emit.
        //
        // The C-locale guarantee comes from passing an explicit
        // locale-less format_to_n (std::format_to_n is locale-
        // independent by default; the locale-aware behaviour is
        // opt-in via the {:L} specifier which sim-path TUs forbid via
        // the lints in §11.1.3). Bit-exactness across platforms is
        // pinned by the standard for the basic {:f}, {:e}, {:g}
        // specifiers (P0067R5 + N4885 §28.5.2.4).
        //
        // The implementation re-uses the std::vformat path because
        // std::vformat's specification is locale-defaulted; the
        // sim-path discipline is enforced at the lint layer (the
        // overlay header bans the {:L} specifier and certain locale-
        // aware functions). For Phase 1g we share the body with
        // Format -- the user-visible difference is the [[deprecated]]
        // applied to Format (not FormatFixed) in sim-path TUs.

        // The body is wrapped in a try/catch-free path: std::vformat
        // can throw std::format_error on internal-state issues that
        // should never happen with a compile-time-checked format
        // string. We funnel any throw into a defensive abort path in
        // FormatFixedRuntime so the noexcept contract is honoured.
        return FormatFixedRuntime(
            ::std::string_view(Fmt.get()),
            ::std::make_format_args(Vs...));
    }

private:
    // Non-template helper for FormatFixed; defined in FStringFormat.cpp
    // so the noexcept contract is honoured by funneling the
    // std::vformat throw path to an abort. The std::format_args type
    // is opaque + non-template; making the helper non-template lets
    // the body live in the .cpp.
    [[nodiscard]] static FString FormatFixedRuntime(
        ::std::string_view Fmt,
        ::std::format_args Args) noexcept;

public:

    // -------------------------------------------------------------
    // C-string accessors (Section 11.1).
    //
    // ToUtf8Cstr -- guarantees null-termination. May ALLOCATE to
    //               append a null terminator (the storage is NOT
    //               null-terminated by default). Sim-path-safe.
    // ToUtf8Ptr  -- raw pointer to the storage; NOT null-terminated.
    //               O(1). Sim-path-safe.
    // -------------------------------------------------------------

    [[nodiscard]] const char* ToUtf8Cstr() const noexcept;
    [[nodiscard]] XPACT_FORCEINLINE const char* ToUtf8Ptr() const noexcept
    {
        return Data();
    }

private:
    // =================================================================
    // Storage (Section 11.1.4 fix Rev 3 M2 re-derivation).
    //
    // 64-byte aligned-to-16 union over SSO and heap layouts. The
    // discriminator is the high bit of byte 63. We use raw std::byte
    // and access via reinterpret_cast helpers because:
    //   * A union with explicit struct members would require the
    //     compiler to commit to one layout or the other; the spec
    //     mandates a 64-byte storage with the discriminator bit
    //     overlapping in either mode, which is cleaner as raw bytes.
    //   * std::byte is the standard "raw memory" type and is the
    //     most defensible vs strict-aliasing rules (per
    //     [basic.lval]/11: std::byte may alias any object).
    //
    // The storage MUST be alignas(16) -- the spec ABI lock.
    // =================================================================

    static constexpr ::int32 kStorageSize       = 64;
    static constexpr ::int32 kSsoMaxBytes       = 47;
    static constexpr ::int32 kDiscriminatorByte = 63;
    static constexpr ::uint8 kSsoFlagBit        = 0x80;
    static constexpr ::uint8 kSsoLenMask        = 0x7F;

    alignas(16) ::std::byte m_storage[kStorageSize];

    // -----------------------------------------------------------------
    // Heap representation layout. Reinterpret-cast over m_storage when
    // !IsInSso(); the byte 63 marker on m_storage signals which mode.
    // -----------------------------------------------------------------
    struct FHeapRep
    {
        char*   m_data;       // bytes 0-7
        ::int64 m_byteLen;    // bytes 8-15
        ::int64 m_byteCap;    // bytes 16-23
        // bytes 24..62 are reserved (cached codepoint count etc.; the
        // FHeapRep struct only models the load-bearing prefix; the
        // discriminator byte 63 is accessed via m_storage[63] directly).
    };

    static_assert(sizeof(FHeapRep) <= 24, "FHeapRep load-bearing prefix must fit in bytes 0-23");

    // -----------------------------------------------------------------
    // Private helpers -- discriminator + accessor + grow protocol.
    // Per Section 11.1.4 IsInSso is PRIVATE.
    // -----------------------------------------------------------------

    XPACT_FORCEINLINE ::uint8& Discriminator() noexcept
    {
        return reinterpret_cast<::uint8&>(m_storage[kDiscriminatorByte]);
    }

    XPACT_FORCEINLINE ::uint8 Discriminator() const noexcept
    {
        return static_cast<::uint8>(m_storage[kDiscriminatorByte]);
    }

    // Section 11.1.4: private helper. The IL2CPP transpiler accesses
    // it as a friend (declared at the bottom of the class body) for
    // the interned-literal layout-stability check.
    [[nodiscard]] XPACT_FORCEINLINE bool IsInSso() const noexcept
    {
        return (Discriminator() & kSsoFlagBit) != 0;
    }

    XPACT_FORCEINLINE ::uint8 GetSsoLen() const noexcept
    {
        return static_cast<::uint8>(Discriminator() & kSsoLenMask);
    }

    XPACT_FORCEINLINE void SetSsoLen(::uint8 LenBytes) noexcept
    {
        // Preserve the SSO flag bit; replace the length bits.
        Discriminator() = static_cast<::uint8>(kSsoFlagBit | (LenBytes & kSsoLenMask));
    }

    // Heap-rep accessor. The reinterpret_cast is safe because
    // FHeapRep is a standard-layout type and m_storage is std::byte.
    XPACT_FORCEINLINE FHeapRep& GetHeap() noexcept
    {
        return *reinterpret_cast<FHeapRep*>(&m_storage[0]);
    }

    XPACT_FORCEINLINE const FHeapRep& GetHeap() const noexcept
    {
        return *reinterpret_cast<const FHeapRep*>(&m_storage[0]);
    }

    // Data pointer accessor -- abstracts the mode.
    XPACT_FORCEINLINE char* Data() noexcept
    {
        return IsInSso()
            ? reinterpret_cast<char*>(&m_storage[0])
            : GetHeap().m_data;
    }

    XPACT_FORCEINLINE const char* Data() const noexcept
    {
        return IsInSso()
            ? reinterpret_cast<const char*>(&m_storage[0])
            : GetHeap().m_data;
    }

    // -----------------------------------------------------------------
    // Construction helpers.
    // -----------------------------------------------------------------

    // Initialise to empty SSO. Called by default ctor; reused by
    // moves/operator= sites that need to reset state.
    void InitEmpty() noexcept
    {
        // Zero-fill the reserved bytes per spec ("undefined / reserved;
        // zero-filled at construction"). Then set the SSO flag with
        // length 0.
        for (::int32 I = 0; I < kStorageSize; ++I)
        {
            m_storage[I] = ::std::byte{0};
        }
        Discriminator() = kSsoFlagBit;  // SSO with length 0
    }

    // Initialise as a fresh heap allocation of NewCap bytes.
    // Does NOT initialise the bytes; caller must follow up with a
    // memcpy. m_byteLen is set to NewLen (caller's responsibility
    // that NewLen <= NewCap).
    void InitHeap(::int32 NewLen, ::int32 NewCap) noexcept;

    // Common assignment-from-buffer path. Handles both SSO and heap
    // selection based on Len.
    void AssignFromBuffer(const char* Src, ::int32 SrcLen) noexcept;

    // Grow the buffer to accommodate at least RequiredLen bytes,
    // promoting SSO to heap if needed. Post-condition: the buffer
    // is at least RequiredLen bytes wide; m_byteLen is unchanged
    // until caller updates it.
    void EnsureCapacity(::int32 RequiredLen) noexcept;

    // -----------------------------------------------------------------
    // UTF-8 codepoint helpers (header-private; declared in the .cpp).
    //
    // DecodeCodepoint: advances *ByteOffset past one codepoint;
    // returns U+FFFD on malformed bytes (Robustness Principle).
    // CountCodepoints: walks the buffer and tallies codepoints.
    // -----------------------------------------------------------------

    static char32_t DecodeCodepoint(const char* Data, ::int32 ByteOffset, ::int32 ByteEnd, ::int32& AdvanceBytes) noexcept;
    static ::int32  CountCodepoints(const char* Data, ::int32 ByteLen) noexcept;
    static ::int32  CodepointToByteOffset(const char* Data, ::int32 ByteLen, ::int32 CpIdx) noexcept;

    // -----------------------------------------------------------------
    // Allocator-tag helper. FString lives at the boundary between
    // user-facing strings (Localization tag fits) and engine
    // internals (Container tag fits). Section 4.1 + Section 11.1
    // implicitly tag FString allocations as Container (string is a
    // container of bytes). FText / loctable paths use Localization
    // tag explicitly when they construct via the FString(const char*)
    // ctor.
    // -----------------------------------------------------------------

    static constexpr ::XCore::HAL::FMemTag kAllocTag = ::XCore::HAL::FMemTag::Container;

    // -----------------------------------------------------------------
    // IL2CPP-side friend: per Section 11.1.5, XIL2CPP emits
    // interned-literal FString objects and needs IsInSso for the
    // layout-stability check. The friendship is forward-declared in
    // a sentinel form: the actual XIL2CPP namespace lives in
    // /Engine/Source/Programs/XIL2CPP/ which XCore-4a does not see;
    // the friend declaration here is the contract. (The friend
    // pattern is preferred over making IsInSso public because the
    // spec says IsInSso "is not part of the public surface".)
    // -----------------------------------------------------------------
    friend struct ::XCore::XCSharpString;
};

// =====================================================================
// XCSharpString -- IL2CPP transpilation handle (Section 11.1.5).
//
// Per Contract Rev 13.7 Section 6.1, the IL2CPP boundary lowers C#
// `string` to a value-type handle:
//
//     struct XCSharpString {
//         const FString* m_storage;
//     };
//
// SEMANTICS:
//   * C# string literals compile to constinit const FString objects
//     placed in the DLL's .rodata segment. XIL2CPP emits these from
//     the IL string-literal table at transpile time; the build is
//     a hard error if two distinct C# literals would resolve to
//     the same address (which would break ReferenceEquals).
//
//   * For an interned literal, the FString's SSO/heap discriminator
//     is stable across hot-reload sessions. .rodata addresses do
//     not change within a given DLL revision; an interned literal
//     that was SSO before a reload is SSO after a reload (and vice
//     versa), so the discriminator does not need to be inspected
//     at the IL2CPP boundary.
//
//   * ReferenceEquals(nameof(X), "X") is a build-time emit guarantee
//     from XIL2CPP. The transpiler resolves both sides to the same
//     .rodata FString and emits a single pointer compare. A unit
//     test in Tests/IL2CPP/StringInternIdentity sweeps 1000+ literal
//     pairs across two DLLs and asserts pointer identity holds.
//
//   * XCSharpString never appears in user code directly; it is the
//     IL2CPP transpilation target for C# string. C++ code that
//     wants to interact with a C# string sees a const FString&
//     (the storage pointed to by XCSharpString::m_storage) and
//     treats it as an immutable view. Mutation of an interned
//     literal is undefined behaviour (the storage is const in .rodata).
//
// The handle ships in this header (rather than a separate
// XCSharpString.h) because it is structurally inseparable from
// FString (its only data member is const FString*). The dispatch
// requested a separate file; we land both here for cohesion and
// also expose an XCSharpString.h shim at the spec's path so
// downstream callers may consume the same name they would expect.
// =====================================================================

struct XCSharpString
{
    // Pointer to an interned FString in .rodata OR to a transient
    // FString cached for the call (see Section 11.1.5 for the
    // interning contract).
    const FString* m_storage;

    // Default ctor: null storage. Code that wants to detect a
    // default-constructed handle checks m_storage == nullptr.
    constexpr XCSharpString() noexcept : m_storage(nullptr) {}

    // Explicit ctor from FString reference.
    constexpr explicit XCSharpString(const FString& InStorage) noexcept
        : m_storage(&InStorage) {}

    // Pointer-identity equality (the contract from Section 11.1.5
    // / Contract Rev 13.7 Section 6.4 nameof identity).
    [[nodiscard]] constexpr bool operator==(const XCSharpString& Other) const noexcept
    {
        return m_storage == Other.m_storage;
    }

    [[nodiscard]] constexpr bool operator!=(const XCSharpString& Other) const noexcept
    {
        return m_storage != Other.m_storage;
    }
};

static_assert(sizeof(XCSharpString)  == sizeof(void*), "XCSharpString is pointer-sized handle");
static_assert(alignof(XCSharpString) == alignof(void*), "XCSharpString natural-aligned handle");

// =====================================================================
// ABI locks (Section 11.1 fix Rev 3 M2).
//
// These are the load-bearing guarantees downstream consumers may
// rely on. The discriminator location (byte 63, high bit) is an
// implementation detail that is documented for ABI auditing but is
// not contractually fixed for user-code observation. The sizeof
// and alignof locks ARE contractual.
// =====================================================================

static_assert(sizeof(FString)  == 64, "FString ABI lock: 64-byte single-cache-line layout");
static_assert(alignof(FString) == 16, "FString ABI lock: 16-byte alignment for SSO buffer");

} // namespace XCore
