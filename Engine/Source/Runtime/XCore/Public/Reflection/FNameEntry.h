// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FNameEntry.h -- the immutable interned-name record (XCore-4b §4.2).
// =====================================================================
//
// XCore-4b Rev 3, Section 4.2 ("Intern-table design (two-pool, sharded)").
//
// FNameEntry is the immutable per-interned-name record stored inside the
// FNamePool. Once written at intern time, the bytes never change for the
// lifetime of the process. The record carries a small fixed header
// followed by a length-counted UTF-8 byte payload.
//
// HEADER LAYOUT (per Rev 2 FIX-21 / Rev 3 FIX-R2-LOW-2; explicitly 6 bytes):
//
//     bytes 0-3  EntryId  (uint32)  shard-local monotonic id (high byte zero;
//                                    the FName.Index packs `(shard_id << 24) | entry_id`)
//     bytes 4-5  Length   (uint16)  UTF-8 byte length (excludes any trailing NUL)
//     bytes 6-N  Bytes[]            UTF-8 payload + one trailing NUL terminator
//                                    (the NUL is for `const char*` consumers and
//                                     is NOT counted in Length).
//
// The header is exactly 6 bytes (Rev 3 FIX-R2-LOW-2 confirms this; the Rev 1
// "8-byte header with Reserved" form was dropped at Rev 2 FIX-21). The trailing
// Bytes[] array is allocated inline as part of the same allocation as the header;
// the entry's total allocation size is `6 + Length + 1` (header + payload + NUL),
// rounded up to the FNameEntry alignment of 8 bytes.
//
// Why an EntryId in the header (and not just an in-pool offset)?
// -------------------------------------------------------------
//   * EntryId is monotonic per-shard so the FName.Index encoding is stable.
//   * The shard-local id is the value that survives a future entry-pool
//     compaction (none planned for MVP, but the layout reserves the option).
//   * The full FName.Index encoding is `(shard_id << 24) | entry_id`; the
//     entry's EntryId field carries the low 24 bits for self-consistency
//     checks (the high 8 bits are the shard the entry lives in, derivable
//     from the entry's address but the EntryId field is a cheap audit hook
//     for sanity-checking entries returned by shard probes).
//
// Why 8-byte alignment?
// ---------------------
//   * The hash table's slot probe walks entries by address; 8-byte alignment
//     means a single 64-bit load fetches the header without misalignment.
//   * The 64 KB block pool (FNamePool) maintains an 8-byte-aligned cursor.
//   * UTF-8 bytes have no alignment requirement so the payload tail is free
//     to be any length (the NUL terminator may sit on an odd byte; the next
//     entry's header reads start at the next 8-byte boundary).
//
// Max name length (per spec §4.2 + UE NameTypes.h:57 NAME_SIZE).
// --------------------------------------------------------------
//   * 1024 bytes. Longer inputs are rejected by FName construction with a
//     diagnostic. Length fits comfortably in the uint16 header field
//     (max representable 65535; we cap at 1024 to keep intern entries small
//     and to leave headroom for the future addition of metadata flags in
//     the high bits of Length if needed).
//
// Hot-reload safety.
// ------------------
//   * No virtual methods (per spec §4.8 + XCore-4b spec-wide hot-reload
//     discipline).
//   * Standard-layout struct so offsetof is well-defined.
//   * Bytes are written exactly once at intern time; subsequent observers
//     read without locks (per shard's FRWLock guards table mutations only,
//     not entry-byte reads of already-published entries).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include <cstddef>

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // Maximum supported FName length in UTF-8 bytes.
    //
    // Per XCore-4b §4.2, matching UE's NAME_SIZE (NameTypes.h:57). The
    // limit is the byte count of the *base* name; numbered-name suffixes
    // (`_N`) live entirely in the 32-bit FName.SerialNumber and do NOT
    // consume intern-table bytes.
    // -----------------------------------------------------------------
    inline constexpr ::SIZE_T kFNameMaxLength = 1024;

    // -----------------------------------------------------------------
    // FNameEntryHeader -- the 6-byte fixed portion of an FNameEntry.
    //
    // Declared as a stand-alone struct so the ABI lock is straightforward
    // (sizeof must be exactly 6). The combined FNameEntry layout embeds
    // this struct followed by the variable-length Bytes[] array.
    // -----------------------------------------------------------------
#pragma pack(push, 1)
    struct FNameEntryHeader
    {
        ::uint32 EntryId;   //  0  +4   shard-local monotonic id (low 24 bits of the FName.Index)
        ::uint16 Length;    //  4  +2   UTF-8 byte length (excludes the trailing NUL terminator)
    };
