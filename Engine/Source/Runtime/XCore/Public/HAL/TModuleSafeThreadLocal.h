// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// TModuleSafeThreadLocal.h -- per-thread storage with module-safe
// hot-reload semantics (Section 13.1 fix B-C1).
// =====================================================================
//
// XCore-4a Rev 3, Section 13.1 + Section 10.5 (TLS module-safe shard
// for FStatTLS) + Section 4.2 (TLS bin cache).
//
// On hot-reloadable DLL targets (Win64 editor + Android), raw
// `thread_local Type t` storage is a footgun: when the owning DLL
// unloads, every still-alive thread has a stale TLS slot pointing at
// freed code. The next module loaded re-uses the slot and observes
// garbage on its first write.
//
// `TModuleSafeThreadLocal<T>` routes through FPlatformTLS::AllocSlot
// so the slot index is platform-managed and the per-thread storage is
// heap-allocated. On the FreeSlot path (module shutdown) the slot is
// returned to the OS; every thread's pointer for that slot becomes
// unreachable but does NOT outlive the module's code.
//
// On Linux (no hot-reload at MVP), the macro `XPACT_TLS_MODULE_SAFE`
// expands to native `thread_local Type Name` -- this header is not
// included in that path. The header is still ship-on-all-platforms so
// test code can exercise the slot-allocator semantics on any host;
// the choice of native vs slot-allocator is at the macro level
// (XPactMacros.h:441-453), not at the template level.
//
// CONTRACT.
//
//   TModuleSafeThreadLocal<T> t;
//   t.Get();      -- returns a T* for the calling thread. The first
//                    call on any given thread allocates a fresh T on
//                    FMemory's `Stat` tag (the typical caller) and
//                    installs it in the slot. Subsequent calls return
//                    the same pointer.
//   t.Set(p);     -- explicitly install a pointer (advanced; mostly
//                    used by tests to inject a fixture). Caller owns
//                    the pointed-at storage.
//   ~t();         -- frees the slot. Per-thread storage is NOT freed
//                    by the destructor (the calling thread is exiting
//                    the module's TU; the thread itself may persist
//                    on other modules). The principled lifecycle is:
//                      (a) thread exits -> caller drains the TLS data
//                          via CrossThreadFlushOnExit-style hook
//                          (FTLSBinCache.cpp's pattern), then frees;
//                      (b) module unloads -> ~TModuleSafeThreadLocal
//                          frees the slot; all threads' storage in
//                          that slot becomes unreachable -- which is
//                          acceptable because the module's TU is
//                          going away.
//
// LAYOUT.
//
//   uint32_t m_slotIdx;  // FPlatformTLS handle or kInvalidTLSSlot.
//
// The template owns ONE pointer-sized handle; the actual T storage is
// per-thread inside the OS-managed slot.
//
// CONSTEXPR-FRIENDLY.
//
// The default ctor cannot be constexpr because FPlatformTLS::AllocSlot
// is a syscall. For constinit-required globals (e.g., the FStatTLS
// shard), callers either:
//   (a) wrap in a function-local static -- the slot is allocated on
//       first call (Phase 1g's pattern); or
//   (b) initialize at PreStaticInit via a constructor / runtime
//       hook -- the slot is allocated at module-load time.
//
// FStatTLS uses pattern (a) so the ABI remains constinit-friendly.
//
// =====================================================================

#include "HAL/FPlatformTLS.h"
#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"
#include "Macros/XPactMacros.h"

#include <cstdint>
#include <new>          // placement-new
#include <utility>      // std::forward
#include <type_traits>  // std::is_trivially_destructible

namespace XCore::HAL
{

template<typename T>
class TModuleSafeThreadLocal
{
public:
    // -----------------------------------------------------------------
    // Default ctor: allocate a slot with the standard destructor.
    //
    // The destructor (declared below as a static template member) is
    // installed at slot-allocation time. When a thread exits with a
    // non-null T* in this slot, the OS calls the destructor on the
    // exiting thread; the destructor invokes T::~T() and frees the
    // storage via FMemory::Free.
    //
    // Failure: AllocSlot may exhaust the per-process TLS table
    // (Win64 cap ~1088; Linux PTHREAD_KEYS_MAX typically 1024). On
    // failure m_slotIdx == kInvalidTLSSlot and subsequent Get() / Set()
    // are no-ops; callers can branch on IsValid() for explicit
    // defense-in-depth.
    // -----------------------------------------------------------------
    TModuleSafeThreadLocal() noexcept
        : m_slotIdx(FPlatformTLS::AllocSlot(&TModuleSafeThreadLocal::DefaultDestructor))
    {
    }

    // -----------------------------------------------------------------
    // Destructor: free the slot.
    //
    // Per-thread storage is NOT freed by this destructor; the per-thread
    // T objects are unreachable after the slot is freed. Callers that
    // need per-thread cleanup (e.g., the FTLSBinCache cross-thread
    // flush on thread exit) must drain BEFORE the module's static
    // destruction runs.
    //
    // The trade: a TModuleSafeThreadLocal<T> instance is typically a
    // file-scope global; its destructor runs once per process at
    // module unload, by which time any threads still holding T objects
    // are exiting anyway (the module unload follows a global quiesce
    // gate). For per-thread cleanup during normal operation, the
    // owner-thread's exit path drains via the thread-exit hook
    // (FTLSBinCache::CrossThreadFlushOnExit pattern).
    // -----------------------------------------------------------------
    ~TModuleSafeThreadLocal() noexcept
    {
        FPlatformTLS::FreeSlot(m_slotIdx);
        m_slotIdx = kInvalidTLSSlot;
    }

