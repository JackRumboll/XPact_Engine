// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// Win64PlatformMisc.cpp -- Win64 bodies for FPlatformMisc.
// =====================================================================
//
// XCore-4a Rev 3, Section 7 (Platform HAL). Per-OS implementation for
// the FPlatformMisc surface declared in Public/HAL/FPlatformMisc.h.
//
// Platform gating: the entire TU body is wrapped in `#if XPACT_PLATFORM_WIN64`.
// XBT discovers every .cpp in the module recursively (BuildMode.cs
// EnumerateModuleFiles line 2023 walks the tree with no platform filter)
// so the per-OS subdir convention is a documentation aid only; the
// compile-out is the platform-gate macro. The TODO at the bottom of
// `XCore.Build.toml` documents this; once XBT grows a typed
// per-platform-source filter (e.g. via .Build.expr) the macro-gate can
// be deleted in favour of the parser-side filter.
//
// Pattern reference:
//   * UE Core Private/Windows/WindowsPlatformMisc.cpp -- the per-method
//     bodies are the inspiration, NOT copied. UE's surface includes a
//     much larger set of methods (GetEnvironmentVariable, AppLogString,
//     OS version queries, etc.); XPact keeps only the 7 documented in
//     FPlatformMisc.h.
//   * GetMachineId pattern: Microsoft's documented MachineGuid registry
//     key (HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid). Stable for
//     the OS install lifetime. Win64-specific (replaces UE's hashed
//     machine-SID approach which requires admin-level SID enumeration).
//   * GetEntropy uses BCryptGenRandom with BCRYPT_USE_SYSTEM_PREFERRED_RNG --
//     the documented "give me crypto-strength randomness" entry point on
//     Win64 (replaces the legacy CryptGenRandom path which is wrapped
//     for AppContainer use).
//
// =====================================================================

#include "HAL/FPlatformMisc.h"

#include "Macros/XPactMacros.h"  // XPACT_PLATFORM_WIN64 selector

#if XPACT_PLATFORM_WIN64

#ifndef WIN32_LEAN_AND_MEAN
    #define WIN32_LEAN_AND_MEAN 1
#endif
#ifndef NOMINMAX
    #define NOMINMAX 1
#endif

#include <Windows.h>
#include <bcrypt.h>   // BCryptGenRandom
#include <intrin.h>   // __debugbreak

#include <cstdio>     // std::snprintf for the version-string placeholder

#include "Containers/FString.h"       // FString concrete definition (Phase 1g)
#include "Macros/XAssertionMacros.h"  // AbortWithMessage

// BCryptGenRandom's NTSTATUS success sentinel; <ntdef.h> would provide
// STATUS_SUCCESS but pulling in ntdef from kernel32-only TUs adds a
// header-cost; defining it locally is the documented pattern (Microsoft
// SDK headers do the same in the bcrypt sample code).
#ifndef STATUS_SUCCESS
    #define STATUS_SUCCESS ((LONG)0)
#endif

#pragma comment(lib, "bcrypt.lib")    // BCryptGenRandom
#pragma comment(lib, "Advapi32.lib")  // RegGetValueW

