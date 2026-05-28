// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FMemory.cpp -- public-facade dispatcher to FMallocBinnedX.
// =====================================================================
//
// XCore-4a Rev 3, Section 4.1.
//
// Thin facade: every FMemory:: method dispatches to the global
// FMallocBinnedX g_Allocator instance. The facade exists so downstream
// modules link against a stable public symbol set; the allocator
// implementation type is private to the XCore-4a module.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"
#include "HAL/FOOMPolicy.h"
#include "HAL/FMallocBinnedX.h"

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"
#include "Macros/XAssertionMacros.h"

#include <atomic>
#include <cstdio>

namespace XCore::HAL
{
    // -----------------------------------------------------------------
    // The single global allocator instance.
    //
    // constinit ensures the instance is zero-initialised at static-
    // storage-duration time (before any user-tier static constructor
    // runs). The actual VM reservation + pool-table init happens in
    // FMemory::__Init (called at PreStaticInit per Section 1.5).
    //
    // The constinit attribute matches the spec body (Section 4.1
    // implementation note: "The global allocator instance is a static
    // FMallocBinnedX g_Allocator; initialized via constinit so it's
    // available pre-static-init.").
    // -----------------------------------------------------------------
    XCONSTINIT FMallocBinnedX g_Allocator{};

    // -----------------------------------------------------------------
    // Per-thread no-alloc scope counter.
    //
    // Section 4.3 / fix A-MIN4: FScopedNoAlloc increments on
    // construction, decrements on destruction. While non-zero, every
    // Malloc / Realloc on the same thread aborts.
    //
    // In Shipping/Test the counter is unused (the FScopedNoAlloc
    // constructor + destructor compile to no-ops); the thread_local
    // declaration is still emitted but the storage is unused.
    // -----------------------------------------------------------------
    namespace
    {
        thread_local ::uint32 g_noAllocScopeDepth = 0;

        // Phase 1g fix F-3: allocator-alive flag. Set true at the end
        // of __Init(); false at the start of __Shutdown(). Reads use
        // memory_order_acquire to pair with the release stores at the
        // transition points.
        //
        // constinit-initialised to false so reads from a TLS
        // destructor BEFORE __Init has run (a configuration that
        // should not occur in a well-formed program but is the right
        // safe default if it does) also short-circuit to "skip the
        // allocation".
        ::std::atomic<bool> g_AllocatorAlive{ false };
    } // anonymous

    // =====================================================================
    // FMemory::FScopedNoAlloc
    // =====================================================================

#if XPACT_DEBUG || XPACT_DEVELOPMENT
    FMemory::FScopedNoAlloc::FScopedNoAlloc() noexcept
    {
        ++g_noAllocScopeDepth;
    }

    FMemory::FScopedNoAlloc::~FScopedNoAlloc() noexcept
    {
        --g_noAllocScopeDepth;
    }
#else  // Shipping / Test -- compiles to no-op (Section 4.3 fix A-MIN4)
    FMemory::FScopedNoAlloc::FScopedNoAlloc() noexcept  = default;
    FMemory::FScopedNoAlloc::~FScopedNoAlloc() noexcept = default;
#endif

    bool FMemory::IsNoAllocScopeActive() noexcept
    {
#if XPACT_DEBUG || XPACT_DEVELOPMENT
        return g_noAllocScopeDepth > 0;
#else
        return false;
#endif
    }

    // -----------------------------------------------------------------
    // FMemory::IsAlive (Phase 1g fix F-3)
    // -----------------------------------------------------------------
    bool FMemory::IsAlive() noexcept
    {
        return g_AllocatorAlive.load(::std::memory_order_acquire);
    }

    // =====================================================================
    // FMemory dispatchers.
    // =====================================================================

    void* FMemory::Malloc(::SIZE_T Size, ::SIZE_T Align, FMemTag Tag) noexcept
    {
        return g_Allocator.Malloc(Size, Align, Tag);
    }

    void* FMemory::Realloc(void* Ptr, ::SIZE_T NewSize, ::SIZE_T Align, FMemTag Tag) noexcept
    {
        return g_Allocator.Realloc(Ptr, NewSize, Align, Tag);
    }

