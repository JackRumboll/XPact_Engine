// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// TArrayCore.h -- internal templated primitive backing TArray<T> + FString.
// =====================================================================
//
// XCore-4a Rev 3, Section 5.1 (fix C-3) + Section 5.4 (GC interaction)
// + Section 5.5 (UE divergences) + dependency-graph step 7.
//
// HISTORICAL CONTEXT.
//
// UE's TArray<T> sits at the foundation of nearly every UE Core data
// structure. FString is implemented as TArray<TCHAR> (with TCHAR
// historically meaning wchar_t). XPact decided to invert this layering:
// FString is the UTF-8 string type (Section 11), and the primitive that
// backs both is an internal-only template: XCore::Detail::TArrayCore<T,
// AllocatorT>. This breaks the FString<->TArray header cycle that
// appears at first glance, per Section 5.1 fix C-3:
//
//   "FString is built on an internal private templated primitive
//    XCore::Internal::TArrayCore<char> (a header-private allocator-
//    backed contiguous byte buffer with SSO); it is NOT built on the
//    public TArray<char>, and TArrayCore is not user-facing."
//
// Note: the spec wording uses "XCore::Internal", but XCore's existing
// convention (Detail namespace for header-private code, also used in
// XResult.h's `XCore::Detail::ResultUnvendoredFallback`) keeps the
// codebase consistent. This file places TArrayCore in `namespace
// XCore::Detail` per the codebase convention.
//
// LAYOUT (Section 5.1 conceptual layout with EBO-friendly allocator):
//
//   template<typename T, typename AllocatorT = DefaultAllocator>
//   class TArrayCore {
//       T*       m_data;         // 8 bytes; allocator-owned buffer
//       int32    m_num;          // 4 bytes; current element count
//       int32    m_max;          // 4 bytes; current capacity (in elements)
//       XPACT_NO_UNIQUE_ADDRESS AllocatorT m_alloc;  // 0 bytes when stateless
//   };
//
// XPACT_NO_UNIQUE_ADDRESS folds the 2-byte DefaultAllocator into the
// trailing-pad of the int32 fields when possible. With m_data (8) +
// m_num (4) + m_max (4) the struct is 16 bytes; the 2-byte alloc
// occupies position 17-18 but with [[no_unique_address]] it can be
// elided entirely on most compilers. Per Section 5.5 row 6: "~10-80 KB
// saved per scene at ~10k TArray instances".
//
// SCOPE OF THIS PRIMITIVE.
//
// TArrayCore is the buffer-management primitive. It implements:
//   * Construction / destruction / move
//   * Capacity (Num, Max, Reserve, Shrink hint via Reset)
//   * Element-level (Add, Emplace, RemoveAt, RemoveAtSwap, operator[],
//     At, GetData)
//   * Iteration (begin/end)
//   * The 1.375x growth + QuantizeSize-by-element protocol
//
// It does NOT implement:
//   * GC root-span registration (that's TArray's partial specialization
//     for T = XObject*; Section 5.4).
//   * Move-only enforcement (TArrayCore allows copy; the public TArray
//     deletes copy for GC-aware T = XObject* via partial specialization).
//   * IndexOf(const FString&) overloads (lives in FString.h per fix C-3).
//   * Any FString-typed parameter (cycle resolution per fix C-3).
//
// COPYING SEMANTICS.
//
// TArrayCore is copyable (deep copy: allocates a new buffer of the
// source's size, copy-constructs each element). The public TArray<T>
// uses TArrayCore as-is for non-XObject T (where copy is correct);
// the GC-aware specialization deletes copy at the TArray layer.
// TArrayCore itself is the non-GC-aware base; it has no knowledge of
// XObject pointers.
//
// CONSTANT-TIME INVARIANTS (Section 5.1 + 5.3).
//
//   * Num() and Max() are O(1) trivial reads.
//   * operator[] / At are O(1).
//   * Add / Emplace / RemoveAtSwap are amortised O(1).
//   * RemoveAt is O(N) due to shift.
//   * Reserve / Reset are O(N) on grow / shrink, but no work when
//     NewMax <= Max().
//
// THREADING (Section 5.2).
//
// TArrayCore is NOT thread-safe. Two threads concurrently mutating the
// same TArrayCore instance is undefined behaviour. The thread-safe
// variants live in TThreadSafeQueue / TConcurrentLinearAllocator
// (Section 5.2 wording).
//
// =====================================================================

#include "Macros/XCoreTypes.h"        // int32, SIZE_T, INDEX_NONE
#include "Macros/XPactMacros.h"       // XPACT_NO_UNIQUE_ADDRESS, XPACT_FORCEINLINE, XPACT_CHECK
#include "Macros/XErrorTypes.h"       // FBoundsError
#include "Macros/XResult.h"           // Result<T, E>, Unexpected
#include "Containers/DefaultAllocator.h"

#include <algorithm>                  // std::sort / std::stable_sort / std::push_heap / std::pop_heap (Rev 1 audit HIGH-3)
#include <cstring>                    // std::memcpy, std::memmove
#include <functional>                 // std::reference_wrapper (for At's Result), std::less
#include <new>                        // placement new
#include <type_traits>                // std::is_trivially_*_v
#include <utility>                    // std::move, std::forward

namespace XCore::Detail
{
    // -----------------------------------------------------------------
    // TIsTriviallyRelocatable -- the "memmove on resize" trait.
    //
    // Section 5.5 row 3 ("Memcpy on resize regardless of T"):
    //   UE memcpy's regardless of T, which silently corrupts self-
    //   referential types. XPact uses the trait to gate memmove vs
    //   move-and-destroy.
    //
    // Default: a type is trivially relocatable if it is both trivially-
    // copyable and trivially-destructible. The trait can be specialised
    // for self-referential types that legitimately want the memmove path.
    // -----------------------------------------------------------------
    template<typename T>
    struct TIsTriviallyRelocatable
    {
        static constexpr bool Value =
            ::std::is_trivially_copyable_v<T> &&
            ::std::is_trivially_destructible_v<T>;
    };