namespace XCore::HAL
{

// ---------------------------------------------------------------------
// GetPlatform -- compile-time-constant; returns Win64.
// ---------------------------------------------------------------------
EPlatform FPlatformMisc::GetPlatform() noexcept
{
    return EPlatform::Win64;
}

// ---------------------------------------------------------------------
// GetMachineId -- HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid.
//
// Microsoft's documented stable per-machine identifier. The value is a
// GUID string in canonical 36-char form (e.g.,
// "11223344-5566-7788-9900-aabbccddeeff"). Persisted across reboots
// for the OS install lifetime; rotated only by a reinstall or by an
// IT admin explicitly resetting the key.
//
// The registry read returns UTF-16; the body transcodes via
// WideCharToMultiByte(CP_UTF8, ...) into a stack buffer and constructs
// the returned FString from the UTF-8 byte buffer. The wide-buffer
// path remains reachable via the internal symbol
// `XPACT_TEST_ReadMachineGuid` for tests that want the raw UTF-16
// without paying the transcode.
// ---------------------------------------------------------------------

namespace
{
    // Internal helper extracting the GUID into a caller-provided
    // UTF-16 stack buffer. Exposed via XPACT_TEST_ReadMachineGuid for
    // Phase 1b testing; production code uses GetMachineId().
    [[nodiscard]] bool ReadMachineGuidUtf16(wchar_t* OutBuf, ::std::size_t OutBufWchars) noexcept
    {
        if (OutBuf == nullptr || OutBufWchars < 2)
        {
            return false;
        }
        DWORD BufBytes = static_cast<DWORD>(OutBufWchars * sizeof(wchar_t));
        // RRF_RT_REG_SZ ensures we only accept REG_SZ values.
        const LSTATUS Status = ::RegGetValueW(
            HKEY_LOCAL_MACHINE,
            L"SOFTWARE\\Microsoft\\Cryptography",
            L"MachineGuid",
            RRF_RT_REG_SZ,
            nullptr,
            OutBuf,
            &BufBytes);
        return Status == ERROR_SUCCESS;
    }
}

FString FPlatformMisc::GetMachineId()
{
    wchar_t WideBuf[64] = { 0 };
    if (!ReadMachineGuidUtf16(WideBuf, sizeof(WideBuf) / sizeof(wchar_t)))
    {
        return FString{};
    }

    // Transcode UTF-16 -> UTF-8. The canonical GUID is 36 ASCII chars
    // (1 byte each in UTF-8), so a 64-byte buffer is comfortably large;
    // we still pass the wide length explicitly so any future kernel
    // format change does not silently truncate.
    const int WideLen = static_cast<int>(::wcslen(WideBuf));
    char Utf8Buf[128] = { 0 };
    const int Utf8Bytes = ::WideCharToMultiByte(
        CP_UTF8, 0,
        WideBuf, WideLen,
        Utf8Buf, static_cast<int>(sizeof(Utf8Buf)),
        nullptr, nullptr);
    if (Utf8Bytes <= 0)
    {
        return FString{};
    }
    return FString(Utf8Buf, static_cast<::int32>(Utf8Bytes));
}

extern "C" int XPACT_TEST_ReadMachineGuid(wchar_t* OutBuf, ::std::size_t OutBufWchars) noexcept
{
    return ReadMachineGuidUtf16(OutBuf, OutBufWchars) ? 1 : 0;
}

// ---------------------------------------------------------------------
// GetCpuCount -- GetSystemInfo().dwNumberOfProcessors.
//
// Returns the logical processor count (hyper-threads counted on x86_64).
// For affinity-mask-bounded subsets (process-group limit, kubelet
// cpuset constraints) GetActiveProcessorCount(ALL_PROCESSOR_GROUPS) is
// the more accurate API; we use GetSystemInfo here because XPact's
// Win64 deployment is desktop / editor (one processor group on all
// supported hardware). A TODO documents the migration if a future
// XPact target runs in a constrained container.
//
// TODO(Phase 2): if XPact ships on Windows Server with active
// processor-group splitting, switch to
// GetActiveProcessorCount(ALL_PROCESSOR_GROUPS) which sums across
// groups; GetSystemInfo only reports the calling thread's group.
// ---------------------------------------------------------------------
::std::uint32_t FPlatformMisc::GetCpuCount() noexcept
{
    SYSTEM_INFO SysInfo;
    ::GetSystemInfo(&SysInfo);
    return static_cast<::std::uint32_t>(SysInfo.dwNumberOfProcessors);
}

// ---------------------------------------------------------------------
// GetTotalPhysicalRamBytes -- GlobalMemoryStatusEx().ullTotalPhys.
//
// The 64-bit memory-status variant. The legacy GlobalMemoryStatus
// returns 32-bit values that saturate at 4 GiB; the Ex version is the
// documented modern path (Microsoft SDK docs: "Applications that
// support more than 4 GB of memory should use GlobalMemoryStatusEx").
// ---------------------------------------------------------------------
::std::uint64_t FPlatformMisc::GetTotalPhysicalRamBytes() noexcept
{
    MEMORYSTATUSEX Stat;
    Stat.dwLength = sizeof(Stat);
    if (!::GlobalMemoryStatusEx(&Stat))
    {
        // GlobalMemoryStatusEx is documented as "essentially always
        // succeeds"; if it fails we have a kernel-level fault and
        // returning 0 lets the caller observe and abort. Do NOT
        // crash here -- this method may be called from a crash-context
        // logger; aborting would cause an abort-loop.
        return 0;
    }
    return static_cast<::std::uint64_t>(Stat.ullTotalPhys);
}

// ---------------------------------------------------------------------
// GetEngineVersionString -- "X.Y.Z-build-<git-shortsha>".
//
// Target format is "X.Y.Z-build-<git-shortsha>" (e.g.,
// "0.1.0-build-b8210cc"); fully populated by the XBT-emitted
// `XCoreVersion.gen.h` which defines `XPACT_ENGINE_VERSION` from
// /Engine/Engine.xengine + the current commit short SHA.
//
// TODO(Phase 1b XBT integration): once XBT's version-emit pass ships
// XCoreVersion.gen.h with XPACT_ENGINE_VERSION, swap the literal below
// for the macro. Until then we return the bare engine semver from
// Engine.xengine ("0.1.0") so callers get a well-formed string rather
// than an empty FString that would null-out logging surfaces.
// ---------------------------------------------------------------------
FString FPlatformMisc::GetEngineVersionString()
{
#if defined(XPACT_ENGINE_VERSION)
    return FString(XPACT_ENGINE_VERSION);
#else
    return FString("0.1.0");
#endif
}

// ---------------------------------------------------------------------
// Shutdown-request state: a one-shot atomic flag + exit code.
//
// Per Section 7.2 contract: "RequestExit is one-shot idempotent;
// concurrent calls land at most one exit." The atomic_compare_exchange
// below enforces the one-shot property; concurrent callers after the
// first see g_ExitRequested == true and the body becomes a no-op.
//
// Storage is constinit-initialised so the static-init-order-fiasco
// cannot bite -- a constinit caller in PreStaticInit can request exit
// before the runtime's main() body runs and the read on the actor
// thread observes the requested state.
//
// The actual process exit is deferred to the next frame boundary
// (per spec); for Phase 1b the placeholder approach is: capture the
// requested state + code; the engine bootstrap polls
// FPlatformMisc::GetExitRequested() once per frame (the accessor lands
// in Phase 1c). For now, set a process-wide signal via
// SetEvent / a global event handle. To keep Phase 1b dependency-free
// we use a constinit atomic + leave the actual exit-driver poll site
// as a TODO.
// ---------------------------------------------------------------------
namespace
{
    constinit ::std::atomic<bool>    g_ExitRequested{ false };
    constinit ::std::atomic<::std::int32_t> g_ExitCode{ 0 };
}

void FPlatformMisc::RequestExit(::std::int32_t Code) noexcept
{
    bool Expected = false;
    if (g_ExitRequested.compare_exchange_strong(
            Expected, true,
            ::std::memory_order_release,
            ::std::memory_order_relaxed))
    {
        // First-call winner: latch the exit code.
        g_ExitCode.store(Code, ::std::memory_order_release);
    }
    // Else: a sister thread already won the race; this call is a no-op.

    // TODO(Phase 1c / XEngineInit): the engine bootstrap polls the
    // g_ExitRequested flag at frame boundaries and routes the call to
    // PostQuitMessage / SetEvent on the shutdown-request event. For
    // Phase 1b the flag is exposed via internal symbols only (no
    // accessor in FPlatformMisc.h surface yet); when the bootstrap
    // lands we add an `extern "C" bool XPACT_GetExitRequested()` shim
    // here.
}

// ---------------------------------------------------------------------
// DebugBreak -- IsDebuggerPresent() ? __debugbreak() : ((void)0).
//
// Per Section 7 spec: "Break into the debugger if one is attached;
// otherwise no-op." The IsDebuggerPresent check ensures the call is
// safe in production where no debugger is attached (an unguarded
// __debugbreak in production would FailFast the process which is the
// WRONG behaviour for an opt-in debug-break primitive).
// ---------------------------------------------------------------------
void FPlatformMisc::DebugBreak() noexcept
{
    if (::IsDebuggerPresent())
    {
        ::__debugbreak();
    }
}

// ---------------------------------------------------------------------
// GetEntropy -- BCryptGenRandom(BCRYPT_USE_SYSTEM_PREFERRED_RNG).
//
// Microsoft-documented crypto-strength entropy primitive. The
// SYSTEM_PREFERRED_RNG flag selects the OS's currently-configured
// preferred CSPRNG (typically the Windows kernel's CTR_DRBG over
// AES-256). FIPS-compliant builds get a FIPS-validated path
// automatically.
//
// Per Section 7 spec: "On any platform syscall failure, the
// implementation aborts (entropy starvation is a fatal condition; the
// engine cannot produce a non-deterministic seed without OS support)."
// We route through AbortWithMessage to surface the diagnostic; the
// abort path is the same FailFast used by XPACT_CHECK violations.
//
// Contract guards:
//   * NumBytes == 0 is a no-op (BCryptGenRandom would return an error
//     for a 0-byte request).
//   * OutBytes == nullptr with NumBytes > 0 is a contract violation
//     handled by AbortWithMessage (instead of letting BCrypt produce
//     ERROR_INVALID_PARAMETER which carries less diagnostic value).
// ---------------------------------------------------------------------
void FPlatformMisc::GetEntropy(void* OutBytes, ::std::size_t NumBytes) noexcept
{
    if (NumBytes == 0)
    {
        return;  // Sanity-pass: zero-byte request is a valid no-op.
    }

    if (OutBytes == nullptr)
    {
        AbortWithMessage(
            "FPlatformMisc::GetEntropy: OutBytes==nullptr with NumBytes>0",
            __FILE__, __LINE__);
    }

    // BCryptGenRandom takes a ULONG NumBytes; on Win64 ULONG is 32 bits.
    // Loop in chunks to handle requests > UINT32_MAX cleanly (rare but
    // not impossible; e.g., a large RNG-seed dump for diagnostics).
    auto* Cursor = static_cast<unsigned char*>(OutBytes);
    ::std::size_t Remaining = NumBytes;
    while (Remaining > 0)
    {
        const ULONG ChunkSize = (Remaining > 0xFFFFFFFFu)
            ? 0xFFFFFFFFu
            : static_cast<ULONG>(Remaining);

        const LONG NtStatus = ::BCryptGenRandom(
            nullptr,
            reinterpret_cast<PUCHAR>(Cursor),
            ChunkSize,
            BCRYPT_USE_SYSTEM_PREFERRED_RNG);

        if (NtStatus != STATUS_SUCCESS)
        {
            // Compose a diagnostic with the NTSTATUS for forensic
            // correlation against the Windows event log.
            char Buf[128];
            ::std::snprintf(Buf, sizeof(Buf),
                "FPlatformMisc::GetEntropy: BCryptGenRandom failed NTSTATUS=0x%08lX",
                static_cast<unsigned long>(NtStatus));
            AbortWithMessage(Buf, __FILE__, __LINE__);
        }

        Cursor    += ChunkSize;
        Remaining -= ChunkSize;
    }
}

} // namespace XCore::HAL

#endif // XPACT_PLATFORM_WIN64
