// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FPlatformMisc.h -- abstract platform-misc surface.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1 (Platform HAL) + Section 14 step 2
// (Platform HAL abstract surface). Per-OS concrete implementations
// land in step 3 (Phase 1b: Win64 / Linux / Android). XCore-4a's
// design collapses UE's 28 platform subclasses (Section 7.6) into
// 3 concrete classes picked at compile time via TargetRules.Platform;
// this header declares the abstract surface those 3 classes implement.
//
// Pattern reference: UE Core's `GenericPlatformMisc.h:117-150` defines
// the per-platform `FGenericPlatformMisc` base. XPact's surface is a
// tighter superset (`GetPlatform` enum returns one of 3; `GetEntropy`
// is the locked Rev 3 fix M5 entropy primitive; no per-platform
// PreInit/PostInit/AppInit lifecycle hooks because XPact's three-step
// phase ladder (Section 1.5) replaces them).
//
// Sim-path discipline (Section 7.3): `GetEntropy` is NOT sim-path-safe
// (returns OS-CSRNG bytes; non-deterministic by definition). The
// sim-path overlay header in Phase 1e decorates this method with
// `[[deprecated("not sim-path-safe; use FRandomStream with synchronized
// seed")]]`. Sim-path TUs cannot call `GetEntropy` -- they must seed
// `FRandomStream` (Section 6.1.6) from the session-start synchronised
// seed. See Section 6.1.6 + Section 7.5 cross-reference for the editor
// random-asset preview / plugin auth token usage that legitimately
// consumes `GetEntropy`.
//
// Abstract surface only -- NO .cpp bodies in Phase 1a. Test compiles
// must NOT link these symbols; use `extern` declarations only. The
// per-OS .cpp implementations land in Phase 1b.
//
// =====================================================================

#include "Macros/XCoreFwd.h"  // FString forward declaration (XCore namespace)

#include <cstddef>
#include <cstdint>

namespace XCore::HAL
{

// ---------------------------------------------------------------------
// Alias FString from the XCore namespace into the XCore::HAL scope
// used by this header.
//
// Section 11 places FString in namespace XCore; XCoreFwd.h's forward
// declaration honours that placement. The HAL surface returns FString
// by value for GetMachineId / GetExecutablePath / GetEngineVersion-
// String; the FString definition is included at the call site (Phase
// 1g delivers FString.h), not here.
// ---------------------------------------------------------------------

using ::XCore::FString;

// ---------------------------------------------------------------------
// EPlatform: compile-time platform enumeration.
//
// Per Section 1.4 + Section 7.1 + Section 2 platforms row: three
// concrete platforms only. UE's 28-platform enum (Section 7.6) is
// collapsed to the three XPact actually targets. Win64 = editor +
// desktop client; Linux = dedicated server; Android = Quest 3 VR
// ARM64.
//
// uint8_t backing: matches FMemTag (Section 4.1) and other 1-byte ABI
// enums; packs efficiently into structs.
// ---------------------------------------------------------------------

enum class EPlatform : uint8_t
{
    Win64   = 0,  // editor / desktop client
    Linux   = 1,  // dedicated server
    Android = 2,  // Quest 3 VR (ARM64)
};

// ---------------------------------------------------------------------
// FPlatformMisc -- abstract platform-misc surface.
//
// Per Section 7.1 spec: each method has a single per-OS .cpp body in
// Phase 1b (Win64 = `Private/HAL/Win64/FPlatformMisc.Win64.cpp`; same
// for Linux/Android). No virtual dispatch -- the abstract class
// presents the surface; the linker links one platform's bodies per
// target. Cost is zero; signatures are common.
//
// Pattern adopted from UE Core `GenericPlatformMisc.h:117-152`: a
// non-instantiable holder class with all-static methods. UE's
// `FGenericPlatformMisc` is a `struct`; XPact uses `class` to keep
// uniformity with the rest of the HAL surface (FPlatformTime,
// FPlatformProcess, etc. are also classes per Section 7.1).
//
// ABI: all methods are `noexcept`. No virtuals. The class is "ABI by
// signature equality": Phase 1b's per-OS implementations have the same
// signatures so a per-platform link selects the correct bodies without
// any vtable cost.
// ---------------------------------------------------------------------

class FPlatformMisc
{
public:
    // -----------------------------------------------------------------
    // Compile-time platform identity. Returns a fixed value per build
    // configuration (resolved at compile-time per TargetRules; the
    // .cpp body in Phase 1b is `return EPlatform::Win64;` in
    // `Win64/FPlatformMisc.Win64.cpp`, etc.).
    //
    // C# binding: mirror `XPact.Core.FPlatform.Current` (spec 7.1).
    // -----------------------------------------------------------------
    static EPlatform GetPlatform() noexcept;

    // -----------------------------------------------------------------
    // Stable per-machine identifier (UTF-8 FString).
    //
    // Phase 1b implementation:
    //   - Win64: hashed combine of machine-SID + machine-GUID
    //   - Linux: hashed combine of /etc/machine-id contents
    //   - Android: hashed combine of ANDROID_ID (Settings.Secure)
    //
    // Returns by value; FString is move-constructed at the call site.
    // -----------------------------------------------------------------
    static FString GetMachineId();

    // -----------------------------------------------------------------
    // Logical CPU count (hyper-threads counted on x86_64; physical cores
    // on ARM64 since Snapdragon XR2 Gen 2 has no SMT).
    //
    // Phase 1b implementation:
    //   - Win64: GetSystemInfo().dwNumberOfProcessors
    //   - Linux: get_nprocs() / sysconf(_SC_NPROCESSORS_ONLN)
    //   - Android: sysconf(_SC_NPROCESSORS_ONLN)
    // -----------------------------------------------------------------
    static uint32_t GetCpuCount() noexcept;