    // -----------------------------------------------------------------
    // ComputeGrowCapacity -- the 1.375x growth + minimum (4) protocol.
    //
    // Section 5.1: "NewCap = NewMax + (3/8) * NewMax + 16 (about 1.375x),
    // quantised by the allocator's QuantizeSize so capacity always
    // matches an actual bin size with no internal waste. First-
    // allocation minimum is 4 elements."
    //
    // The formula is the UE Core formula (ContainerAllocationPolicies.h
    // line 192: `Grow = SIZE_T(NewMax) + UE_CONTAINER_SLACK_GROWTH_FACTOR_
    // NUMERATOR * SIZE_T(NewMax) / UE_CONTAINER_SLACK_GROWTH_FACTOR_
    // DENOMINATOR + ConstantGrow`, where N/D = 3/8 and ConstantGrow = 16
    // by default per lines 121-133 of the same file).
    //
    // We don't reach into FMemory::QuantizeSize at this layer (the
    // allocator hides that detail); the underlying Malloc returns a
    // bin-rounded block, and the caller can query the actual capacity
    // via Max(). In practice, the bin rounding is invisible at this
    // layer; the test suite asserts that Max() matches a real bin size
    // by checking that GetAllocatedBytes / sizeof(T) >= Max().
    // -----------------------------------------------------------------
    [[nodiscard]] XPACT_FORCEINLINE constexpr ::int32 ComputeGrowCapacity(::int32 NewNum, ::int32 CurrentMax) noexcept
    {
        // First-alloc minimum is 4 elements (Section 5.1).
        constexpr ::int32 kFirstGrow    = 4;
        constexpr ::int32 kConstantGrow = 16;

        ::int32 Grow = kFirstGrow;

        // The branch mirrors UE's pattern: only apply the 1.375x slack
        // when we're already growing past first-alloc.
        if (CurrentMax > 0 || NewNum > kFirstGrow)
        {
            // 1.375x = NewNum + 3*NewNum/8 + 16. Done in int64 to avoid
            // 32-bit overflow on very large arrays; cast back at the end
            // (which the static_cast clamps to int32_max if overflowing).
            const ::int64 GrowSize64 =
                static_cast<::int64>(NewNum) +
                (3 * static_cast<::int64>(NewNum)) / 8 +
                static_cast<::int64>(kConstantGrow);

            // Clamp to int32 max. An overflow at this scale is
            // pathological; the OOM would fire long before we reach
            // 2**31 elements.
            constexpr ::int64 kInt32Max = static_cast<::int64>(2147483647);
            Grow = static_cast<::int32>(GrowSize64 > kInt32Max ? kInt32Max : GrowSize64);
        }
        else if (NewNum > kFirstGrow)
        {
            // Honor caller's explicit larger request even on first alloc.
            Grow = NewNum;
        }

        // Always at least NewNum.
        if (Grow < NewNum)
        {
            Grow = NewNum;
        }

        return Grow;
    }

    // -----------------------------------------------------------------
    // TArrayCore -- the internal primitive.
    //
    // Documented invariants (Section 5.1):
    //   * m_num >= 0 (negative count is UB at the public API; XPACT_CHECK
    //     in Debug).
    //   * m_max >= m_num.
    //   * m_data == nullptr <=> m_max == 0.
    //   * When m_data != nullptr, m_data is a block returned by m_alloc
    //     of at least m_max * sizeof(T) bytes, alignof(T)-aligned.
    //   * The first m_num elements at m_data are alive (constructed);
    //     positions m_num .. m_max are raw uninitialised memory.
    // -----------------------------------------------------------------

    template<typename T, typename AllocatorT = ::XCore::DefaultAllocator>
    class TArrayCore
    {
    public:
        // -----------------------------------------------------------------
        // Type aliases. Mirrors STL/UE patterns for compatibility with
        // template machinery that asks for ElementType.
        // -----------------------------------------------------------------
        using ElementType   = T;
        using AllocatorType = AllocatorT;
        using SizeType      = ::int32;

        // =================================================================
        // Construction / destruction.
        // =================================================================

        // -----------------------------------------------------------------
        // Default ctor: empty array, no buffer allocated.
        //
        // Per Section 5.1 conceptual layout. The buffer is allocated
        // lazily on first Add/Reserve.
        // -----------------------------------------------------------------
        XPACT_FORCEINLINE TArrayCore() noexcept
            : m_data(nullptr)
            , m_num(0)
            , m_max(0)
            , m_alloc()
        {
        }

        // -----------------------------------------------------------------
        // Allocator-taking ctor: lets callers carry a custom FMemTag
        // (e.g., TArrayCore<float, DefaultAllocator>{DefaultAllocator{FMemTag::Math}}).
        // -----------------------------------------------------------------
        XPACT_FORCEINLINE explicit TArrayCore(const AllocatorT& InAlloc) noexcept
            : m_data(nullptr)
            , m_num(0)
            , m_max(0)
            , m_alloc(InAlloc)
        {
        }

        // -----------------------------------------------------------------
        // Destructor.
        //
        // Destroys every constructed element then deallocates the buffer.
        // The element destructors are skipped at the TArrayCore layer if
        // T is trivially destructible (optimisation; Section 5.5 row 3).
        // -----------------------------------------------------------------
        ~TArrayCore() noexcept
        {
            DestroyElementsAndFreeBuffer();
        }

        // -----------------------------------------------------------------
        // Copy ctor: deep copy of source.
        //
        // Section 5.1 wording for the public TArray says copy is deleted
        // for GC-aware specialisations; the non-GC TArrayCore preserves
        // copyability so non-GC TArray instantiations can leverage it
        // verbatim. The public TArray's GC-aware partial specialization
        // (TArray<T*, AllocatorT> where T : XObject) deletes its copy
        // ctor; that delete is at the public TArray layer, not here.
        // -----------------------------------------------------------------
        TArrayCore(const TArrayCore& Other)
            : m_data(nullptr)
            , m_num(0)
            , m_max(0)
            , m_alloc(Other.m_alloc)
        {
            if (Other.m_num > 0)
            {
                ReserveAtLeast(Other.m_num);
                CopyConstructRange(m_data, Other.m_data, Other.m_num);
                m_num = Other.m_num;
            }
        }

