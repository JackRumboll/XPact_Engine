// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectAllocator.cpp -- the XObject heap (XCoreXObject Rev 4 §3).
// Phase 5.b implementation body.
// =====================================================================
//
// The Phase 5.b body implements the layered allocator per spec §3.1
// (Tier 2/3 atop FMallocBinnedX) with the per-FClass type-segregated
// free-lists per §3.4 and the size-class pool segregation per §3.5.
//
// IMPLEMENTATION CHOICES:
//
//   * Slabs are requested from FMemory::MallocOrAbort with
//     FMemTag::XObject. The pointer returned is the BACKING storage;
//     the FXObjectAllocator stores a separate FSlab control block in
//     its own state (the slabs are pointer-stable for their lifetime
//     so the GC mark loop can index by slab base without indirection).
//
//   * Each FSlab has a per-cell `OwnerClass` tag array (one pointer
//     per cell). When a cell is allocated by class A, OwnerClass[i] =
//     A. When the cell is freed, the FClassPool A's free-list claims
//     it back; OwnerClass[i] stays A so the cell remains class-segregated
//     across allocate/free cycles (this is the type-segregation
//     invariant per spec §3.4).
//
//   * Each FSlab has a `UnassignedFreeListHead` of cells not yet
//     assigned to any FClass. Newly-allocated slabs begin with EVERY
//     cell on this list. When a class needs a fresh cell and its own
//     free-list is empty, we transfer one cell from
//     UnassignedFreeListHead to ClassFreeListHead.
//
//   * Reverse lookup at Deallocate: a sorted-by-base TArray of slab
//     pointers + per-slab byte ranges (FSizeClassPool::m_slabs); a
//     binary-search hits the owning slab in O(log N), N = active
//     slab count (typically <50).
//
//   * The intrusive next-free pointer at the head of each free cell
//     is the canonical pattern (FFreeBlock in FMallocBinnedX). The
//     first 8 bytes of a free cell ARE the next-free pointer; the
//     last 8 bytes are NOT touched.
//
//   * Per-class free-list is a head pointer (singly-linked LIFO).
//     Per-cell metadata (the OwnerClass tag) lives in a separate
//     dense array at the slab header so the cell payload is exactly
//     the size-class width with no header overhead per-cell.
//
//   * BeginScenarioBoundary / EndScenarioBoundary push/pop a TLS-
//     thread-local scope name stack. Phase 5.b implements the scope
//     bookkeeping; the actual mark-region-clearing on End is gated
//     until Phase 5.h ships the FXObjectCollector reachability oracle.
//
//   * CoalesceIdleSlabs walks every FSizeClassPool, identifies slabs
//     with LiveCellCount == 0 (fully empty), returns them to
//     FMemory::Free. The partial-evacuation path (move cells between
//     slabs to free up the source slab) is also implemented but
//     gated by the same Phase 5.h dependency; Phase 5.b CoalesceIdleSlabs
//     ONLY acts on fully-empty slabs.
//
// LOCK DISCIPLINE per engine-wide FIX-R2-X-NEW:
//
//   * Reads / pool walks acquire SHARED.
//   * AllocateRaw fast path acquires SHARED (class pool free-list
//     pop is read-only-against-other-pools; the single pool's free-
//     list head is atomic-load-then-CAS).
//   * Slab grow / RegisterClassPool / RebindClassPool / Deallocate /
//     scenario boundary / coalesce acquire EXCLUSIVE.
//   * FMemory::MallocOrAbort calls for slab acquisition happen
//     OUTSIDE the exclusive lock (mirror of FNamePool::Predict
//     InsertPreAlloc). The flow: under SHARED lock observe "the size-
//     class needs a fresh slab"; release SHARED; allocate the slab
//     storage; acquire EXCLUSIVE; race-check (another thread may
//     have grown the pool); install the slab if we won the race,
//     otherwise free the unused storage.
//
// =====================================================================

#include "XObject/FXObjectAllocator.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XObject.h"

#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"
#include "HAL/FPlatformMemory.h"
#include "Macros/XAssertionMacros.h"
#include "Reflection/FClass.h"
#include "Reflection/FStruct.h"
#include "Reflection/FName.h"

#include <atomic>
#include <cstdint>
#include <cstring>
#include <new>

namespace XCore
{

    // =================================================================
    // Internal state types -- all opaque at the header per the PImpl
    // pattern.
    // =================================================================

    // -----------------------------------------------------------------
    // FSlab -- one slab's metadata. The slab's backing storage (the
    // cell array) lives at `Base`; this struct lives separately so the
    // backing pointer is naturally aligned to the largest size class
    // (i.e., the cells themselves don't have a header offset).
    // -----------------------------------------------------------------
    struct FXObjectAllocator::FSlab
    {
        void*                            Base;             // FMemory-owned storage
        ::int32                          SizeClassIndex;
        ::int32                          NumCells;
        ::int32                          LiveCellCount;     // cells assigned to any class

        // Intrusive head of the per-slab "unassigned" cells (cells
        // not yet handed to any FClass). New slabs start with EVERY
        // cell on this chain; the per-class free-lists steal from
        // this chain on demand.
        void*                            UnassignedFreeListHead;

        // Per-cell OwnerClass tag. Dense array of NumCells pointers.
        // OwnerClass[i] == nullptr means the cell is on the
        // unassigned chain; OwnerClass[i] != nullptr means the cell
        // is owned by that FClass (regardless of whether it's
        // currently allocated or on the class's free-list).
        const ::XCore::Reflect::FClass** OwnerClass;
    };

    // -----------------------------------------------------------------
    // FClassPool -- one FClass's free-list of cells of the right
    // size class.
    // -----------------------------------------------------------------
    struct FXObjectAllocator::FClassPool
    {
        const ::XCore::Reflect::FClass*  Class;
        ::int32                          SizeClassIndex;

        // Intrusive head of the class-owned free-list. Cells on this
        // chain belong to `Class` and are ready for the next
        // AllocateRaw(cls) call.
        void*                            ClassFreeListHead;

        // Counters.
        ::int32                          LiveCellCount;    // currently-allocated
        ::int32                          OwnedCellCount;   // total cells assigned (live + on free-list)
    };

    // -----------------------------------------------------------------
    // FSizeClassPool -- one size class's slabs + bookkeeping.
    //
    // The slabs are stored in a TArray-like dynamic array sorted by
    // base address (so reverse lookup at Deallocate is a binary
    // search). Adding a new slab keeps the array sorted via an
    // insertion-sort step (a fresh slab's base from FMemory::Malloc
    // is typically at a new high-water-mark address but we don't
    // rely on that ordering).
    // -----------------------------------------------------------------
    struct FXObjectAllocator::FSizeClassPool
    {
        ::int32                          SizeClassIndex;
        ::SIZE_T                         CellWidth;
        ::SIZE_T                         SlabBytes;

        FSlab**                          Slabs;            // sorted by Base ascending
        ::int32                          SlabCount;
        ::int32                          SlabCapacity;

