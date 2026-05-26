// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// TMap.h -- SwissTable-style flat open-addressing hash map (Section 5).
// =====================================================================
//
// XCore-4a Rev 3, Section 5.1 + Section 5.3 + Section 5.5.
//
// TMap<K, V> is built on TPair<K, V> stored in a TSet-like backing, but
// with hash/equal comparators that key off the K only -- NOT the full
// TPair<K, V>. We don't reuse TSet<TPair<K, V>> verbatim because TSet's
// GetTypeHash dispatch is on the whole element type; for TMap we need
// the hash to depend on Key alone (so that two pairs with the same Key
// collide even when their Values differ).
//
// The clean implementation: duplicate just the SwissTable core into a
// TMap class, parameterized to look at Pair.Key for hash/equal. This
// is ~250 LoC of focused code sharing with TSet's algorithmic
// invariants but with the right type seam for K-only dispatch.
//
// Alternative considered: a `KeyView` adapter on TSet that compares
// the first half of a TPair only. Rejected: too much template
// complexity for too little code shared (the TSet body is itself only
// ~600 LoC; duplicating ~250 of them with the K-only hooks is cleaner
// than adding the indirection layer).
//
// PUBLIC API SURFACE (mirrors UE's TMap minus stable addresses; Section
// 5.5 row 4 documents the divergence).
//
//   TMap<K, V> default-constructs empty.
//   Reserve(N) for capacity.
//   Add(Key, Value) -- inserts or overwrites; returns reference to Value.
//   Add(TPair<K,V>) -- same.
//   Remove(Key) -- erases by key; returns true if found.
//   Find(Key) -- returns V* or nullptr.
//   Contains(Key) -- returns bool.
//   operator[](Key) -- returns V&; inserts default-constructed Value
//                       if absent.
//   Num() returns count.
//   Reset() destroys elements; keeps buffer.
//   Iteration yields TPair<K, V>& in implementation-defined order.
//
// THREADING. Not thread-safe (Section 5.2). Concurrent mutation is UB.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"
#include "Macros/XCoreFwd.h"   // forward decl for TArray (UE-parity GenerateKeyArray/GenerateValueArray)
#include "Containers/TPair.h"
#include "Containers/TSet.h"  // for the SwissTable Detail helpers + GetTypeHash
#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"
#include "Hash/FXxh3.h"

#include <algorithm>           // std::sort (Rev 3 FIX-R2-MED-NEW-1 KeySort/ValueSort)
#include <cstring>
#include <new>
#include <utility>

namespace XCore
{
    // -----------------------------------------------------------------
    // TMap<K, V> -- the user-facing hash map.
    //
    // Element layout is TPair<K, V>, stored in a flat SwissTable-style
    // backing. Hash + equal dispatch on the K-portion of the pair only.
    //
    // Default tag is FMemTag::Container.
    // -----------------------------------------------------------------

    template<typename K, typename V>
    class TMap
    {
    public:
        using KeyType      = K;
        using ValueType    = V;
        using PairType     = TPair<K, V>;
        using SizeType     = ::int32;

        // =================================================================
        // Construction / destruction.
        // =================================================================

        TMap() noexcept
            : m_ctrl(::XCore::Detail::EmptyGroup())
            , m_slots(nullptr)
            , m_size(0)
            , m_capacity(0)
            , m_growthLeft(0)
            , m_tag(::XCore::HAL::FMemTag::Container)
        {
        }

        explicit TMap(::XCore::HAL::FMemTag InTag) noexcept
            : m_ctrl(::XCore::Detail::EmptyGroup())
            , m_slots(nullptr)
            , m_size(0)
            , m_capacity(0)
            , m_growthLeft(0)
            , m_tag(InTag)
        {
        }

        ~TMap() noexcept
        {
            ClearAndDeallocate();
        }

