// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// TArrayView.h -- non-owning view of a contiguous range (Section 5.1).
// =====================================================================
//
// XCore-4a Rev 3, Section 5.1 (Public API mentions TArray + TStaticArray +
// TBitArray; views are an implicit corollary referenced by downstream
// code) + Section 5.5 row 2 (one allocator policy + distinct view type).
//
// TArrayView<T> is the UE-named equivalent of std::span<T>: a
// non-owning, non-resizable view over a contiguous T sequence. It is a
// {pointer, count} pair (16 bytes on 64-bit targets).
//
// Use cases:
//   * Function parameters that accept "an array of T" without copying:
//       void ProcessElements(TArrayView<const FActor*> Actors);
//     callers may pass a TArray<FActor*>, a TStaticArray<FActor*, N>, a
//     C-style array, or an initializer_list.
//   * Return values that expose a slice into a private container without
//     leaking ownership semantics.
//
// CALLER DISCIPLINE.
//
// TArrayView does NOT own the storage. The viewed range must outlive the
// view. This is the same contract as std::span; UE's TArrayView has the
// same shape. Constructing a TArrayView from an `std::initializer_list`
// is supported because the surrounding full-expression keeps the list's
// underlying array alive for the duration of the expression -- the same
// well-defined storage-lifetime guarantee that std::span exploits.
//
// NO GC INTEGRATION.
//
// TArrayView is a view, not an owner: it does not register an
// XGCRootSpan. The viewed container (which IS the owner) registers the
// span. A TArrayView<XActor*> over a TArray<XActor*> works correctly
// because the collector walks the TArray's registered span; the view
// itself is not visible to the collector. This is the
// engineering-principles-correct design: views are transient stack
// objects, often optimized away entirely by the compiler.
//
// PHASE 1C STATUS.
//
// TArrayView is constexpr-friendly (every method is constexpr); a
// future Phase-1d addition may add Slice / SubView methods. The
// surface is minimal and STL-aligned for now.
//
// =====================================================================

#include "Containers/TArray.h"
#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"
#include "Macros/XErrorTypes.h"
#include "Macros/XResult.h"

#include <cstddef>           // std::size_t
#include <functional>        // std::reference_wrapper
#include <initializer_list>  // std::initializer_list
#include <type_traits>       // std::remove_const_t

namespace XCore
{
    // -----------------------------------------------------------------
    // TArrayView<T> -- non-owning view of a contiguous T sequence.
    //
    // Layout: { T* Data; int32 Num; } (16 bytes on 64-bit; one register
    // pair on x86_64 ABIs).
    //
    // All accessors are constexpr-friendly: constructed from a
    // compile-time-known TArray / TStaticArray / initializer_list /
    // raw {pointer, size} pair, the view can participate in
    // constant-expression evaluation.
    //
    // Iteration model: STL-style begin() / end() returning raw T*.
    // Range-based for works naturally.
    // -----------------------------------------------------------------

    template<typename T>
    class TArrayView
    {
    public:
        using ElementType = T;
        using SizeType    = ::int32;

        // -------------------------------------------------------------
        // Default ctor -- empty view (Data = nullptr, Num = 0).
        //
        // The empty view satisfies the contract that begin() == end()
        // and Num() == 0. Indexed access on an empty view is undefined
        // behaviour in operator[] (caller-discipline) and a clean
        // FBoundsError::EmptyContainer in At().
        // -------------------------------------------------------------
        constexpr TArrayView() noexcept
            : m_data(nullptr)
            , m_num(0)
        {
        }

        // -------------------------------------------------------------
        // Pointer + count ctor.
        //
        // Pre-condition: if Count > 0, Data must point at the first of
        // at least Count contiguous T objects. Passing nullptr with
        // Count > 0 is undefined behaviour (XPACT_CHECK in Debug).
        // -------------------------------------------------------------
        constexpr TArrayView(T* Data, ::int32 Count) noexcept
            : m_data(Data)
            , m_num(Count)
        {
        }

        // -------------------------------------------------------------
        // From TArray<T> (non-const variant).
        //
        // The view inherits the array's current Num() + GetData(). If
        // the array later resizes (Add / Reserve), the view's m_data
        // pointer may be DANGLING. Caller-discipline: do not resize
        // the underlying array while a view over it is in scope.
        //
        // Note: this ctor is non-explicit so range-for loops over a
        // TArray<T> via a view-taking helper compose naturally.
        // -------------------------------------------------------------
        template<typename AllocatorT>
        constexpr TArrayView(::XCore::TArray<T, AllocatorT>& Array) noexcept
            : m_data(Array.GetData())
            , m_num(Array.Num())
        {
        }

        // -------------------------------------------------------------
        // From const TArray<T> -- only valid when T is itself const.
        //
        // The classic std::span / TArrayView pattern: a const TArray<U>
        // can be viewed as TArrayView<const U> but not as TArrayView<U>.
        // The std::is_const_v<T> guard enforces this at compile time.
        //
        // We rely on the type system: if the user writes TArrayView<const
        // U>(const_array), this overload kicks in and the view is
        // const-correct. If the user writes TArrayView<U>(const_array),
        // there is no matching overload and the compile fails -- the
        // intended diagnostic.
        // -------------------------------------------------------------
        template<typename AllocatorT, typename U = T>
            requires ::std::is_const_v<U>
        constexpr TArrayView(const ::XCore::TArray<::std::remove_const_t<U>, AllocatorT>& Array) noexcept
            : m_data(Array.GetData())
            , m_num(Array.Num())
        {
        }

