// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// TStaticArray.h -- fixed-size stack array (Section 5.1 + Section 18 OPEN-4).
// =====================================================================
//
// XCore-4a Rev 3, Section 5.1 (`template<typename T, size_t N> class
// TStaticArray; // pure stack storage; no fallback`) + Section 18
// OPEN-4 RECOMMENDED ("pure stack storage; no heap fallback").
//
// TStaticArray<T, N> is a fixed-size compile-time-sized array that
// lives entirely in automatic storage. No allocator interaction, no
// heap fallback (the user explicitly opted in to a fixed-size storage
// by choosing this type), no GC integration.
//
// Section 5.5 row 2 names TStaticArray as the explicit-distinct
// alternative to UE's TInlineAllocator template arg: rather than
// `TArray<T, TInlineAllocator<N>>`, the XPact pattern is
// `TStaticArray<T, N>` -- a DIFFERENT type with a DIFFERENT contract,
// not a template-arg variation on TArray.
//
// Per Section 18 OPEN-4 RECOMMENDED: "stack-only; no heap fallback".
// If the caller needs a dynamic array, TArray is the type; if the
// caller needs an inline+heap-overflow allocator pattern, that is
// XCore-4d (later phase) work. TStaticArray as shipped here is the
// minimum-viable pure-stack type.
//
// USE CASES.
//
//   * Compile-time-sized lookup tables (e.g., FMemTag::Container
//     allocation-bin sizes, math-table coefficients).
//   * Fixed-size buffers in hot paths where allocation is forbidden
//     (sim-path inner loops are the canonical example; see Section
//     4.3 FScopedNoAlloc).
//   * Inline storage that ALWAYS fits (e.g., a small fixed roster of
//     PhysX actors per simulation tick).
//
// CALLER-DISCIPLINE.
//
// TStaticArray's storage is N elements of T, default-constructed in
// place. There is no Add / RemoveAt -- the size IS the size. Bounds
// access is bounds-checked in Debug via XPACT_CHECK; a Result-returning
// At() accessor is the always-safe path (sim-path code uses At;
// C# transpiled code uses At; native C++ caller-discipline can use
// operator[] when the index is provably in range).
//
// DETERMINISM.
//
// TStaticArray is fully constexpr: instantiation, indexed access, and
// iteration can participate in constant-expression evaluation. The
// determinism contract of Section 5.3 holds trivially (no
// allocator, no hashing).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"
#include "Macros/XErrorTypes.h"
#include "Macros/XResult.h"

#include <cstddef>
#include <functional>     // std::reference_wrapper for At() return
#include <type_traits>

namespace XCore
{
    // -----------------------------------------------------------------
    // TStaticArray<T, N> -- fixed-size stack array of N elements.
    //
    // Layout: N consecutive T objects in m_data. Stride = sizeof(T);
    // total size = N * sizeof(T) (plus T's natural alignment padding
    // which the compiler computes).
    //
    // No copy/move semantics overrides: the compiler-generated copy
    // ctor / move ctor / assignments do the right thing (per-element
    // copy/move; the array is value-type-style).
    //
    // The constexpr-friendliness depends on T being constexpr-
    // constructible; for trivially-default-constructible T this is
    // automatic. Non-constexpr T types (e.g., types whose default
    // ctor allocates) can still be used at runtime; the constexpr
    // qualifiers above just make the constant-expression cases work
    // when T supports it.
    // -----------------------------------------------------------------

    template<typename T, ::SIZE_T N>
    class TStaticArray
    {
    public:
        using ElementType = T;
        using SizeType    = ::int32;

        // -------------------------------------------------------------
        // The storage is a public-by-design member so aggregate-init
        // patterns like `TStaticArray<int, 3>{1, 2, 3}` work.
        //
        // The brace-initialization works because TStaticArray is a
        // standard-layout aggregate when T is standard-layout, and
        // m_data is its single member. We accept the design constraint
        // that the field is publicly visible (UE's TStaticArray has
        // the same public field; this is the C++ idiom for
        // aggregate-initialization-friendly fixed arrays).
        //
        // For empty arrays (N == 0), C++ does not permit a zero-size
        // raw array; the workaround is the [N == 0 ? 1 : N] gate so
        // the struct compiles for N = 0 (and Num() returns 0 as
        // expected). The single uninitialized element is never
        // accessed because IsValidIndex(0) returns false.
        // -------------------------------------------------------------
        T m_data[N == 0 ? 1 : N];

        // =================================================================
        // Accessors.
        // =================================================================

        // -------------------------------------------------------------
        // Num -- the compile-time size as a runtime int32.
        //
        // The static_cast clamps to int32 if N is huge (unlikely for a
        // stack array). The constexpr qualifier lets callers use
        // Num() in constant-expression contexts (e.g., as the bound
        // of a fold expression).
        // -------------------------------------------------------------
        [[nodiscard]] constexpr ::int32 Num() const noexcept
        {
            return static_cast<::int32>(N);
        }

        [[nodiscard]] constexpr bool IsEmpty() const noexcept
        {
            return N == 0;
        }