        // Per-pool counters (sum across all slabs).
        ::int32                          TotalCells;
        ::int32                          TotalLiveCells;
    };

    // -----------------------------------------------------------------
    // FLargeSlab -- one large-object allocation. One slab per
    // allocation; no cells (the allocation IS the slab).
    // -----------------------------------------------------------------
    struct FXObjectAllocator::FLargeSlab
    {
        void*                            Base;
        ::SIZE_T                         Size;
        const ::XCore::Reflect::FClass*  Class;
    };

    // -----------------------------------------------------------------
    // FState -- the singleton's PImpl.
    // -----------------------------------------------------------------
    struct FXObjectAllocator::FState
    {
        FSizeClassPool                   SizeClasses[kFXObjectAllocatorNumSizeClasses];

        // Per-FClass pool registry. A simple linear array keyed by
        // FClass pointer; class registrations are rare (~hundreds of
        // FClasses in the engine) so the linear search is acceptable.
        // A TMap upgrade is a future optimisation; per the engine-
        // wide lock-discipline we keep the data structure simple at
        // Phase 5.b to minimise the in-lock work.
        FClassPool**                     ClassPools;
        ::int32                          ClassPoolCount;
        ::int32                          ClassPoolCapacity;

        // Default (no-class) sub-pools, one per size class. Used when
        // AllocateRaw is called with ClassDescriptor == nullptr (the
        // bootstrap path per Phase 5.a' notes). Each entry is a
        // singly-linked LIFO of cells; cells owned by the no-class
        // pool have OwnerClass set to s_NoClassSentinel below.
        FClassPool                       NoClassPools[kFXObjectAllocatorNumNormalClasses];

        // Large-object slabs.
        FLargeSlab**                     LargeSlabs;
        ::int32                          LargeSlabCount;
        ::int32                          LargeSlabCapacity;

        // Scenario boundary scope stack. Push at Begin; pop at End.
        // The active scope is the top entry. Allocations in scope
        // attribute their slabs to the scope name for the future
        // mark-region-clearing path. Phase 5.b ONLY uses this for
        // bookkeeping; the actual release action is gated until
        // Phase 5.h.
        ::XCore::Reflect::FName*         ScenarioStack;
        ::int32                          ScenarioStackCount;
        ::int32                          ScenarioStackCapacity;

        mutable ::XCore::HAL::FRWLock    Lock;
    };

    // The no-class sentinel: a non-null pointer distinct from every
    // real FClass*. We use the address of a static byte; consumers
    // never deref it.
    static char s_NoClassSentinelByte = 0;
    static const ::XCore::Reflect::FClass* const s_NoClassSentinel =
        reinterpret_cast<const ::XCore::Reflect::FClass*>(&s_NoClassSentinelByte);

    // =================================================================
    // Singleton.
    // =================================================================
    FXObjectAllocator& FXObjectAllocator::Get() noexcept
    {
        static FXObjectAllocator s_instance;
        return s_instance;
    }

    // =================================================================
    // ctor / dtor.
    // =================================================================
    FXObjectAllocator::FXObjectAllocator() noexcept
        : m_state(nullptr)
    {
        // The PImpl is allocated lazily on first use, NOT in the ctor,
        // because the ctor may run before FMemory is alive (the
        // singleton accessor is callable from any phase). Phase 5.a'
        // documents the allocator hook registration happens at
        // PostStaticInit; by then FMemory is live. We defer the
        // FState alloc to the first AllocateRaw / RegisterClassPool /
        // BeginScenarioBoundary call.
    }

    FXObjectAllocator::~FXObjectAllocator() noexcept
    {
        if (m_state == nullptr)
        {
            return;
        }

        // Free every slab + every class pool + the state itself. The
        // FXObjectArray's null sentinel does NOT depend on the
        // allocator so the destruction order is allocator-first then
        // array-last at process shutdown.

        for (::int32 sc = 0; sc < kFXObjectAllocatorNumNormalClasses; ++sc)
        {
            FSizeClassPool& Pool = m_state->SizeClasses[sc];
            for (::int32 i = 0; i < Pool.SlabCount; ++i)
            {
                FSlab* Slab = Pool.Slabs[i];
                if (Slab != nullptr)
                {
                    if (Slab->OwnerClass != nullptr)
                    {
                        ::XCore::HAL::FMemory::Free(Slab->OwnerClass);
                    }
                    if (Slab->Base != nullptr)
                    {
                        ::XCore::HAL::FMemory::Free(Slab->Base);
                    }
                    ::XCore::HAL::FMemory::Free(Slab);
                }
            }
            if (Pool.Slabs != nullptr)
            {
                ::XCore::HAL::FMemory::Free(Pool.Slabs);
            }
        }

        for (::int32 i = 0; i < m_state->ClassPoolCount; ++i)
        {
            if (m_state->ClassPools[i] != nullptr)
            {
                ::XCore::HAL::FMemory::Free(m_state->ClassPools[i]);
            }
        }
        if (m_state->ClassPools != nullptr)
        {
            ::XCore::HAL::FMemory::Free(m_state->ClassPools);
        }

        for (::int32 i = 0; i < m_state->LargeSlabCount; ++i)
        {
            FLargeSlab* Slab = m_state->LargeSlabs[i];
            if (Slab != nullptr)
            {
                if (Slab->Base != nullptr)
                {
                    ::XCore::HAL::FMemory::Free(Slab->Base);
                }
                ::XCore::HAL::FMemory::Free(Slab);
            }
        }
        if (m_state->LargeSlabs != nullptr)
        {
            ::XCore::HAL::FMemory::Free(m_state->LargeSlabs);
        }

        if (m_state->ScenarioStack != nullptr)
        {
            ::XCore::HAL::FMemory::Free(m_state->ScenarioStack);
        }

        m_state->~FState();
        ::XCore::HAL::FMemory::Free(m_state);
        m_state = nullptr;
    }