    void FMemory::Free(void* Ptr) noexcept
    {
        g_Allocator.Free(Ptr);
    }

    // =====================================================================
    // Abort-on-null wrappers.
    // =====================================================================

    void* FMemory::MallocOrAbort(::SIZE_T Size, ::SIZE_T Align, FMemTag Tag) noexcept
    {
        void* Ptr = g_Allocator.Malloc(Size, Align, Tag);
        if (XPACT_UNLIKELY(Ptr == nullptr))
        {
            // Under Abort/PanicSnapshot policies Malloc never returns
            // null; reaching this branch means the policy was
            // ReturnNull and the caller (a container) cannot
            // tolerate it. Abort cleanly with a tagged diagnostic.
            char Buf[256];
            ::std::snprintf(Buf, sizeof(Buf),
                            "FMemory::MallocOrAbort: null return under ReturnNull policy; "
                            "tag=%s size=%llu align=%llu",
                            GetMemTagName(Tag),
                            static_cast<unsigned long long>(Size),
                            static_cast<unsigned long long>(Align));
            ::XCore::HAL::AbortWithMessage(Buf, __FILE__, __LINE__);
        }
        return Ptr;
    }

    void* FMemory::ReallocOrAbort(void* Ptr, ::SIZE_T NewSize, ::SIZE_T Align, FMemTag Tag) noexcept
    {
        void* NewPtr = g_Allocator.Realloc(Ptr, NewSize, Align, Tag);
        if (XPACT_UNLIKELY(NewPtr == nullptr && NewSize != 0))
        {
            char Buf[256];
            ::std::snprintf(Buf, sizeof(Buf),
                            "FMemory::ReallocOrAbort: null return under ReturnNull policy; "
                            "tag=%s newSize=%llu align=%llu",
                            GetMemTagName(Tag),
                            static_cast<unsigned long long>(NewSize),
                            static_cast<unsigned long long>(Align));
            ::XCore::HAL::AbortWithMessage(Buf, __FILE__, __LINE__);
        }
        return NewPtr;
    }

    // =====================================================================
    // Diagnostics.
    // =====================================================================

    ::uint64 FMemory::GetAllocatedBytes(FMemTag Tag) noexcept
    {
        return g_Allocator.GetAllocatedBytes(Tag);
    }

    void FMemory::DumpUsageReport(::XCore::FArchive& /*Archive*/) noexcept
    {
        // TODO(Phase 1c): wire the FArchive consumer once XSerialization
        // ships. For Phase 1b we emit the report to stderr directly so
        // a Dev build's `stat memory` console command can still display
        // it (the console command will route through the Phase 1c
        // FArchive path; for now stderr is sufficient).
        std::fprintf(stderr, "[FMemory] usage by tag:\n");
        for (::uint16 I = 0; I < kMemTagEngineSlotCount; ++I)
        {
            const FMemTag Tag        = static_cast<FMemTag>(I);
            const ::uint64 Bytes     = g_Allocator.GetAllocatedBytes(Tag);
            if (Bytes != 0)
            {
                std::fprintf(stderr, "  %-16s %llu bytes\n",
                             GetMemTagName(Tag),
                             static_cast<unsigned long long>(Bytes));
            }
        }
        std::fflush(stderr);
    }

    // =====================================================================
    // Init / Shutdown.
    // =====================================================================

    void FMemory::__Init() noexcept
    {
        g_Allocator.Init();
        // Phase 1g fix F-3: publish allocator-alive flag AFTER the
        // allocator's own Init has succeeded. memory_order_release so a
        // subsequent IsAlive() acquire-load is happens-after the
        // allocator's internal initialisation.
        g_AllocatorAlive.store(true, ::std::memory_order_release);
    }

    void FMemory::__Shutdown() noexcept
    {
        // Phase 1g fix F-3: clear allocator-alive flag BEFORE tearing
        // down the allocator. Any thread-local destructor that runs
        // between this store and Shutdown's completion sees IsAlive()
        // == false and skips the allocation. memory_order_release
        // pairs with subsequent acquire-loads.
        g_AllocatorAlive.store(false, ::std::memory_order_release);
        g_Allocator.Shutdown();
    }

} // namespace XCore::HAL
