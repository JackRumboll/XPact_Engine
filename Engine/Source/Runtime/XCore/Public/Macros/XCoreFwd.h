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
    // -----------------------------------------------------------------
    // The default container allocator is forward-declared here so the
    // TArray / TMap / TSet template signatures match the Phase-1c
    // definitions exactly. The class body ships in
    // Containers/DefaultAllocator.h (Phase 1c step 7 of the dependency
    // graph; Section 5.1 / Section 5.5 row 2 "one default allocator").
    // -----------------------------------------------------------------
    class DefaultAllocator;

    template<typename T, typename AllocatorT = DefaultAllocator>
    class TArray;

    template<typename K, typename V>
    class TMap;

    template<typename T>
    class TSet;

    template<typename T, ::SIZE_T N>
    class TStaticArray;

    class TBitArray;

    template<typename T>
    class TArrayView;
}

// -----------------------------------------------------------------
// XCore::Detail forward declarations (Section 5.1 fix C-3).
//
// TArrayCore is the internal primitive that backs both TArray<T> and
// FString (Phase 1d). Living in the Detail namespace makes it
// header-private in spirit -- consumers see TArray<T> / FString, not
// the underlying primitive.
//
// IMPORTANT (locked decision in Section 5.1 fix C-3): "the public
// TArray.h header takes only const char* in its diagnostic paths;
// it NEVER includes FString.h". TArrayCore therefore takes no
// FString parameter in any signature - the cycle resolves via that
// rule. The IndexOf(const FString&) overload lives in FString.h
// (step 8 of the dependency graph), not here.
// -----------------------------------------------------------------
namespace XCore::Detail
{
    template<typename T, typename AllocatorT>
    class TArrayCore;
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
// 8-byte opaque handle. XCore-4a forward-declared the type so containers
// could compile TMap<FName, V> / TSet<FName> without depending on the
// XCore-4b interning runtime. XCore-4b ships the COMPLETE struct
// definition in Reflection/FName.h (per XCore-4b Rev 3 §4.1). Consumers
// that need the layout (sizeof, offsetof, member access) must include
// the full header; consumers that only forward-reference FName via
// template parameters (TMap<FName, V> declarations, function-pointer
// signatures with FName parameters) can include this XCoreFwd.h only.
//
// `Index` is the slot into XCore-4b's interning table; `SerialNumber`
// distinguishes numbered-name suffixes (Actor_1, Actor_2). Both are
// uint32; total handle is 8 bytes with 4-byte alignment. The ABI lock
// fires at the full struct's declaration site (Reflection/FName.h).
//
// GetTypeHash(FName) is declared here (forward only) so XCore-4a
// containers can compile a TMap<FName, V> without a circular
// dependency on the XCore-4b interning runtime. The actual
// implementation lives in XCore-4b's intern-table runtime.
// ---------------------------------------------------------------------

namespace XCore::Reflect
{
    struct FName;

    [[nodiscard]] ::uint64 GetTypeHash(FName Name) noexcept;
}