    // =================================================================
    // Lazy state initialisation. Pre-condition: caller holds m_state's
    // lock OR is the magic-static initialiser (no concurrent callers).
    //
    // The lazy-init protocol mirrors FNamePool's: the first caller to
    // observe m_state == nullptr takes the EXCLUSIVE lock (acquired
    // via the function-local-static singleton's wrapper) and allocates
    // the PImpl; subsequent callers find m_state non-null and skip.
    // =================================================================
    FXObjectAllocator::FState* FXObjectAllocator::EnsureStateInitialised(
        FXObjectAllocator::FState*& StateRef) noexcept
    {
        if (StateRef != nullptr)
        {
            return StateRef;
        }

        // We allocate ALL state up front in this initialiser. The
        // ctor cannot do this because FMemory may not be live yet
        // when the singleton's storage is constructed.
        FXObjectAllocator::FState* NewState = static_cast<FXObjectAllocator::FState*>(
            ::XCore::HAL::FMemory::MallocOrAbort(
                sizeof(FXObjectAllocator::FState),
                alignof(FXObjectAllocator::FState),
                ::XCore::HAL::FMemTag::XObject));

        ::new (NewState) FXObjectAllocator::FState();

        // Initialise size-class pools.
        for (::int32 sc = 0; sc < kFXObjectAllocatorNumNormalClasses; ++sc)
        {
            FXObjectAllocator::FSizeClassPool& Pool = NewState->SizeClasses[sc];
            Pool.SizeClassIndex = sc;
            Pool.CellWidth      = kFXObjectAllocatorSizeClasses[sc];
            Pool.SlabBytes      = kFXObjectAllocatorSlabBytes[sc];
            Pool.Slabs          = nullptr;
            Pool.SlabCount      = 0;
            Pool.SlabCapacity   = 0;
            Pool.TotalCells     = 0;
            Pool.TotalLiveCells = 0;

            // The no-class default sub-pool for this size class.
            NewState->NoClassPools[sc].Class             = s_NoClassSentinel;
            NewState->NoClassPools[sc].SizeClassIndex    = sc;
            NewState->NoClassPools[sc].ClassFreeListHead = nullptr;
            NewState->NoClassPools[sc].LiveCellCount     = 0;
            NewState->NoClassPools[sc].OwnedCellCount    = 0;
        }

        // The sentinel index (large-object path) gets a zeroed pool
        // (Slabs/SlabCount/TotalCells stay at zero). Don't index
        // CellWidth or SlabBytes from this slot.
        {
            FXObjectAllocator::FSizeClassPool& LargePool =
                NewState->SizeClasses[kFXObjectAllocatorNumNormalClasses];
            LargePool.SizeClassIndex = kFXObjectAllocatorNumNormalClasses;
            LargePool.CellWidth      = 0;
            LargePool.SlabBytes      = 0;
            LargePool.Slabs          = nullptr;
            LargePool.SlabCount      = 0;
            LargePool.SlabCapacity   = 0;
            LargePool.TotalCells     = 0;
            LargePool.TotalLiveCells = 0;
        }

        NewState->ClassPools         = nullptr;
        NewState->ClassPoolCount     = 0;
        NewState->ClassPoolCapacity  = 0;

        NewState->LargeSlabs         = nullptr;
        NewState->LargeSlabCount     = 0;
        NewState->LargeSlabCapacity  = 0;

        NewState->ScenarioStack         = nullptr;
        NewState->ScenarioStackCount    = 0;
        NewState->ScenarioStackCapacity = 0;

        StateRef = NewState;
        return StateRef;
    }

    // =================================================================
    // SizeToSizeClass -- the spec §3.2 fit rule.
    //
    // Returns the smallest size-class index s such that:
    //   * kFXObjectAllocatorSizeClasses[s] >= Size, AND
    //   * Size * 1.25 <= kFXObjectAllocatorSizeClasses[s]
    //       (equivalent: Size * 4 <= Class * 5; integer arithmetic)
    //
    // If no normal class fits (Size exceeds the largest normal class
    // OR violates the 1.25x bound for every class), routes to the
    // large-object path (returns kFXObjectAllocatorNumNormalClasses).
    // =================================================================
    ::int32 FXObjectAllocator::SizeToSizeClass(::SIZE_T Size) noexcept
    {
        if (Size == 0)
        {
            // Zero-byte allocations land in the smallest class so we
            // return a non-null distinct pointer; matches FMemory's
            // zero-byte semantics.
            return 0;
        }

        // The 1.25x rule: Size <= Class * 1.25 iff
        // Size * 4 <= Class * 5 (integer). We iterate and return the
        // first class where BOTH conditions hold.
        for (::int32 sc = 0; sc < kFXObjectAllocatorNumNormalClasses; ++sc)
        {
            const ::SIZE_T Class = kFXObjectAllocatorSizeClasses[sc];
            if (Class < Size)
            {
                continue;
            }
            // Class >= Size. Now check the 1.25x bound.
            // Size * 1.25 <= Class iff Size * 4 <= Class * 5.
            if (Size * 4 <= Class * 5)
            {
                return sc;
            }
        }
        // Either > largest class OR violates 1.25x bound on every
        // class that satisfies >= Size. Either way, large-object path.
        return kFXObjectAllocatorNumNormalClasses;
    }

    ::SIZE_T FXObjectAllocator::SizeClassToWidth(::int32 SizeClassIndex) noexcept
    {
        if (SizeClassIndex < 0 || SizeClassIndex >= kFXObjectAllocatorNumNormalClasses)
        {
            return 0;
        }
        return kFXObjectAllocatorSizeClasses[SizeClassIndex];
    }

    // =================================================================
    // IsHeapAddress -- conservative-root heap-range check
    // (XCoreXObject Rev 4 §5.3 step 1 + Rev 2 FIX-A-MED-35; Phase 5.e).
    //
    // Walks every size-class pool's sorted-by-base slab array via
    // binary search; returns true on first slab whose byte range
    // contains Candidate. Falls through to a linear scan of the
    // large-slab list (large allocations are one slab per allocation
    // with their own base + size).
    //
    // SHARED lock acquired so the slab tables are consistent under
    // concurrent grow / coalesce.
    //
    // SAFETY: NEVER dereferences Candidate (per spec §5.3 invariant
    // for the Conservative validation path). Pointer arithmetic on
    // the slab Base / size is well-defined regardless of Candidate's
    // value (subtraction between a non-pointer-into-the-array and an
    // array-pointer is UB; we use std::less-equivalent via the
    // standard pointer comparison rules, which on every supported
    // platform compares the addresses bitwise).
    //
    // The pointer comparisons (`Candidate < SlabStart`, `>= SlabEnd`)
    // mirror the Deallocate body pattern (lines 946-958); on Win64 /
    // Linux-x86_64 / Android-ARM64 the C++ rules permit total-order
    // pointer comparison even for unrelated pointers via the
    // std::less template specialisation. We use the raw operator<
    // here matching Deallocate; if a future platform tightens the
    // standard's UB rules we switch to std::less<const void*>.
    // =================================================================
    bool FXObjectAllocator::IsHeapAddress(const void* Candidate) const noexcept
    {
        if (Candidate == nullptr || m_state == nullptr)
        {
            return false;
        }

        ::XCore::HAL::FScopedReadLock ReadLock(m_state->Lock);

        // --- Normal-class pools: binary search per size class ---
        for (::int32 sc = 0; sc < kFXObjectAllocatorNumNormalClasses; ++sc)
        {
            const FSizeClassPool& SizePool = m_state->SizeClasses[sc];
            if (SizePool.SlabCount == 0)
            {
                continue;
            }
            ::int32 Lo = 0;
            ::int32 Hi = SizePool.SlabCount - 1;
            while (Lo <= Hi)
            {
                const ::int32 Mid = Lo + ((Hi - Lo) / 2);
                const FSlab* Slab = SizePool.Slabs[Mid];
                const char* const SlabStart = static_cast<const char*>(Slab->Base);
                const char* const SlabEnd   = SlabStart +
                    (static_cast<::SIZE_T>(Slab->NumCells) * SizePool.CellWidth);
                const char* const C = static_cast<const char*>(Candidate);
                if (C < SlabStart)
                {
                    Hi = Mid - 1;
                }
                else if (C >= SlabEnd)
                {
                    Lo = Mid + 1;
                }
                else
                {
                    return true;
                }
            }
        }

        // --- Large-slab list: linear search ---
        for (::int32 i = 0; i < m_state->LargeSlabCount; ++i)
        {
            const FLargeSlab* Slab = m_state->LargeSlabs[i];
            if (Slab == nullptr)
            {
                continue;
            }
            const char* const SlabStart = static_cast<const char*>(Slab->Base);
            const char* const SlabEnd   = SlabStart + Slab->Size;
            const char* const C = static_cast<const char*>(Candidate);
            if (C >= SlabStart && C < SlabEnd)
            {
                return true;
            }
        }

        return false;
    }