        // -----------------------------------------------------------------
        // Copy-assign: deep copy with strong exception guarantee.
        //
        // Self-assign-safe. Frees current contents first.
        // -----------------------------------------------------------------
        TArrayCore& operator=(const TArrayCore& Other)
        {
            if (this != &Other)
            {
                // Strong guarantee not required at the public surface
                // because OOM aborts (Section 4.1 / Section 5.1 OOM
                // contract); we destroy-and-reallocate.
                DestroyElementsAndFreeBuffer();
                m_alloc = Other.m_alloc;
                if (Other.m_num > 0)
                {
                    ReserveAtLeast(Other.m_num);
                    CopyConstructRange(m_data, Other.m_data, Other.m_num);
                    m_num = Other.m_num;
                }
            }
            return *this;
        }

        // -----------------------------------------------------------------
        // Move ctor: transfer ownership; leave source empty.
        //
        // noexcept: Section 5.1 wording "TArray(TArray&&) noexcept".
        // -----------------------------------------------------------------
        XPACT_FORCEINLINE TArrayCore(TArrayCore&& Other) noexcept
            : m_data(Other.m_data)
            , m_num(Other.m_num)
            , m_max(Other.m_max)
            , m_alloc(::std::move(Other.m_alloc))
        {
            Other.m_data = nullptr;
            Other.m_num  = 0;
            Other.m_max  = 0;
        }

        // -----------------------------------------------------------------
        // Move-assign: transfer ownership.
        //
        // noexcept: Section 5.1 wording. Self-move is well-defined as a
        // no-op (the if-check is the canonical safety).
        // -----------------------------------------------------------------
        XPACT_FORCEINLINE TArrayCore& operator=(TArrayCore&& Other) noexcept
        {
            if (this != &Other)
            {
                DestroyElementsAndFreeBuffer();
                m_data       = Other.m_data;
                m_num        = Other.m_num;
                m_max        = Other.m_max;
                m_alloc      = ::std::move(Other.m_alloc);
                Other.m_data = nullptr;
                Other.m_num  = 0;
                Other.m_max  = 0;
            }
            return *this;
        }

        // =================================================================
        // Capacity.
        // =================================================================

        // -----------------------------------------------------------------
        // Num -- current element count.
        // -----------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE ::int32 Num() const noexcept
        {
            return m_num;
        }

        // -----------------------------------------------------------------
        // Max -- current capacity (in elements).
        //
        // Note: this may be larger than the requested ReserveAtLeast
        // value because the underlying allocator's bin-rounding gives
        // back at least the requested bytes; the caller can either
        // re-derive the bin size from FMemory::GetAllocatedBytes or
        // accept the current Max as the effective capacity.
        //
        // Phase 1c contract: m_max reflects the value computed by
        // ComputeGrowCapacity (which is the "requested" capacity);
        // the actual allocation may be slightly larger. Future Phase
        // 1d may expose a "MaxActual" accessor for diagnostic use.
        // -----------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE ::int32 Max() const noexcept
        {
            return m_max;
        }

        // -----------------------------------------------------------------
        // IsEmpty -- convenience.
        // -----------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE bool IsEmpty() const noexcept
        {
            return m_num == 0;
        }

        // -----------------------------------------------------------------
        // IsValidIndex -- 0 <= Index < m_num.
        // -----------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE bool IsValidIndex(::int32 Index) const noexcept
        {
            return Index >= 0 && Index < m_num;
        }

        // -----------------------------------------------------------------
        // Reserve -- ensure capacity at least NewMax. No-op if NewMax <=
        // m_max.
        //
        // Per Section 5.1 / 5.5: Reserve grows but does not shrink. To
        // shrink, call Reset (with optional hint) or rebuild from scratch.
        // -----------------------------------------------------------------
        void Reserve(::int32 NewMax)
        {
            XPACT_CHECK(NewMax >= 0);
            if (NewMax > m_max)
            {
                ReserveAtLeast(NewMax);
            }
        }

        // -----------------------------------------------------------------
        // Reset -- destroy all elements; optionally drop the buffer if
        // NewCapacityHint < current Max.
        //
        // Section 5.1: `void Reset(int32 NewCapacityHint = 0)`. The
        // "hint" is non-binding: if NewCapacityHint > current Max we may
        // grow; if NewCapacityHint < current Max we DROP the buffer and
        // reallocate to the hint.
        //
        // Semantics: post-Reset, m_num == 0; m_max == 0 if hint is 0 OR
        // hint < old m_max, else m_max unchanged (kept the existing
        // buffer when hint requests retain).
        // -----------------------------------------------------------------
        void Reset(::int32 NewCapacityHint = 0) noexcept
        {
            // Destroy elements (skip for trivial types).
            DestroyElementsOnly();
            m_num = 0;

            // The hint asks for a new capacity. If hint == 0, free entirely.
            // If hint < current Max, drop the old buffer and allocate the
            // smaller one. If hint >= current Max, keep the buffer.
            if (NewCapacityHint == 0)
            {
                if (m_data != nullptr)
                {
                    m_alloc.Deallocate(m_data);
                    m_data = nullptr;
                    m_max  = 0;
                }
            }
            else if (NewCapacityHint < m_max)
            {
                // Drop the larger buffer; allocate the smaller hint.
                if (m_data != nullptr)
                {
                    m_alloc.Deallocate(m_data);
                    m_data = nullptr;
                    m_max  = 0;
                }
                ReserveAtLeast(NewCapacityHint);
            }
            // else: keep existing buffer (NewCapacityHint >= m_max).
        }

        // =================================================================
        // Element-level access.
        // =================================================================

        // -----------------------------------------------------------------
        // operator[] -- O(1) indexed access; Debug bounds-check only.
        //
        // Section 5.1 / 5.5 row 1: native-C++ caller-discipline. Sim-path
        // code uses At() instead; C# list[i] also lowers to At().
        // -----------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE T& operator[](::int32 Index) noexcept
        {
            XPACT_CHECK(IsValidIndex(Index));
            return m_data[Index];
        }

        [[nodiscard]] XPACT_FORCEINLINE const T& operator[](::int32 Index) const noexcept
        {
            XPACT_CHECK(IsValidIndex(Index));
            return m_data[Index];
        }

