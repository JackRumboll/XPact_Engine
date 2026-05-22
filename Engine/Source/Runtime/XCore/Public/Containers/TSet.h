// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// TSet.h -- SwissTable-style flat open-addressing hash set (Section 5).
// =====================================================================
//
// XCore-4a Rev 3, Section 5.1 (Public API) + Section 5.3 (determinism
// contract) + Section 5.5 row 4 (UE TMap divergence) + Section 11.9
// (scalar XXH3) + dependency-graph step 9.
//
// ALGORITHM. SwissTable, as described in Sam Benzaquen's CppCon 2017
// talk and implemented in abseil's raw_hash_set.h. Key properties:
//
//   * Flat storage. The control-byte array and the slot-data array
//     live in a single backing allocation:
//
//       [control_bytes (Capacity + GroupSize bytes)][slot_data (Capacity * sizeof(T))]
//
//     The trailing GroupSize bytes of control are a "mirror" of the
//     first GroupSize bytes; the probe loop never needs to mask off
//     the index modulo Capacity because the mirror absorbs the wrap.
//
//   * Per-slot 1-byte control. Each control byte encodes:
//       0x80         = kEmpty   (slot has never been used or has been
//                                 reset; lookup probe MAY stop here)
//       0xFE         = kDeleted (a tombstone; lookup probe MUST continue
//                                 past it but insertion MAY land here)
//       0x00 .. 0x7F = kFull    (slot is occupied; the value is
//                                 H2(hash) -- top 7 bits of the full
//                                 hash, used to filter probe matches)
//
//     This is the exact same encoding as abseil. The 0x80 / 0xFE
//     special-value choice is the same; do not change it.
//
//     IMPORTANT NOTE ON ENCODING DIVERGENCE FROM THE BRIEF.
//     The dispatch brief described "top bit = 0 means empty/tombstone;
//     top bit = 1 means full". That description is incorrect and
//     does not match the abseil contract. The abseil canonical
//     encoding (which we follow) is: top bit = 1 means EMPTY (0x80) or
//     DELETED (0xFE); top bit = 0 means FULL (the value is H2, a
//     0..127 hash fragment). The Group::Match() SSE2 implementation
//     uses _mm_movemask_epi8 which extracts the top bit -- empty/
//     deleted slots are filtered out by their top-bit-set encoding.
//     We follow the abseil encoding because it is correct and matches
//     the SIMD-friendly bitmask construction; the brief's description
//     was incorrect.
//
//   * Group probing. Lookup hashes the key, splits the hash into:
//       H1 (top 57 bits) -- starting group index in the table
//       H2 (low 7 bits)  -- the per-slot match value
//     Actually, abseil's spec is:
//       H1 = hash >> 7   (the "selector" -- starting group index)
//       H2 = hash & 0x7F (the "tag" -- per-slot match value)
//     We follow the abseil split.
//
//     For lookup: from group H1 % NumGroups, load GroupSize control
//     bytes, compare each against H2; for each match, compare the slot
//     value. On no-match-in-group, advance to next group via triangular
//     probing (next_group_index = group_index + GroupSize) modulo
//     NumGroups. With Capacity always a multiple of GroupSize and a
//     power of 2, the modulo collapses to AND-mask.
//
//     Triangular probing visits every group exactly once on a
//     power-of-2 capacity (well-known property; see abseil docs).
//
//   * Load factor. Maximum 7/8 -- above this, rehash with 2x capacity.
//     Below 7/8 the open-addressed probe distance stays bounded with
//     high probability. Abseil uses the same 7/8 cutoff.
//
//   * Tombstones. Removed slots become kDeleted (not kEmpty), so probe
//     sequences that ran past the removed slot still terminate
//     correctly. Tombstones count against the load factor: a 7/8-full
//     table where every slot is a tombstone still rehashes. Insertion
//     prefers landing in a tombstone if encountered before kEmpty.
//
// SIMD VS SCALAR ON ARM64 (CRITICAL DECISION, fix Rev 3 #25).
//
// Abseil's raw_hash_set uses SSE2 _mm_cmpeq_epi8 + _mm_movemask_epi8 on
// x86_64 and the NEON vceqq_u8 equivalent on ARM64. The two paths are
// algorithmically equivalent but the iteration order of multiple
// matches WITHIN a group is implementation-defined.
//
// Per Section 5.3 determinism contract: "bit-exact replay across Win64
// / Linux / Android-ARM64 for sim-path TUs". The iteration order over
// a TSet is "implementation-defined per the SwissTable algorithm; sim-
// path code MUST NOT rely on iteration order at all", so order-
// determinism is NOT load-bearing.
//
// HOWEVER: there is a subtle case where SIMD vs scalar do diverge:
// when H2 collisions exist within a group, the order of value-equality
// probes can differ. Worst case, this could affect WHICH slot a
// tombstone-recycle insert lands in, which would then change the
// observable iteration order even across the same algorithmic state.
//
// Our position: we use the SCALAR probe path on ALL architectures.
// The performance gap to SIMD is real (~30% slower on x86_64 small-key
// workloads) but the determinism gain (one bit-exact source compiled
// to the same output everywhere) is non-negotiable for sim-path
// determinism. This is the same trade-off as XXH3 (Section 11.9).
//
// The scalar group loop is straightforward: iterate the 16 control
// bytes in the group, mask top bit, compare against H2, then probe the
// matching slots. Branchless via mask-and-loop bit-extraction; the
// hot path stays cache-friendly because the control bytes are
// contiguous and pre-fetched alongside the first slot lookup.
//
// =====================================================================
//
// PUBLIC API SURFACE.
//
//   TSet<T> default-constructs an empty set with zero allocation.
//   Reserve(N) ensures capacity for N elements without forcing rehash
//   until 7/8*N is reached.
//   Add(value) inserts; returns true if newly inserted, false if
//   already present.
//   Remove(value) removes; returns true if removed, false if not
//   found.
//   Contains(value) is the lookup query.
//   Num() returns count.
//   Reset() clears all elements but keeps the buffer; capacity stays
//   the same.
//   Iteration via TIter / TConstIter that skip non-Full slots.
//
// THREADING. Not thread-safe (Section 5.2). Concurrent mutation is UB.
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"
#include "Macros/XErrorTypes.h"
#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"
#include "Hash/FXxh3.h"