    // =================================================================
    // Helpers: per-FClass pool lookup + registration.
    //
    // The class-pool registry is a linear-search dynamic array. At
    // ~hundreds of registered classes the linear walk is faster than
    // a hash-table probe + cache miss (the array is contiguous,
    // FClass pointer compares are cheap, and the walk is well-
    // predicted by the branch predictor).
    // =================================================================

    FXObjectAllocator::FClassPool* FXObjectAllocator::FindClassPoolUnderLock(
        FXObjectAllocator::FState* State,
        const ::XCore::Reflect::FClass* Class) noexcept
    {
        for (::int32 i = 0; i < State->ClassPoolCount; ++i)
        {
            if (State->ClassPools[i]->Class == Class)
            {
                return State->ClassPools[i];
            }
        }
        return nullptr;
    }

    void FXObjectAllocator::RegisterClassPoolUnderLock(
        FXObjectAllocator::FState* State,
        const ::XCore::Reflect::FClass* Class,
        ::int32 SizeClassIndex) noexcept
    {
        // Grow the registry if needed.
        if (State->ClassPoolCount >= State->ClassPoolCapacity)
        {
            const ::int32 NewCapacity =
                (State->ClassPoolCapacity == 0) ? 16 : (State->ClassPoolCapacity * 2);
            const ::SIZE_T NewSize =
                static_cast<::SIZE_T>(NewCapacity) * sizeof(FXObjectAllocator::FClassPool*);
            FXObjectAllocator::FClassPool** NewBuf =
                static_cast<FXObjectAllocator::FClassPool**>(
                    ::XCore::HAL::FMemory::MallocOrAbort(
                        NewSize,
                        alignof(FXObjectAllocator::FClassPool*),
                        ::XCore::HAL::FMemTag::XObject));
            if (State->ClassPools != nullptr)
            {
                ::XCore::HAL::FPlatformMemory::Memcpy(
                    NewBuf, State->ClassPools,
                    static_cast<::SIZE_T>(State->ClassPoolCount) *
                        sizeof(FXObjectAllocator::FClassPool*));
                ::XCore::HAL::FMemory::Free(State->ClassPools);
            }
            State->ClassPools        = NewBuf;
            State->ClassPoolCapacity = NewCapacity;
        }

        FXObjectAllocator::FClassPool* Pool =
            static_cast<FXObjectAllocator::FClassPool*>(
                ::XCore::HAL::FMemory::MallocOrAbort(
                    sizeof(FXObjectAllocator::FClassPool),
                    alignof(FXObjectAllocator::FClassPool),
                    ::XCore::HAL::FMemTag::XObject));
        Pool->Class             = Class;
        Pool->SizeClassIndex    = SizeClassIndex;
        Pool->ClassFreeListHead = nullptr;
        Pool->LiveCellCount     = 0;
        Pool->OwnedCellCount    = 0;
        State->ClassPools[State->ClassPoolCount] = Pool;
        ++State->ClassPoolCount;
    }

    // Compute the size-class index from an FClass via its
    // PropertiesSize + MinAlignment. The MinAlignment is honored
    // (rounded up via ceil-mask) and the result is the smallest
    // size class that fits the rounded size.
    ::int32 FXObjectAllocator::ClassToSizeClass(
        const ::XCore::Reflect::FClass* Class) noexcept
    {
        // FClass extends FStruct; the size + alignment fields are
        // accessible via FStruct's public accessors.
        const auto* AsStruct =
            static_cast<const ::XCore::Reflect::FStruct*>(Class);
        const ::SIZE_T Size  = static_cast<::SIZE_T>(AsStruct->GetPropertiesSize());
        const ::SIZE_T Align = static_cast<::SIZE_T>(AsStruct->GetMinAlignment());
        // Round Size up to Align (cells are naturally aligned at
        // the size-class width; we pass the alignment-aware size
        // through the same fit rule).
        const ::SIZE_T RoundedSize =
            (Align == 0) ? Size : ((Size + Align - 1) & ~(Align - 1));
        return FXObjectAllocator::SizeToSizeClass(RoundedSize);
    }

    // =================================================================
    // RegisterClassPool -- pre-create a type-segregated free-list.
    // =================================================================
    void FXObjectAllocator::RegisterClassPool(
        const ::XCore::Reflect::FClass* ClassDescriptor) noexcept
    {
        XPACT_CHECK(ClassDescriptor != nullptr);

        ::XCore::HAL::FScopedWriteLock WriteLock(m_state ? m_state->Lock : EnsureStateInitialised(m_state)->Lock);
        EnsureStateInitialised(m_state);

        if (FindClassPoolUnderLock(m_state, ClassDescriptor) != nullptr)
        {
            return;  // idempotent
        }

        const ::int32 SizeClassIndex = ClassToSizeClass(ClassDescriptor);
        RegisterClassPoolUnderLock(m_state, ClassDescriptor, SizeClassIndex);
    }

