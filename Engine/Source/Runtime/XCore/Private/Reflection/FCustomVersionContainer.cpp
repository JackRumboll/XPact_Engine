// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FCustomVersionContainer.cpp -- sorted custom-version table body.
// =====================================================================
//
// XCore-4b Rev 3, Section 8.2. Sorted-by-Key invariant maintained at
// every public method exit.
//
// =====================================================================

#include "Reflection/FCustomVersionContainer.h"

#include "Macros/XCoreTypes.h"

#include <limits>     // std::numeric_limits<::int32>::min
#include <utility>    // std::move

namespace
{
    // -----------------------------------------------------------------
    // Header-private little-endian helpers (mirrors the helpers in
    // FCustomVersion.cpp; kept local because dragging them into a
    // shared header for two callers would be over-engineering at this
    // file count).
    // -----------------------------------------------------------------

    inline void WriteU32Le(::uint8* OutBuffer, ::uint32 Value) noexcept
    {
        OutBuffer[0] = static_cast<::uint8>( Value        & 0xFFu);
        OutBuffer[1] = static_cast<::uint8>((Value >>  8) & 0xFFu);
        OutBuffer[2] = static_cast<::uint8>((Value >> 16) & 0xFFu);
        OutBuffer[3] = static_cast<::uint8>((Value >> 24) & 0xFFu);
    }

    inline ::uint32 ReadU32Le(const ::uint8* InBuffer) noexcept
    {
        return  static_cast<::uint32>(InBuffer[0])
             | (static_cast<::uint32>(InBuffer[1]) <<  8)
             | (static_cast<::uint32>(InBuffer[2]) << 16)
             | (static_cast<::uint32>(InBuffer[3]) << 24);
    }
} // anonymous namespace