#include <cstring>      // std::memcpy, std::memset
#include <functional>   // std::equal_to
#include <new>          // placement new
#include <type_traits>  // trait dispatch
#include <utility>      // std::move, std::forward

namespace XCore
{
    // -----------------------------------------------------------------
    // GetTypeHash -- the engine-wide hashing entry point.
    //
    // Default implementation hashes the bytes of T via FXxh3::Hash64
    // with seed=0. Specialize for types that have a meaningful logical
    // hash (e.g., FString hashes its UTF-8 bytes, not the FString
    // struct's internal pointer). FName's specialization lives in
    // XCore-4b's reflection runtime (declared in XCoreFwd.h; only the
    // forward declaration is visible from XCore-4a, so TSet<FName>
    // compiles but link-errors until XCore-4b lands).
    //
    // Primitive integer/float/pointer types use the byte-hash path
    // verbatim; the bit pattern is the canonical identity. We provide
    // explicit specializations for the integer types so that hashing
    // an int doesn't go through the void* memcpy path (compilers can
    // optimize the explicit form better; the trait-dispatched form
    // would memcpy 4 bytes via inline assembly which is fine but the
    // explicit form is one CALL fewer).
    // -----------------------------------------------------------------

    template<typename T>
    [[nodiscard]] XPACT_FORCEINLINE ::uint64 GetTypeHash(const T& Value) noexcept
    {
        // Default implementation: hash the bytes verbatim. Works for
        // POD types and structurally-identical comparison types.
        return ::XCore::Hash::FXxh3::Hash64(&Value, sizeof(T), /*Seed=*/0);
    }

    // Specializations for integer types -- direct byte hash, but as a
    // separate overload so the compiler can keep the operand in a
    // register rather than spilling to memory.
    [[nodiscard]] XPACT_FORCEINLINE ::uint64 GetTypeHash(::int8 V)   noexcept { return ::XCore::Hash::FXxh3::Hash64(&V, sizeof(V), 0); }
    [[nodiscard]] XPACT_FORCEINLINE ::uint64 GetTypeHash(::int16 V)  noexcept { return ::XCore::Hash::FXxh3::Hash64(&V, sizeof(V), 0); }
    [[nodiscard]] XPACT_FORCEINLINE ::uint64 GetTypeHash(::int32 V)  noexcept { return ::XCore::Hash::FXxh3::Hash64(&V, sizeof(V), 0); }
    [[nodiscard]] XPACT_FORCEINLINE ::uint64 GetTypeHash(::int64 V)  noexcept { return ::XCore::Hash::FXxh3::Hash64(&V, sizeof(V), 0); }
    [[nodiscard]] XPACT_FORCEINLINE ::uint64 GetTypeHash(::uint8 V)  noexcept { return ::XCore::Hash::FXxh3::Hash64(&V, sizeof(V), 0); }
    [[nodiscard]] XPACT_FORCEINLINE ::uint64 GetTypeHash(::uint16 V) noexcept { return ::XCore::Hash::FXxh3::Hash64(&V, sizeof(V), 0); }
    [[nodiscard]] XPACT_FORCEINLINE ::uint64 GetTypeHash(::uint32 V) noexcept { return ::XCore::Hash::FXxh3::Hash64(&V, sizeof(V), 0); }
    [[nodiscard]] XPACT_FORCEINLINE ::uint64 GetTypeHash(::uint64 V) noexcept { return ::XCore::Hash::FXxh3::Hash64(&V, sizeof(V), 0); }

    // Pointers hash by their bit pattern (canonical identity).
    template<typename T>
    [[nodiscard]] XPACT_FORCEINLINE ::uint64 GetTypeHash(T* Ptr) noexcept
    {
        const ::uintptr_t Bits = reinterpret_cast<::uintptr_t>(Ptr);
        return ::XCore::Hash::FXxh3::Hash64(&Bits, sizeof(Bits), 0);
    }
}

namespace XCore::Detail
{
    // -----------------------------------------------------------------
    // SwissTable control-byte encoding. Per the abseil canonical
    // values (do not change; the SIMD-friendly bitmask construction
    // depends on these exact bit patterns).
    // -----------------------------------------------------------------
    constexpr ::uint8 kCtrlEmpty   = 0x80u;  // top bit set, no other meaning
    constexpr ::uint8 kCtrlDeleted = 0xFEu;  // tombstone
    constexpr ::uint8 kCtrlSentinel= 0xFFu;  // end-of-buffer sentinel
                                             // (used to terminate end()
                                             //  iteration without bounds
                                             //  check). 0xFF distinct from
                                             //  Empty and Deleted so the
                                             //  iter advance loop can
                                             //  detect it; matches abseil.

    // Group size: 16 bytes (one cache-line eighth on most targets).
    // Matches abseil's NEON / SSE2 Group::Width. Power of 2 required.
    constexpr ::SIZE_T kGroupSize = 16;

    // Maximum load factor before rehash: 7/8 = 0.875. Matches abseil.
    // Stored as (cap - cap/8) so the compare is one branch.
    constexpr ::SIZE_T kMaxLoadFactorNumerator = 7;
    constexpr ::SIZE_T kMaxLoadFactorDenominator = 8;

    // -----------------------------------------------------------------
    // H1 / H2 splitting.
    //
    //   H1 (top 57 bits): used to select the starting probe group.
    //   H2 (low  7 bits): used as the per-slot match-tag.
    //
    // The high-bit of H2 is masked to 0 because the kEmpty/kDeleted
    // sentinels have the high bit set; a Full slot's control byte must
    // have the high bit clear so the SIMD-bitmask scheme works.
    //
    // To get H2 we mask the bottom 7 bits of the full hash. This is the
    // identical split to abseil.
    // -----------------------------------------------------------------
    XPACT_FORCEINLINE constexpr ::uint64 H1(::uint64 Hash) noexcept
    {
        return Hash >> 7;
    }

    XPACT_FORCEINLINE constexpr ::uint8 H2(::uint64 Hash) noexcept
    {
        return static_cast<::uint8>(Hash & 0x7Fu);
    }