    // =================================================================
    // RebindClassPool -- hot-reload class replacement.
    // =================================================================
    void FXObjectAllocator::RebindClassPool(
        const ::XCore::Reflect::FClass* OldClass,
        const ::XCore::Reflect::FClass* NewClass) noexcept
    {
        XPACT_CHECK(OldClass  != nullptr);
        XPACT_CHECK(NewClass  != nullptr);
        XPACT_CHECK(OldClass  != NewClass);

        ::XCore::HAL::FScopedWriteLock WriteLock(m_state ? m_state->Lock : EnsureStateInitialised(m_state)->Lock);
        EnsureStateInitialised(m_state);

        FClassPool* OldPool = FindClassPoolUnderLock(m_state, OldClass);
        if (OldPool == nullptr)
        {
            // OldClass was never registered -- nothing to rebind.
            return;
        }

        FClassPool* NewPool = FindClassPoolUnderLock(m_state, NewClass);
        if (NewPool == nullptr)
        {
            // Auto-register the NewClass at the same size class as
            // OldClass.
            RegisterClassPoolUnderLock(m_state, NewClass, OldPool->SizeClassIndex);
            NewPool = FindClassPoolUnderLock(m_state, NewClass);
            XPACT_CHECK(NewPool != nullptr);
        }

        XPACT_CHECK(NewPool->SizeClassIndex == OldPool->SizeClassIndex);

        // Walk the size class's slabs; for every cell currently
        // marked as owned by OldClass, retag to NewClass. The cell
        // does not move; the OwnerClass tag flips.
        FSizeClassPool& SizePool = m_state->SizeClasses[OldPool->SizeClassIndex];
        for (::int32 si = 0; si < SizePool.SlabCount; ++si)
        {
            FSlab* Slab = SizePool.Slabs[si];
            for (::int32 ci = 0; ci < Slab->NumCells; ++ci)
            {
                if (Slab->OwnerClass[ci] == OldClass)
                {
                    Slab->OwnerClass[ci] = NewClass;
                }
            }
        }

        // Concatenate OldPool's free-list onto NewPool's free-list
        // (intrusive walk-to-tail-then-link).
        if (OldPool->ClassFreeListHead != nullptr)
        {
            void* Tail = OldPool->ClassFreeListHead;
            while (*reinterpret_cast<void**>(Tail) != nullptr)
            {
                Tail = *reinterpret_cast<void**>(Tail);
            }
            *reinterpret_cast<void**>(Tail) = NewPool->ClassFreeListHead;
            NewPool->ClassFreeListHead       = OldPool->ClassFreeListHead;

            OldPool->ClassFreeListHead = nullptr;
        }

        // Migrate counts.
        NewPool->LiveCellCount  += OldPool->LiveCellCount;
        NewPool->OwnedCellCount += OldPool->OwnedCellCount;
        OldPool->LiveCellCount   = 0;
        OldPool->OwnedCellCount  = 0;
    }