        // -----------------------------------------------------------------
        // At -- always-checked indexed access; returns Result-wrapped.
        //
        // Section 5.1 fix B-MIN5: returns
        // Result<std::reference_wrapper<T>, FBoundsError>. C# list[i]
        // lowers here (the cost is one branch on Shipping; the benefit
        // is C# language-level safety preserved across all configs per
        // Section 5.1 footnote A-MIN3).
        //
        // PHASE 1C STATUS: Result<T, E> is currently a polyfill
        // placeholder on C++20 toolchains (XResult.h). The function
        // signatures below COMPILE on any toolchain (they only mention
        // Result<...> in their return type without instantiating it
        // until called). Once the tl::expected polyfill lands the
        // function bodies will work end-to-end.
        // -----------------------------------------------------------------
        [[nodiscard]] ::XCore::Result<::std::reference_wrapper<T>, ::XCore::FBoundsError> At(::int32 Index) noexcept
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

        [[nodiscard]] ::XCore::Result<::std::reference_wrapper<const T>, ::XCore::FBoundsError> At(::int32 Index) const noexcept
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
            return ::std::reference_wrapper<const T>(m_data[Index]);
        }

        // -----------------------------------------------------------------
        // GetData -- raw pointer to the underlying buffer (may be nullptr).
        // -----------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE T* GetData() noexcept
        {
            return m_data;
        }

        [[nodiscard]] XPACT_FORCEINLINE const T* GetData() const noexcept
        {
            return m_data;
        }

        // =================================================================
        // Mutation.
        // =================================================================

        // -----------------------------------------------------------------
        // Add -- copy-construct a new element at the end.
        //
        // Returns the index of the new element.
        // -----------------------------------------------------------------
        ::int32 Add(const T& Value)
        {
            if (m_num >= m_max)
            {
                GrowByOne();
            }
            // Placement new at m_num.
            ::new (static_cast<void*>(m_data + m_num)) T(Value);
            return m_num++;
        }

        // -----------------------------------------------------------------
        // Emplace -- move-construct a new element at the end.
        //
        // Returns the index of the new element. The single-argument
        // version mirrors the spec body; a forwarding-Args variant could
        // ship in Phase 1d if call-site demand justifies it.
        // -----------------------------------------------------------------
        ::int32 Emplace(T&& Value)
        {
            if (m_num >= m_max)
            {
                GrowByOne();
            }
            ::new (static_cast<void*>(m_data + m_num)) T(::std::move(Value));
            return m_num++;
        }

        // -----------------------------------------------------------------
        // RemoveAt -- O(N) ordered removal. Shifts elements after the
        // removed slot one position toward the front.
        //
        // For trivially-relocatable types uses memmove; otherwise uses
        // a move loop. The removed element's destructor runs.
        // -----------------------------------------------------------------
        void RemoveAt(::int32 Index) noexcept(::std::is_nothrow_destructible_v<T>)
        {
            XPACT_CHECK(IsValidIndex(Index));

            // Destroy the element at Index.
            m_data[Index].~T();

            // Shift everything after Index down by one.
            const ::int32 NumToShift = m_num - Index - 1;
            if (NumToShift > 0)
            {
                if constexpr (TIsTriviallyRelocatable<T>::Value)
                {
                    ::std::memmove(
                        static_cast<void*>(m_data + Index),
                        static_cast<const void*>(m_data + Index + 1),
                        static_cast<::SIZE_T>(NumToShift) * sizeof(T));
                }
                else
                {
                    // Move-construct + destroy chain. This is the
                    // engineering-principles-correct path for self-
                    // referential types (Section 5.5 row 3); it's
                    // measurably slower than memmove on POD types,
                    // which is why the trait-gated specialisation above
                    // exists.
                    for (::int32 I = Index; I < m_num - 1; ++I)
                    {
                        ::new (static_cast<void*>(m_data + I)) T(::std::move(m_data[I + 1]));
                        m_data[I + 1].~T();
                    }
                }
            }
            --m_num;
        }

        // -----------------------------------------------------------------
        // RemoveAtSwap -- O(1) unordered removal. Moves the last element
        // into the removed slot. Section 5.1 wording: "ordering not
        // preserved".
        // -----------------------------------------------------------------
        void RemoveAtSwap(::int32 Index) noexcept(::std::is_nothrow_destructible_v<T> &&
                                                   ::std::is_nothrow_move_assignable_v<T>)
        {
            XPACT_CHECK(IsValidIndex(Index));

            const ::int32 LastIndex = m_num - 1;
            if (Index != LastIndex)
            {
                // Move the last element into the removed slot's storage.
                // The removed slot is destroyed first; the last element
                // moves into the now-raw slot via placement new.
                m_data[Index].~T();
                ::new (static_cast<void*>(m_data + Index)) T(::std::move(m_data[LastIndex]));
                m_data[LastIndex].~T();
            }
            else
            {
                // Removing the last element: just destroy it.
                m_data[LastIndex].~T();
            }
            --m_num;
        }

        // =================================================================
        // Phase 1d / Rev 1 audit HIGH-3 API surface expansion.
        // =================================================================
        //
        // The following methods bring TArrayCore in line with UE's
        // TArray surface (Runtime/Core/Public/Containers/Array.h) for
        // System 5+ readiness:
        //
        //   * Empty(NewSlack)     -- clear + reserve.
        //   * Pop(bAllowShrink)   -- remove + return last element.
        //   * Last(IdxFromEnd)    -- access last-N element by ref.
        //   * Top()               -- alias for Last(0); stack-style.
        //   * Append(...)         -- range append (3 overloads).
        //   * Insert(Value, Idx)  -- insert at position.
        //   * Find(Value)         -- linear search; returns index.
        //   * IndexOfByPredicate  -- predicate search; returns index.
        //   * Contains(Value)     -- alias for Find(x) != INDEX_NONE.
        //   * Swap(IdxA, IdxB)    -- swap two elements.
        //   * Sort() / Sort(P)    -- in-place sort.
        //   * StableSort(P)       -- in-place stable sort.
        //   * RemoveAll(P)        -- remove all matching predicate.
        //   * RemoveSingle(V)     -- remove first match by value.
        //   * HeapPush / HeapPop  -- binary-heap operations.
        //
        // Each method follows the same conventions as the existing
        // surface (XPACT_CHECK in Debug; trivially-relocatable
        // memmove fast path where possible; no Result<> at the
        // raw-index surface -- callers wanting bounds-checked access
        // use At()).
        // =================================================================