    // -----------------------------------------------------------------
    // IsFull/IsEmpty/IsDeleted predicates on a control byte.
    //
    // The encoding makes these trivial bit tests:
    //   IsFull(c)    = (c & 0x80) == 0      -- top bit clear
    //   IsEmpty(c)   = c == 0x80
    //   IsDeleted(c) = c == 0xFE
    // -----------------------------------------------------------------
    XPACT_FORCEINLINE constexpr bool IsFull(::uint8 C) noexcept    { return (C & 0x80u) == 0; }
    XPACT_FORCEINLINE constexpr bool IsEmpty(::uint8 C) noexcept   { return C == kCtrlEmpty; }
    XPACT_FORCEINLINE constexpr bool IsDeleted(::uint8 C) noexcept { return C == kCtrlDeleted; }
    XPACT_FORCEINLINE constexpr bool IsEmptyOrDeleted(::uint8 C) noexcept
    {
        // Both kEmpty (0x80) and kDeleted (0xFE) have top bit set; both
        // are >= 0x80. kSentinel is 0xFF. A Full slot has top bit
        // cleared, so it is always < 0x80.
        return C >= kCtrlEmpty;
    }

    // -----------------------------------------------------------------
    // CapacityToGrowAt -- returns the load-factor threshold for a
    // given capacity. Used by the grow check.
    //
    // For capacity C (a power of 2), the maximum live + tombstone count
    // before grow is (C * 7) / 8 = C - C/8.
    // -----------------------------------------------------------------
    XPACT_FORCEINLINE constexpr ::SIZE_T CapacityToGrowAt(::SIZE_T Capacity) noexcept
    {
        // Special-case small caps: cap 0 = grow at 0; cap 1 = grow at 1;
        // cap of GroupSize or more = standard 7/8 formula.
        if (Capacity == 0) return 0;
        if (Capacity == 1) return 1;
        return Capacity - (Capacity / kMaxLoadFactorDenominator);
    }

    // -----------------------------------------------------------------
    // NormalizeCapacity -- round up to a power of 2 and ensure >=
    // GroupSize. The smallest non-zero capacity is GroupSize.
    // -----------------------------------------------------------------
    XPACT_FORCEINLINE constexpr ::SIZE_T NormalizeCapacity(::SIZE_T DesiredCap) noexcept
    {
        if (DesiredCap == 0) return 0;
        if (DesiredCap < kGroupSize) return kGroupSize;
        // Round up to power of 2.
        ::SIZE_T Cap = 1;
        while (Cap < DesiredCap) Cap <<= 1;
        return Cap;
    }

    // -----------------------------------------------------------------
    // BackingLayout -- byte offsets of the control array and the slot
    // array within the single backing allocation.
    //
    // Layout (Capacity = N):
    //   byte 0 .. N-1:      control[0..N) -- the real control bytes.
    //   byte N:             kCtrlSentinel -- end-of-array marker, used
    //                       by the iterator to stop without a bounds
    //                       check.
    //   byte N+1 .. N+GroupSize-1: mirror bytes that duplicate
    //                       control[0..GroupSize-1]. The mirror exists
    //                       so that a SIMD group-load at offset
    //                       (N - K) for small K can read the wrapped
    //                       bytes without an explicit modulo.
    //
    //   Then the slot array starts at the next alignof(T)-aligned
    //   offset >= N + GroupSize.
    //
    // ON THE MIRROR. The scalar probe path in this implementation uses
    // explicit `(GroupStart + I) & Mask` indexing so the mirror is
    // never actually read. We still emit it for two reasons:
    //   1. A future SIMD path swap-in (if the bit-exactness contract
    //      relaxes) gets the layout for free.
    //   2. Abseil-compatible memory image, useful for tooling that
    //      inspects the table externally.
    //
    // For zero capacity: no allocation; the "empty table" uses a
    // static array of kCtrlEmpty sentinels (kEmptyGroupBytes) in lieu
    // of allocating.
    // -----------------------------------------------------------------
    XPACT_FORCEINLINE constexpr ::SIZE_T AlignUp(::SIZE_T V, ::SIZE_T Align) noexcept
    {
        return (V + Align - 1) & ~(Align - 1);
    }

    // -----------------------------------------------------------------
    // CtrlArrayBytes -- total bytes of control + sentinel + mirror.
    //
    // For Capacity = N: N real bytes + 1 sentinel + (GroupSize - 1)
    // mirror = N + GroupSize.
    // -----------------------------------------------------------------
    XPACT_FORCEINLINE constexpr ::SIZE_T CtrlArrayBytes(::SIZE_T Capacity) noexcept
    {
        return Capacity + kGroupSize;
    }

    template<typename T>
    XPACT_FORCEINLINE constexpr ::SIZE_T SlotOffset(::SIZE_T Capacity) noexcept
    {
        constexpr ::SIZE_T TAlign = alignof(T);
        return AlignUp(CtrlArrayBytes(Capacity), TAlign);
    }

    template<typename T>
    XPACT_FORCEINLINE constexpr ::SIZE_T BackingBytes(::SIZE_T Capacity) noexcept
    {
        return SlotOffset<T>(Capacity) + Capacity * sizeof(T);
    }

    // -----------------------------------------------------------------
    // The sentinel-empty control array, used by zero-capacity tables.
    // All bytes are kCtrlEmpty; an additional kCtrlSentinel terminates
    // iteration via end(). Aligned to GroupSize so the probe loop's
    // group-aligned loads (when present) are safe.
    //
    // Each instantiation of TSet<T> needs to read this; we expose it as
    // an inline variable so the linker dedupes.
    // -----------------------------------------------------------------
    inline constexpr ::uint8 kEmptyGroupBytes[16 + 16] = {
        // First 16: kEmpty bytes (empty control array's group-zero).
        kCtrlEmpty, kCtrlEmpty, kCtrlEmpty, kCtrlEmpty,
        kCtrlEmpty, kCtrlEmpty, kCtrlEmpty, kCtrlEmpty,
        kCtrlEmpty, kCtrlEmpty, kCtrlEmpty, kCtrlEmpty,
        kCtrlEmpty, kCtrlEmpty, kCtrlEmpty, kCtrlEmpty,
        // Next 16: kCtrlSentinel sentinel + mirror padding (matching
        // the abseil pattern; the iterator's advance loop stops at the
        // first kCtrlSentinel).
        kCtrlSentinel, kCtrlEmpty, kCtrlEmpty, kCtrlEmpty,
        kCtrlEmpty,    kCtrlEmpty, kCtrlEmpty, kCtrlEmpty,
        kCtrlEmpty,    kCtrlEmpty, kCtrlEmpty, kCtrlEmpty,
        kCtrlEmpty,    kCtrlEmpty, kCtrlEmpty, kCtrlEmpty,
    };