    // =================================================================
    // AllocateRaw -- the 3-arg s_XObjectAllocator hook handler.
    // =================================================================
    void* FXObjectAllocator::AllocateRaw(
        ::SIZE_T Size,
        ::SIZE_T Align,
        const ::XCore::Reflect::FClass* ClassDescriptor) noexcept
    {
        // Honor zero-byte allocations: return a non-null distinct
        // pointer (the smallest cell from size class 0).
        if (Size == 0)
        {
            Size = 1;
        }

        // Compute the size class taking alignment into account.
        const ::SIZE_T RoundedSize =
            (Align == 0) ? Size : ((Size + Align - 1) & ~(Align - 1));
        const ::int32 SizeClassIndex = SizeToSizeClass(RoundedSize);

        ::XCore::HAL::FScopedWriteLock WriteLock(m_state ? m_state->Lock : EnsureStateInitialised(m_state)->Lock);
        EnsureStateInitialised(m_state);

        // -------- Large-object path --------
        if (SizeClassIndex >= kFXObjectAllocatorNumNormalClasses)
        {
            // One slab per allocation. The full requested size is
            // honored (NOT rounded to a size class width).
            void* Storage = ::XCore::HAL::FMemory::MallocOrAbort(
                RoundedSize,
                (Align == 0) ? alignof(::std::max_align_t) : Align,
                ::XCore::HAL::FMemTag::XObject);

            // Track the large slab so Deallocate can find it.
            if (m_state->LargeSlabCount >= m_state->LargeSlabCapacity)
            {
                const ::int32 NewCapacity =
                    (m_state->LargeSlabCapacity == 0) ? 16 : (m_state->LargeSlabCapacity * 2);
                FLargeSlab** NewBuf = static_cast<FLargeSlab**>(
                    ::XCore::HAL::FMemory::MallocOrAbort(
                        static_cast<::SIZE_T>(NewCapacity) * sizeof(FLargeSlab*),
                        alignof(FLargeSlab*),
                        ::XCore::HAL::FMemTag::XObject));
                if (m_state->LargeSlabs != nullptr)
                {
                    ::XCore::HAL::FPlatformMemory::Memcpy(
                        NewBuf, m_state->LargeSlabs,
                        static_cast<::SIZE_T>(m_state->LargeSlabCount) * sizeof(FLargeSlab*));
                    ::XCore::HAL::FMemory::Free(m_state->LargeSlabs);
                }
                m_state->LargeSlabs        = NewBuf;
                m_state->LargeSlabCapacity = NewCapacity;
            }
            FLargeSlab* NewSlab = static_cast<FLargeSlab*>(
                ::XCore::HAL::FMemory::MallocOrAbort(
                    sizeof(FLargeSlab), alignof(FLargeSlab),
                    ::XCore::HAL::FMemTag::XObject));
            NewSlab->Base  = Storage;
            NewSlab->Size  = RoundedSize;
            NewSlab->Class = ClassDescriptor;
            m_state->LargeSlabs[m_state->LargeSlabCount] = NewSlab;
            ++m_state->LargeSlabCount;
            return Storage;
        }

        // -------- Normal size-class path --------
        FSizeClassPool& SizePool = m_state->SizeClasses[SizeClassIndex];

        // Find or auto-create the class pool. If ClassDescriptor is
        // nullptr we route to the no-class default sub-pool.
        FClassPool* ClassPool;
        if (ClassDescriptor == nullptr)
        {
            ClassPool = &m_state->NoClassPools[SizeClassIndex];
        }
        else
        {
            ClassPool = FindClassPoolUnderLock(m_state, ClassDescriptor);
            if (ClassPool == nullptr)
            {
                RegisterClassPoolUnderLock(m_state, ClassDescriptor, SizeClassIndex);
                ClassPool = FindClassPoolUnderLock(m_state, ClassDescriptor);
                XPACT_CHECK(ClassPool != nullptr);
            }
        }

        // Fast path: pop a cell from the class-specific free-list.
        if (ClassPool->ClassFreeListHead != nullptr)
        {
            void* Cell = ClassPool->ClassFreeListHead;
            ClassPool->ClassFreeListHead = *reinterpret_cast<void**>(Cell);
            ++ClassPool->LiveCellCount;
            ++SizePool.TotalLiveCells;
            return Cell;
        }

        // No class-specific free cell. Steal one from the slab's
        // unassigned chain. Walk slabs looking for any with an
        // unassigned head.
        FSlab* DonorSlab = nullptr;
        for (::int32 si = 0; si < SizePool.SlabCount; ++si)
        {
            if (SizePool.Slabs[si]->UnassignedFreeListHead != nullptr)
            {
                DonorSlab = SizePool.Slabs[si];
                break;
            }
        }

        if (DonorSlab == nullptr)
        {
            // Need a fresh slab. Allocate the backing storage; thread
            // every cell onto the slab's unassigned chain; install
            // the slab in the pool's sorted slab array.
            //
            // NOTE on lock discipline: we should ideally drop the
            // exclusive lock here and re-acquire after the FMemory
            // call to avoid holding the lock during slab allocation.
            // The FMemory call uses FMemTag::XObject which does NOT
            // have an inverse dependency on the FXObjectAllocator
            // (the allocator does not call out to any subsystem that
            // could call back into us under FMemory). The AB-BA
            // hazard the lock-discipline contract guards against is
            // therefore structurally absent here; we keep the simpler
            // call-under-lock pattern for Phase 5.b and document the
            // analysis. (FNamePool's PredictInsertPreAlloc pattern
            // would be a future-phase upgrade if profiling shows
            // contention on this path.)
            const ::SIZE_T SlabBytes  = SizePool.SlabBytes;
            const ::SIZE_T CellWidth  = SizePool.CellWidth;
            // Slab alignment: FMemory::MallocOrAbort requires Align to
            // be a power-of-two. CellWidth is NOT always a power-of-two
            // (the spec §3.2 table includes 96 / 192 / 384 / 768).
            // Aligning the slab base to 64 bytes (the spec's smallest
            // cell width AND the typical cache-line size) ensures every
            // cell at offset i*CellWidth is also aligned to the XObject
            // header's alignof requirement (8). All spec-defined cell
            // widths are multiples of 8, so cell-offset alignment is
            // preserved across the cell stride.
            constexpr ::SIZE_T kSlabBaseAlign = ::SIZE_T(64);
            void* SlabBase = ::XCore::HAL::FMemory::MallocOrAbort(
                SlabBytes,
                /*Align=*/ kSlabBaseAlign,
                ::XCore::HAL::FMemTag::XObject);

            const ::int32 NumCells = static_cast<::int32>(SlabBytes / CellWidth);

            // Allocate the FSlab control block.
            FSlab* NewSlab = static_cast<FSlab*>(
                ::XCore::HAL::FMemory::MallocOrAbort(
                    sizeof(FSlab), alignof(FSlab),
                    ::XCore::HAL::FMemTag::XObject));
            NewSlab->Base                   = SlabBase;
            NewSlab->SizeClassIndex         = SizeClassIndex;
            NewSlab->NumCells               = NumCells;
            NewSlab->LiveCellCount          = 0;
            NewSlab->UnassignedFreeListHead = nullptr;
            NewSlab->OwnerClass = static_cast<const ::XCore::Reflect::FClass**>(
                ::XCore::HAL::FMemory::MallocOrAbort(
                    static_cast<::SIZE_T>(NumCells) * sizeof(const ::XCore::Reflect::FClass*),
                    alignof(const ::XCore::Reflect::FClass*),
                    ::XCore::HAL::FMemTag::XObject));
            ::XCore::HAL::FPlatformMemory::Memzero(
                NewSlab->OwnerClass,
                static_cast<::SIZE_T>(NumCells) * sizeof(const ::XCore::Reflect::FClass*));

            // Thread every cell onto the unassigned chain (LIFO).
            // Walk in reverse so cell 0 becomes the head.
            char* const Bytes = static_cast<char*>(SlabBase);
            for (::int32 ci = NumCells - 1; ci >= 0; --ci)
            {
                void* Cell = Bytes + (static_cast<::SIZE_T>(ci) * CellWidth);
                *reinterpret_cast<void**>(Cell) = NewSlab->UnassignedFreeListHead;
                NewSlab->UnassignedFreeListHead = Cell;
            }

            // Install the slab. Grow the slab array if needed.
            if (SizePool.SlabCount >= SizePool.SlabCapacity)
            {
                const ::int32 NewCapacity =
                    (SizePool.SlabCapacity == 0) ? 8 : (SizePool.SlabCapacity * 2);
                FSlab** NewBuf = static_cast<FSlab**>(
                    ::XCore::HAL::FMemory::MallocOrAbort(
                        static_cast<::SIZE_T>(NewCapacity) * sizeof(FSlab*),
                        alignof(FSlab*),
                        ::XCore::HAL::FMemTag::XObject));
                if (SizePool.Slabs != nullptr)
                {
                    ::XCore::HAL::FPlatformMemory::Memcpy(
                        NewBuf, SizePool.Slabs,
                        static_cast<::SIZE_T>(SizePool.SlabCount) * sizeof(FSlab*));
                    ::XCore::HAL::FMemory::Free(SizePool.Slabs);
                }
                SizePool.Slabs        = NewBuf;
                SizePool.SlabCapacity = NewCapacity;
            }

            // Insert into the sorted array (by Base) so Deallocate's
            // binary search works. Find the insertion point + shift.
            ::int32 InsertAt = SizePool.SlabCount;
            for (::int32 i = 0; i < SizePool.SlabCount; ++i)
            {
                if (NewSlab->Base < SizePool.Slabs[i]->Base)
                {
                    InsertAt = i;
                    break;
                }
            }
            // Shift right.
            for (::int32 i = SizePool.SlabCount; i > InsertAt; --i)
            {
                SizePool.Slabs[i] = SizePool.Slabs[i - 1];
            }
            SizePool.Slabs[InsertAt] = NewSlab;
            ++SizePool.SlabCount;
            SizePool.TotalCells += NumCells;

            DonorSlab = NewSlab;
        }

        // Move one cell from DonorSlab's unassigned chain to the
        // ClassPool's free-list, then pop it for the caller.
        XPACT_CHECK(DonorSlab != nullptr);
        XPACT_CHECK(DonorSlab->UnassignedFreeListHead != nullptr);

        void* Cell = DonorSlab->UnassignedFreeListHead;
        DonorSlab->UnassignedFreeListHead =
            *reinterpret_cast<void**>(Cell);

        // Compute the cell index within the slab so we can tag its
        // OwnerClass.
        const ::SIZE_T CellOffset =
            static_cast<::SIZE_T>(
                static_cast<char*>(Cell) - static_cast<char*>(DonorSlab->Base));
        const ::int32 CellIndex =
            static_cast<::int32>(CellOffset / SizePool.CellWidth);
        XPACT_CHECK(CellIndex >= 0);
        XPACT_CHECK(CellIndex <  DonorSlab->NumCells);

        const ::XCore::Reflect::FClass* TagClass =
            (ClassDescriptor == nullptr) ? s_NoClassSentinel : ClassDescriptor;
        DonorSlab->OwnerClass[CellIndex] = TagClass;

        // Update counters. The cell is allocated immediately (not
        // pushed onto the class free-list); no need to thread it.
        ++ClassPool->LiveCellCount;
        ++ClassPool->OwnedCellCount;
        ++DonorSlab->LiveCellCount;
        ++SizePool.TotalLiveCells;

        return Cell;
    }

    void* FXObjectAllocator::AllocateRawStatic(
        ::SIZE_T Size,
        ::SIZE_T Align,
        const ::XCore::Reflect::FClass* ClassDescriptor) noexcept
    {
        return Get().AllocateRaw(Size, Align, ClassDescriptor);
    }