        // -----------------------------------------------------------------
        // Empty -- destroy all elements, optionally reserving NewSlack.
        //
        // Differs from Reset in semantics-only: Empty's documented
        // semantic is "clear and prepare for fresh population at
        // approximately NewSlack capacity"; Reset is "clear and keep
        // existing buffer if it fits". For all-zero NewSlack the two
        // behave identically; for NewSlack > current Max, Empty grows.
        // -----------------------------------------------------------------
        void Empty(::int32 NewSlack = 0)
        {
            XPACT_CHECK(NewSlack >= 0);
            DestroyElementsOnly();
            m_num = 0;

            if (NewSlack == 0)
            {
                if (m_data != nullptr)
                {
                    m_alloc.Deallocate(m_data);
                    m_data = nullptr;
                    m_max  = 0;
                }
            }
            else if (NewSlack > m_max)
            {
                // Grow up to NewSlack.
                ReserveAtLeast(NewSlack);
            }
            else if (NewSlack < m_max)
            {
                // Shrink to NewSlack: drop the larger buffer.
                if (m_data != nullptr)
                {
                    m_alloc.Deallocate(m_data);
                    m_data = nullptr;
                    m_max  = 0;
                }
                ReserveAtLeast(NewSlack);
            }
            // else NewSlack == m_max: keep existing buffer.
        }

        // -----------------------------------------------------------------
        // Pop -- remove and return the last element.
        //
        // Returns the value of the removed element (move-out).
        // Pre-condition: m_num > 0.
        //
        // bAllowShrinking is reserved for compat with UE's signature;
        // the current implementation does NOT shrink the buffer
        // because the underlying allocator's bin-rounding makes
        // shrink-after-Pop a perf trap. A future Phase 1d may honour
        // the hint via Reset.
        // -----------------------------------------------------------------
        T Pop(bool /*bAllowShrinking*/ = true) noexcept(::std::is_nothrow_move_constructible_v<T> &&
                                                        ::std::is_nothrow_destructible_v<T>)
        {
            XPACT_CHECK(m_num > 0);
            const ::int32 LastIndex = m_num - 1;
            T Result(::std::move(m_data[LastIndex]));
            m_data[LastIndex].~T();
            --m_num;
            return Result;
        }

        // -----------------------------------------------------------------
        // Last -- access the IndexFromEnd-th-from-last element by ref.
        //
        // IndexFromEnd == 0 returns the last element; 1 returns the
        // second-to-last; etc.
        // -----------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE T& Last(::int32 IndexFromEnd = 0) noexcept
        {
            XPACT_CHECK(IndexFromEnd >= 0);
            XPACT_CHECK(IndexFromEnd < m_num);
            return m_data[m_num - 1 - IndexFromEnd];
        }

        [[nodiscard]] XPACT_FORCEINLINE const T& Last(::int32 IndexFromEnd = 0) const noexcept
        {
            XPACT_CHECK(IndexFromEnd >= 0);
            XPACT_CHECK(IndexFromEnd < m_num);
            return m_data[m_num - 1 - IndexFromEnd];
        }

        // -----------------------------------------------------------------
        // Top -- alias for Last(0); stack-style nomenclature.
        // -----------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE T& Top() noexcept
        {
            return Last(0);
        }

        [[nodiscard]] XPACT_FORCEINLINE const T& Top() const noexcept
        {
            return Last(0);
        }

        // -----------------------------------------------------------------
        // Append (const TArrayCore&) -- copy-append another array's
        // elements to the tail of this one.
        //
        // Returns the index of the first appended element.
        // -----------------------------------------------------------------
        ::int32 Append(const TArrayCore& Other)
        {
            if (Other.m_num <= 0)
            {
                return m_num;
            }
            const ::int32 StartIndex = m_num;
            const ::int32 NewNum     = m_num + Other.m_num;
            if (NewNum > m_max)
            {
                ReserveAtLeast(NewNum);
            }
            CopyConstructRange(m_data + m_num, Other.m_data, Other.m_num);
            m_num = NewNum;
            return StartIndex;
        }

        // -----------------------------------------------------------------
        // Append (TArrayCore&&) -- move-append.
        //
        // Other is left empty after the call (its elements moved out).
        // Returns the index of the first appended element.
        // -----------------------------------------------------------------
        ::int32 Append(TArrayCore&& Other)
        {
            if (Other.m_num <= 0)
            {
                return m_num;
            }
            const ::int32 StartIndex = m_num;
            const ::int32 NewNum     = m_num + Other.m_num;
            if (NewNum > m_max)
            {
                ReserveAtLeast(NewNum);
            }
            // Move-construct each element.
            for (::int32 I = 0; I < Other.m_num; ++I)
            {
                ::new (static_cast<void*>(m_data + m_num + I)) T(::std::move(Other.m_data[I]));
                Other.m_data[I].~T();
            }
            m_num = NewNum;
            // Mark Other as empty; its destructor will release the
            // (now-empty) buffer.
            Other.m_num = 0;
            return StartIndex;
        }

        // -----------------------------------------------------------------
        // Append (const T*, int32 Count) -- raw-range copy-append.
        //
        // Returns the index of the first appended element. Count == 0
        // is a no-op (returns current m_num).
        // -----------------------------------------------------------------
        ::int32 Append(const T* Source, ::int32 Count)
        {
            XPACT_CHECK(Count >= 0);
            if (Count <= 0)
            {
                return m_num;
            }
            XPACT_CHECK(Source != nullptr);
            const ::int32 StartIndex = m_num;
            const ::int32 NewNum     = m_num + Count;
            if (NewNum > m_max)
            {
                ReserveAtLeast(NewNum);
            }
            CopyConstructRange(m_data + m_num, Source, Count);
            m_num = NewNum;
            return StartIndex;
        }