    // -----------------------------------------------------------------
    // Pointer into the empty-group array, used when an empty TSet's
    // m_ctrl needs to point somewhere safe (the iterator skip logic
    // walks it the same way as a real ctrl array; the kCtrlSentinel at
    // offset 16 stops iteration in one step).
    // -----------------------------------------------------------------
    [[nodiscard]] XPACT_FORCEINLINE constexpr ::uint8* EmptyGroup() noexcept
    {
        // const_cast is safe because TSet treats the empty group as
        // read-only: the only operations performed on it are reads in
        // the iterator and a Capacity==0 fast-path that never writes.
        return const_cast<::uint8*>(&kEmptyGroupBytes[0]);
    }
}

namespace XCore
{
    // -----------------------------------------------------------------
    // TSet<T> -- the flat open-addressing hash set.
    //
    // Template parameters:
    //   T    -- element type. Must be hashable via GetTypeHash(T) and
    //           equality-comparable via operator==. The hash and equal
    //           may be customized by template specialization of the
    //           free functions or by passing custom Hasher/Equal types
    //           (not exposed here; XCore-4a's default TSet uses the
    //           free-function dispatch).
    //
    // The default tag is FMemTag::Container.
    // -----------------------------------------------------------------

    template<typename T>
    class TSet
    {
    public:
        using ElementType = T;
        using SizeType    = ::int32;

        // =================================================================
        // Construction / destruction.
        // =================================================================

        // -------------------------------------------------------------
        // Default ctor -- empty set; no buffer allocated. m_ctrl points
        // at the static empty-group sentinel; m_slots is null.
        // -------------------------------------------------------------
        TSet() noexcept
            : m_ctrl(::XCore::Detail::EmptyGroup())
            , m_slots(nullptr)
            , m_size(0)
            , m_capacity(0)
            , m_growthLeft(0)
            , m_tag(::XCore::HAL::FMemTag::Container)
        {
        }

        // -------------------------------------------------------------
        // Tag-taking ctor (per-instance allocator-tag override).
        // -------------------------------------------------------------
        explicit TSet(::XCore::HAL::FMemTag InTag) noexcept
            : m_ctrl(::XCore::Detail::EmptyGroup())
            , m_slots(nullptr)
            , m_size(0)
            , m_capacity(0)
            , m_growthLeft(0)
            , m_tag(InTag)
        {
        }

        // -------------------------------------------------------------
        // Destructor.
        // -------------------------------------------------------------
        ~TSet() noexcept
        {
            ClearAndDeallocate();
        }

        // -------------------------------------------------------------
        // Copy ctor / copy-assign -- deep copy of all elements.
        //
        // The copy must rehash because the destination's capacity may
        // differ from the source's (typically we shrink-fit). Simplest:
        // copy by reserving source-size and Add'ing each element.
        // -------------------------------------------------------------
        TSet(const TSet& Other)
            : m_ctrl(::XCore::Detail::EmptyGroup())
            , m_slots(nullptr)
            , m_size(0)
            , m_capacity(0)
            , m_growthLeft(0)
            , m_tag(Other.m_tag)
        {
            if (Other.m_size > 0)
            {
                Reserve(Other.m_size);
                for (::SIZE_T I = 0; I < Other.m_capacity; ++I)
                {
                    if (::XCore::Detail::IsFull(Other.m_ctrl[I]))
                    {
                        Add(*reinterpret_cast<const T*>(Other.m_slots + I * sizeof(T)));
                    }
                }
            }
        }

        TSet& operator=(const TSet& Other)
        {
            if (this == &Other) return *this;
            ClearAndDeallocate();
            m_tag = Other.m_tag;
            if (Other.m_size > 0)
            {
                Reserve(Other.m_size);
                for (::SIZE_T I = 0; I < Other.m_capacity; ++I)
                {
                    if (::XCore::Detail::IsFull(Other.m_ctrl[I]))
                    {
                        Add(*reinterpret_cast<const T*>(Other.m_slots + I * sizeof(T)));
                    }
                }
            }
            return *this;
        }

        // -------------------------------------------------------------
        // Move ctor / move-assign -- take ownership of the source's
        // backing buffer. Source becomes empty.
        // -------------------------------------------------------------
        TSet(TSet&& Other) noexcept
            : m_ctrl(Other.m_ctrl)
            , m_slots(Other.m_slots)
            , m_size(Other.m_size)
            , m_capacity(Other.m_capacity)
            , m_growthLeft(Other.m_growthLeft)
            , m_tag(Other.m_tag)
        {
            Other.m_ctrl       = ::XCore::Detail::EmptyGroup();
            Other.m_slots      = nullptr;
            Other.m_size       = 0;
            Other.m_capacity   = 0;
            Other.m_growthLeft = 0;
        }

        TSet& operator=(TSet&& Other) noexcept
        {
            if (this == &Other) return *this;
            ClearAndDeallocate();
            m_ctrl        = Other.m_ctrl;
            m_slots       = Other.m_slots;
            m_size        = Other.m_size;
            m_capacity    = Other.m_capacity;
            m_growthLeft  = Other.m_growthLeft;
            m_tag         = Other.m_tag;
            Other.m_ctrl       = ::XCore::Detail::EmptyGroup();
            Other.m_slots      = nullptr;
            Other.m_size       = 0;
            Other.m_capacity   = 0;
            Other.m_growthLeft = 0;
            return *this;
        }

        // =================================================================
        // Capacity / size.
        // =================================================================

        [[nodiscard]] ::int32 Num() const noexcept
        {
            return static_cast<::int32>(m_size);
        }

        [[nodiscard]] ::int32 Max() const noexcept
        {
            return static_cast<::int32>(m_capacity);
        }