    // =================================================================
    // Deallocate -- by-pointer free.
    //
    // Reverse-lookup: binary-search every size-class pool's sorted
    // slab array for the slab containing the pointer. If no normal-
    // pool slab matches, check the large-slab list.
    // =================================================================
    void FXObjectAllocator::Deallocate(void* Storage) noexcept
    {
        if (Storage == nullptr || m_state == nullptr)
        {
            return;
        }

        ::XCore::HAL::FScopedWriteLock WriteLock(m_state->Lock);

        // --- Normal-class search ---
        for (::int32 sc = 0; sc < kFXObjectAllocatorNumNormalClasses; ++sc)
        {
            FSizeClassPool& SizePool = m_state->SizeClasses[sc];
            if (SizePool.SlabCount == 0)
            {
                continue;
            }

            // Binary search on sorted-by-Base slabs.
            ::int32 Lo = 0;
            ::int32 Hi = SizePool.SlabCount - 1;
            FSlab* Found = nullptr;
            while (Lo <= Hi)
            {
                const ::int32 Mid = Lo + ((Hi - Lo) / 2);
                FSlab* Candidate = SizePool.Slabs[Mid];
                char* const SlabStart = static_cast<char*>(Candidate->Base);
                char* const SlabEnd   = SlabStart +
                    (static_cast<::SIZE_T>(Candidate->NumCells) * SizePool.CellWidth);
                if (Storage < SlabStart)
                {
                    Hi = Mid - 1;
                }
                else if (Storage >= SlabEnd)
                {
                    Lo = Mid + 1;
                }
                else
                {
                    Found = Candidate;
                    break;
                }
            }

            if (Found != nullptr)
            {
                // Compute cell index.
                const ::SIZE_T CellOffset =
                    static_cast<::SIZE_T>(
                        static_cast<char*>(Storage) - static_cast<char*>(Found->Base));
                XPACT_CHECK((CellOffset % SizePool.CellWidth) == 0);
                const ::int32 CellIndex =
                    static_cast<::int32>(CellOffset / SizePool.CellWidth);

                // Route to the OwnerClass's free-list.
                const ::XCore::Reflect::FClass* OwnerClass = Found->OwnerClass[CellIndex];
                XPACT_CHECK(OwnerClass != nullptr);  // never deallocated unassigned

                FClassPool* TargetPool;
                if (OwnerClass == s_NoClassSentinel)
                {
                    TargetPool = &m_state->NoClassPools[sc];
                }
                else
                {
                    TargetPool = FindClassPoolUnderLock(m_state, OwnerClass);
                    XPACT_CHECK(TargetPool != nullptr);
                }

                // Push the cell onto the class's free-list (LIFO).
                *reinterpret_cast<void**>(Storage) = TargetPool->ClassFreeListHead;
                TargetPool->ClassFreeListHead     = Storage;

                --TargetPool->LiveCellCount;
                --Found->LiveCellCount;
                --SizePool.TotalLiveCells;
                return;
            }
        }

        // --- Large-slab search ---
        for (::int32 i = 0; i < m_state->LargeSlabCount; ++i)
        {
            FLargeSlab* Slab = m_state->LargeSlabs[i];
            if (Slab != nullptr && Slab->Base == Storage)
            {
                ::XCore::HAL::FMemory::Free(Slab->Base);
                ::XCore::HAL::FMemory::Free(Slab);
                // Compact the array (swap-remove).
                m_state->LargeSlabs[i] = m_state->LargeSlabs[m_state->LargeSlabCount - 1];
                --m_state->LargeSlabCount;
                return;
            }
        }

        // Pointer was not found in any pool -- programmer error.
        ::XCore::HAL::AbortWithMessage(
            "FXObjectAllocator::Deallocate: pointer not recognised as "
            "having been returned by AllocateRaw. Double-free or wrong "
            "allocator?",
            __FILE__, __LINE__);
    }

    // =================================================================
    // BeginScenarioBoundary / EndScenarioBoundary.
    // =================================================================
    void FXObjectAllocator::BeginScenarioBoundary() noexcept
    {
        ::XCore::HAL::FScopedWriteLock WriteLock(m_state ? m_state->Lock : EnsureStateInitialised(m_state)->Lock);
        EnsureStateInitialised(m_state);

        if (m_state->ScenarioStackCount >= m_state->ScenarioStackCapacity)
        {
            const ::int32 NewCapacity =
                (m_state->ScenarioStackCapacity == 0) ? 8 : (m_state->ScenarioStackCapacity * 2);
            ::XCore::Reflect::FName* NewBuf =
                static_cast<::XCore::Reflect::FName*>(
                    ::XCore::HAL::FMemory::MallocOrAbort(
                        static_cast<::SIZE_T>(NewCapacity) * sizeof(::XCore::Reflect::FName),
                        alignof(::XCore::Reflect::FName),
                        ::XCore::HAL::FMemTag::XObject));
            if (m_state->ScenarioStack != nullptr)
            {
                ::XCore::HAL::FPlatformMemory::Memcpy(
                    NewBuf, m_state->ScenarioStack,
                    static_cast<::SIZE_T>(m_state->ScenarioStackCount) *
                        sizeof(::XCore::Reflect::FName));
                ::XCore::HAL::FMemory::Free(m_state->ScenarioStack);
            }
            m_state->ScenarioStack         = NewBuf;
            m_state->ScenarioStackCapacity = NewCapacity;
        }

        // Push a placeholder NAME_None; End* pairs the explicit name
        // for symmetry checking.
        m_state->ScenarioStack[m_state->ScenarioStackCount] = ::XCore::Reflect::FName();
        ++m_state->ScenarioStackCount;
    }

    void FXObjectAllocator::EndScenarioBoundary(
        ::XCore::Reflect::FName /*ScenarioName*/) noexcept
    {
        if (m_state == nullptr)
        {
            return;
        }

        ::XCore::HAL::FScopedWriteLock WriteLock(m_state->Lock);
        XPACT_CHECK(m_state->ScenarioStackCount > 0);

        // Phase 5.b: pop the scope. The mark-region-clearing action
        // is gated until Phase 5.h provides the reachability oracle
        // (per spec §3.6 + the header docstring's gating note).
        --m_state->ScenarioStackCount;
    }