        // -------------------------------------------------------------
        // Copy ctor / copy-assign -- deep copy.
        // -------------------------------------------------------------
        TMap(const TMap& Other)
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
                        const PairType* P = reinterpret_cast<const PairType*>(Other.m_slots + I * sizeof(PairType));
                        Add(P->Key, P->Value);
                    }
                }
            }
        }

        TMap& operator=(const TMap& Other)
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
                        const PairType* P = reinterpret_cast<const PairType*>(Other.m_slots + I * sizeof(PairType));
                        Add(P->Key, P->Value);
                    }
                }
            }
            return *this;
        }

        // -------------------------------------------------------------
        // Move ctor / move-assign -- take ownership.
        // -------------------------------------------------------------
        TMap(TMap&& Other) noexcept
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

        TMap& operator=(TMap&& Other) noexcept
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

        [[nodiscard]] ::int32 Num() const noexcept     { return static_cast<::int32>(m_size); }
        [[nodiscard]] ::int32 Max() const noexcept     { return static_cast<::int32>(m_capacity); }

        void Reserve(::int32 MinCapacity)
        {
            if (MinCapacity <= 0) return;
            const ::SIZE_T Needed = static_cast<::SIZE_T>(MinCapacity);
            const ::SIZE_T MinCap = (Needed * 8 + 6) / 7;
            const ::SIZE_T NewCap = ::XCore::Detail::NormalizeCapacity(MinCap);
            if (NewCap > m_capacity)
            {
                Rehash(NewCap);
            }
        }

        void Reset() noexcept
        {
            if (m_capacity == 0) return;
            for (::SIZE_T I = 0; I < m_capacity; ++I)
            {
                if (::XCore::Detail::IsFull(m_ctrl[I]))
                {
                    reinterpret_cast<PairType*>(m_slots + I * sizeof(PairType))->~PairType();
                }
            }
            std::memset(m_ctrl, ::XCore::Detail::kCtrlEmpty, m_capacity + ::XCore::Detail::kGroupSize);
            m_ctrl[m_capacity] = ::XCore::Detail::kCtrlSentinel;
            m_size       = 0;
            m_growthLeft = ::XCore::Detail::CapacityToGrowAt(m_capacity);
        }

        // =================================================================
        // Lookup.
        // =================================================================

        [[nodiscard]] bool Contains(const K& Key) const noexcept
        {
            return FindIndex(Key) != static_cast<::SIZE_T>(-1);
        }

        // -------------------------------------------------------------
        // Find -- returns pointer to Value or nullptr.
        // -------------------------------------------------------------
        [[nodiscard]] V* Find(const K& Key) noexcept
        {
            const ::SIZE_T Idx = FindIndex(Key);
            if (Idx == static_cast<::SIZE_T>(-1)) return nullptr;
            return &reinterpret_cast<PairType*>(m_slots + Idx * sizeof(PairType))->Value;
        }

        [[nodiscard]] const V* Find(const K& Key) const noexcept
        {
            const ::SIZE_T Idx = FindIndex(Key);
            if (Idx == static_cast<::SIZE_T>(-1)) return nullptr;
            return &reinterpret_cast<const PairType*>(m_slots + Idx * sizeof(PairType))->Value;
        }

        // =================================================================
        // Insertion + element access.
        // =================================================================

        // -------------------------------------------------------------
        // Add(Key, Value) -- insert or overwrite. Returns reference to
        // the stored Value (whether newly inserted or pre-existing).
        // -------------------------------------------------------------
        V& Add(const K& Key, const V& Value)
        {
            return AddImpl(Key, Value);
        }

        V& Add(K&& Key, V&& Value)
        {
            return AddImpl(::std::move(Key), ::std::move(Value));
        }

        V& Add(const K& Key, V&& Value)
        {
            return AddImpl(Key, ::std::move(Value));
        }

        V& Add(K&& Key, const V& Value)
        {
            return AddImpl(::std::move(Key), Value);
        }

        // -------------------------------------------------------------
        // operator[] -- returns Value reference; default-constructs a
        // Value entry if Key is not present.
        //
        // The mutating overload returns a non-const reference so the
        // caller can do `map[k] = v;`. There is no const operator[]
        // because the absent-key path requires inserting.
        // -------------------------------------------------------------
        V& operator[](const K& Key)
        {
            V* Existing = Find(Key);
            if (Existing != nullptr) return *Existing;
            // Insert default-constructed Value.
            return AddImpl(Key, V{});
        }

        // =================================================================
        // UE-parity surface additions (Rev 3 Round 2 audit FIX-R2-MED-NEW-1).
        //
        // The methods below mirror UE's TMap surface (Engine/Source/Runtime
        // /Core/Public/Containers/Map.h + Map.h.inl) where the prior XPact
        // TMap was missing the convenience methods. Each has the same
        // signature semantics as UE's; the implementations are XPact-native
        // (built on the SwissTable backing rather than UE's TSparseArray).
        // =================================================================

        // -------------------------------------------------------------
        // FindOrAdd -- get-or-create.
        //
        // Returns a reference to the value associated with Key. If Key
        // is not present, inserts a default-constructed V and returns
        // a reference to it. Equivalent to operator[] in semantics but
        // matches UE's named API for call-site readability.
        //
        // Linear search + insert; O(1) amortised under load-factor
        // bound (see TSet::AddImpl probe-cost analysis at section 5.3
        // determinism contract).
        // -------------------------------------------------------------
        V& FindOrAdd(const K& Key)
        {
            if (V* Existing = Find(Key); Existing != nullptr) return *Existing;
            return AddImpl(Key, V{});
        }

        V& FindOrAdd(K&& Key)
        {
            if (V* Existing = Find(Key); Existing != nullptr) return *Existing;
            return AddImpl(::std::move(Key), V{});
        }

        // -------------------------------------------------------------
        // FindRef -- by-value-or-default lookup.
        //
        // Returns the value associated with Key by value, or a default-
        // constructed V if Key is absent. UE-parity (UE Map.h.inl:661).
        // Used for null-safe lookups where the caller wants a value
        // rather than a pointer.
        // -------------------------------------------------------------
        [[nodiscard]] V FindRef(const K& Key) const
        {
            if (const V* Existing = Find(Key); Existing != nullptr) return *Existing;
            return V{};
        }

        // -------------------------------------------------------------
        // Append -- bulk merge from another TMap.
        //
        // Copies (or moves) every entry from Other into *this. If a key
        // collision occurs, Other's value WINS (overwrite semantics;
        // matches UE Map.h.inl:1347 Append). The destination's tag is
        // preserved (Other's tag is NOT inherited).
        //
        // Cost: O(N) where N is Other's size.
        // -------------------------------------------------------------
        void Append(const TMap& Other)
        {
            if (&Other == this) return;
            for (::SIZE_T I = 0; I < Other.m_capacity; ++I)
            {
                if (::XCore::Detail::IsFull(Other.m_ctrl[I]))
                {
                    const PairType* P = reinterpret_cast<const PairType*>(Other.m_slots + I * sizeof(PairType));
                    AddImpl(P->Key, P->Value);
                }
            }
        }

        void Append(TMap&& Other)
        {
            if (&Other == this) return;
            for (::SIZE_T I = 0; I < Other.m_capacity; ++I)
            {
                if (::XCore::Detail::IsFull(Other.m_ctrl[I]))
                {
                    PairType* P = reinterpret_cast<PairType*>(Other.m_slots + I * sizeof(PairType));
                    AddImpl(::std::move(P->Key), ::std::move(P->Value));
                }
            }
            // The source map is left in a moved-from state; calling
            // ClearAndDeallocate makes the post-move state observable
            // and frees Other's buffer so the caller sees Num() == 0.
            Other.ClearAndDeallocate();
        }

        // -------------------------------------------------------------
        // KeySort / ValueSort -- in-place ordering by predicate.
        //
        // SwissTable storage does not preserve insertion order, and the
        // mirror byte/slot layout does not lend itself to in-place
        // partial reordering. We collect into a temporary array of
        // pointers (to avoid copying the PairType), sort the pointer
        // array by predicate, then rehash from the sorted order so
        // subsequent iteration visits keys (or values) in sorted order
        // until the next Add/Remove.
        //
        // NOTE: iteration order is otherwise implementation-defined per
        // §5.3 determinism contract. KeySort/ValueSort give a transient
        // sorted view; any mutation invalidates it.
        //
        // Cost: O(N log N) on the predicate plus a full rehash.
        //
        // UE-parity: UE Map.h.inl:1090 (KeySort) / :1110 (ValueSort).
        // -------------------------------------------------------------
        template<typename Pred>
        void KeySort(Pred Predicate)
        {
            if (m_size <= 1) return;
            SortAndRehash([Predicate](const PairType& A, const PairType& B) noexcept {
                return Predicate(A.Key, B.Key);
            });
        }

        template<typename Pred>
        void ValueSort(Pred Predicate)
        {
            if (m_size <= 1) return;
            SortAndRehash([Predicate](const PairType& A, const PairType& B) noexcept {
                return Predicate(A.Value, B.Value);
            });
        }

        // -------------------------------------------------------------
        // GenerateKeyArray / GenerateValueArray -- bulk extraction.
        //
        // Appends every key (or value) into the output array. The
        // output array is NOT cleared first -- the caller may pre-
        // populate it; this matches UE's semantics (UE Map.h.inl:733).
        //
        // The template form on the array's allocator lets the caller
        // pass any TArray<KeyType, AllocatorT> / TArray<ValueType,
        // AllocatorT> regardless of the destination tag.
        //
        // Cost: O(N).
        // -------------------------------------------------------------
        template<typename AllocatorT>
        void GenerateKeyArray(::XCore::TArray<KeyType, AllocatorT>& Out) const
        {
            Out.Reserve(Out.Num() + static_cast<::int32>(m_size));
            for (::SIZE_T I = 0; I < m_capacity; ++I)
            {
                if (::XCore::Detail::IsFull(m_ctrl[I]))
                {
                    const PairType* P = reinterpret_cast<const PairType*>(m_slots + I * sizeof(PairType));
                    Out.Add(P->Key);
                }
            }
        }

        template<typename AllocatorT>
        void GenerateValueArray(::XCore::TArray<ValueType, AllocatorT>& Out) const
        {
            Out.Reserve(Out.Num() + static_cast<::int32>(m_size));
            for (::SIZE_T I = 0; I < m_capacity; ++I)
            {
                if (::XCore::Detail::IsFull(m_ctrl[I]))
                {
                    const PairType* P = reinterpret_cast<const PairType*>(m_slots + I * sizeof(PairType));
                    Out.Add(P->Value);
                }
            }
        }

        // =================================================================
        // Removal.
        // =================================================================

        bool Remove(const K& Key) noexcept
        {
            const ::SIZE_T Idx = FindIndex(Key);
            if (Idx == static_cast<::SIZE_T>(-1)) return false;
            EraseAt(Idx);
            return true;
        }

        // -------------------------------------------------------------
        // FindAndRemoveChecked -- find by key, remove, return value.
        //
        // Aborts via XPACT_CHECK if Key is not present (the "Checked"
        // suffix in UE parlance). Useful when the caller has invariant
        // knowledge that Key MUST be in the map.
        //
        // UE-parity: UE Map.h.inl:1332.
        // -------------------------------------------------------------
        ValueType FindAndRemoveChecked(const K& Key)
        {
            const ::SIZE_T Idx = FindIndex(Key);
            XPACT_CHECK(Idx != static_cast<::SIZE_T>(-1));
            PairType* P = reinterpret_cast<PairType*>(m_slots + Idx * sizeof(PairType));
            ValueType Out = ::std::move(P->Value);
            EraseAt(Idx);
            return Out;
        }

        // -------------------------------------------------------------
        // RemoveAndCopyValue -- find by key, remove, copy out the value.
        //
        // Returns true if removed; the value is copy-assigned (move-
        // assigned) into the out parameter. Returns false (leaving Out
        // unmodified) if the key is absent.
        //
        // UE-parity: UE Map.h.inl:1282.
        // -------------------------------------------------------------
        bool RemoveAndCopyValue(const K& Key, ValueType& Out)
        {
            const ::SIZE_T Idx = FindIndex(Key);
            if (Idx == static_cast<::SIZE_T>(-1)) return false;
            PairType* P = reinterpret_cast<PairType*>(m_slots + Idx * sizeof(PairType));
            Out = ::std::move(P->Value);
            EraseAt(Idx);
            return true;
        }

        // =================================================================
        // Iteration.
        // =================================================================

        class TIter
        {
        public:
            TIter() noexcept : m_ctrl(nullptr), m_slot(nullptr) {}
            TIter(::uint8* Ctrl, char* Slot) noexcept : m_ctrl(Ctrl), m_slot(Slot) { SkipNonFull(); }

            PairType& operator*() const noexcept { return *reinterpret_cast<PairType*>(m_slot); }
            PairType* operator->() const noexcept { return reinterpret_cast<PairType*>(m_slot); }

            TIter& operator++() noexcept
            {
                ++m_ctrl;
                m_slot += sizeof(PairType);
                SkipNonFull();
                return *this;
            }

            bool operator==(const TIter& Other) const noexcept { return m_ctrl == Other.m_ctrl; }
            bool operator!=(const TIter& Other) const noexcept { return m_ctrl != Other.m_ctrl; }

        private:
            void SkipNonFull() noexcept
            {
                while (*m_ctrl != ::XCore::Detail::kCtrlSentinel
                       && !::XCore::Detail::IsFull(*m_ctrl))
                {
                    ++m_ctrl;
                    m_slot += sizeof(PairType);
                }
            }

            ::uint8* m_ctrl;
            char*    m_slot;
        };

        class TConstIter
        {
        public:
            TConstIter() noexcept : m_ctrl(nullptr), m_slot(nullptr) {}
            TConstIter(const ::uint8* Ctrl, const char* Slot) noexcept : m_ctrl(Ctrl), m_slot(Slot) { SkipNonFull(); }

            const PairType& operator*() const noexcept { return *reinterpret_cast<const PairType*>(m_slot); }
            const PairType* operator->() const noexcept { return reinterpret_cast<const PairType*>(m_slot); }

            TConstIter& operator++() noexcept
            {
                ++m_ctrl;
                m_slot += sizeof(PairType);
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
                    m_slot += sizeof(PairType);
                }
            }

            const ::uint8* m_ctrl;
            const char*    m_slot;
        };

        [[nodiscard]] TIter      begin() noexcept       { return m_capacity == 0 ? TIter() : TIter(m_ctrl, m_slots); }
        [[nodiscard]] TIter      end() noexcept         { return m_capacity == 0 ? TIter() : TIter(m_ctrl + m_capacity, m_slots + m_capacity * sizeof(PairType)); }
        [[nodiscard]] TConstIter begin() const noexcept { return m_capacity == 0 ? TConstIter() : TConstIter(m_ctrl, m_slots); }
        [[nodiscard]] TConstIter end() const noexcept   { return m_capacity == 0 ? TConstIter() : TConstIter(m_ctrl + m_capacity, m_slots + m_capacity * sizeof(PairType)); }

    private:
        // =============================================================
        // Internal helpers.
        // =============================================================

        [[nodiscard]] ::SIZE_T FindIndex(const K& Key) const noexcept
        {
            if (m_capacity == 0) return static_cast<::SIZE_T>(-1);

            const ::uint64 Hash = ::XCore::GetTypeHash(Key);
            const ::uint8  Tag  = ::XCore::Detail::H2(Hash);
            const ::SIZE_T Mask = m_capacity - 1;
            ::SIZE_T GroupStart = static_cast<::SIZE_T>(::XCore::Detail::H1(Hash)) & Mask;

            // Triangular probing: see TSet::FindIndex for explanation.
            ::SIZE_T ProbeIndex = 0;
            for (::SIZE_T ProbeStep = 0; ; ++ProbeStep)
            {
                for (::SIZE_T I = 0; I < ::XCore::Detail::kGroupSize; ++I)
                {
                    const ::SIZE_T SlotIdx = (GroupStart + I) & Mask;
                    const ::uint8 C = m_ctrl[SlotIdx];
                    if (C == Tag)
                    {
                        const PairType* P = reinterpret_cast<const PairType*>(m_slots + SlotIdx * sizeof(PairType));
                        if (P->Key == Key) return SlotIdx;
                    }
                    else if (::XCore::Detail::IsEmpty(C))
                    {
                        return static_cast<::SIZE_T>(-1);
                    }
                }
                ProbeIndex += ::XCore::Detail::kGroupSize;
                GroupStart = (GroupStart + ProbeIndex) & Mask;
                if (ProbeStep >= m_capacity / ::XCore::Detail::kGroupSize)
                {
                    return static_cast<::SIZE_T>(-1);
                }
            }
        }

        template<typename KArg, typename VArg>
        V& AddImpl(KArg&& InKey, VArg&& InValue)
        {
            if (m_growthLeft == 0)
            {
                Rehash(m_capacity == 0 ? ::XCore::Detail::kGroupSize : m_capacity * 2);
            }

            const ::uint64 Hash = ::XCore::GetTypeHash(static_cast<const K&>(InKey));
            const ::uint8  Tag  = ::XCore::Detail::H2(Hash);
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
                        PairType* P = reinterpret_cast<PairType*>(m_slots + SlotIdx * sizeof(PairType));
                        if (P->Key == static_cast<const K&>(InKey))
                        {
                            // Overwrite Value; preserve Key.
                            P->Value = ::std::forward<VArg>(InValue);
                            return P->Value;
                        }
                    }
                    else if (::XCore::Detail::IsEmpty(C))
                    {
                        if (InsertSlot == static_cast<::SIZE_T>(-1))
                        {
                            InsertSlot = SlotIdx;
                            InsertSlotIsDeleted = false;
                        }
                        new (m_slots + InsertSlot * sizeof(PairType))
                            PairType(::std::forward<KArg>(InKey), ::std::forward<VArg>(InValue));
                        m_ctrl[InsertSlot] = Tag;
                        if (InsertSlot + 1 < ::XCore::Detail::kGroupSize)
                        {
                            m_ctrl[m_capacity + 1 + InsertSlot] = Tag;
                        }
                        ++m_size;
                        if (!InsertSlotIsDeleted) --m_growthLeft;
                        return reinterpret_cast<PairType*>(m_slots + InsertSlot * sizeof(PairType))->Value;
                    }
                    else if (::XCore::Detail::IsDeleted(C) && InsertSlot == static_cast<::SIZE_T>(-1))
                    {
                        InsertSlot = SlotIdx;
                        InsertSlotIsDeleted = true;
                    }
                }
                ProbeIndex += ::XCore::Detail::kGroupSize;
                GroupStart = (GroupStart + ProbeIndex) & Mask;
                if (ProbeStep >= m_capacity / ::XCore::Detail::kGroupSize)
                {
                    Rehash(m_capacity * 2);
                    return AddImpl(::std::forward<KArg>(InKey), ::std::forward<VArg>(InValue));
                }
            }
        }

        void EraseAt(::SIZE_T Index) noexcept
        {
            reinterpret_cast<PairType*>(m_slots + Index * sizeof(PairType))->~PairType();
            m_ctrl[Index] = ::XCore::Detail::kCtrlDeleted;
            if (Index + 1 < ::XCore::Detail::kGroupSize)
            {
                m_ctrl[m_capacity + 1 + Index] = ::XCore::Detail::kCtrlDeleted;
            }
            --m_size;
        }

        void Rehash(::SIZE_T NewCap)
        {
            NewCap = ::XCore::Detail::NormalizeCapacity(NewCap);
            if (NewCap == 0)
            {
                ClearAndDeallocate();
                return;
            }

            const ::SIZE_T Bytes = ::XCore::Detail::BackingBytes<PairType>(NewCap);
            char* NewBacking = static_cast<char*>(::XCore::HAL::FMemory::MallocOrAbort(Bytes, alignof(PairType) > 16 ? alignof(PairType) : 16, m_tag));

            ::uint8* NewCtrl = reinterpret_cast<::uint8*>(NewBacking);
            char*    NewSlots = NewBacking + ::XCore::Detail::SlotOffset<PairType>(NewCap);

            std::memset(NewCtrl, ::XCore::Detail::kCtrlEmpty, NewCap + ::XCore::Detail::kGroupSize);
            NewCtrl[NewCap] = ::XCore::Detail::kCtrlSentinel;

            const ::SIZE_T NewMask = NewCap - 1;
            ::SIZE_T NewSize = 0;
            for (::SIZE_T OldIdx = 0; OldIdx < m_capacity; ++OldIdx)
            {
                if (!::XCore::Detail::IsFull(m_ctrl[OldIdx])) continue;
                PairType* OldP = reinterpret_cast<PairType*>(m_slots + OldIdx * sizeof(PairType));
                const ::uint64 Hash = ::XCore::GetTypeHash(OldP->Key);
                const ::uint8 Tag  = ::XCore::Detail::H2(Hash);

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
                new (NewSlots + InsertSlot * sizeof(PairType)) PairType(::std::move(*OldP));
                OldP->~PairType();
                NewCtrl[InsertSlot] = Tag;
                if (InsertSlot + 1 < ::XCore::Detail::kGroupSize)
                {
                    NewCtrl[NewCap + 1 + InsertSlot] = Tag;
                }
                ++NewSize;
            }

            if (m_slots != nullptr)
            {
                ::XCore::HAL::FMemory::Free(reinterpret_cast<void*>(m_ctrl));
            }

            m_ctrl       = NewCtrl;
            m_slots      = NewSlots;
            m_capacity   = NewCap;
            m_size       = NewSize;
            m_growthLeft = ::XCore::Detail::CapacityToGrowAt(NewCap) - NewSize;
        }

        void ClearAndDeallocate() noexcept
        {
            if (m_capacity > 0)
            {
                for (::SIZE_T I = 0; I < m_capacity; ++I)
                {
                    if (::XCore::Detail::IsFull(m_ctrl[I]))
                    {
                        reinterpret_cast<PairType*>(m_slots + I * sizeof(PairType))->~PairType();
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

        // -------------------------------------------------------------
        // SortAndRehash -- shared helper for KeySort / ValueSort.
        //
        // Collects every Full slot's PairType into a tagged temporary
        // buffer, sorts the buffer by Comp, then rebuilds *this from
        // the sorted order. The rebuild uses a fresh Rehash so the
        // SwissTable backing matches the sorted insertion order; until
        // the next Add/Remove, iteration visits entries in sorted
        // order.
        //
        // Note: this approach trades performance for SwissTable-state
        // correctness. An in-place stable sort over the slot array
        // would corrupt the control bytes (slot[I] no longer matches
        // ctrl[I]'s H2). Rebuilding is the safe path.
        //
        // The temporary buffer carries m_tag so the allocation
        // attribution is consistent with the rest of the TMap.
        // -------------------------------------------------------------
        template<typename Comp>
        void SortAndRehash(Comp Comparator)
        {
            const ::SIZE_T N = m_size;
            if (N <= 1) return;

            // Allocate temp buffer for N PairType move-targets.
            void* TempPtr = ::XCore::HAL::FMemory::MallocOrAbort(
                N * sizeof(PairType),
                alignof(PairType) > 16 ? alignof(PairType) : 16,
                m_tag);
            PairType* Temp = static_cast<PairType*>(TempPtr);

            // Move every Full slot into the temp buffer.
            ::SIZE_T Filled = 0;
            for (::SIZE_T I = 0; I < m_capacity; ++I)
            {
                if (::XCore::Detail::IsFull(m_ctrl[I]))
                {
                    PairType* P = reinterpret_cast<PairType*>(m_slots + I * sizeof(PairType));
                    new (&Temp[Filled]) PairType(::std::move(*P));
                    P->~PairType();
                    ++Filled;
                }
            }
            XPACT_CHECK(Filled == N);

            // Sort the temp buffer.
            ::std::sort(Temp, Temp + N, Comparator);

            // Wipe the SwissTable backing so the next AddImpl sees
            // a clean state at the original capacity (preserves the
            // existing allocation; only the control bytes + slot
            // contents are reset).
            std::memset(m_ctrl, ::XCore::Detail::kCtrlEmpty,
                        m_capacity + ::XCore::Detail::kGroupSize);
            m_ctrl[m_capacity] = ::XCore::Detail::kCtrlSentinel;
            m_size       = 0;
            m_growthLeft = ::XCore::Detail::CapacityToGrowAt(m_capacity);

            // Re-insert in sorted order.
            for (::SIZE_T I = 0; I < N; ++I)
            {
                AddImpl(::std::move(Temp[I].Key), ::std::move(Temp[I].Value));
                Temp[I].~PairType();
            }

            ::XCore::HAL::FMemory::Free(TempPtr);
        }

        ::uint8*               m_ctrl;
        char*                  m_slots;
        ::SIZE_T               m_size;
        ::SIZE_T               m_capacity;
        ::SIZE_T               m_growthLeft;
        ::XCore::HAL::FMemTag  m_tag;
    };

} // namespace XCore
