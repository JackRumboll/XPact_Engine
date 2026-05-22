// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FUTF16String.h -- Win32 wide-char escape hatch (Section 11.1).
// =====================================================================
//
// XCore-4a Rev 3, Section 11.1 + Section 11.5 +
// Section 2 String-encoding row.
//
// FUTF16String is the ONLY place in the engine where wide-character
// UTF-16 is permitted. It exists exclusively at the boundary with
// Win32 *W-suffix syscalls (CreateFileW, GetUserNameW, etc.); every
// other code path in the engine uses UTF-8 (FString).
//
// USAGE PATTERN:
//
//   FString Path = u8"C:/path/with/non-ascii-chars-Ä.txt";
//   FUTF16String Wide = FUTF16String::FromFString(Path);
//   HANDLE H = ::CreateFileW(Wide.WideCStr(), ...);
//   // ... use H ...
//   ::CloseHandle(H);
//
// The conversion runs at the syscall boundary, never silently;
// bare std::wstring is banned via a clang-tidy rule shipped with
// the engine (per Section 11.1.5 / locked item 1).
//
// CROSS-PLATFORM:
//
//   * Win64: uses WideCharToMultiByte / MultiByteToWideChar.
//   * Linux: no syscall consumer (POSIX has UTF-8 native paths);
//            the type is defined but rarely used. ICU's iconv could
//            be wired in a future revision; for Phase 1d the
//            Linux path is a hand-rolled UTF-8 <-> UTF-16 converter
//            (see FUTF16String.cpp).
//   * Android: same hand-rolled converter as Linux (Android NDK
//              has no UTF-16 syscall surface).
//
// STORAGE:
//
//   The type holds a heap-allocated, null-terminated UTF-16 buffer.
//   No SSO -- the use case is short-lived call-site conversions,
//   not long-term storage. Allocations are tagged FMemTag::Platform
//   so per-tag accounting can detect leaks at the Win32-syscall
//   wrapper layer.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"
#include "Containers/FString.h"

namespace XCore
{

class FUTF16String
{
public:
    // Default ctor: empty (zero-length, with a null-terminated empty
    // buffer that returns "" for WideCStr).
    FUTF16String() noexcept;

    // Destructor: frees the buffer.
    ~FUTF16String() noexcept;

    // Copy and move.
    FUTF16String(const FUTF16String& Other);
    FUTF16String(FUTF16String&& Other) noexcept;
    FUTF16String& operator=(const FUTF16String& Other);
    FUTF16String& operator=(FUTF16String&& Other) noexcept;

    // -------------------------------------------------------------
    // Conversion factory: UTF-8 FString -> UTF-16 FUTF16String.
    // The conversion preserves codepoints; surrogate-pair encoding
    // is correct for codepoints >= U+10000.
    // -------------------------------------------------------------
    [[nodiscard]] static FUTF16String FromFString(const FString& Src);

    // Reverse direction: UTF-16 -> UTF-8 FString.
    [[nodiscard]] static FString ToFString(const FUTF16String& Src);

    // -------------------------------------------------------------
    // Accessors.
    //
    // WideCStr: null-terminated UTF-16 buffer. Always non-null
    //           (empty strings return a pointer to a single-element
    //           "" buffer).
    // WideLen:  UTF-16 code-unit count (NOT codepoint count). For
    //           strings containing supplementary-plane codepoints,
    //           WideLen counts surrogate pairs as 2.
    // -------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE const wchar_t* WideCStr() const noexcept
    {
        return m_data;
    }

    [[nodiscard]] XPACT_FORCEINLINE ::int32 WideLen() const noexcept
    {
        return m_len;
    }

    [[nodiscard]] XPACT_FORCEINLINE bool IsEmpty() const noexcept
    {
        return m_len == 0;
    }

private:
    // Heap-allocated, null-terminated UTF-16 buffer. The buffer is
    // m_len UTF-16 units plus one for the null terminator.
    wchar_t* m_data;
    ::int32  m_len;     // UTF-16 code units, NOT codepoints
    ::int32  m_capacity; // capacity in UTF-16 units (excluding null)

    // Internal allocator dispatch.
    void Allocate(::int32 NewLen) noexcept;
    void Deallocate() noexcept;
    void AssignFromUtf16(const wchar_t* Src, ::int32 SrcLen);
};

} // namespace XCore