        // -----------------------------------------------------------------
        // Insert (const T&, int32 Index) -- copy-insert at position.
        //
        // 0 <= Index <= m_num. Insertion at Index == m_num is
        // equivalent to Add. Elements at positions Index..m_num-1
        // shift right by one to make room.
        // Returns the inserted element's index (== Index).
        // -----------------------------------------------------------------
        ::int32 Insert(const T& Value, ::int32 Index)
        {
            XPACT_CHECK(Index >= 0);
            XPACT_CHECK(Index <= m_num);

            if (m_num >= m_max)
            {
                GrowByOne();
            }

            // Shift elements [Index, m_num) right by one slot. The
            // tail slot at m_data[m_num] is raw uninitialised storage.
            const ::int32 NumToShift = m_num - Index;
            if (NumToShift > 0)
            {
                if constexpr (TIsTriviallyRelocatable<T>::Value)
                {
                    ::std::memmove(
                        static_cast<void*>(m_data + Index + 1),
                        static_cast<const void*>(m_data + Index),
                        static_cast<::SIZE_T>(NumToShift) * sizeof(T));
                }
                else
                {
                    // Move from the back forward to avoid clobbering.
                    // The last slot (m_data + m_num) is raw; move-construct
                    // there from m_data[m_num - 1], then move-assign down.
                    ::new (static_cast<void*>(m_data + m_num)) T(::std::move(m_data[m_num - 1]));
                    for (::int32 I = m_num - 1; I > Index; --I)
                    {
                        m_data[I] = ::std::move(m_data[I - 1]);
                    }
                    m_data[Index].~T();
                }
            }
            ::new (static_cast<void*>(m_data + Index)) T(Value);
            ++m_num;
            return Index;
        }

        // -----------------------------------------------------------------
        // Insert (T&&, int32 Index) -- move-insert at position.
        // -----------------------------------------------------------------
        ::int32 Insert(T&& Value, ::int32 Index)
        {
            XPACT_CHECK(Index >= 0);
            XPACT_CHECK(Index <= m_num);

            if (m_num >= m_max)
            {
                GrowByOne();
            }

            const ::int32 NumToShift = m_num - Index;
            if (NumToShift > 0)
            {
                if constexpr (TIsTriviallyRelocatable<T>::Value)
                {
                    ::std::memmove(
                        static_cast<void*>(m_data + Index + 1),
                        static_cast<const void*>(m_data + Index),
                        static_cast<::SIZE_T>(NumToShift) * sizeof(T));
                }
                else
                {
                    ::new (static_cast<void*>(m_data + m_num)) T(::std::move(m_data[m_num - 1]));
                    for (::int32 I = m_num - 1; I > Index; --I)
                    {
                        m_data[I] = ::std::move(m_data[I - 1]);
                    }
                    m_data[Index].~T();
                }
            }
            ::new (static_cast<void*>(m_data + Index)) T(::std::move(Value));
            ++m_num;
            return Index;
        }

        // -----------------------------------------------------------------
        // Find -- linear search for the first element equal to Value.
        //
        // Returns the index of the first match, or INDEX_NONE if not
        // found. Uses operator== on T.
        // -----------------------------------------------------------------
        [[nodiscard]] ::int32 Find(const T& Value) const noexcept
        {
            for (::int32 I = 0; I < m_num; ++I)
            {
                if (m_data[I] == Value)
                {
                    return I;
                }
            }
            return ::XCore::INDEX_NONE;
        }

        // -----------------------------------------------------------------
        // IndexOfByPredicate -- linear search by predicate.
        //
        // Returns the index of the first element for which Pred(elem)
        // returns true, or INDEX_NONE if none match.
        // -----------------------------------------------------------------
        template<typename Predicate>
        [[nodiscard]] ::int32 IndexOfByPredicate(Predicate Pred) const
        {
            for (::int32 I = 0; I < m_num; ++I)
            {
                if (Pred(m_data[I]))
                {
                    return I;
                }
            }
            return ::XCore::INDEX_NONE;
        }

        // -----------------------------------------------------------------
        // FindByPredicate -- linear search by predicate; returns pointer.
        //
        // Returns a pointer to the first matching element, or nullptr
        // if none match. Mirrors UE's TArray::FindByPredicate.
        // -----------------------------------------------------------------
        template<typename Predicate>
        [[nodiscard]] T* FindByPredicate(Predicate Pred)
        {
            for (::int32 I = 0; I < m_num; ++I)
            {
                if (Pred(m_data[I]))
                {
                    return m_data + I;
                }
            }
            return nullptr;
        }

        template<typename Predicate>
        [[nodiscard]] const T* FindByPredicate(Predicate Pred) const
        {
            for (::int32 I = 0; I < m_num; ++I)
            {
                if (Pred(m_data[I]))
                {
                    return m_data + I;
                }
            }
            return nullptr;
        }

        // -----------------------------------------------------------------
        // Contains -- linear search by value; bool.
        //
        // Convenience alias for Find(Value) != INDEX_NONE.
        // -----------------------------------------------------------------
        [[nodiscard]] bool Contains(const T& Value) const noexcept
        {
            return Find(Value) != ::XCore::INDEX_NONE;
        }

        // -----------------------------------------------------------------
        // Swap -- swap two elements by index (in-place).
        //
        // Both indices must be valid; same-index is a no-op.
        // -----------------------------------------------------------------
        void Swap(::int32 IndexA, ::int32 IndexB) noexcept(::std::is_nothrow_swappable_v<T>)
        {
            XPACT_CHECK(IsValidIndex(IndexA));
            XPACT_CHECK(IsValidIndex(IndexB));
            if (IndexA != IndexB)
            {
                using ::std::swap;
                swap(m_data[IndexA], m_data[IndexB]);
            }
        }

        // -----------------------------------------------------------------
        // Sort -- in-place sort using a user-supplied predicate.
        //
        // The predicate must define a strict-weak ordering: a Pred(a, b)
        // returning true iff a < b (operator<-like).
        //
        // Sort is NOT guaranteed stable; use StableSort if equal
        // elements must preserve insertion order.
        // -----------------------------------------------------------------
        template<typename Predicate>
        void Sort(Predicate Pred)
        {
            ::std::sort(m_data, m_data + m_num, Pred);
        }

        // -----------------------------------------------------------------
        // Sort -- in-place sort using operator<.
        // -----------------------------------------------------------------
        void Sort()
        {
            ::std::sort(m_data, m_data + m_num, ::std::less<T>());
        }