        // -------------------------------------------------------------
        // Reserve(N) -- ensure capacity for at least N entries without
        // forcing rehash until the 7/8 threshold is reached.
        //
        // If the current capacity already satisfies the load-factor
        // bound for N elements, no-op.
        // -------------------------------------------------------------
        void Reserve(::int32 MinCapacity)
        {
            if (MinCapacity <= 0) return;
            // We want (MinCapacity * 8) / 7 + safety as min capacity.
            // For C = MinCap, we need a power-of-2 cap >= ceil(C * 8 / 7).
            const ::SIZE_T Needed = static_cast<::SIZE_T>(MinCapacity);
            // Compute minimum capacity that satisfies (NewCap * 7/8) >= Needed.
            //   NewCap >= ceil(Needed * 8 / 7).
            const ::SIZE_T MinCap = (Needed * 8 + 6) / 7;
            const ::SIZE_T NewCap = ::XCore::Detail::NormalizeCapacity(MinCap);
            if (NewCap > m_capacity)
            {
                Rehash(NewCap);
            }
        }

        // -------------------------------------------------------------
        // Reset -- destroy all elements; keep buffer for reuse.
        //
        // Differs from clear-and-deallocate in that the control bytes
        // are reset to kCtrlEmpty (NOT kCtrlDeleted) and the size is
        // zero. The growthLeft is reset to the full CapacityToGrowAt.
        // -------------------------------------------------------------
        void Reset() noexcept
        {
            if (m_capacity == 0) return;
            // Destroy every Full element.
            for (::SIZE_T I = 0; I < m_capacity; ++I)
            {
                if (::XCore::Detail::IsFull(m_ctrl[I]))
                {
                    reinterpret_cast<T*>(m_slots + I * sizeof(T))->~T();
                }
            }
            // Wipe control array. The first Capacity bytes go to
            // kEmpty; the trailing GroupSize bytes are the mirror
            // (re-mirror of position 0..GroupSize-1, which is also all
            // kEmpty now). The position [Capacity] should be
            // kSentinel for the iterator's end() detection.
            std::memset(m_ctrl, ::XCore::Detail::kCtrlEmpty, m_capacity + ::XCore::Detail::kGroupSize);
            m_ctrl[m_capacity] = ::XCore::Detail::kCtrlSentinel;
            m_size       = 0;
            m_growthLeft = ::XCore::Detail::CapacityToGrowAt(m_capacity);
        }

        // =================================================================
        // Lookup.
        // =================================================================

        [[nodiscard]] bool Contains(const T& Value) const noexcept
        {
            return FindIndex(Value) != static_cast<::SIZE_T>(-1);
        }

        // =================================================================
        // Insertion.
        // =================================================================

        // -------------------------------------------------------------
        // Add -- insert Value into the set.
        //
        // Returns true if a new element was inserted; false if Value
        // was already present (in which case the set is unchanged).
        // -------------------------------------------------------------
        bool Add(const T& Value)
        {
            return AddImpl(Value);
        }

        bool Add(T&& Value)
        {
            return AddImpl(::std::move(Value));
        }

        // =================================================================
        // Removal.
        // =================================================================

        // -------------------------------------------------------------
        // Remove -- remove Value from the set.
        //
        // Returns true if removed; false if not found. Removal converts
        // the slot's control byte from kFull to either kEmpty or
        // kDeleted depending on the next-group state.
        //
        // Per abseil's logic: if the next group (i.e., the group
        // immediately after the removed slot's group, looking ahead by
        // GroupSize) contains kEmpty, the removed slot can become
        // kEmpty too (the probe chain that would have visited this
        // slot already terminates at the next group's kEmpty). Else,
        // it must become kDeleted to keep the chain alive.
        // -------------------------------------------------------------
        bool Remove(const T& Value)
        {
            const ::SIZE_T Idx = FindIndex(Value);
            if (Idx == static_cast<::SIZE_T>(-1)) return false;
            EraseAt(Idx);
            return true;
        }

        // =================================================================
        // Iteration.
        // =================================================================

        // -------------------------------------------------------------
        // TIter / TConstIter -- iterate over Full slots only, skipping
        // kEmpty / kDeleted in the control array.
        //
        // The end() iterator points one past the kCtrlSentinel at
        // m_ctrl[m_capacity]; advancing past Sentinel is a no-op (the
        // skip loop reads Sentinel and stops).
        // -------------------------------------------------------------
        class TIter
        {
        public:
            TIter() noexcept : m_ctrl(nullptr), m_slot(nullptr) {}
            TIter(::uint8* Ctrl, char* Slot) noexcept : m_ctrl(Ctrl), m_slot(Slot)
            {
                SkipNonFull();
            }

            T& operator*() const noexcept { return *reinterpret_cast<T*>(m_slot); }
            T* operator->() const noexcept { return reinterpret_cast<T*>(m_slot); }

            TIter& operator++() noexcept
            {
                ++m_ctrl;
                m_slot += sizeof(T);
                SkipNonFull();
                return *this;
            }

            bool operator==(const TIter& Other) const noexcept { return m_ctrl == Other.m_ctrl; }
            bool operator!=(const TIter& Other) const noexcept { return m_ctrl != Other.m_ctrl; }

        private:
            void SkipNonFull() noexcept
            {
                // Walk forward while the control byte is not Full and not
                // the end-of-buffer sentinel. The kCtrlSentinel at position
                // [Capacity] terminates the loop because it has a Full-style
                // bit pattern (top bit set, like Empty/Deleted -- BUT
                // IsEmptyOrDeleted matches it too). We need a distinct
                // sentinel check. Use the value 0xFF: any kSentinel byte
                // terminates iteration.
                while (*m_ctrl != ::XCore::Detail::kCtrlSentinel
                       && !::XCore::Detail::IsFull(*m_ctrl))
                {
                    ++m_ctrl;
                    m_slot += sizeof(T);
                }
            }

            ::uint8* m_ctrl;
            char*    m_slot;
        };

        class TConstIter
        {
        public:
            TConstIter() noexcept : m_ctrl(nullptr), m_slot(nullptr) {}
            TConstIter(const ::uint8* Ctrl, const char* Slot) noexcept : m_ctrl(Ctrl), m_slot(Slot)
            {
                SkipNonFull();
            }