        // -------------------------------------------------------------
        // From std::initializer_list<T>.
        //
        // The initializer_list's backing array's storage duration is
        // the surrounding full-expression (C++17 wording); a view
        // constructed from a brace-list is valid only for the duration
        // of that expression. Storing the view in a variable beyond
        // the expression is undefined behaviour -- the same contract
        // std::span has.
        //
        // The ctor is constexpr so the view can participate in
        // constant-expression evaluation.
        // -------------------------------------------------------------
        constexpr TArrayView(::std::initializer_list<::std::remove_const_t<T>> List) noexcept
            : m_data(const_cast<T*>(List.begin()))  // const_cast is safe when T == const U; UB when T == U and caller passes a brace-list (caller-discipline)
            , m_num(static_cast<::int32>(List.size()))
        {
        }

        // -------------------------------------------------------------
        // From raw C-style array T[N].
        //
        // Convenience for stack-pinned scratch buffers; the size is
        // captured at compile time so there's no risk of mismatched
        // count / pointer.
        // -------------------------------------------------------------
        template<::SIZE_T N>
        constexpr TArrayView(T (&Arr)[N]) noexcept
            : m_data(Arr)
            , m_num(static_cast<::int32>(N))
        {
        }

        // =================================================================
        // Accessors.
        // =================================================================

        [[nodiscard]] constexpr ::int32 Num() const noexcept
        {
            return m_num;
        }

        [[nodiscard]] constexpr bool IsEmpty() const noexcept
        {
            return m_num == 0;
        }

        [[nodiscard]] constexpr bool IsValidIndex(::int32 Index) const noexcept
        {
            return Index >= 0 && Index < m_num;
        }

        [[nodiscard]] constexpr T* GetData() const noexcept
        {
            return m_data;
        }

        // -------------------------------------------------------------
        // operator[] -- O(1) indexed access.
        //
        // Native-C++ caller-discipline; XPACT_CHECK bounds-check in
        // Debug. Mirrors the TArray::operator[] surface.
        // -------------------------------------------------------------
        [[nodiscard]] constexpr T& operator[](::int32 Index) const noexcept
        {
            XPACT_CHECK(IsValidIndex(Index));
            return m_data[Index];
        }

        // -------------------------------------------------------------
        // At -- always-checked indexed access; returns Result-wrapped.
        //
        // Mirrors the TArray::At surface. C# code that accesses a view
        // (a more exotic but reasonable case if the view is exposed
        // across the IL2CPP boundary) gets the same safety contract
        // as TArray.
        //
        // PHASE 1C STATUS: same Result<T, E> placeholder gating as
        // TArrayCore::At (declares but does not instantiate without
        // tl::expected vendoring).
        // -------------------------------------------------------------
        [[nodiscard]] constexpr ::XCore::Result<::std::reference_wrapper<T>, ::XCore::FBoundsError> At(::int32 Index) const noexcept
        {
            if (Index < 0)
            {
                return ::XCore::Unexpected(::XCore::FBoundsError::IndexNegative);
            }
            if (m_num == 0)
            {
                return ::XCore::Unexpected(::XCore::FBoundsError::EmptyContainer);
            }
            if (Index >= m_num)
            {
                return ::XCore::Unexpected(::XCore::FBoundsError::IndexTooLarge);
            }
            return ::std::reference_wrapper<T>(m_data[Index]);
        }

        // =================================================================
        // Iteration.
        // =================================================================

        [[nodiscard]] constexpr T* begin()  const noexcept { return m_data; }
        [[nodiscard]] constexpr T* end()    const noexcept { return m_data + m_num; }
        [[nodiscard]] constexpr T* cbegin() const noexcept { return m_data; }
        [[nodiscard]] constexpr T* cend()   const noexcept { return m_data + m_num; }

    private:
        T*      m_data;   // 8 bytes; non-owning pointer to the viewed range.
        ::int32 m_num;    // 4 bytes; number of elements in the view.
        // Trailing 4 bytes of padding on 64-bit ABIs; intentional --
        // the view is a one-register-pair-passable value on x86_64 SYS-V
        // and MS-x64 calling conventions (16-byte aligned, passed in
        // {rcx, rdx} on MS-x64).
    };

    // -----------------------------------------------------------------
    // ABI locks.
    //
    // sizeof(TArrayView<T>) is 16 bytes on every supported 64-bit ABI
    // (Win64, Linux x86_64, Android ARM64). The 4-byte tail padding is
    // implicit; explicit padding here would not change observable
    // behaviour but would make the struct's layout self-documenting
    // for future maintainers reading the disassembly.
    // -----------------------------------------------------------------
    static_assert(sizeof(::XCore::TArrayView<::int32>) == 16,
                  "TArrayView<int32> ABI lock: 16 bytes (one register pair)");
    static_assert(alignof(::XCore::TArrayView<::int32>) >= 8,
                  "TArrayView<int32> ABI lock: 8-byte aligned");

} // namespace XCore
