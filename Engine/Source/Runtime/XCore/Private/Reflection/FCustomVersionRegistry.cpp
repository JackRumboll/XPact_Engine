// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FCustomVersionRegistry.cpp -- process-singleton registry body.
// =====================================================================
//
// XCore-4b Rev 3, Section 8.3. Thread-safe via FRWLock; reads acquire
// shared, writes acquire exclusive.
//
// The singleton instance lives as a function-local static so the
// constructor runs on first call to Get(). C++11+ guarantees the
// function-local-static initialiser runs exactly once even under
// concurrent first-callers (the "magic statics" rule); no manual
// double-checked locking required.
//
// =====================================================================

#include "Reflection/FCustomVersionRegistry.h"

#include "HAL/FRWLock.h"
#include "Macros/XCoreTypes.h"

namespace XCore::Reflect
{

FCustomVersionRegistry& FCustomVersionRegistry::Get() noexcept
{
    // Function-local static: thread-safe single initialisation per
    // C++11 [stmt.dcl]/4. The instance lives until process exit; we
    // never destroy it explicitly (the FRWLock destructor is benign
    // but we avoid the order-of-destruction lottery by leaving the
    // static alive).
    static FCustomVersionRegistry Instance;
    return Instance;
}

ERegisterResult FCustomVersionRegistry::RegisterCustomVersion(FGuid Key,
                                                              ::int32 Version,
                                                              FName FriendlyName) noexcept
{
    // Reject the all-zero GUID: it is reserved as the "uninitialised"
    // sentinel and cannot be a valid registration key.
    if (Key.IsZero())
    {
        return ERegisterResult::InvalidKey;
    }

    // Take the exclusive lock for the read-then-write probe.
    ::XCore::HAL::FScopedWriteLock WriteLock(Lock);

    if (const FCustomVersion* Existing = Container.Find(Key))
    {
        // Monotonic-discipline check: reject if the new Version regresses.
        if (Version < Existing->Version)
        {
            return ERegisterResult::Rejected;
        }
        // Version >= existing: overwrite. We use Insert because it
        // handles the "key exists; update in place" path.
        Container.Insert(FCustomVersion(Key, Version, FriendlyName));
        return ERegisterResult::Updated;
    }

    // First registration of this Key.
    Container.Insert(FCustomVersion(Key, Version, FriendlyName));
    return ERegisterResult::Registered;
}

bool FCustomVersionRegistry::UnregisterCustomVersion(FGuid Key) noexcept
{
    ::XCore::HAL::FScopedWriteLock WriteLock(Lock);
    return Container.Remove(Key);
}

const FCustomVersion* FCustomVersionRegistry::GetRegisteredVersion(FGuid Key) const noexcept
{
    // Shared lock for the read. The returned pointer is valid only
    // until the next exclusive lock acquires (i.e., the caller's stack
    // frame). The header documents this.
    ::XCore::HAL::FScopedReadLock ReadLock(Lock);
    return Container.Find(Key);
}

FCustomVersion FCustomVersionRegistry::GetRegisteredVersionCopy(FGuid Key, bool& OutFound) const noexcept
{
    ::XCore::HAL::FScopedReadLock ReadLock(Lock);
    if (const FCustomVersion* Entry = Container.Find(Key))
    {
        OutFound = true;
        return *Entry;
    }
    OutFound = false;
    return FCustomVersion{};
}

bool FCustomVersionRegistry::Contains(FGuid Key) const noexcept
{
    ::XCore::HAL::FScopedReadLock ReadLock(Lock);
    return Container.Contains(Key);
}

::int32 FCustomVersionRegistry::Size() const noexcept
{
    ::XCore::HAL::FScopedReadLock ReadLock(Lock);
    return Container.Size();
}

FCustomVersionContainer FCustomVersionRegistry::GetAllRegistered() const noexcept
{
    ::XCore::HAL::FScopedReadLock ReadLock(Lock);
    // Copy-construct the container. FCustomVersion is trivially
    // copyable, so the copy is a memcpy of the underlying TArray
    // buffer.
    return Container;
}

void FCustomVersionRegistry::EmptyForTesting() noexcept
{
    ::XCore::HAL::FScopedWriteLock WriteLock(Lock);
    Container.Empty();
}

} // namespace XCore::Reflect