        // -----------------------------------------------------------------
        // StableSort -- in-place stable sort with user predicate.
        //
        // Equal elements retain their relative insertion order.
        // -----------------------------------------------------------------
        template<typename Predicate>
        void StableSort(Predicate Pred)
        {
            ::std::stable_sort(m_data, m_data + m_num, Pred);
        }

        // -----------------------------------------------------------------
        // StableSort -- in-place stable sort with operator<.
        // -----------------------------------------------------------------
        void StableSort()
        {
            ::std::stable_sort(m_data, m_data + m_num, ::std::less<T>());
        }

        // -----------------------------------------------------------------
        // RemoveAll -- remove every element matching predicate.
        //
        // Returns the number of elements removed. Order of remaining
        // elements is preserved (the implementation uses an
        // erase-remove-like compaction pattern with explicit
        // destruction of removed elements).
        // -----------------------------------------------------------------
        template<typename Predicate>
        ::int32 RemoveAll(Predicate Pred)
        {
            ::int32 WriteIdx = 0;
            for (::int32 ReadIdx = 0; ReadIdx < m_num; ++ReadIdx)
            {
                if (Pred(m_data[ReadIdx]))
                {
                    // Element matches; destroy and skip.
                    m_data[ReadIdx].~T();
                }
                else
                {
                    if (WriteIdx != ReadIdx)
                    {
                        // Move the kept element into the WriteIdx slot.
                        ::new (static_cast<void*>(m_data + WriteIdx)) T(::std::move(m_data[ReadIdx]));
                        m_data[ReadIdx].~T();
                    }
                    ++WriteIdx;
                }
            }
            const ::int32 Removed = m_num - WriteIdx;
            m_num = WriteIdx;
            return Removed;
        }

        // -----------------------------------------------------------------
        // RemoveSingle -- remove the first element equal to Value.
        //
        // Returns 1 if an element was removed, 0 otherwise. Uses
        // operator== on T.
        // -----------------------------------------------------------------
        ::int32 RemoveSingle(const T& Value) noexcept(::std::is_nothrow_destructible_v<T>)
        {
            const ::int32 Idx = Find(Value);
            if (Idx == ::XCore::INDEX_NONE)
            {
                return 0;
            }
            RemoveAt(Idx);
            return 1;
        }

        // -----------------------------------------------------------------
        // HeapPush -- insert into a binary-heap-ordered array.
        //
        // The array is assumed to satisfy the heap property under Pred
        // BEFORE the call. After the call the heap property is
        // restored with Value as a participant.
        //
        // Pred is the strict-weak-ordering comparator (a < b returns
        // true iff a < b). std::push_heap builds a max-heap by
        // Pred; the top is the GREATEST element.
        // -----------------------------------------------------------------
        template<typename Predicate>
        ::int32 HeapPush(T&& Value, Predicate Pred)
        {
            const ::int32 Idx = Emplace(::std::move(Value));
            ::std::push_heap(m_data, m_data + m_num, Pred);
            return Idx;
        }

        template<typename Predicate>
        ::int32 HeapPush(const T& Value, Predicate Pred)
        {
            const ::int32 Idx = Add(Value);
            ::std::push_heap(m_data, m_data + m_num, Pred);
            return Idx;
        }

        // -----------------------------------------------------------------
        // HeapPop -- pop the top (greatest by Pred) element from the heap.
        //
        // Returns the popped element. Pre-condition: m_num > 0.
        // -----------------------------------------------------------------
        template<typename Predicate>
        T HeapPop(Predicate Pred) noexcept(::std::is_nothrow_move_constructible_v<T> &&
                                            ::std::is_nothrow_destructible_v<T>)
        {
            XPACT_CHECK(m_num > 0);
            ::std::pop_heap(m_data, m_data + m_num, Pred);
            return Pop(true);
        }

        // =================================================================
        // Iteration.
        // =================================================================

        [[nodiscard]] XPACT_FORCEINLINE T* begin()       noexcept { return m_data; }
        [[nodiscard]] XPACT_FORCEINLINE T* end()         noexcept { return m_data + m_num; }
        [[nodiscard]] XPACT_FORCEINLINE const T* begin() const noexcept { return m_data; }
        [[nodiscard]] XPACT_FORCEINLINE const T* end()   const noexcept { return m_data + m_num; }
        [[nodiscard]] XPACT_FORCEINLINE const T* cbegin() const noexcept { return m_data; }
        [[nodiscard]] XPACT_FORCEINLINE const T* cend()   const noexcept { return m_data + m_num; }

        // =================================================================
        // Internal grow protocol (used by Phase 1d FString).
        //
        // The dispatch's "internal grow protocol used by Phase 1d
        // FString" comment names this as `__GrowByOne` (double-underscore
        // prefix, matching the engine-internal-bootstrap convention used
        // by `FMemory::__Init`). FString::AppendCodepoint will call this
        // when appending one or more bytes; the FString layer handles
        // its own m_num bookkeeping. To keep the contract surface
        // discoverable for FString, the method is public but the name
        // signals "engine-internal" (user-tier code should use Add /
        // Emplace).
        // =================================================================

        // -----------------------------------------------------------------
        // __GrowByOne -- grow the buffer by at least one element's slot.
        //
        // Used by Phase 1d's FString::AppendByte / AppendCodepoint when
        // it wants to ensure capacity for one more byte without going
        // through Add (which would also copy-construct the element).
        //
        // Post-condition: m_max > m_num. The caller is responsible for
        // either constructing or skipping the slot at m_data[m_num] and
        // adjusting m_num accordingly.
        // -----------------------------------------------------------------
        void __GrowByOne()
        {
            GrowByOne();
        }

        // -----------------------------------------------------------------
        // __BumpNumUninit -- bump m_num by 1 without writing anything.
        //
        // Used by the GC-aware TArray<T*> partial specialization (TArray.h
        // Section 5.4): the caller has already invoked XGC_WriteBarrier
        // and written the new pointer value into the tail slot; this
        // method finalizes the count.
        //
        // Pre-condition: m_num < m_max (the caller has grown the buffer
        // if needed via __GrowByOne).
        // -----------------------------------------------------------------
        void __BumpNumUninit() noexcept
        {
            XPACT_CHECK(m_num < m_max);
            ++m_num;
        }

