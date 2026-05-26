// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FCustomVersionContainer.h -- per-archive sorted custom-version table.
// =====================================================================
//
// XCore-4b Rev 3, Section 8.2 (FCustomVersionContainer).
//
// FCustomVersionContainer holds the per-archive (per-package) custom-
// version table. When an archive is saved (XSerialization Layer 9), the
// container is written into the archive header; on load, the container
// is read first and consulted to resolve version-mismatched
// serialization.
//
// LAYOUT:
//
//   class FCustomVersionContainer {
//       TArray<FCustomVersion>  Versions;   // sorted by Key
//   };
//
// SORTING DISCIPLINE (Prime Directive: do it right the first time).
//
// The internal TArray is kept sorted by FGuid Key at all times. This
// gives:
//
//   * O(log N) Find via binary search (vs O(N) linear scan in UE's
//     plain TArray<FCustomVersion>).
//   * Deterministic byte-exact serialization independent of insertion
//     order (Acceptance gate D1: "round-trip 100 random custom
//     versions; output bytes are byte-exact equal to input bytes").
//   * Stable iteration order (TArray iteration walks the sorted
//     sequence; consumers that want a deterministic dump get one for
//     free).
//
// The sorting cost is O(N) per Insert because we keep the array sorted
// rather than re-sorting on demand. For the expected size of a custom-
// version container (typically <100 entries per archive; the engine has
// ~60 well-known custom-versions today across Core / Engine /
// Networking / etc., per UE's CustomVersion-tracking inventory), the
// insertion cost is dominated by the binary-search probe.
//
// THREAD SAFETY: NOT thread-safe by itself. The container is intended
// for single-threaded use during archive read/write. Concurrent access
// is the registry's domain (FCustomVersionRegistry.h is thread-safe via
// FRWLock).
//
// HOT-RELOAD SAFETY: no virtual methods; trivially destructible
// (Versions is a TArray<FCustomVersion> which is trivially
// relocatable).
//
// =====================================================================

#include "Containers/TArray.h"
#include "Macros/XCoreTypes.h"
#include "Reflection/FCustomVersion.h"
#include "Reflection/FGuid.h"

