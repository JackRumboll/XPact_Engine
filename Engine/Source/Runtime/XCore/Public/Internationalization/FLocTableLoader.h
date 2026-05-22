// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FLocTableLoader.h -- loctable file I/O (Section 11.2 + 18 OPEN-5).
// =====================================================================
//
// XCore-4a Rev 3 Section 11.2 + Section 18 OPEN-5 (per-locale binary
// file format) + Section 11.9 (BLAKE3 verification).
//
// FLocTableLoader is the file I/O surface that reads a binary loctable
// from disk, verifies the BLAKE3 payload digest, and parses the
// entries into a TArray of (namespace, key, value) triples.
//
// File location convention (Section 11.2):
//   {Engine}/Content/Localization/<Locale>.loctable
//
// Where {Engine} is derived from the running executable path via
// `FPlatformProcess::GetExecutablePath()`. The path-walk is:
//
//   ExecutablePath: Engine/Binaries/<Platform>/<ExeName>.exe
//                   (e.g., Engine/Binaries/Win64/XPactEditor.exe)
//   Walk up:        ../../..
//   Append:         Content/Localization/<Locale>.loctable
//
// The loader uses FString-level path manipulation; no platform-
// specific path-walk APIs are needed because Engine/Binaries/<Plat>/
// is a fixed three-level walk under every supported target.
//
// ERROR HANDLING (Result<TArray, FParseError>):
//   * FParseError::Empty            -- file does not exist or is
//                                      zero bytes.
//   * FParseError::Malformed        -- magic, version, or entry-
//                                      length mismatch.
//   * FParseError::InvalidUtf8      -- BLAKE3 digest does not match
//                                      the payload (the "tampered
//                                      loctable" case in Section 17.8
//                                      acceptance). The error enum
//                                      lacks a dedicated
//                                      "TamperedDigest" code at Phase
//                                      1f, so we use the existing
//                                      Malformed code with a dev-
//                                      warning that names the
//                                      mismatch.
//
// THREADING: not thread-safe at the loader surface; concurrent calls
// to LoadFromFile from multiple threads with the same path are UB
// (the filesystem read is undefined under concurrent open). Callers
// should serialise (FLocalizationManager does this via its FRWLock).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XErrorTypes.h"
#include "Macros/XResult.h"
#include "Containers/FString.h"
#include "Containers/TArray.h"

namespace XCore::Loc
{

// ---------------------------------------------------------------------
// FLocTableLoader -- static surface for binary loctable I/O.
// ---------------------------------------------------------------------
class FLocTableLoader
{
public:
    // -----------------------------------------------------------------
    // FLocEntry -- one parsed (namespace, key, value) triple.
    //
    // Phase 1f shape: all three fields are FString. This is the
    // simplest correct lifetime story (each entry owns its own heap
    // bytes); the cost is three FString allocations per entry. For a
    // typical loctable (~1000 entries, ~80 bytes per FString
    // including SSO) the loctable-load is well under 1 ms per locale.
    //
    // TODO(Phase 2): coalesce namespace and key bytes into a single
    // per-loctable buffer with const char* offsets to improve cache
    // locality during Lookup. The FString-per-field shape here is
    // the correctness-first form.
    // -----------------------------------------------------------------
    struct FLocEntry
    {
        ::XCore::FString Namespace;
        ::XCore::FString Key;
        ::XCore::FString Value;
    };

    // -----------------------------------------------------------------
    // LoadFromFile -- read + verify + parse a loctable from disk.
    //
    // Path: a full filesystem path (the caller composes the path; the
    // FLocalizationManager wires the {Engine}/Content/Localization/
    // <Locale>.loctable convention).
    //
    // Returns Result<TArray<FLocEntry>, FParseError>:
    //   * Ok(entries): the parsed entries.
    //   * Err(FParseError::Empty): file does not exist or is empty.
    //   * Err(FParseError::Malformed): header / format mismatch OR
    //                                  BLAKE3 digest mismatch.
    //
    // The entries are returned in file-order (not sorted; the caller
    // is responsible for whatever indexing it needs).
    //
    // Sim-path: NOT sim-path-safe (filesystem read; the sim-path
    // overlay header [[deprecated]]s this method).
    // -----------------------------------------------------------------
    [[nodiscard]] static ::XCore::Result<
        ::XCore::TArray<FLocEntry, ::XCore::DefaultAllocator>,
        ::XCore::FParseError>
    LoadFromFile(const ::XCore::FString& Path);

    // -----------------------------------------------------------------
    // SaveToFile -- serialise entries to disk.
    //
    // The inverse of LoadFromFile: takes a TArray<FLocEntry>, computes
    // the BLAKE3 digest of the payload, writes the file. Returns
    // true on success, false on filesystem error.
    //
    // Used by the test fixture-generation pipeline (the sample
    // en-US.loctable and ja-JP.loctable test fixtures are produced by
    // calling SaveToFile from a one-off test harness; the binaries
    // are committed to the repo).
    //
    // Sim-path: NOT sim-path-safe (filesystem write).
    // -----------------------------------------------------------------
    [[nodiscard]] static bool SaveToFile(
        const ::XCore::FString& Path,
        const ::XCore::TArray<FLocEntry, ::XCore::DefaultAllocator>& Entries);
};

} // namespace XCore::Loc