namespace XCore::Reflect
{

::int32 FCustomVersionContainer::BinarySearch(FGuid Key, bool& OutFound) const noexcept
{
    // Standard lower-bound binary search. Returns the leftmost index `i`
    // such that Versions[i].Key >= Key; sets OutFound=true if
    // Versions[i].Key == Key, false otherwise.
    //
    // Loop invariant: 0 <= Lo <= Hi <= Versions.Num().
    ::int32 Lo = 0;
    ::int32 Hi = Versions.Num();
    while (Lo < Hi)
    {
        const ::int32 Mid = Lo + ((Hi - Lo) / 2);
        const FGuid& MidKey = Versions[Mid].Key;
        if (MidKey < Key)
        {
            Lo = Mid + 1;
        }
        else
        {
            Hi = Mid;
        }
    }
    OutFound = (Lo < Versions.Num()) && (Versions[Lo].Key == Key);
    return Lo;
}

void FCustomVersionContainer::Insert(const FCustomVersion& Version)
{
    bool bFound = false;
    const ::int32 Idx = BinarySearch(Version.Key, bFound);
    if (bFound)
    {
        // Overwrite the existing entry with the same Key. The Version
        // and FriendlyName fields are updated; the Key is unchanged
        // (it is the lookup key, after all).
        Versions[Idx].Version      = Version.Version;
        Versions[Idx]._Reserved    = Version._Reserved;
        Versions[Idx].FriendlyName = Version.FriendlyName;
        return;
    }

    // Insert at Idx. The cheapest way is to Add at the end, then
    // bubble down to Idx. TArray does not have a native Insert at
    // arbitrary index, so we implement it by:
    //   1. Add at end (the Version itself).
    //   2. Bubble the new entry leftward to position Idx.
    //
    // The bubble loop is straightforward swap-with-previous. Because
    // FCustomVersion is trivially copyable, the swap is byte-cheap.
    const ::int32 NewIdx = Versions.Add(Version);

    // Bubble down from NewIdx to Idx (NewIdx is always >= Idx because
    // BinarySearch returned the LOWER bound; the new entry at the end
    // is correctly ordered relative to entries at index <= NewIdx
    // only after the swap-back).
    for (::int32 I = NewIdx; I > Idx; --I)
    {
        // Swap Versions[I-1] and Versions[I]. Manual swap to avoid
        // pulling <utility> into a hot path.
        const FCustomVersion Tmp = Versions[I - 1];
        Versions[I - 1] = Versions[I];
        Versions[I] = Tmp;
    }
}

void FCustomVersionContainer::SetVersion(FGuid Key, ::int32 Version, FName FriendlyName)
{
    Insert(FCustomVersion(Key, Version, FriendlyName));
}

const FCustomVersion* FCustomVersionContainer::Find(FGuid Key) const noexcept
{
    bool bFound = false;
    const ::int32 Idx = BinarySearch(Key, bFound);
    if (bFound)
    {
        // GetData() returns the raw buffer pointer; indexing into it is
        // safe because Idx < Versions.Num() iff bFound is true.
        return &Versions.GetData()[Idx];
    }
    return nullptr;
}

::int32 FCustomVersionContainer::GetVersion(FGuid Key) const noexcept
{
    if (const FCustomVersion* Entry = Find(Key))
    {
        return Entry->Version;
    }
    // Sentinel "not present" -- see header for rationale.
    return ::std::numeric_limits<::int32>::min();
}

bool FCustomVersionContainer::Contains(FGuid Key) const noexcept
{
    bool bFound = false;
    (void)BinarySearch(Key, bFound);
    return bFound;
}

bool FCustomVersionContainer::Remove(FGuid Key) noexcept
{
    bool bFound = false;
    const ::int32 Idx = BinarySearch(Key, bFound);
    if (!bFound)
    {
        return false;
    }
    // Ordered remove (preserves the sorted invariant). O(N) shift but
    // unavoidable; the caller knew this when they reached for Remove.
    Versions.RemoveAt(Idx);
    return true;
}

::int32 FCustomVersionContainer::Size() const noexcept
{
    return Versions.Num();
}

bool FCustomVersionContainer::IsEmpty() const noexcept
{
    return Versions.Num() == 0;
}

void FCustomVersionContainer::Empty() noexcept
{
    // Reset(0) drops the buffer and zeros the count; equivalent to
    // destroying all entries (FCustomVersion is trivially destructible
    // so no per-element teardown is required).
    Versions.Reset(0);
}

::SIZE_T FCustomVersionContainer::GetSerializedByteCount() const noexcept
{
    // 4 bytes for the count header + 32 bytes per entry.
    return static_cast<::SIZE_T>(4) +
           static_cast<::SIZE_T>(Versions.Num()) * FCustomVersion::kSerializedSize;
}

void FCustomVersionContainer::WriteToBuffer(::uint8* OutBuffer) const noexcept
{
    const ::uint32 Count = static_cast<::uint32>(Versions.Num());
    WriteU32Le(OutBuffer, Count);
    ::SIZE_T WriteOffset = 4;
    for (::int32 I = 0; I < Versions.Num(); ++I)
    {
        Versions[I].WriteToBuffer(OutBuffer + WriteOffset);
        WriteOffset += FCustomVersion::kSerializedSize;
    }
}

bool FCustomVersionContainer::ReadFromBuffer(const ::uint8* InBuffer, ::SIZE_T ByteCount) noexcept
{
    if (ByteCount < 4)
    {
        // Buffer too short to hold the count header.
        return false;
    }
    const ::uint32 Count = ReadU32Le(InBuffer);

    // Multiplication overflow check: Count * 32 must fit in SIZE_T.
    // SIZE_T is 64-bit on every supported target so the practical limit
    // is well above any sane archive. Still, guard against malformed
    // input.
    constexpr ::SIZE_T kMaxReasonableEntries = (::SIZE_T(1) << 31); // 2 billion entries
    if (Count > kMaxReasonableEntries)
    {
        return false;
    }
    const ::SIZE_T Expected = static_cast<::SIZE_T>(4) +
                              static_cast<::SIZE_T>(Count) * FCustomVersion::kSerializedSize;
    if (ByteCount != Expected)
    {
        // Truncated or over-long buffer.
        return false;
    }

    // Stage the deserialised entries into a local container; on success
    // swap into Versions. On failure (none currently; included for
    // future-proof error paths), Versions is left untouched.
    ::XCore::TArray<FCustomVersion> Staging;
    Staging.Reserve(static_cast<::int32>(Count));
    ::SIZE_T ReadOffset = 4;
    for (::uint32 I = 0; I < Count; ++I)
    {
        Staging.Add(FCustomVersion::ReadFromBuffer(InBuffer + ReadOffset));
        ReadOffset += FCustomVersion::kSerializedSize;
    }

    // Canonicalise: the buffer producer is expected to have written in
    // sorted order, but we sort here to be robust to external producers
    // that don't honour the discipline (e.g., an older XPact revision
    // that wrote unsorted entries). The sort is stable insertion-sort
    // because the typical N is small (<100); for N > 256 the cost
    // approaches O(N^2) but the use case (per-archive container, N
    // dominated by registered-version count) keeps N comfortably small.
    //
    // RATIONALE for insertion-sort rather than std::sort: the typical
    // input is already sorted (round-trip case), so insertion-sort
    // hits its O(N) best case. std::sort would be O(N log N)
    // regardless; for the typical N this is a small win and saves us
    // pulling <algorithm> into a header-private TU.
    for (::int32 I = 1; I < Staging.Num(); ++I)
    {
        const FCustomVersion Tmp = Staging[I];
        ::int32 J = I;
        while (J > 0 && Tmp.Key < Staging[J - 1].Key)
        {
            Staging[J] = Staging[J - 1];
            --J;
        }
        Staging[J] = Tmp;
    }

    // Commit the staging container. Move-assign is amortised constant
    // time (just swap the buffer pointers) on the TArray surface.
    Versions = ::std::move(Staging);
    return true;
}

} // namespace XCore::Reflect