// FArchive belongs to XSerialization (Layer 9). The Section 8.2
// spec body lists `void Serialize(FArchive& Ar);` as a member of
// FCustomVersionContainer; the body lives in XSerialization (which
// owns the FArchive class). XCore-4b ships ONLY the byte-buffer
// helpers (WriteToBuffer / ReadFromBuffer) for now; the FArchive
// overload will be added in a sibling header at XSerialization
// integration time (Phase 4b.7+) and called as
// `FCustomVersionContainer::SerializeViaArchive(Ar, Container)` (the
// non-member form keeps this XCore-4b header free of an FArchive
// forward dependency it cannot satisfy at link time).
//
// TODO(Phase 4b.7 / XSerialization): introduce the FArchive overload
// in an XSerialization-owned header that includes both
// FCustomVersionContainer.h and FArchive.h.

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // FCustomVersionContainer -- ordered table of FCustomVersion entries.
    //
    // Sorted-by-Key invariant: at every public method exit, the internal
    // Versions array is sorted in ascending order of FGuid::operator<.
    // Insert maintains the invariant; Remove preserves it; Find relies
    // on it for O(log N) binary search.
    // -----------------------------------------------------------------
    class FCustomVersionContainer
    {
    public:
        // -------------------------------------------------------------
        // Construction.
        //
        // The default constructor produces an empty container. Copy +
        // move are defaulted because the TArray<FCustomVersion> backing
        // is trivially relocatable and the contained type is a POD.
        // -------------------------------------------------------------
        FCustomVersionContainer() = default;
        FCustomVersionContainer(const FCustomVersionContainer&) = default;
        FCustomVersionContainer(FCustomVersionContainer&&) noexcept = default;
        FCustomVersionContainer& operator=(const FCustomVersionContainer&) = default;
        FCustomVersionContainer& operator=(FCustomVersionContainer&&) noexcept = default;
        ~FCustomVersionContainer() = default;

        // -------------------------------------------------------------
        // Insert -- add `Version` to the container, or update the
        // existing entry with the same Key if one is present.
        //
        // O(log N) probe + O(N) shift in the worst case (insertion
        // at the front). For the typical container size of <100
        // entries this is well below 1 microsecond.
        //
        // If an entry with the same Key already exists, its Version
        // and FriendlyName are overwritten with `Version`'s. This
        // matches UE's FCustomVersionContainer::SetVersion semantics.
        //
        // The all-zero GUID is accepted (FCustomVersionContainer is
        // shape-neutral; the registry-level reject lives in
        // FCustomVersionRegistry::RegisterCustomVersion). A
        // synthesised round-trip test could legitimately want a
        // zero-GUID entry.
        // -------------------------------------------------------------
        void Insert(const FCustomVersion& Version);

        // SetVersion -- convenience wrapper matching UE's API shape.
        // Equivalent to Insert(FCustomVersion(Key, Version,
        // FriendlyName)).
        void SetVersion(FGuid Key, ::int32 Version, FName FriendlyName);

        // -------------------------------------------------------------
        // Find -- return a pointer to the entry with `Key`, or nullptr
        // if not present.
        //
        // O(log N) via binary search. The returned pointer is valid
        // until the next mutation of the container (Insert / Remove /
        // assign / destroy).
        // -------------------------------------------------------------
        [[nodiscard]] const FCustomVersion* Find(FGuid Key) const noexcept;

        // -------------------------------------------------------------
        // GetVersion -- return the version int for `Key`, or
        // INT32_MIN if not present.
        //
        // The sentinel `INT32_MIN` is chosen because real custom
        // versions are monotonically increasing from 0 and unlikely
        // to ever reach INT32_MIN; a future revision that needs to
        // disambiguate "present at version INT32_MIN" from "absent"
        // should call Find / Contains instead.
        // -------------------------------------------------------------
        [[nodiscard]] ::int32 GetVersion(FGuid Key) const noexcept;

        // -------------------------------------------------------------
        // Contains -- true iff an entry with `Key` is in the container.
        // -------------------------------------------------------------
        [[nodiscard]] bool Contains(FGuid Key) const noexcept;

        // -------------------------------------------------------------
        // HasVersion -- spec-named alias for Contains (matches §8.2
        // public surface).
        // -------------------------------------------------------------
        [[nodiscard]] bool HasVersion(FGuid Key) const noexcept
        {
            return Contains(Key);
        }

        // -------------------------------------------------------------
        // Remove -- erase the entry with `Key`. Returns true if an
        // entry was removed, false if no entry with `Key` was present.
        //
        // O(log N) probe + O(N) shift in the worst case.
        // -------------------------------------------------------------
        bool Remove(FGuid Key) noexcept;

        // -------------------------------------------------------------
        // Size / IsEmpty.
        // -------------------------------------------------------------
        [[nodiscard]] ::int32 Size() const noexcept;
        [[nodiscard]] bool IsEmpty() const noexcept;

        // -------------------------------------------------------------
        // Empty -- clear all entries (matches UE's API shape).
        // -------------------------------------------------------------
        void Empty() noexcept;

        // -------------------------------------------------------------
        // GetAllVersions -- raw access to the underlying TArray.
        //
        // Returns a const reference to the internal sorted array; the
        // entries are in FGuid-ascending order, which is also the
        // iteration order for range-based for-loops over the container.
        //
        // The reference is valid until the next mutation.
        // -------------------------------------------------------------
        [[nodiscard]] const ::XCore::TArray<FCustomVersion>& GetAllVersions() const noexcept
        {
            return Versions;
        }

        // -------------------------------------------------------------
        // Range-based for-loop support.
        //
        // The iterators walk the sorted sequence; const-only because
        // mutation must go through the public API to preserve the
        // sorted invariant.
        // -------------------------------------------------------------
        [[nodiscard]] const FCustomVersion* begin() const noexcept
        {
            return Versions.GetData();
        }

        [[nodiscard]] const FCustomVersion* end() const noexcept
        {
            return Versions.GetData() + Versions.Num();
        }

        // -------------------------------------------------------------
        // Byte-level serialization.
        //
        // The on-the-wire layout (matching the FCustomVersion
        // serialization at §8.1):
        //
        //   bytes [0..3]    Count (uint32 little-endian) -- number of entries
        //   bytes [4..]     each entry serialised as 32 bytes via
        //                   FCustomVersion::WriteToBuffer
        //
        // Total = 4 + 32 * Count bytes. The byte order is little-endian
        // throughout (matching XCore-4a's loctable header §11.4).
        //
        // GetSerializedByteCount returns the number of bytes
        // WriteToBuffer will write for this container's current state.
        // -------------------------------------------------------------
        [[nodiscard]] ::SIZE_T GetSerializedByteCount() const noexcept;
        void WriteToBuffer(::uint8* OutBuffer) const noexcept;

        // ReadFromBuffer replaces the container's contents with the
        // entries deserialised from `InBuffer`. `ByteCount` must equal
        // GetSerializedByteCount of the same container that originally
        // wrote the buffer.
        //
        // Returns true on success; returns false on a truncated or
        // malformed buffer (in which case the container is left in
        // the same state as on entry).
        //
        // The post-condition on success is the sorted-by-Key invariant
        // (ReadFromBuffer sorts after deserialising, so a buffer
        // written by an external producer in arbitrary order is
        // canonicalised).
        bool ReadFromBuffer(const ::uint8* InBuffer, ::SIZE_T ByteCount) noexcept;

    private:
        // -------------------------------------------------------------
        // Internal probe: returns the index where `Key` belongs.
        //
        // If the entry exists, returns its index (and OutFound = true).
        // If not, returns the insertion index where a new entry with
        // `Key` should be placed to preserve the sorted invariant (and
        // OutFound = false).
        // -------------------------------------------------------------
        [[nodiscard]] ::int32 BinarySearch(FGuid Key, bool& OutFound) const noexcept;

        ::XCore::TArray<FCustomVersion>  Versions;
    };

} // namespace XCore::Reflect
