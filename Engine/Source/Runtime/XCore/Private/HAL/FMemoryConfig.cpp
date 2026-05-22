// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FMemoryConfig.cpp -- OOM policy active global + test-only setter.
// =====================================================================
//
// XCore-4a Rev 3, Section 4.1 + Section 4.5 (OOM contract / fix M-2).
//
// The active FOOMPolicy is a process-wide global, initialised at
// FMemory::__Init from kDefaultOOMPolicy in FOOMPolicy.h. The
// production path NEVER changes the policy after __Init; the
// test-only setter (__SetActiveOOMPolicy_TestOnly) is provided so
// test fixtures can flip the policy to ReturnNull and exercise the
// MallocOrAbort path in a Debug-config build (where the compile-time
// default is PanicSnapshot).
//
// The global is XCONSTINIT so it is read-correct even from constinit
// constructors that fire before main(). The initial value is
// kDefaultOOMPolicy from FOOMPolicy.h (compile-time selected per
// XPACT_DEBUG/DEVELOPMENT/TEST/SHIPPING).
//
// =====================================================================

#include "HAL/FOOMPolicy.h"

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include <atomic>

namespace XCore::HAL
{
    // -----------------------------------------------------------------
    // The active policy global. constinit-initialised to the
    // build-configuration default. The atomic-store in
    // __SetActiveOOMPolicy_TestOnly ensures cross-thread visibility
    // even though production code never calls it.
    //
    // Stored as std::atomic<uint8> rather than std::atomic<FOOMPolicy>
    // because std::atomic<enum class> requires the underlying type to
    // be lock-free and our toolchain ABI sometimes fails to lock-free
    // an enum-class atomic when the underlying type is lock-free
    // (varies by libstdc++ revision). The uint8 underlying is always
    // lock-free on every supported XPact target.
    // -----------------------------------------------------------------
    namespace
    {
        XCONSTINIT ::std::atomic<::uint8> g_activeOOMPolicy{ static_cast<::uint8>(kDefaultOOMPolicy) };
    } // anonymous

    // -----------------------------------------------------------------
    // GetActiveOOMPolicy -- runtime accessor.
    // -----------------------------------------------------------------
    FOOMPolicy GetActiveOOMPolicy() noexcept
    {
        return static_cast<FOOMPolicy>(g_activeOOMPolicy.load(::std::memory_order_acquire));
    }

    // -----------------------------------------------------------------
    // __SetActiveOOMPolicy_TestOnly -- test-only setter.
    //
    // Production code MUST NOT call this; the XBT linker scan in
    // Phase 1c will fail the build on non-test TUs that reference
    // this symbol.
    // TODO(Phase 1c): land the XBT linker rule.
    // -----------------------------------------------------------------
    void __SetActiveOOMPolicy_TestOnly(FOOMPolicy NewPolicy) noexcept
    {
        g_activeOOMPolicy.store(static_cast<::uint8>(NewPolicy), ::std::memory_order_release);
    }

} // namespace XCore::HAL
