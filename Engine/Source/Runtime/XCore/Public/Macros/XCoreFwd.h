// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XCoreFwd.h -- forward declarations of types XCore-4a will define.
// =====================================================================
//
// XCore-4a Rev 3, Section 3 step 1 ("zero internal dependencies; every
// other XCore-4a header sits on top of these"). Files that depend on
// any of these types (in their declared form, not their definitions)
// can include this single header rather than pulling in the heavy
// definitions; that breaks the apparent FString <-> TArray cycle at
// the header level (Section 5 fix C-3) and is the same pattern that
// resolves the FName forward-declaration in Section 11.8.
//
// CRITICAL: FName is the one exception case (Section 1.2 + 11.8). The
// XCore-4b reflection runtime will define FName's actual interning,
// hashing, equality, FromString, ToString -- but the 8-byte handle is
// declared here so XCore-4a's TMap and TSet can instantiate
// TMap<FName, V> and TSet<FName> without circular-depending on
// XCore-4b. The ABI lock on the layout is the load-bearing guarantee.
//
// Per Section 11.8 the FName declaration lives in namespace
// XCore::Reflect; the prior Section 11.8 wording placed it at
// :: scope with `struct FName { uint32_t Index; uint32_t SerialNumber; };`
// -- the dispatch instructions for this subagent quoted the :: scope
// form but the spec body in Section 11.8 puts it in namespace
// XCore::Reflect with `Index` + `SerialNumber` fields and 4-byte
// alignment. The namespace placement is the binding one (it matches
// the rest of the reflect-tier types); a global-scope `FName` would
// collide with downstream UE-style code that may want its own
// reflection-free `FName` for asset-path purposes. We follow the
// Section-11.8 spec literally: namespace XCore::Reflect.
//
// =====================================================================

#include "Macros/XCoreTypes.h"

// ---------------------------------------------------------------------
// Container forward declarations (Section 5 / step 7-9 of dep graph).
// ---------------------------------------------------------------------

namespace XCore
{
    template<typename T>
    class TArray;

    template<typename K, typename V>
    class TMap;

    template<typename T>
    class TSet;

    template<typename T, ::SIZE_T N>
    class TStaticArray;

    class TBitArray;
}

// ---------------------------------------------------------------------
// String forward declarations (Section 11 / step 8 of dep graph).
//
// FString is the UTF-8 string. FUTF16String is the Win32 wide-char
// escape hatch (only used at the boundary with Win32 *W-suffix syscalls;
// see Section 11.5 and master plan Section 2 String encoding row).
// FText is the localised-text type that wraps FString with a loctable
// lookup (Section 11.2).
// ---------------------------------------------------------------------

namespace XCore
{
    class FString;
    class FUTF16String;
    class FText;
}

// ---------------------------------------------------------------------
// FName forward declaration (Section 1.2 + 11.8; fix C-1).
//
// 8-byte opaque handle. XCore-4a defines ONLY the layout + ABI
// asserts; interning, hashing, equality, FromString, ToString all
// live in XCore-4b (Section 4 step 4 of the master plan). Any
// XCore-4a-only test build that takes the address of GetTypeHash(FName)
// or calls FromString is a link error -- that is the contract.
//
// `Index` is the slot into XCore-4b's interning table; `SerialNumber`
// distinguishes intern-reuse of the same slot (free-list reclaim).
// Both are uint32 so the handle is exactly 8 bytes with 4-byte
// alignment.
// ---------------------------------------------------------------------

namespace XCore::Reflect
{
    struct FName
    {
        ::uint32 Index;
        ::uint32 SerialNumber;
    };

    static_assert(sizeof(FName)  == 8, "FName ABI lock: must be 8 bytes");
    static_assert(alignof(FName) == 4, "FName ABI lock: 4-byte alignment");

    // GetTypeHash(FName) is declared here (forward only) so XCore-4a
    // containers can compile a TMap<FName, V> without a circular
    // dependency on the XCore-4b interning runtime. The actual
    // implementation lives in XCore-4b's interning table. An
    // XCore-4a-only test build that *uses* TMap<FName, V> via
    // operator[] / Find / etc. will get a link error here -- which is
    // the contract; XCore-4a alone cannot resolve an FName key.
    [[nodiscard]] ::uint64 GetTypeHash(FName Name) noexcept;
}