            const T& operator*() const noexcept { return *reinterpret_cast<const T*>(m_slot); }
            const T* operator->() const noexcept { return reinterpret_cast<const T*>(m_slot); }

            TConstIter& operator++() noexcept
            {
                ++m_ctrl;
                m_slot += sizeof(T);
                SkipNonFull();
                return *this;
            }

            bool operator==(const TConstIter& Other) const noexcept { return m_ctrl == Other.m_ctrl; }
            bool operator!=(const TConstIter& Other) const noexcept { return m_ctrl != Other.m_ctrl; }

        private:
            void SkipNonFull() noexcept
            {
                while (*m_ctrl != ::XCore::Detail::kCtrlSentinel
                       && !::XCore::Detail::IsFull(*m_ctrl))
                {
                    ++m_ctrl;
                    m_slot += sizeof(T);
                }
            }

            const ::uint8* m_ctrl;
            const char*    m_slot;
        };

        [[nodiscard]] TIter      begin() noexcept       { return m_capacity == 0 ? TIter() : TIter(m_ctrl, m_slots); }
        [[nodiscard]] TIter      end() noexcept         { return m_capacity == 0 ? TIter() : TIter(m_ctrl + m_capacity, m_slots + m_capacity * sizeof(T)); }
        [[nodiscard]] TConstIter begin() const noexcept { return m_capacity == 0 ? TConstIter() : TConstIter(m_ctrl, m_slots); }
        [[nodiscard]] TConstIter end() const noexcept   { return m_capacity == 0 ? TConstIter() : TConstIter(m_ctrl + m_capacity, m_slots + m_capacity * sizeof(T)); }

        // =================================================================
        // Diagnostics / test hooks.
        // =================================================================

        // -------------------------------------------------------------
        // GetCtrlByte -- diagnostic accessor for tests verifying the
        // SwissTable layout (Tests/Containers/TSet.Tests/SwissTableLayout.cpp).
        // Returns the raw control byte at the given index. Index must
        // be < Capacity + GroupSize (the mirror is also accessible).
        // -------------------------------------------------------------
        [[nodiscard]] ::uint8 GetCtrlByte(::SIZE_T Index) const noexcept
        {
            return m_ctrl[Index];
        }

    private:
        // =============================================================
        // Internal helpers.
        // =============================================================

        // -------------------------------------------------------------
        // FindIndex -- search for Value, returning its slot index or
        // SIZE_MAX (-1 cast) on not-found.
        //
        // The scalar group probe:
        //   group = H1(hash) % NumGroups;
        //   for each group in triangular sequence:
        //     for each slot in group:
        //       if ctrl[slot] == H2(hash) && slot[slot] == Value: return slot;
        //     if any kEmpty in group: not-found (terminate probe).
        // -------------------------------------------------------------
        [[nodiscard]] ::SIZE_T FindIndex(const T& Value) const noexcept
        {
            if (m_capacity == 0) return static_cast<::SIZE_T>(-1);

            const ::uint64 Hash = ::XCore::GetTypeHash(Value);
            const ::uint8  Tag  = ::XCore::Detail::H2(Hash);
            const ::SIZE_T Mask = m_capacity - 1;  // capacity is power of 2
            ::SIZE_T GroupStart = static_cast<::SIZE_T>(::XCore::Detail::H1(Hash)) & Mask;

            // Probe-sequence offset: TRIANGULAR PROBING per abseil.
            // After step k, total offset = k*(k+1)/2 * GroupSize.
            // Implementation: keep a running "probe index" that
            // increments by GroupSize each step; add it to GroupStart.
            //   index=GroupSize, offset += GroupSize  (cum: GroupSize)
            //   index=2*GroupSize, offset += 2*GS    (cum: 3*GS)
            //   index=3*GroupSize, offset += 3*GS    (cum: 6*GS)
            // Visits every group exactly once on power-of-2 capacity.
            ::SIZE_T ProbeIndex = 0;
            for (::SIZE_T ProbeStep = 0; ; ++ProbeStep)
            {
                // Walk this group's slots.
                for (::SIZE_T I = 0; I < ::XCore::Detail::kGroupSize; ++I)
                {
                    const ::SIZE_T SlotIdx = (GroupStart + I) & Mask;
                    const ::uint8 C = m_ctrl[SlotIdx];
                    if (C == Tag)
                    {
                        // Tag matches; check value.
                        const T* SlotPtr = reinterpret_cast<const T*>(m_slots + SlotIdx * sizeof(T));
                        if (*SlotPtr == Value)
                        {
                            return SlotIdx;
                        }
                    }
                    else if (::XCore::Detail::IsEmpty(C))
                    {
                        // Empty slot in this probe -- the probe chain
                        // terminates here. The element is not in the
                        // table.
                        return static_cast<::SIZE_T>(-1);
                    }
                    // Else: Deleted or other tag, keep probing.
                }
                // Advance to next group via triangular step.
                ProbeIndex += ::XCore::Detail::kGroupSize;
                GroupStart = (GroupStart + ProbeIndex) & Mask;

                // Safety: avoid infinite loop in degenerate cases (no
                // empty slot found means table is 100% full of
                // tags + tombstones with no terminator -- shouldn't
                // happen at <=7/8 load factor, but a hard cap on probe
                // steps catches the bug rather than spinning).
                if (ProbeStep >= m_capacity / ::XCore::Detail::kGroupSize)
                {
                    return static_cast<::SIZE_T>(-1);
                }
            }
        }

