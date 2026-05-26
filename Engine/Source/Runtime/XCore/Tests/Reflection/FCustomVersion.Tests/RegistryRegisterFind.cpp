// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FCustomVersion.Tests/RegistryRegisterFind.cpp -- gate D3.
// =====================================================================
//
// XCore-4b Rev 3, Section 13 Acceptance gate D3 (partial):
//
//   "D3. FCustomVersion registration: a module that registers a custom
//    version at module init is consultable via
//    FCustomVersionRegistry::FindVersion(Key)..."
//
// This test exercises the single-threaded registration path. The
// multi-thread stress test lives in a separate file
// (RegistryConcurrentStress.cpp) so the failure modes are
// distinguishable in test output.
//
// EXERCISED PATHS:
//
//   * Register a fresh GUID -> Registered.
//   * Register the same GUID with a higher Version -> Updated.
//   * Register the same GUID with the same Version -> Updated.
//   * Register the same GUID with a LOWER Version -> Rejected; state
//     unchanged.
//   * Register the zero GUID -> InvalidKey; state unchanged.
//   * GetRegisteredVersion returns nullptr for absent Keys.
//   * GetRegisteredVersionCopy round-trips the entry.
//   * Unregister removes the entry.
//   * GetAllRegistered returns a snapshot copy.
//
// =====================================================================

#include "Reflection/FCustomVersionRegistry.h"
#include "Reflection/FCustomVersion.h"
#include "Reflection/FCustomVersionContainer.h"
#include "Reflection/FGuid.h"

#include <iostream>

namespace
{
    int g_FailureCount = 0;

    void Check(bool Condition, const char* Diagnostic)
    {
        if (!Condition)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++g_FailureCount;
        }
    }

    using ::XCore::Reflect::ERegisterResult;
    using ::XCore::Reflect::FCustomVersion;
    using ::XCore::Reflect::FCustomVersionContainer;
    using ::XCore::Reflect::FCustomVersionRegistry;
    using ::XCore::Reflect::FGuid;
    using ::XCore::Reflect::FName;
}

