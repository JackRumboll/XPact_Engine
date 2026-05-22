// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FPlatformTLS.h -- abstract platform TLS-slot surface.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1 (Platform HAL) + Section 13.1 fix B-C1
// (XPACT_TLS_MODULE_SAFE module-safe TLS macro). Per-OS concrete
// implementations land alongside this header in Phase 1g.
//
// WHY THIS EXISTS.
//
// Raw `thread_local` storage in a hot-reloadable DLL leaves dangling
// fiber-local-storage entries when the DLL is unloaded: the entries
// continue to live in every existing thread's TLS table and point at
// freed code/data. The next module loaded into the same TLS slot then
// observes garbage. The slot-allocator pattern routes through a
// globally-allocated TLS slot whose destruction is sequenced with the
// owning module's unload -- UE's `FPlatformTLS::AllocTlsSlot` /
// `GetTlsValue` / `FreeTlsSlot` is the well-trodden parallel.
//
// On Linux server (no DLL hot-reload at MVP) the macro
// `XPACT_TLS_MODULE_SAFE` falls through to native `thread_local`;
// `FPlatformTLS` is still defined here so test code can exercise the
// slot-allocator path on every platform.
//
// SURFACE.
//
//   AllocSlot()             -- reserve a TLS slot index; returns a
//                              platform-opaque uint32 handle.
//   FreeSlot(uint32)        -- release a previously-allocated slot.
//   GetValueInSlot(uint32)  -- read the calling thread's pointer for
//                              the given slot. Returns nullptr if
//                              never-set on this thread.
//   SetValueInSlot(uint32,
//                  void*)   -- write the calling thread's pointer for
//                              the given slot.
//
// PHASE 1g IMPLEMENTATION:
//   - Win64: TlsAlloc / TlsFree / TlsGetValue / TlsSetValue
//   - Linux: pthread_key_create / pthread_key_delete / pthread_getspecific / pthread_setspecific
//   - Android: same as Linux (Bionic pthread).
//
// Sim-path discipline: the slot APIs are NOT sim-path-safe (TLS slot
// values are per-thread, and the sim path is single-threaded; a
// non-zero TLS read in a sim-path TU would imply multi-threaded
// state). The sim-path overlay header (Phase 1e) decorates the slot-
// read with `[[deprecated("not sim-path-safe")]]`.
//
// =====================================================================

#include <cstdint>

namespace XCore::HAL
{

// ---------------------------------------------------------------------
// kInvalidTLSSlot -- sentinel for "no slot allocated".
//
// 0xFFFFFFFFu mirrors Win32's TLS_OUT_OF_INDEXES. The Linux pthread_key_t
// is opaque (typically a small unsigned integer); we cast to uint32_t
// for cross-platform uniformity. The sentinel is checked at slot-free
// time so a double-free is detected.
// ---------------------------------------------------------------------
inline constexpr ::std::uint32_t kInvalidTLSSlot = 0xFFFFFFFFu;

// ---------------------------------------------------------------------
// FTLSDestructor -- per-thread destructor callback signature.
//
// Called when a thread exits with a non-null TLS slot value. The
// callback receives the slot's pointer and is responsible for
// destruction + deallocation. Mirrors POSIX pthread_key_create's
// destructor argument and Win32 FlsAlloc's PFLS_CALLBACK_FUNCTION.
//
// IMPORTANT: the callback runs on the EXITING thread, with that
// thread's storage still live. The callback runs BEFORE the thread's
// TLS bookkeeping is torn down (the typical OS guarantee for
// pthread_key destructors / Fls callbacks). Implementations should
// be lock-free / minimal-allocation; heavy lifting (e.g., publishing
// to a global queue for later drain) is fine because the publish
// itself is fast.
// ---------------------------------------------------------------------
using FTLSDestructor = void (*)(void* Value);

class FPlatformTLS
{
public:
    // -----------------------------------------------------------------
    // AllocSlot -- reserve one TLS slot, optionally with a destructor.
    //
    // Returns a platform-opaque slot handle. On Win64 with a non-null
    // destructor this routes through FlsAlloc (so the OS calls back
    // at thread exit); with a null destructor it routes through
    // TlsAlloc (fast path, no callback). On Linux/Android pthread_key_
    // create accepts the destructor directly.
    //
    // Returns kInvalidTLSSlot if the platform refuses (Win64 has a
    // 1088-slot per-process cap; Linux PTHREAD_KEYS_MAX is typically
    // 1024). On failure the caller must abort / OOM-handle; a leaked
    // TLS slot does not regenerate.
    // -----------------------------------------------------------------
    static ::std::uint32_t AllocSlot(FTLSDestructor Destructor = nullptr) noexcept;

    // -----------------------------------------------------------------
    // FreeSlot -- release a previously-allocated slot.
    //
    // After FreeSlot the slot handle is invalid; subsequent
    // Get/SetValueInSlot calls on the freed handle are undefined.
    // Passing kInvalidTLSSlot is a no-op.
    // -----------------------------------------------------------------
    static void FreeSlot(::std::uint32_t SlotIndex) noexcept;

    // -----------------------------------------------------------------
    // GetValueInSlot -- read the calling thread's TLS pointer.
    //
    // Returns nullptr if SetValueInSlot has never been called on the
    // calling thread for this slot. The cast to/from void* is the
    // standard pattern (UE FPlatformTLS uses the same).
    // -----------------------------------------------------------------
    static void* GetValueInSlot(::std::uint32_t SlotIndex) noexcept;

    // -----------------------------------------------------------------
    // SetValueInSlot -- write the calling thread's TLS pointer.
    //
    // The previous value (if any) is overwritten without destructor
    // invocation. Callers responsible for managing the pointed-at
    // object's lifetime (the slot does not own the storage).
    // -----------------------------------------------------------------
    static void SetValueInSlot(::std::uint32_t SlotIndex, void* Value) noexcept;
};

} // namespace XCore::HAL
