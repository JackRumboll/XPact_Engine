// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FStatId.h -- compile-time 64-bit stat identifier (Section 10.1).
// =====================================================================
//
// XCore-4a Rev 3, Section 10.1 + fix M-3 (64-bit hash).
//
// Per Section 10.1 spec wording:
//
//   "Fix M-3: 64-bit hash. With <= 10,000 distinct stats, the birthday-
//    collision probability is ~2.7e-12 -- effectively zero. Storage
//    overhead vs. uint32 is negligible because the side-table dominates.
//    struct FStatId { uint64_t Hash; const char* Name; const char* Group; };"
//
// And the dispatch wording:
//
//   "FStatId.h -- per Rev 2 fix M-3:
//      struct FStatId {
//          uint64_t Hash;
//          const char* Name;
//          const char* Group;
//      };
//      static_assert(sizeof(FStatId) == 24, "FStatId ABI lock");"
//
// FStatId is a value type carrying:
//   * A compile-time XXH3 hash of the stat's textual name + group.
//   * A rodata pointer to the name string literal.
//   * A rodata pointer to the group string literal.
//
// The hash is the lookup key into the per-thread FStatShard and the
// global perfect-hash side table. The Name + Group pointers are the
// diagnostic surface (profiler dump, perfect-hash side-table value).
//
// CONSTRUCTION: the XSTAT_DECL macro builds an FStatId at namespace
// scope via inline constexpr. The Name and Group fields are
// string-literal pointers; the Hash field is computed at compile time
// via FXxh3::Hash64 over the concatenated name + group bytes (the
// concatenation is the build-time deduplication discriminator).
//
// THREADING: FStatId values are read-only after construction (literal
// rodata). Concurrent reads are safe; there are no writes after
// static-init.
//
// ABI: 24 bytes locked at static_assert. The layout is:
//   bytes 0..7    Hash
//   bytes 8..15   Name
//   bytes 16..23  Group
//
// =====================================================================

#include "Macros/XCoreTypes.h"

namespace XCore::Stat
{

    // -----------------------------------------------------------------
    // FStatId -- the compile-time stat identifier.
    //
    // Trivially-copyable, trivially-destructible. Inline constexpr at
    // every XSTAT_DECL site; the storage is rodata.
    // -----------------------------------------------------------------
    struct FStatId
    {
        ::uint64    Hash;     // XXH3(name + group, len, seed=0); compile-time
        const char* Name;     // rodata pointer to the name literal
        const char* Group;    // rodata pointer to the group literal
    };

    static_assert(sizeof(FStatId)  == 24, "FStatId ABI lock: 24 bytes");
    static_assert(alignof(FStatId) ==  8, "FStatId ABI lock: 8-byte alignment");

} // namespace XCore::Stat