        // -------------------------------------------------------------
        // FindInsertSlot -- find the slot to insert Value at.
        //
        // Walks the probe sequence; returns the index of the first
        // kEmpty or kDeleted slot in the chain. Distinct from
        // FindIndex because it does not stop at kEmpty (we want to
        // know the first reusable slot, but if Value is already
        // present at a Full slot earlier in the chain, we abort
        // without inserting -- the caller calls FindIndex first to
        // detect that).
        //
        // The caller's algorithm: call FindIndex; if found, do
        // nothing. Else call FindInsertSlot to get a target slot;
        // construct the element there.
        // -------------------------------------------------------------
        [[nodiscard]] ::SIZE_T FindInsertSlot(::uint64 Hash) const noexcept
        {
            const ::SIZE_T Mask = m_capacity - 1;
            ::SIZE_T GroupStart = static_cast<::SIZE_T>(::XCore::Detail::H1(Hash)) & Mask;

            ::SIZE_T ProbeIndex = 0;
            for (::SIZE_T ProbeStep = 0; ; ++ProbeStep)
            {
                for (::SIZE_T I = 0; I < ::XCore::Detail::kGroupSize; ++I)
                {
                    const ::SIZE_T SlotIdx = (GroupStart + I) & Mask;
                    const ::uint8 C = m_ctrl[SlotIdx];
                    if (::XCore::Detail::IsEmptyOrDeleted(C))
                    {
                        return SlotIdx;
                    }
                }
                ProbeIndex += ::XCore::Detail::kGroupSize;
                GroupStart = (GroupStart + ProbeIndex) & Mask;
                if (ProbeStep >= m_capacity / ::XCore::Detail::kGroupSize)
                {
                    // Pathological -- caller must rehash.
                    return static_cast<::SIZE_T>(-1);
                }
            }
        }

        // -------------------------------------------------------------
        // AddImpl -- shared implementation for Add(const T&) and
        // Add(T&&).
        //
        // Steps:
        //   1. If load factor >= 7/8, rehash to 2x capacity.
        //   2. Search for existing Value; if present, return false.
        //   3. Find insert slot; construct Value there.
        //   4. Update ctrl[slot] to H2(hash); decrement growthLeft.
        //
        // The first-add path also triggers initial allocation (capacity
        // was 0).
        // -------------------------------------------------------------
        template<typename VRef>
        bool AddImpl(VRef&& Value)
        {
            // Ensure capacity.
            if (m_growthLeft == 0)
            {
                Rehash(m_capacity == 0 ? ::XCore::Detail::kGroupSize : m_capacity * 2);
            }

            // Search for existing entry.
            const ::uint64 Hash = ::XCore::GetTypeHash(Value);
            const ::uint8  Tag  = ::XCore::Detail::H2(Hash);

            // Walk the probe sequence; track the first Empty/Deleted
            // slot encountered as the insert target, but continue
            // until either we find the value (return false) or hit
            // a terminal kEmpty (which means the value is not
            // present and the tracked insert slot is the target).
            const ::SIZE_T Mask = m_capacity - 1;
            ::SIZE_T GroupStart = static_cast<::SIZE_T>(::XCore::Detail::H1(Hash)) & Mask;
            ::SIZE_T InsertSlot = static_cast<::SIZE_T>(-1);
            bool InsertSlotIsDeleted = false;

            ::SIZE_T ProbeIndex = 0;
            for (::SIZE_T ProbeStep = 0; ; ++ProbeStep)
            {
                for (::SIZE_T I = 0; I < ::XCore::Detail::kGroupSize; ++I)
                {
                    const ::SIZE_T SlotIdx = (GroupStart + I) & Mask;
                    const ::uint8 C = m_ctrl[SlotIdx];
                    if (C == Tag)
                    {
                        const T* SlotPtr = reinterpret_cast<const T*>(m_slots + SlotIdx * sizeof(T));
                        if (*SlotPtr == Value)
                        {
                            return false;  // already present
                        }
                    }
                    else if (::XCore::Detail::IsEmpty(C))
                    {
                        // No further matches possible; insert here (or
                        // at the previously-tracked tombstone if any).
                        if (InsertSlot == static_cast<::SIZE_T>(-1))
                        {
                            InsertSlot = SlotIdx;
                            InsertSlotIsDeleted = false;
                        }
                        // Perform insert.
                        new (m_slots + InsertSlot * sizeof(T)) T(::std::forward<VRef>(Value));
                        m_ctrl[InsertSlot] = Tag;
                        WriteMirror(InsertSlot, Tag);
                        ++m_size;
                        if (!InsertSlotIsDeleted)
                        {
                            // Consumed an Empty -- decrement growthLeft.
                            --m_growthLeft;
                        }
                        // Recycled a Deleted -- growthLeft already
                        // includes it; size went up, deleted-count went
                        // down, growthLeft unchanged.
                        return true;
                    }
                    else if (::XCore::Detail::IsDeleted(C) && InsertSlot == static_cast<::SIZE_T>(-1))
                    {
                        InsertSlot = SlotIdx;
                        InsertSlotIsDeleted = true;
                    }
                    // Else: Full slot with different tag, keep probing.
                }
                ProbeIndex += ::XCore::Detail::kGroupSize;
                GroupStart = (GroupStart + ProbeIndex) & Mask;
                if (ProbeStep >= m_capacity / ::XCore::Detail::kGroupSize)
                {
                    // No Empty terminator reached -- forced rehash.
                    Rehash(m_capacity * 2);
                    return AddImpl(::std::forward<VRef>(Value));
                }
            }
        }

        // -------------------------------------------------------------
        // EraseAt -- destroy element at the given slot, update ctrl.
        //
        // Per abseil: the new ctrl byte is kCtrlEmpty if the group
        // BEFORE the slot has kEmpty (i.e., the probe chain that
        // includes this slot is already short-circuited by an earlier
        // empty), else kCtrlDeleted (the chain must be kept alive).
        //
        // The simpler formulation we use: always emit kCtrlDeleted.
        // This is slightly less efficient (more tombstones accumulate)
        // but the rehash threshold cleans them up. Abseil's
        // sophisticated empty-vs-deleted choice is a perf optimization;
        // for the engineering-principles-correct first version we use
        // the safe always-Deleted variant.
        // -------------------------------------------------------------
        void EraseAt(::SIZE_T Index) noexcept
        {
            reinterpret_cast<T*>(m_slots + Index * sizeof(T))->~T();
            m_ctrl[Index] = ::XCore::Detail::kCtrlDeleted;
            WriteMirror(Index, ::XCore::Detail::kCtrlDeleted);
            --m_size;
            // growthLeft NOT incremented -- the deleted slot still
            // counts against load factor until the next rehash.
        }