        [[nodiscard]] constexpr bool IsValidIndex(::int32 Index) const noexcept
        {
            return Index >= 0 && static_cast<::SIZE_T>(Index) < N;
        }

        // -------------------------------------------------------------
        // operator[] -- O(1) indexed access; XPACT_CHECK in Debug.
        //
        // Native-C++ caller-discipline. Mirrors the TArray::operator[]
        // surface for surface-consistency.
        // -------------------------------------------------------------
        [[nodiscard]] constexpr T& operator[](::int32 Index) noexcept
        {
            XPACT_CHECK(IsValidIndex(Index));
            return m_data[Index];
        }

        [[nodiscard]] constexpr const T& operator[](::int32 Index) const noexcept
        {
            XPACT_CHECK(IsValidIndex(Index));
            return m_data[Index];
        }

        // -------------------------------------------------------------
        // At -- always-checked indexed access; returns Result-wrapped.
        //
        // Per Section 5.1 footnote A-MIN3 (promoted to MAJOR): C# code
        // accessing the static array (via the IL2CPP boundary) lowers
        // its `list[i]` to At() for bounds-check preservation across
        // all build configs.
        //
        // PHASE 1C STATUS: Result<T, E> is a polyfill placeholder on
        // C++20 toolchains; the signature compiles but instantiation
        // would fire the XResult.h diagnostic until tl::expected is
        // vendored. See XResult.h for the deferral details.
        // -------------------------------------------------------------
        [[nodiscard]] constexpr ::XCore::Result<::std::reference_wrapper<T>, ::XCore::FBoundsError> At(::int32 Index) noexcept
        {
            if (Index < 0)
            {
                return ::XCore::Unexpected(::XCore::FBoundsError::IndexNegative);
            }
            if constexpr (N == 0)
            {
                return ::XCore::Unexpected(::XCore::FBoundsError::EmptyContainer);
            }
            else
            {
                if (static_cast<::SIZE_T>(Index) >= N)
                {
                    return ::XCore::Unexpected(::XCore::FBoundsError::IndexTooLarge);
                }
                return ::std::reference_wrapper<T>(m_data[Index]);
            }
        }

        [[nodiscard]] constexpr ::XCore::Result<::std::reference_wrapper<const T>, ::XCore::FBoundsError> At(::int32 Index) const noexcept
        {
            if (Index < 0)
            {
                return ::XCore::Unexpected(::XCore::FBoundsError::IndexNegative);
            }
            if constexpr (N == 0)
            {
                return ::XCore::Unexpected(::XCore::FBoundsError::EmptyContainer);
            }
            else
            {
                if (static_cast<::SIZE_T>(Index) >= N)
                {
                    return ::XCore::Unexpected(::XCore::FBoundsError::IndexTooLarge);
                }
                return ::std::reference_wrapper<const T>(m_data[Index]);
            }
        }

        // -------------------------------------------------------------
        // GetData -- raw pointer to the underlying buffer.
        //
        // For N == 0 the returned pointer is to the single
        // dummy-slot uninitialized element; callers must not
        // dereference it (IsValidIndex(0) returns false, so the
        // safe-accessor At() catches this).
        // -------------------------------------------------------------
        [[nodiscard]] constexpr T* GetData() noexcept
        {
            return m_data;
        }

        [[nodiscard]] constexpr const T* GetData() const noexcept
        {
            return m_data;
        }

        // =================================================================
        // Iteration.
        //
        // For N == 0, begin() == end() (both point at the single dummy
        // slot, but Num() == 0 means the range-based for loop body
        // executes zero times).
        // =================================================================

        [[nodiscard]] constexpr T* begin() noexcept
        {
            return m_data;
        }

        [[nodiscard]] constexpr T* end() noexcept
        {
            return m_data + N;
        }

        [[nodiscard]] constexpr const T* begin() const noexcept
        {
            return m_data;
        }

        [[nodiscard]] constexpr const T* end() const noexcept
        {
            return m_data + N;
        }

        [[nodiscard]] constexpr const T* cbegin() const noexcept
        {
            return m_data;
        }

        [[nodiscard]] constexpr const T* cend() const noexcept
        {
            return m_data + N;
        }
    };

    // -----------------------------------------------------------------
    // ABI locks. The struct is exactly N * sizeof(T) bytes for N > 0
    // (modulo alignment padding of T itself). The static asserts pin
    // a few representative instantiations so a future ABI drift
    // (e.g., the [N == 0 ? 1 : N] workaround accidentally inflating
    // the struct for non-zero N) is caught at compile time.
    // -----------------------------------------------------------------
    static_assert(sizeof(::XCore::TStaticArray<::int32,  4>) == 4 * sizeof(::int32),
                  "TStaticArray ABI lock: N=4 case is N*sizeof(T) bytes");
    static_assert(sizeof(::XCore::TStaticArray<::int64, 16>) == 16 * sizeof(::int64),
                  "TStaticArray ABI lock: N=16 case is N*sizeof(T) bytes");

} // namespace XCore