    // -----------------------------------------------------------------
    // Total physical RAM in bytes.
    //
    // Phase 1b implementation:
    //   - Win64: GlobalMemoryStatusEx().ullTotalPhys
    //   - Linux: sysinfo().totalram * sysinfo().mem_unit
    //   - Android: /proc/meminfo MemTotal
    //
    // Cross-references FPlatformMemory::GetMemoryStats() (same value
    // available on `FPlatformMemoryStats::TotalPhysical`); this method
    // is the lighter-weight single-value query for boot-time RAM-bucket
    // decisions.
    // -----------------------------------------------------------------
    static uint64_t GetTotalPhysicalRamBytes() noexcept;

    // -----------------------------------------------------------------
    // Engine version string (UTF-8 FString).
    //
    // Format: "X.Y.Z-build-<git-shortsha>" (e.g., "0.1.0-build-b8210cc").
    // Phase 1b populates this from XBT-generated `XCoreVersion.gen.h`.
    // -----------------------------------------------------------------
    static FString GetEngineVersionString();

    // -----------------------------------------------------------------
    // Request engine shutdown with the given exit code.
    //
    // Idempotent: concurrent calls land at most one exit (per Section
    // 7.2 threading contract). The actual exit happens at the next
    // frame boundary so in-flight work can flush cleanly.
    //
    // Phase 1b implementation:
    //   - Win64: SetEvent on the shutdown-request event
    //   - Linux/Android: write to the shutdown-request eventfd
    // -----------------------------------------------------------------
    static void RequestExit(int32_t Code) noexcept;

    // -----------------------------------------------------------------
    // Break into the debugger if one is attached; otherwise no-op.
    //
    // Phase 1b implementation:
    //   - Win64: IsDebuggerPresent() ? __debugbreak() : ((void)0)
    //   - Linux: IsBeingTraced() ? raise(SIGTRAP) : ((void)0)
    //   - Android: same as Linux
    //
    // Pattern reference: UE Core `GenericPlatformMisc.h:46-50` defines
    // `UE_DEBUG_BREAK_IMPL` as a per-platform macro. XPact unifies the
    // surface to a single static method; per-OS implementations supply
    // the platform-specific body.
    // -----------------------------------------------------------------
    static void DebugBreak() noexcept;

    // -----------------------------------------------------------------
    // Cryptographically-secure random bytes (NOT sim-path-safe).
    //
    // Fix Rev 3 M5: non-deterministic entropy primitive used by non-sim
    // consumers (editor random-asset preview; plugin authentication
    // tokens; FRandomStream initial-seed sources outside sim-path).
    //
    // Phase 1b implementation:
    //   - Win64: BCryptGenRandom (CNG; BCRYPT_USE_SYSTEM_PREFERRED_RNG)
    //   - Linux: getrandom() syscall (Linux 3.17+; GRND_NONBLOCK + retry)
    //   - Android: getrandom() syscall (NDK 28+); fall back to
    //              /dev/urandom on older NDKs
    //
    // SIM-PATH DEPRECATION (Phase 1e overlay): the sim-path overlay
    // header decorates this method with
    //     [[deprecated("not sim-path-safe; use FRandomStream with
    //                   synchronized seed")]]
    // so sim-path TUs reject the call at compile time.
    //
    // Cross-references Section 6.1.6 (FRandomStream) + Section 7.5
    // (FDateTime entropy cross-reference).
    //
    // Contract:
    //   - OutBytes != nullptr; NumBytes > 0
    //   - On any platform syscall failure, the implementation aborts
    //     (entropy starvation is a fatal condition; the engine cannot
    //     produce a non-deterministic seed without OS support)
    //   - Output is filled with NumBytes cryptographically-random bytes
    // -----------------------------------------------------------------
    static void GetEntropy(void* OutBytes, size_t NumBytes) noexcept;
};

// ---------------------------------------------------------------------
// ABI lock: EPlatform is single-byte-backed.
// ---------------------------------------------------------------------

static_assert(sizeof(EPlatform) == 1,
              "EPlatform ABI lock: must be 1 byte (uint8_t-backed)");

} // namespace XCore::HAL

// =====================================================================
// TODO(Phase 1b):
//   - Implement FPlatformMisc::GetPlatform per OS (`return
//     EPlatform::Win64/Linux/Android;` in three .cpp files).
//   - Implement FPlatformMisc::GetMachineId via SID+GUID hash on Win64,
//     /etc/machine-id on Linux, ANDROID_ID on Android.
//   - Implement FPlatformMisc::GetCpuCount via GetSystemInfo (Win64),
//     get_nprocs (Linux), sysconf (Android).
//   - Implement FPlatformMisc::GetTotalPhysicalRamBytes via
//     GlobalMemoryStatusEx (Win64), sysinfo (Linux), /proc/meminfo
//     (Android).
//   - Implement FPlatformMisc::GetEngineVersionString from XBT-emitted
//     `XCoreVersion.gen.h`.
//   - Implement FPlatformMisc::RequestExit via shutdown-event signal.
//   - Implement FPlatformMisc::DebugBreak via __debugbreak / SIGTRAP.
//   - Implement FPlatformMisc::GetEntropy via BCryptGenRandom (Win64),
//     getrandom syscall (Linux + Android NDK 28+).
// =====================================================================