    // Non-copyable / non-movable -- slot index is uniquely owned.
    TModuleSafeThreadLocal(const TModuleSafeThreadLocal&)            = delete;
    TModuleSafeThreadLocal& operator=(const TModuleSafeThreadLocal&) = delete;
    TModuleSafeThreadLocal(TModuleSafeThreadLocal&&)                 = delete;
    TModuleSafeThreadLocal& operator=(TModuleSafeThreadLocal&&)      = delete;

    // -----------------------------------------------------------------
    // IsValid -- slot-allocation succeeded.
    //
    // Returns false if AllocSlot exhausted the OS slot table at ctor
    // time. Callers can branch on this before the first Get/Set.
    // -----------------------------------------------------------------
    [[nodiscard]] XPACT_FORCEINLINE bool IsValid() const noexcept
    {
        return m_slotIdx != kInvalidTLSSlot;
    }

    // -----------------------------------------------------------------
    // Get -- read (or lazily-create) the calling thread's T*.
    //
    // First call on a given thread: heap-allocates a default-constructed
    // T on the FMemory Stat tag and installs it in the slot. The Stat
    // tag is the right tag because the most common caller (FStatTLS)
    // uses this template for stat shards; other callers can pre-populate
    // via Set() to override the tag.
    //
    // Subsequent calls on the same thread: returns the installed pointer.
    //
    // Returns nullptr if AllocSlot failed at ctor time.
    // -----------------------------------------------------------------
    [[nodiscard]] T* Get() noexcept
    {
        if (XPACT_UNLIKELY(!IsValid()))
        {
            return nullptr;
        }
        T* Ptr = static_cast<T*>(FPlatformTLS::GetValueInSlot(m_slotIdx));
        if (XPACT_LIKELY(Ptr != nullptr))
        {
            return Ptr;
        }
        // Lazy-create per-thread storage.
        void* Raw = ::XCore::HAL::FMemory::MallocOrAbort(
            sizeof(T),
            alignof(T),
            ::XCore::HAL::FMemTag::Stat);
        Ptr = ::new (Raw) T();
        FPlatformTLS::SetValueInSlot(m_slotIdx, Ptr);
        return Ptr;
    }

    // -----------------------------------------------------------------
    // Set -- install a pointer explicitly.
    //
    // The previous installed pointer (if any) is overwritten WITHOUT
    // destructor invocation. Caller owns the lifetime of the new
    // pointer.
    // -----------------------------------------------------------------
    void Set(T* Value) noexcept
    {
        if (XPACT_UNLIKELY(!IsValid()))
        {
            return;
        }
        FPlatformTLS::SetValueInSlot(m_slotIdx, Value);
    }

    // -----------------------------------------------------------------
    // Clear -- explicitly free the calling thread's T storage.
    //
    // Calls T's destructor and FMemory::Free. Used by thread-exit
    // hooks (the calling thread is winding down; clear so the slot
    // does not retain a dangling pointer when the slot value is read
    // again by a new thread).
    // -----------------------------------------------------------------
    void Clear() noexcept
    {
        if (!IsValid())
        {
            return;
        }
        T* Ptr = static_cast<T*>(FPlatformTLS::GetValueInSlot(m_slotIdx));
        if (Ptr == nullptr)
        {
            return;
        }
        Ptr->~T();
        ::XCore::HAL::FMemory::Free(Ptr);
        FPlatformTLS::SetValueInSlot(m_slotIdx, nullptr);
    }

    // -----------------------------------------------------------------
    // Operator-> / operator* -- ergonomic accessor.
    //
    // Forwarded to Get(); enables `g_shard->Inc(Hash)` syntax.
    // -----------------------------------------------------------------
    [[nodiscard]] XPACT_FORCEINLINE T* operator->() noexcept { return Get(); }
    [[nodiscard]] XPACT_FORCEINLINE T& operator*()  noexcept { return *Get(); }

private:
    // -----------------------------------------------------------------
    // DefaultDestructor -- the per-thread T cleanup callback.
    //
    // Installed at slot-allocation time. Called by the OS on each
    // thread that exits with a non-null T* in this slot; invokes
    // T::~T() and FMemory::Free on the per-thread storage.
    //
    // Static so it has a stable function-pointer address suitable for
    // the FTLSDestructor signature (a free function with C linkage
    // semantics).
    // -----------------------------------------------------------------
    static void DefaultDestructor(void* Value) noexcept
    {
        if (Value == nullptr)
        {
            return;
        }
        T* Ptr = static_cast<T*>(Value);
        Ptr->~T();
        ::XCore::HAL::FMemory::Free(Ptr);
    }

    ::std::uint32_t m_slotIdx;
};

} // namespace XCore::HAL
