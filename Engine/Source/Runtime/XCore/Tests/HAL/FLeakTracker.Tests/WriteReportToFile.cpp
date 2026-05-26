// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// WriteReportToFile.cpp -- Rev 2 FIX-3 / C7 WriteReport file-output test.
// =====================================================================
//
// Per Rev 2 FIX-3 / Round 2: `FLeakTracker::WriteReport(FString Path)`
// must actually write the report to the named file. The prior Phase 1b
// implementation ignored Path and emitted a one-line summary to stderr;
// this test verifies the fixed implementation:
//
//   1. Deliberately leaks 50 x 128-byte allocations.
//   2. Calls WriteReport with a temp-file path.
//   3. Verifies the file exists, is non-empty, and contains the
//      structured markers required by the report format
//      (FLeakTracker WriteReport header + "Total leaked bytes" + at
//       least one "Bucket #" section with a frames listing).
//
// Gated on XPACT_LEAK_TRACKING_ENABLED. In Test + Shipping (where
// tracking is off), this test compiles to a no-op main().
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XCoreDefines.h"

#if XPACT_LEAK_TRACKING_ENABLED

#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"
#include "HAL/FLeakTracker.h"
#include "Containers/FString.h"

#include <cstdio>
#include <cstdlib>
#include <cstring>

#if defined(_WIN32)
    // GetTempPathA / GetCurrentProcessId via direct prototype; avoids
    // pulling Windows.h into a unit-test TU.
    extern "C" __declspec(dllimport) unsigned long __stdcall GetTempPathA(
        unsigned long nBufferLength, char* lpBuffer);
    extern "C" __declspec(dllimport) unsigned long __stdcall GetCurrentProcessId();
#else
    #include <unistd.h>  // getpid
#endif

namespace
{
    // Compose a process-unique temp path for the report. We use the
    // platform temp directory + the current PID to ensure no collision
    // with a parallel test invocation.
    bool ComposeTempPath(char* OutBuf, ::SIZE_T BufLen)
    {
#if defined(_WIN32)
        char TempDir[260];
        const unsigned long TempLen = ::GetTempPathA(
            static_cast<unsigned long>(sizeof(TempDir)), TempDir);
        if (TempLen == 0 || TempLen >= sizeof(TempDir))
        {
            return false;
        }
        const int W = std::snprintf(OutBuf, BufLen,
                                    "%sxpact_leak_report_%u.txt",
                                    TempDir,
                                    static_cast<unsigned>(::GetCurrentProcessId()));
        return W > 0 && static_cast<::SIZE_T>(W) < BufLen;
#else
        const int W = std::snprintf(OutBuf, BufLen,
                                    "/tmp/xpact_leak_report_%u.txt",
                                    static_cast<unsigned>(::getpid()));
        return W > 0 && static_cast<::SIZE_T>(W) < BufLen;
#endif
    }

    // Slurp the file into a heap buffer. Caller owns the returned ptr.
    // Returns nullptr on failure; *OutSize gets the byte count.
    char* SlurpFile(const char* Path, ::SIZE_T* OutSize)
    {
        ::std::FILE* F = ::std::fopen(Path, "rb");
        if (F == nullptr) { return nullptr; }
        ::std::fseek(F, 0, SEEK_END);
        const long SizeRaw = ::std::ftell(F);
        ::std::fseek(F, 0, SEEK_SET);
        if (SizeRaw < 0) { ::std::fclose(F); return nullptr; }
        const ::SIZE_T Size = static_cast<::SIZE_T>(SizeRaw);
        char* Buf = static_cast<char*>(::std::malloc(Size + 1));
        if (Buf == nullptr) { ::std::fclose(F); return nullptr; }
        const ::SIZE_T Read = ::std::fread(Buf, 1, Size, F);
        Buf[Read] = '\0';
        ::std::fclose(F);
        *OutSize = Read;
        return Buf;
    }
}

int main()
{
    using ::XCore::HAL::FMemory;
    using ::XCore::HAL::FMemTag;
    using ::XCore::HAL::FLeakTracker;

    FMemory::__Init();
    FLeakTracker::__Init();

    // ---------------------------------------------------------------
    // Step 1: produce some leaks.
    // ---------------------------------------------------------------
    constexpr int kLeakCount = 50;
    constexpr ::SIZE_T kLeakSize = 128;
    for (int I = 0; I < kLeakCount; ++I)
    {
        void* P = FMemory::Malloc(kLeakSize, 8, FMemTag::Container);
        if (P == nullptr)
        {
            std::fprintf(stderr, "FAIL: Malloc returned null at alloc %d\n", I);
            return 1;
        }
        (void)P;  // intentionally leaked
    }

    // ---------------------------------------------------------------
    // Step 2: WriteReport to a temp file.
    // ---------------------------------------------------------------
    char PathBuf[300];
    if (!ComposeTempPath(PathBuf, sizeof(PathBuf)))
    {
        std::fprintf(stderr, "FAIL: ComposeTempPath\n");
        return 1;
    }

    ::XCore::FString PathStr(PathBuf);
    FLeakTracker::WriteReport(PathStr);

    // ---------------------------------------------------------------
    // Step 3: Verify file exists + has required structure.
    // ---------------------------------------------------------------
    ::SIZE_T FileSize = 0;
    char* Content = SlurpFile(PathBuf, &FileSize);
    if (Content == nullptr)
    {
        std::fprintf(stderr, "FAIL: report file '%s' not readable\n", PathBuf);
        return 1;
    }
    if (FileSize == 0)
    {
        std::fprintf(stderr, "FAIL: report file '%s' is empty\n", PathBuf);
        ::std::free(Content);
        return 1;
    }

    // Required markers per the report format.
    const char* kRequiredMarkers[] = {
        "FLeakTracker WriteReport",
        "Total leaked bytes:",
        "Total leaked allocations:",
    };
    for (const char* M : kRequiredMarkers)
    {
        if (::std::strstr(Content, M) == nullptr)
        {
            std::fprintf(stderr,
                "FAIL: report file missing required marker '%s'\n", M);
            ::std::free(Content);
            return 1;
        }
    }

    // Each leak above is a deliberate one; we should have at least
    // ONE "Bucket #" line in the output.
    if (::std::strstr(Content, "Bucket #") == nullptr)
    {
        std::fprintf(stderr,
            "FAIL: report file missing any 'Bucket #' section "
            "(no stack-bucket emissions)\n");
        ::std::free(Content);
        return 1;
    }

    ::std::free(Content);

    // Cleanup -- remove the report file (harmless if it fails).
    ::std::remove(PathBuf);

    FLeakTracker::__Shutdown();
    FMemory::__Shutdown();
    std::printf("WriteReportToFile: PASS (report at '%s')\n", PathBuf);
    return 0;
}

#else  // XPACT_LEAK_TRACKING_ENABLED == 0

#include <cstdio>
int main()
{
    std::printf("WriteReportToFile: SKIP (XPACT_LEAK_TRACKING_ENABLED = 0)\n");
    return 0;
}

#endif
