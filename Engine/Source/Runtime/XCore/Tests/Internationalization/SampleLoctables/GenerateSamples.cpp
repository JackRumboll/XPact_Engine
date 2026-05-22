// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// SampleLoctables/GenerateSamples.cpp -- generate the test fixtures.
// =====================================================================
//
// One-shot tool that writes en-US.loctable and ja-JP.loctable into
// the same directory as this source file. Run once at build time to
// produce the sample binary loctable fixtures used by the locale-
// switch / blake-verification / round-trip tests.
//
// Phase 1f: this tool is invoked by the test-runner the first time the
// fixtures are referenced (via a `__GenerateSampleLoctables()` symbol
// that the test TUs link against). The fixtures are also generated
// once at commit time so they live in the repo, but the live-generation
// path exists so a developer wiping the .loctable files can regenerate
// them by running any of the consuming tests.
//
// Each fixture has ~10 entries spanning common namespace+key patterns.
// The values demonstrate locale-specific content:
//   * en-US.loctable: English source strings.
//   * ja-JP.loctable: Japanese (Hiragana + Kanji) translations.
//
// =====================================================================
// TEST ORCHESTRATION CONTRACT (Phase 1g fix MIN-4):
//
// The test runner MUST invoke
//     XPact_GenerateSampleLoctables(GetTempDirectory() + "/SampleLoctables/")
// in its per-process startup hook BEFORE running any FText.Tests test.
//
// The two tests that depend on these fixtures are:
//   * Engine/Source/Runtime/XCore/Tests/Internationalization/
//       FText.Tests/LocaleSwitch.cpp
//   * Engine/Source/Runtime/XCore/Tests/Internationalization/
//       FText.Tests/RoundTripBinary.cpp
//
// Both tests call XPact_GenerateSampleLoctables() directly in their
// test bodies as a defense-in-depth measure (so they pass when invoked
// in isolation), but the test runner should be aware that these
// fixtures are NOT committed binaries -- they are generated artefacts
// that live under the test process's temp directory for the duration
// of the run, then deleted by the runner's teardown.
//
// Rationale for not committing the .loctable binaries:
//   * Binary fixtures drift quietly when the FLocTable serializer
//     evolves; regenerating from a source script keeps the binary
//     in lock-step with the schema.
//   * The repo stays text-only-changes-friendly (no large-file diffs
//     on serializer revisions).
//   * The runner's startup-hook contract surfaces missing-fixture
//     scenarios immediately rather than letting them masquerade as
//     test failures.
//
// XCore.Tests.Build.toml lists the test-runner dependency on this
// TU; see the corresponding documentation block there.
// =====================================================================

#include "Internationalization/FLocTableLoader.h"
#include "Internationalization/FLocTable.h"
#include "HAL/FMemory.h"
#include "HAL/XInitPhase.h"

#include <cstdio>

namespace
{
    using FLocEntry = ::XCore::Loc::FLocTableLoader::FLocEntry;

    static FLocEntry MakeEntry(const char* Ns, const char* Key, const char* Value)
    {
        FLocEntry E;
        E.Namespace = ::XCore::FString(Ns);
        E.Key       = ::XCore::FString(Key);
        E.Value     = ::XCore::FString(Value);
        return E;
    }
} // namespace