        // -----------------------------------------------------------------
        // __SetData -- raw write to the buffer at m_data[Index] without
        // a destructor or constructor invocation.
        //
        // Used by the GC-aware TArray<T*> partial specialization for the
        // Set(int32, T*) accessor. Caller has already invoked the barrier
        // and is overwriting a fresh pointer value into an existing slot.
        // For non-trivially-relocatable T this method is INVALID (the
        // old value's destructor is not called, the new value's
        // constructor is not called) -- the GC-aware specialization is
        // T = pointer-type only, so this is safe by construction.
        //
        // The method is XPACT_CHECK-bounded so an out-of-range index
        // aborts cleanly in Debug.
        // -----------------------------------------------------------------
        void __SetDataAtIndexRaw(::int32 Index, const T& NewValue) noexcept
        {
            XPACT_CHECK(IsValidIndex(Index));
            m_data[Index] = NewValue;
        }

        // -----------------------------------------------------------------
        // Allocator accessor for callers that need it (notably the
        // GC-aware partial specialization in TArray.h needs to thread
        // the allocator through).
        // -----------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE AllocatorT& GetAllocator() noexcept
        {
            return m_alloc;
        }

        [[nodiscard]] XPACT_FORCEINLINE const AllocatorT& GetAllocator() const noexcept
        {
            return m_alloc;
        }

    private:
        // =================================================================
        // Internal helpers.
        // =================================================================

        // -----------------------------------------------------------------
        // ReserveAtLeast -- ensure m_max >= NewMax; allocate / relocate
        // as needed.
        //
        // The growth formula is applied by callers (Add path); Reserve
        // takes the explicit NewMax verbatim. This helper does the
        // allocation + relocation under whichever NewMax the caller
        // supplies; it doesn't apply the 1.375x grow formula itself.
        // -----------------------------------------------------------------
        void ReserveAtLeast(::int32 NewMax)
        {
            if (NewMax <= m_max)
            {
                return;
            }

            const ::SIZE_T NewSizeBytes = static_cast<::SIZE_T>(NewMax) * sizeof(T);
            const ::SIZE_T Align        = alignof(T) < 8 ? 8 : alignof(T);

            T* NewData = nullptr;

            if constexpr (TIsTriviallyRelocatable<T>::Value)
            {
                // Realloc path: the allocator can in-place-resize when
                // the bin accommodates, and the byte-copy is equivalent
                // to memmove for trivially-relocatable types. This is
                // the common path for the POD types (int, float,
                // pointer, etc.).
                NewData = static_cast<T*>(
                    m_alloc.Reallocate(static_cast<void*>(m_data), NewSizeBytes, Align));
            }
            else
            {
                // Move-construct path: allocate a new block, move each
                // element, destroy the originals, free the old block.
                NewData = static_cast<T*>(m_alloc.Allocate(NewSizeBytes, Align));
                if (m_data != nullptr)
                {
                    // Move-construct existing elements into the new
                    // buffer. The existing elements' destructors run
                    // after the move (typical move-from-stable-state
                    // contract).
                    for (::int32 I = 0; I < m_num; ++I)
                    {
                        ::new (static_cast<void*>(NewData + I)) T(::std::move(m_data[I]));
                        m_data[I].~T();
                    }
                    m_alloc.Deallocate(m_data);
                }
            }

            m_data = NewData;
            m_max  = NewMax;
        }

        // -----------------------------------------------------------------
        // GrowByOne -- compute the next grow capacity and reserve to it.
        //
        // Used by Add / Emplace / __GrowByOne. The new capacity is
        // computed by ComputeGrowCapacity above (1.375x + min 4 +
        // constant 16).
        // -----------------------------------------------------------------
        void GrowByOne()
        {
            const ::int32 RequestedNum = m_num + 1;
            const ::int32 NewMax       = ComputeGrowCapacity(RequestedNum, m_max);
            ReserveAtLeast(NewMax);
        }

        // -----------------------------------------------------------------
        // CopyConstructRange -- copy-construct Count elements from Src
        // into Dst.
        //
        // For trivially-copyable types, memcpy. Else, a copy-construct
        // loop.
        // -----------------------------------------------------------------
        static void CopyConstructRange(T* Dst, const T* Src, ::int32 Count)
        {
            if constexpr (::std::is_trivially_copyable_v<T>)
            {
                ::std::memcpy(
                    static_cast<void*>(Dst),
                    static_cast<const void*>(Src),
                    static_cast<::SIZE_T>(Count) * sizeof(T));
            }
            else
            {
                for (::int32 I = 0; I < Count; ++I)
                {
                    ::new (static_cast<void*>(Dst + I)) T(Src[I]);
                }
            }
        }

        // -----------------------------------------------------------------
        // DestroyElementsOnly -- run destructors but leave the buffer
        // allocated. Used by Reset / copy-assign.
        // -----------------------------------------------------------------
        void DestroyElementsOnly() noexcept
        {
            if constexpr (!::std::is_trivially_destructible_v<T>)
            {
                for (::int32 I = 0; I < m_num; ++I)
                {
                    m_data[I].~T();
                }
            }
            // m_num is reset by the caller (Reset sets it to 0 immediately).
        }

        // -----------------------------------------------------------------
        // DestroyElementsAndFreeBuffer -- destruct elements + free
        // buffer; reset all fields to empty.
        // -----------------------------------------------------------------
        void DestroyElementsAndFreeBuffer() noexcept
        {
            DestroyElementsOnly();
            if (m_data != nullptr)
            {
                m_alloc.Deallocate(m_data);
                m_data = nullptr;
            }
            m_num = 0;
            m_max = 0;
        }

        // =================================================================
        // Data members (Section 5.1 conceptual layout).
        // =================================================================

        T* m_data;        // Buffer base; nullptr when m_max == 0.
        ::int32 m_num;    // Element count (always >= 0).
        ::int32 m_max;    // Capacity in elements (always >= m_num).

        // XPACT_NO_UNIQUE_ADDRESS folds a stateless allocator (the default
        // 2-byte DefaultAllocator with no_unique_address can collapse into
        // the trailing 4 bytes of padding behind m_max when the compiler
        // chooses; with no_unique_address even an empty allocator type
        // costs nothing).
        XPACT_NO_UNIQUE_ADDRESS AllocatorT m_alloc;
    };

} // namespace XCore::Detail