#pragma pack(pop)

    static_assert(sizeof(FNameEntryHeader)  == 6,
                  "FNameEntryHeader ABI lock: must be exactly 6 bytes (Rev 2 FIX-21 dropped the Reserved slot)");

    // The header is intentionally NOT alignas(8); the alignment requirement
    // lives on FNameEntry (which contains the header + the trailing payload).

    // -----------------------------------------------------------------
    // FNameEntry -- the full intern-table record.
    //
    // 8-byte aligned so the per-shard block walker can issue aligned 64-bit
    // loads when scanning. The Bytes[] flexible-array member is the inline
    // UTF-8 payload; an entry's total allocation size is
    // `offsetof(FNameEntry, Bytes) + Length + 1` (the +1 is the trailing NUL),
    // rounded up to alignof(FNameEntry) when placing the next entry in the
    // shard's block.
    //
    // The trailing NUL terminator (Bytes[Length] == '\0') is included so a
    // `const char*` consumer can treat the payload as a C string. The NUL is
    // NOT counted in Length; iterating `for (int i = 0; i < Length; ++i)`
    // yields exactly the interned bytes.
    // -----------------------------------------------------------------
    struct alignas(8) FNameEntry
    {
        FNameEntryHeader Header;       //  0  +6   shard-local id + byte length
        char             Bytes[1];     //  6  +N   UTF-8 payload (length-counted; NUL-terminated)

        // The Bytes[1] declaration is the canonical "flexible array member"
        // workaround for C++ (true C99 FAMs are not C++ standard). The
        // actual allocation reserves enough room for `Length + 1` payload
        // bytes; accessing Bytes[i] for `0 <= i <= Length` is well-defined
        // by virtue of the allocation's actual size, even though the
        // declared type says Bytes[1]. This matches the canonical pattern
        // used throughout UE Core (e.g., `FNameStringView::String[]`).

        // -------------------------------------------------------------
        // Accessors. Marked noexcept; trivial inlining cost.
        // -------------------------------------------------------------

        [[nodiscard]] XPACT_FORCEINLINE ::uint32 GetEntryId() const noexcept
        {
            return Header.EntryId;
        }

        [[nodiscard]] XPACT_FORCEINLINE ::uint16 GetLength() const noexcept
        {
            return Header.Length;
        }

        [[nodiscard]] XPACT_FORCEINLINE const char* GetBytes() const noexcept
        {
            return &Bytes[0];
        }
    };

    // -----------------------------------------------------------------
    // ABI locks. Per Rev 3 §11.1 the FNameEntry HEADER is locked at 6 bytes;
    // the full FNameEntry struct's sizeof is NOT meaningful (it includes the
    // trailing-array-member declared size of 1, padded to alignof(8) = 8).
    // The load-bearing invariants are:
    //
    //   * sizeof(FNameEntryHeader)        == 6  (header layout)
    //   * offsetof(FNameEntry, Bytes)     == 6  (payload start)
    //   * alignof(FNameEntry)             == 8  (block-walker alignment)
    //
    // The first ABI lock fires at FNameEntryHeader's declaration above; the
    // remaining two fire here.
    // -----------------------------------------------------------------
    static_assert(offsetof(FNameEntry, Bytes) == 6,
                  "FNameEntry ABI lock: Bytes payload must start at offset 6 (Rev 2 FIX-21)");
    static_assert(alignof(FNameEntry) == 8,
                  "FNameEntry ABI lock: 8-byte alignment for fast block-walking");

    // -----------------------------------------------------------------
    // Compute the total byte size of an FNameEntry record holding `Length`
    // UTF-8 bytes of payload. Includes the header, the payload bytes, the
    // trailing NUL terminator, and the round-up to the next 8-byte boundary.
    //
    // The round-up is required because the block-pool allocator places the
    // next entry directly after this one and the next entry's header must
    // be 8-byte aligned.
    //
    // constexpr so callers can compute sizes at compile time when needed
    // (e.g., the unit tests' 1024-byte boundary fixture).
    // -----------------------------------------------------------------
    [[nodiscard]] inline constexpr ::SIZE_T ComputeFNameEntryAllocSize(::SIZE_T Length) noexcept
    {
        const ::SIZE_T RawSize = sizeof(FNameEntryHeader) + Length + 1;  // header + payload + NUL
        // Round up to the 8-byte alignment of FNameEntry.
        return (RawSize + 7) & ~::SIZE_T(7);
    }

} // namespace XCore::Reflect