// Public entry point. Returns 0 on success, non-zero on failure.
extern "C" int XPact_GenerateSampleLoctables(const char* OutputDir)
{
    using namespace ::XCore;

    // ------- en-US.loctable -------
    {
        TArray<FLocEntry, DefaultAllocator> Entries(
            DefaultAllocator(::XCore::HAL::FMemTag::Localization));

        Entries.Emplace(MakeEntry("FTextTests", "Hello",     "Hello World"));
        Entries.Emplace(MakeEntry("FTextTests", "Bye",       "Goodbye"));
        Entries.Emplace(MakeEntry("FTextTests", "Greeting",  "Greetings, traveler."));
        Entries.Emplace(MakeEntry("FTextTests", "PlayerWin", "You win!"));
        Entries.Emplace(MakeEntry("FTextTests", "GameOver",  "Game over."));
        Entries.Emplace(MakeEntry("FTextTests", "Pause",     "Paused"));
        Entries.Emplace(MakeEntry("FTextTests", "Resume",    "Resume"));
        Entries.Emplace(MakeEntry("MainMenu",   "Start",     "Start Game"));
        Entries.Emplace(MakeEntry("MainMenu",   "Options",   "Options"));
        Entries.Emplace(MakeEntry("MainMenu",   "Quit",      "Quit"));

        ::XCore::FString Path(OutputDir);
        Path.Append("/en-US.loctable");

        const bool Ok = ::XCore::Loc::FLocTableLoader::SaveToFile(Path, Entries);
        if (!Ok)
        {
            std::fprintf(stderr,
                "[GenerateSamples] FAIL: cannot write %s\n", Path.ToUtf8Cstr());
            return 1;
        }
    }

    // ------- ja-JP.loctable -------
    {
        TArray<FLocEntry, DefaultAllocator> Entries(
            DefaultAllocator(::XCore::HAL::FMemTag::Localization));

        // UTF-8 byte sequences for Hiragana / Kanji.
        Entries.Emplace(MakeEntry("FTextTests", "Hello",     "\xe3\x81\x93\xe3\x82\x93\xe3\x81\xab\xe3\x81\xa1\xe3\x81\xaf"));            // konnichi-wa
        Entries.Emplace(MakeEntry("FTextTests", "Bye",       "\xe3\x81\x95\xe3\x82\x88\xe3\x81\x86\xe3\x81\xaa\xe3\x82\x89"));            // sayonara
        Entries.Emplace(MakeEntry("FTextTests", "Greeting",  "\xe3\x82\x88\xe3\x81\x86\xe3\x81\x93\xe3\x81\x9d"));                         // youkoso
        Entries.Emplace(MakeEntry("FTextTests", "PlayerWin", "\xe3\x81\x82\xe3\x81\xaa\xe3\x81\x9f\xe3\x81\xae\xe5\x8b\x9d\xe3\x81\xa1"));// anata no kachi
        Entries.Emplace(MakeEntry("FTextTests", "GameOver",  "\xe3\x82\xb2\xe3\x83\xbc\xe3\x83\xa0\xe3\x82\xaa\xe3\x83\xbc\xe3\x83\x90\xe3\x83\xbc")); // GE-MUO-BA-
        Entries.Emplace(MakeEntry("FTextTests", "Pause",     "\xe4\xb8\x80\xe6\x99\x82\xe5\x81\x9c\xe6\xad\xa2"));                         // ichiji-teishi
        Entries.Emplace(MakeEntry("FTextTests", "Resume",    "\xe5\x86\x8d\xe9\x96\x8b"));                                                 // saikai
        Entries.Emplace(MakeEntry("MainMenu",   "Start",     "\xe3\x82\xb9\xe3\x82\xbf\xe3\x83\xbc\xe3\x83\x88"));                         // SUTA-TO
        Entries.Emplace(MakeEntry("MainMenu",   "Options",   "\xe8\xa8\xad\xe5\xae\x9a"));                                                 // settei
        Entries.Emplace(MakeEntry("MainMenu",   "Quit",      "\xe7\xb5\x82\xe4\xba\x86"));                                                 // shuuryou

        ::XCore::FString Path(OutputDir);
        Path.Append("/ja-JP.loctable");

        const bool Ok = ::XCore::Loc::FLocTableLoader::SaveToFile(Path, Entries);
        if (!Ok)
        {
            std::fprintf(stderr,
                "[GenerateSamples] FAIL: cannot write %s\n", Path.ToUtf8Cstr());
            return 1;
        }
    }

    return 0;
}
