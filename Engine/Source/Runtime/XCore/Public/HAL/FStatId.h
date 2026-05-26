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
//   * A compile-time FNV-1a-64 hash (Phase 1g errata; constexpr XXH3
//     deferred to Phase 2 when XXH3 gains constexpr support per
//     C++ ISO P-XXXX) of the stat's textual name + group. See
//     FStatTLS.h for the constexpr StatHashNameGroup implementation
//     and the Phase 1g judgement-call wording explaining the
//     temporary divergence from the spec's XXH3 placeholder. Both
//     the compile-time XSTAT_DECL and the runtime FStatTLS path
//     use the SAME FNV-1a-64 function so the produced FStatId.Hash
//     bytes match.
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
// via XCore::Stat::StatHashNameGroup (constexpr FNV-1a-64 per Phase
// 1g errata) over the concatenated name + group bytes (the
// concatenation is the build-time deduplication discriminator).
// Phase 2 may swap FNV-1a-64 for constexpr XXH3 simultaneously at
// both the compile-time XSTAT_DECL site and the runtime FStatTLS
// path; both paths must continue to produce the same Hash for the
// same (Name, Group) tuple.
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
        ::uint64    Hash;     // StatHashNameGroup(name, group) -- compile-time
                              // FNV-1a-64 per Phase 1g errata (constexpr XXH3
                              // deferred to Phase 2). The same function backs
                              // the runtime FStatTLS path.
        const char* Name;     // rodata pointer to the name literal
        const char* Group;    // rodata pointer to the group literal
    };

    static_assert(sizeof(FStatId)  == 24, "FStatId ABI lock: 24 bytes");
    static_assert(alignof(FStatId) ==  8, "FStatId ABI lock: 8-byte alignment");

} // namespace XCore::Stat
