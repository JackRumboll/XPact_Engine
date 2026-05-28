// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXSweepCandidateQueue.cpp -- Phase 5.g producer / Phase 5.h consumer
// queue body.
// =====================================================================
//
// XCoreXObject Rev 4 §4.2 step 6. Phase 5.g writes; Phase 5.h reads.
// SPSC discipline; lock-protected for simplicity (the producer +
// consumer never overlap in time across the cycle's mark / sweep
// boundary).
//
// =====================================================================

#include "XObject/FXSweepCandidateQueue.h"

#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"

#include <cstring>

namespace XCore
{

FXSweepCandidateQueue& FXSweepCandidateQueue::Get() noexcept
{
    static FXSweepCandidateQueue s_instance;
    return s_instance;
}

namespace
{
    // Initial capacity sized for typical 5%-10% of total objects
    // collected per cycle. At Foundation Prototype's ~50k objects,
    // a 5% reclaim rate is 2500 candidates; 256 is the conservative
    // starting size; growth doubles.
    constexpr ::std::size_t kSweepCandidateInitialCapacity = 256;
}

FXSweepCandidateQueue::FXSweepCandidateQueue() noexcept
    : m_entries(nullptr)
    , m_count(0)
    , m_capacity(0)
    , m_lock()
{
}

FXSweepCandidateQueue::~FXSweepCandidateQueue() noexcept
{
    if (m_entries != nullptr)
    {
        ::XCore::HAL::FMemory::Free(m_entries);
        m_entries = nullptr;
    }
}

void FXSweepCandidateQueue::GrowIfNeededUnderLock(
    ::std::size_t RequiredSlots) noexcept
{
    const ::std::size_t Needed = m_count + RequiredSlots;
    if (m_capacity >= Needed)
    {
        return;
    }
    ::std::size_t NewCapacity = (m_capacity == 0)
        ? kSweepCandidateInitialCapacity
        : m_capacity;
    while (NewCapacity < Needed)
    {
        NewCapacity *= 2;
    }
    ::std::int32_t* const NewEntries = static_cast<::std::int32_t*>(
        ::XCore::HAL::FMemory::MallocOrAbort(
            NewCapacity * sizeof(::std::int32_t),
            alignof(::std::int32_t),
            ::XCore::HAL::FMemTag::Reflection));
    if (m_entries != nullptr)
    {
        ::std::memcpy(NewEntries, m_entries, m_count * sizeof(::std::int32_t));
        ::XCore::HAL::FMemory::Free(m_entries);
    }
    m_entries  = NewEntries;
    m_capacity = NewCapacity;
}

void FXSweepCandidateQueue::Append(::std::int32_t InternalIndex) noexcept
{
    ::XCore::HAL::FScopedLock Lock(m_lock);
    GrowIfNeededUnderLock(1);
    m_entries[m_count] = InternalIndex;
    ++m_count;
}

void FXSweepCandidateQueue::AppendBatch(
    const ::std::int32_t* Entries,
    ::std::size_t         Count) noexcept
{
    if (Count == 0 || Entries == nullptr)
    {
        return;
    }
    ::XCore::HAL::FScopedLock Lock(m_lock);
    GrowIfNeededUnderLock(Count);
    ::std::memcpy(m_entries + m_count, Entries, Count * sizeof(::std::int32_t));
    m_count += Count;
}

::std::size_t FXSweepCandidateQueue::Size() const noexcept
{
    ::XCore::HAL::FScopedLock Lock(m_lock);
    return m_count;
}

bool FXSweepCandidateQueue::IsEmpty() const noexcept
{
    ::XCore::HAL::FScopedLock Lock(m_lock);
    return m_count == 0;
}

void FXSweepCandidateQueue::__ResetForTests() noexcept
{
    ::std::int32_t* OldEntries = nullptr;
    {
        ::XCore::HAL::FScopedLock Lock(m_lock);
        OldEntries = m_entries;
        m_entries  = nullptr;
        m_count    = 0;
        m_capacity = 0;
    }
    if (OldEntries != nullptr)
    {
        ::XCore::HAL::FMemory::Free(OldEntries);
    }
}

} // namespace XCore