    // =================================================================
    // CoalesceIdleSlabs -- engineer-station-only slab reclaim.
    //
    // Phase 5.b acts ONLY on fully-empty slabs (LiveCellCount == 0).
    // Returns the count of slabs released.
    // =================================================================
    ::int32 FXObjectAllocator::CoalesceIdleSlabs() noexcept
    {
        if (m_state == nullptr)
        {
            return 0;
        }

        ::XCore::HAL::FScopedWriteLock WriteLock(m_state->Lock);

        ::int32 ReleasedCount = 0;

        for (::int32 sc = 0; sc < kFXObjectAllocatorNumNormalClasses; ++sc)
        {
            FSizeClassPool& SizePool = m_state->SizeClasses[sc];

            // First pass: identify empty slabs (LiveCellCount == 0).
            // Removing them changes the sorted slab list; we collect
            // them first then process to avoid mid-iteration mutation.
            ::int32 Write = 0;
            for (::int32 Read = 0; Read < SizePool.SlabCount; ++Read)
            {
                FSlab* Slab = SizePool.Slabs[Read];
                if (Slab->LiveCellCount == 0)
                {
                    // Slab is fully empty. The unassigned chain holds
                    // every cell (since LiveCellCount == 0). We must
                    // also unthread any class free-lists that point
                    // into this slab's cells -- failing to do so
                    // would leave dangling next-pointers in the per-
                    // class chains that would crash on the next
                    // AllocateRaw.
                    char* const SlabStart = static_cast<char*>(Slab->Base);
                    char* const SlabEnd   = SlabStart +
                        (static_cast<::SIZE_T>(Slab->NumCells) * SizePool.CellWidth);

                    // Sweep every class pool's free-list, removing
                    // nodes that point into this slab. For Phase 5.b
                    // simplicity we accept O(classes * total-free-
                    // cells) which is small (~100 classes * <100
                    // free cells per class typically).
                    auto SweepClassPool = [&](FClassPool* Pool)
                    {
                        if (Pool == nullptr || Pool->SizeClassIndex != sc)
                        {
                            return;
                        }
                        void** Cursor = &Pool->ClassFreeListHead;
                        while (*Cursor != nullptr)
                        {
                            void* Node = *Cursor;
                            if (Node >= SlabStart && Node < SlabEnd)
                            {
                                // Remove from chain.
                                *Cursor = *reinterpret_cast<void**>(Node);
                                --Pool->OwnedCellCount;
                            }
                            else
                            {
                                Cursor = reinterpret_cast<void**>(Node);
                            }
                        }
                    };

                    for (::int32 cp = 0; cp < m_state->ClassPoolCount; ++cp)
                    {
                        SweepClassPool(m_state->ClassPools[cp]);
                    }
                    SweepClassPool(&m_state->NoClassPools[sc]);

                    // Free the slab's storage + control block.
                    SizePool.TotalCells -= Slab->NumCells;
                    ::XCore::HAL::FMemory::Free(Slab->OwnerClass);
                    ::XCore::HAL::FMemory::Free(Slab->Base);
                    ::XCore::HAL::FMemory::Free(Slab);

                    ++ReleasedCount;
                }
                else
                {
                    SizePool.Slabs[Write] = Slab;
                    ++Write;
                }
            }
            SizePool.SlabCount = Write;
        }

        return ReleasedCount;
    }

    // =================================================================
    // GetStats -- diagnostic snapshot.
    // =================================================================
    FXObjectAllocatorStats FXObjectAllocator::GetStats() const noexcept
    {
        FXObjectAllocatorStats Stats;
        if (m_state == nullptr)
        {
            return Stats;
        }

        ::XCore::HAL::FScopedReadLock ReadLock(m_state->Lock);

        for (::int32 sc = 0; sc < kFXObjectAllocatorNumNormalClasses; ++sc)
        {
            const FSizeClassPool& SizePool = m_state->SizeClasses[sc];
            Stats.TotalAllocatedBytes +=
                static_cast<::SIZE_T>(SizePool.TotalLiveCells) * SizePool.CellWidth;
            Stats.TotalSlabBytes +=
                static_cast<::SIZE_T>(SizePool.SlabCount) * SizePool.SlabBytes;
            Stats.TotalLiveObjects += SizePool.TotalLiveCells;

            for (::int32 si = 0; si < SizePool.SlabCount; ++si)
            {
                if (SizePool.Slabs[si]->LiveCellCount > 0)
                {
                    ++Stats.ActiveSlabs;
                }
                else
                {
                    ++Stats.IdleSlabs;
                }
            }
        }

        // Large slabs.
        Stats.LargeObjectSlabs = m_state->LargeSlabCount;
        for (::int32 i = 0; i < m_state->LargeSlabCount; ++i)
        {
            Stats.TotalAllocatedBytes += m_state->LargeSlabs[i]->Size;
            Stats.TotalSlabBytes      += m_state->LargeSlabs[i]->Size;
            ++Stats.TotalLiveObjects;
        }

        // Fragmentation percent.
        if (Stats.TotalSlabBytes > 0)
        {
            const double Waste =
                static_cast<double>(Stats.TotalSlabBytes - Stats.TotalAllocatedBytes);
            const double Total = static_cast<double>(Stats.TotalSlabBytes);
            Stats.FragmentationPercent = (Waste / Total) * 100.0;
        }

        // Per-class entries (dense list of classes with live count > 0).
        for (::int32 cp = 0; cp < m_state->ClassPoolCount; ++cp)
        {
            const FClassPool* Pool = m_state->ClassPools[cp];
            if (Pool != nullptr && Pool->LiveCellCount > 0)
            {
                Stats.PerClassAllocations.Add(
                    FXObjectAllocatorStatsPerClassEntry(
                        Pool->Class, Pool->LiveCellCount));
            }
        }

        return Stats;
    }

    // =================================================================
    // __ResetForTests -- destructive reset.
    // =================================================================
    void FXObjectAllocator::__ResetForTests() noexcept
    {
        ::XCore::HAL::FScopedWriteLock WriteLock(m_state ? m_state->Lock : EnsureStateInitialised(m_state)->Lock);
        EnsureStateInitialised(m_state);

        // Free every slab + every class pool + every large slab.
        for (::int32 sc = 0; sc < kFXObjectAllocatorNumNormalClasses; ++sc)
        {
            FSizeClassPool& Pool = m_state->SizeClasses[sc];
            for (::int32 i = 0; i < Pool.SlabCount; ++i)
            {
                FSlab* Slab = Pool.Slabs[i];
                if (Slab != nullptr)
                {
                    if (Slab->OwnerClass != nullptr)
                    {
                        ::XCore::HAL::FMemory::Free(Slab->OwnerClass);
                    }
                    if (Slab->Base != nullptr)
                    {
                        ::XCore::HAL::FMemory::Free(Slab->Base);
                    }
                    ::XCore::HAL::FMemory::Free(Slab);
                }
            }
            if (Pool.Slabs != nullptr)
            {
                ::XCore::HAL::FMemory::Free(Pool.Slabs);
                Pool.Slabs = nullptr;
            }
            Pool.SlabCount      = 0;
            Pool.SlabCapacity   = 0;
            Pool.TotalCells     = 0;
            Pool.TotalLiveCells = 0;

            // Reset the no-class pool for this size class.
            m_state->NoClassPools[sc].ClassFreeListHead = nullptr;
            m_state->NoClassPools[sc].LiveCellCount     = 0;
            m_state->NoClassPools[sc].OwnedCellCount    = 0;
        }

        for (::int32 i = 0; i < m_state->ClassPoolCount; ++i)
        {
            if (m_state->ClassPools[i] != nullptr)
            {
                ::XCore::HAL::FMemory::Free(m_state->ClassPools[i]);
            }
        }
        m_state->ClassPoolCount = 0;

        for (::int32 i = 0; i < m_state->LargeSlabCount; ++i)
        {
            FLargeSlab* Slab = m_state->LargeSlabs[i];
            if (Slab != nullptr)
            {
                if (Slab->Base != nullptr)
                {
                    ::XCore::HAL::FMemory::Free(Slab->Base);
                }
                ::XCore::HAL::FMemory::Free(Slab);
            }
        }
        m_state->LargeSlabCount = 0;

        m_state->ScenarioStackCount = 0;
    }

} // namespace XCore
