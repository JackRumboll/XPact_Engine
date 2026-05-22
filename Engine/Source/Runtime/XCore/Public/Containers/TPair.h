// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// TPair.h -- minimal key/value pair (used by TMap).
// =====================================================================
//
// XCore-4a Rev 3, Section 5.1 (TMap built on TSet<TPair<K, V>> with
// key-only hash/equal comparators).
//
// Why a custom TPair and not std::pair? Three reasons:
//
//   1. Member-name discipline. UE's TPair uses `Key` and `Value`;
//      std::pair uses `first` and `second`. Engine code consistently
//      references the Key/Value spelling. Mirroring TPair preserves the
//      idiomatic spelling without a translation layer.
//
//   2. Hash/equal dispatch. TMap's internal TSet<TPair<K, V>> needs
//      hash/equal comparators that look at the Key only -- not Value.
//      A custom type lets us write tag-dispatch helpers (in TMap.h)
//      that key off the type rather than partial specializations of
//      std::pair (which would compete with std-shipped specializations
//      in surprising ways).
//
//   3. Bit-for-bit C# interop. The C# IL2CPP transpilation maps
//      KeyValuePair<K, V> -> TPair<K, V> via [StructLayout(Sequential)]
//      with fields named exactly Key and Value. std::pair's first/second
//      would force a per-call renaming in the IL2CPP layer.
//
// TPair is trivially-relocatable when K and V are: the layout is two
// consecutive fields with no hidden state. Move and copy semantics
// follow the elements' semantics.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include <type_traits>
#include <utility>

namespace XCore
{
    // -----------------------------------------------------------------
    // TPair<K, V> -- key + value pair.
    //
    // Layout: { K Key; V Value; }. No hidden fields, no padding beyond
    // what the C++ alignment rules require for K and V individually.
    //
    // The constructors mirror std::pair's surface: default-construct
    // both members, value-construct both, perfect-forward variadic
    // for in-place construction.
    // -----------------------------------------------------------------

    template<typename K, typename V>
    struct TPair
    {
        using KeyType   = K;
        using ValueType = V;

        K Key;
        V Value;

        // -------------------------------------------------------------
        // Default ctor -- default-constructs Key and Value. Only
        // available when both K and V are default-constructible (the
        // template instantiation gives a clean diagnostic when not).
        // -------------------------------------------------------------
        constexpr TPair() = default;

        // -------------------------------------------------------------
        // Value ctors. The forwarding-reference template handles all
        // four (lvalue/rvalue) x (lvalue/rvalue) input combinations
        // by perfect-forwarding into the K and V slot members. The
        // enable_if guards against the ctor competing with copy/move
        // (which TPair gets via = default below).
        //
        // We provide a single forwarding-reference ctor rather than
        // separate (const K&, const V&) + (K&&, V&&) overloads to
        // avoid ambiguity at deduction time when one side is an
        // implicitly-convertible expression.
        // -------------------------------------------------------------
        template<typename KArg, typename VArg,
                 typename = ::std::enable_if_t<
                     !::std::is_same_v<::std::decay_t<KArg>, TPair> &&
                     ::std::is_constructible_v<K, KArg&&> &&
                     ::std::is_constructible_v<V, VArg&&>>>
        constexpr TPair(KArg&& InKey, VArg&& InValue)
            noexcept(::std::is_nothrow_constructible_v<K, KArg&&> &&
                     ::std::is_nothrow_constructible_v<V, VArg&&>)
            : Key(::std::forward<KArg>(InKey))
            , Value(::std::forward<VArg>(InValue)) {}

        // -------------------------------------------------------------
        // Copy / move are defaulted; honor K and V's semantics.
        // -------------------------------------------------------------
        TPair(const TPair&)            = default;
        TPair(TPair&&)                 = default;
        TPair& operator=(const TPair&) = default;
        TPair& operator=(TPair&&)      = default;
    };

    // -----------------------------------------------------------------
    // MakePair -- factory mirroring std::make_pair.
    // -----------------------------------------------------------------
    template<typename K, typename V>
    [[nodiscard]] constexpr TPair<::std::decay_t<K>, ::std::decay_t<V>>
    MakePair(K&& InKey, V&& InValue)
        noexcept(::std::is_nothrow_constructible_v<TPair<::std::decay_t<K>, ::std::decay_t<V>>, K&&, V&&>)
    {
        return TPair<::std::decay_t<K>, ::std::decay_t<V>>{
            ::std::forward<K>(InKey),
            ::std::forward<V>(InValue)
        };
    }

} // namespace XCore