int main()
{
    FCustomVersionRegistry& Registry = FCustomVersionRegistry::Get();

    // Start clean -- the test owns the registry for this run.
    Registry.EmptyForTesting();
    Check(Registry.Size() == 0, "EmptyForTesting did not clear the registry");

    // Invalid (zero) GUID is rejected with InvalidKey.
    {
        const ERegisterResult Result = Registry.RegisterCustomVersion(
            FGuid{0, 0, 0, 0}, 1, FName(0xCAFE, 0));
        Check(Result == ERegisterResult::InvalidKey,
              "Register with zero GUID did not return InvalidKey");
        Check(Registry.Size() == 0, "Registry size changed despite InvalidKey");
    }

    // First registration of a Key.
    const FGuid Key1{0x11111111, 0x22222222, 0x33333333, 0x44444444};
    const FName Name1(0x100, 0);
    {
        const ERegisterResult Result = Registry.RegisterCustomVersion(Key1, 5, Name1);
        Check(Result == ERegisterResult::Registered,
              "First registration did not return Registered");
    }
    Check(Registry.Size() == 1, "Registry size after first registration != 1");
    Check(Registry.Contains(Key1), "Contains(Key1) returned false");

    // GetRegisteredVersion returns the entry.
    {
        // NOTE: we capture the value (not pointer) because the pointer
        // returned by GetRegisteredVersion is valid only inside the
        // call scope (the read lock is released on return). Holding
        // the pointer beyond that is racy in a multi-thread scenario;
        // single-threaded test code is safe but we use the Copy
        // variant to validate that path too.
        bool bFound = false;
        const FCustomVersion Copy = Registry.GetRegisteredVersionCopy(Key1, bFound);
        Check(bFound, "GetRegisteredVersionCopy on registered key did not set OutFound=true");
        Check(Copy.Key == Key1, "Copy.Key != Key1");
        Check(Copy.Version == 5, "Copy.Version != 5");
        Check(Copy.FriendlyName == Name1, "Copy.FriendlyName != Name1");
    }

    // Same-version re-registration is Updated (overwrite path).
    {
        const ERegisterResult Result = Registry.RegisterCustomVersion(Key1, 5, FName(0x999, 0));
        Check(Result == ERegisterResult::Updated,
              "Same-Version re-registration did not return Updated");
        bool bFound = false;
        const FCustomVersion Copy = Registry.GetRegisteredVersionCopy(Key1, bFound);
        Check(bFound && Copy.FriendlyName == FName(0x999, 0),
              "Updated did not overwrite FriendlyName");
    }

    // Higher-version re-registration is Updated.
    {
        const ERegisterResult Result = Registry.RegisterCustomVersion(Key1, 6, Name1);
        Check(Result == ERegisterResult::Updated,
              "Higher-Version re-registration did not return Updated");
        bool bFound = false;
        const FCustomVersion Copy = Registry.GetRegisteredVersionCopy(Key1, bFound);
        Check(bFound && Copy.Version == 6,
              "GetRegisteredVersionCopy did not return Version=6 after Updated");
    }

    // Lower-version re-registration is Rejected.
    {
        const ERegisterResult Result = Registry.RegisterCustomVersion(Key1, 4, FName(0xAAA, 0));
        Check(Result == ERegisterResult::Rejected,
              "Lower-Version re-registration did not return Rejected");
        bool bFound = false;
        const FCustomVersion Copy = Registry.GetRegisteredVersionCopy(Key1, bFound);
        Check(bFound && Copy.Version == 6,
              "Rejected re-registration changed the state (Version should still be 6)");
        Check(Copy.FriendlyName == Name1,
              "Rejected re-registration changed FriendlyName");
    }

    // Register a second key.
    const FGuid Key2{0xAAAAAAAA, 0xBBBBBBBB, 0xCCCCCCCC, 0xDDDDDDDD};
    {
        const ERegisterResult Result = Registry.RegisterCustomVersion(Key2, 1, FName(0x200, 0));
        Check(Result == ERegisterResult::Registered, "Register Key2 did not return Registered");
        Check(Registry.Size() == 2, "Registry size != 2 after two registrations");
    }

    // GetRegisteredVersion for absent key returns nullptr.
    {
        const FCustomVersion* Absent =
            Registry.GetRegisteredVersion(FGuid{0xDEAD, 0xBEEF, 0, 0});
        Check(Absent == nullptr, "GetRegisteredVersion on absent key did not return nullptr");

        bool bFound = true;
        const FCustomVersion Copy =
            Registry.GetRegisteredVersionCopy(FGuid{0xDEAD, 0xBEEF, 0, 0}, bFound);
        Check(!bFound, "GetRegisteredVersionCopy on absent key did not set OutFound=false");
        Check(Copy == FCustomVersion{},
              "GetRegisteredVersionCopy on absent key did not return zero-init record");
    }

    // GetAllRegistered returns a snapshot.
    {
        const FCustomVersionContainer Snapshot = Registry.GetAllRegistered();
        Check(Snapshot.Size() == 2, "snapshot size != 2");
        Check(Snapshot.Contains(Key1), "snapshot missing Key1");
        Check(Snapshot.Contains(Key2), "snapshot missing Key2");
    }

    // Unregister removes the entry.
    {
        const bool bRemoved = Registry.UnregisterCustomVersion(Key2);
        Check(bRemoved, "UnregisterCustomVersion returned false for registered key");
        Check(Registry.Size() == 1, "Registry size != 1 after unregister");
        Check(!Registry.Contains(Key2), "Registry still contains Key2 after unregister");
    }

    // Unregister of absent key returns false.
    {
        const bool bRemoved = Registry.UnregisterCustomVersion(FGuid{0xDEAD, 0xBEEF, 0, 0});
        Check(!bRemoved, "UnregisterCustomVersion on absent key returned true");
    }

    // Clean up.
    Registry.EmptyForTesting();
    Check(Registry.Size() == 0, "Registry not empty after final cleanup");

    if (g_FailureCount > 0)
    {
        std::cerr << "FCustomVersion.RegistryRegisterFind: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FCustomVersion.RegistryRegisterFind: PASS\n";
    return 0;
}