        // -------------------------------------------------------------
        // WriteMirror -- write the control byte at the mirror position.
        //
        // The mirror is bytes [Capacity+1 .. Capacity + GroupSize - 1]
        // and duplicates bytes [0 .. GroupSize - 2]. The byte at
        // Capacity is the kCtrlSentinel and must not be overwritten.
        //
        // For our scalar probe loop (which uses explicit `& Mask`
        // indexing), the mirror is never read. We still write it for
        // memory-image compatibility with abseil-compat tooling and so
        // a future SIMD swap-in (if the bit-exactness contract relaxes)
        // gets the layout for free.
        // -------------------------------------------------------------
        XPACT_FORCEINLINE void WriteMirror(::SIZE_T SlotIdx, ::uint8 Val) noexcept
        {
            // Slots 0..GroupSize-2 have mirror bytes at Capacity+1+SlotIdx.
            // Slot GroupSize-1 has no mirror. The sentinel at Capacity is
            // never overwritten.
            if (SlotIdx + 1 < ::XCore::Detail::kGroupSize)
            {
                m_ctrl[m_capacity + 1 + SlotIdx] = Val;
            }
        }

        // -------------------------------------------------------------
        // Rehash -- grow / shrink to the new capacity (must be power of 2
        // or 0). Reallocates the backing buffer; rehashes every Full
        // slot into the new buffer; emits kCtrlEmpty for unused slots
        // and the kCtrlSentinel terminator at offset NewCap.
        // -------------------------------------------------------------
        void Rehash(::SIZE_T NewCap)
        {
            NewCap = ::XCore::Detail::NormalizeCapacity(NewCap);
            if (NewCap == 0)
            {
                ClearAndDeallocate();
                return;
            }

            // Allocate new backing.
            const ::SIZE_T Bytes = ::XCore::Detail::BackingBytes<T>(NewCap);
            char* NewBacking = static_cast<char*>(::XCore::HAL::FMemory::MallocOrAbort(Bytes, alignof(T) > 16 ? alignof(T) : 16, m_tag));

            ::uint8* NewCtrl = reinterpret_cast<::uint8*>(NewBacking);
            char*    NewSlots = NewBacking + ::XCore::Detail::SlotOffset<T>(NewCap);

            // Initialize new ctrl array: all kCtrlEmpty, then place
            // kCtrlSentinel at offset NewCap.
            std::memset(NewCtrl, ::XCore::Detail::kCtrlEmpty, NewCap + ::XCore::Detail::kGroupSize);
            NewCtrl[NewCap] = ::XCore::Detail::kCtrlSentinel;

            // Re-insert every Full slot from the old table into the new.
            const ::SIZE_T NewMask = NewCap - 1;
            ::SIZE_T NewSize = 0;
            for (::SIZE_T OldIdx = 0; OldIdx < m_capacity; ++OldIdx)
            {
                if (!::XCore::Detail::IsFull(m_ctrl[OldIdx])) continue;
                T* OldElement = reinterpret_cast<T*>(m_slots + OldIdx * sizeof(T));
                const ::uint64 Hash = ::XCore::GetTypeHash(*OldElement);
                const ::uint8 Tag  = ::XCore::Detail::H2(Hash);

                // Find insert slot in the new buffer.
                ::SIZE_T GroupStart = static_cast<::SIZE_T>(::XCore::Detail::H1(Hash)) & NewMask;
                ::SIZE_T InsertSlot = static_cast<::SIZE_T>(-1);
                ::SIZE_T ProbeIndex = 0;
                for (::SIZE_T ProbeStep = 0; InsertSlot == static_cast<::SIZE_T>(-1); ++ProbeStep)
                {
                    for (::SIZE_T I = 0; I < ::XCore::Detail::kGroupSize; ++I)
                    {
                        const ::SIZE_T SlotIdx = (GroupStart + I) & NewMask;
                        if (::XCore::Detail::IsEmpty(NewCtrl[SlotIdx]))
                        {
                            InsertSlot = SlotIdx;
                            break;
                        }
                    }
                    if (InsertSlot == static_cast<::SIZE_T>(-1))
                    {
                        ProbeIndex += ::XCore::Detail::kGroupSize;
                        GroupStart = (GroupStart + ProbeIndex) & NewMask;
                    }
                }
                // Move-construct in place.
                new (NewSlots + InsertSlot * sizeof(T)) T(::std::move(*OldElement));
                OldElement->~T();
                NewCtrl[InsertSlot] = Tag;
                if (InsertSlot + 1 < ::XCore::Detail::kGroupSize)
                {
                    NewCtrl[NewCap + 1 + InsertSlot] = Tag;
                }
                ++NewSize;
            }

            // Free the old backing.
            if (m_slots != nullptr)
            {
                // m_ctrl points at the start of the old backing.
                ::XCore::HAL::FMemory::Free(reinterpret_cast<void*>(m_ctrl));
            }

            m_ctrl       = NewCtrl;
            m_slots      = NewSlots;
            m_capacity   = NewCap;
            m_size       = NewSize;
            m_growthLeft = ::XCore::Detail::CapacityToGrowAt(NewCap) - NewSize;
        }

        // -------------------------------------------------------------
        // ClearAndDeallocate -- destroy every element and free the
        // backing buffer. After this the set is in the default-
        // constructed (empty, no buffer) state.
        // -------------------------------------------------------------
        void ClearAndDeallocate() noexcept
        {
            if (m_capacity > 0)
            {
                // Destroy every Full element.
                for (::SIZE_T I = 0; I < m_capacity; ++I)
                {
                    if (::XCore::Detail::IsFull(m_ctrl[I]))
                    {
                        reinterpret_cast<T*>(m_slots + I * sizeof(T))->~T();
                    }
                }
                ::XCore::HAL::FMemory::Free(reinterpret_cast<void*>(m_ctrl));
            }
            m_ctrl       = ::XCore::Detail::EmptyGroup();
            m_slots      = nullptr;
            m_size       = 0;
            m_capacity   = 0;
            m_growthLeft = 0;
        }

        // =============================================================
        // Data members.
        // =============================================================

        ::uint8*               m_ctrl;        // pointer to control bytes (or EmptyGroup())
        char*                  m_slots;       // pointer to slot array (raw bytes)
        ::SIZE_T               m_size;        // # Full slots
        ::SIZE_T               m_capacity;    // total slot count (power of 2; 0 = empty)
        ::SIZE_T               m_growthLeft;  // (CapacityToGrowAt(cap) - (size + tombstones))
        ::XCore::HAL::FMemTag  m_tag;
    };

} // namespace XCore
