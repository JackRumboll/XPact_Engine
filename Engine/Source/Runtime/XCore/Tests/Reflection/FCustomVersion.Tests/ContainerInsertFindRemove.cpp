// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FCustomVersion.Tests/ContainerInsertFindRemove.cpp -- container API.
// =====================================================================
//
// XCore-4b Rev 3, Section 8.2. Exercises:
//
//   * Insert produces a sorted-by-Key array.
//   * Find returns nullptr for absent keys, the correct entry for
//     present keys.
//   * GetVersion returns INT32_MIN for absent keys.
//   * Remove erases the entry and preserves sorted order.
//   * Contains / HasVersion agree with Find result.
//   * Range-based for-loop walks in sorted order.
//   * Insert with an existing Key overwrites the Version / FriendlyName.
//   * Empty resets the container.
//
// Sorted-order is asserted by walking the post-state container and
// checking that each Key is strictly greater than the previous.
//
// =====================================================================

#include "Reflection/FCustomVersionContainer.h"
#include "Reflection/FCustomVersion.h"
#include "Reflection/FGuid.h"

#include <iostream>
#include <limits>

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

    using ::XCore::Reflect::FCustomVersion;
    using ::XCore::Reflect::FCustomVersionContainer;
    using ::XCore::Reflect::FGuid;
    using ::XCore::Reflect::FName;

    // Helper: produce a stable FName from a small integer. Phase 4b.2
    // tests run before FName's intern-table-bootstrap is guaranteed in
    // the test harness, so we use the (Index, SerialNumber) ctor
    // directly. These "FName" values are not actually interned -- we
    // just need a distinguishable 8-byte handle. Containers and
    // registry treat FName as an opaque 8-byte payload; the round-trip
    // tests verify byte-exact preservation, not intern-table
    // resolution.
    FName MakeFakeFName(::uint32 Index, ::uint32 Serial = 0)
    {
        return FName(Index, Serial);
    }
}

int main()
{
    FCustomVersionContainer Container;

    Check(Container.IsEmpty(), "default-constructed container not empty");
    Check(Container.Size() == 0, "default size != 0");
    Check(Container.Find(FGuid{1, 2, 3, 4}) == nullptr,
          "Find on empty container did not return nullptr");
    Check(!Container.Contains(FGuid{1, 2, 3, 4}),
          "Contains on empty container returned true");
    Check(!Container.HasVersion(FGuid{1, 2, 3, 4}),
          "HasVersion on empty container returned true");

    // Insert in REVERSE-sorted order to exercise the bubble-down path.
    Container.Insert(FCustomVersion(FGuid{ 100, 0, 0, 0 }, 1, MakeFakeFName(10)));
    Container.Insert(FCustomVersion(FGuid{  50, 0, 0, 0 }, 2, MakeFakeFName(20)));
    Container.Insert(FCustomVersion(FGuid{ 200, 0, 0, 0 }, 3, MakeFakeFName(30)));
    Container.Insert(FCustomVersion(FGuid{   1, 0, 0, 0 }, 4, MakeFakeFName(40)));
    Container.Insert(FCustomVersion(FGuid{ 150, 0, 0, 0 }, 5, MakeFakeFName(50)));

    Check(Container.Size() == 5, "size after 5 inserts != 5");
    Check(!Container.IsEmpty(), "non-empty container reports IsEmpty");

    // Walk the container and verify ascending Key order.
    {
        const FGuid* Previous = nullptr;
        for (const FCustomVersion& Entry : Container)
        {
            if (Previous != nullptr)
            {
                Check(*Previous < Entry.Key,
                      "container iteration order is not strictly ascending");
            }
            Previous = &Entry.Key;
        }
    }

    // Find each Key and verify the stored Version / FriendlyName.
    Check(Container.GetVersion(FGuid{   1, 0, 0, 0 }) == 4, "GetVersion(1) != 4");
    Check(Container.GetVersion(FGuid{  50, 0, 0, 0 }) == 2, "GetVersion(50) != 2");
    Check(Container.GetVersion(FGuid{ 100, 0, 0, 0 }) == 1, "GetVersion(100) != 1");
    Check(Container.GetVersion(FGuid{ 150, 0, 0, 0 }) == 5, "GetVersion(150) != 5");
    Check(Container.GetVersion(FGuid{ 200, 0, 0, 0 }) == 3, "GetVersion(200) != 3");

    if (const FCustomVersion* Entry = Container.Find(FGuid{ 100, 0, 0, 0 }))
    {
        Check(Entry->FriendlyName == MakeFakeFName(10),
              "Find(100).FriendlyName != fake-name 10");
    }
    else
    {
        Check(false, "Find(100) returned nullptr");
    }

    // Find for absent key returns nullptr; GetVersion returns INT32_MIN.
    Check(Container.Find(FGuid{ 999, 0, 0, 0 }) == nullptr,
          "Find(999) did not return nullptr");
    Check(Container.GetVersion(FGuid{ 999, 0, 0, 0 }) == ::std::numeric_limits<::int32>::min(),
          "GetVersion on absent key did not return INT32_MIN sentinel");

    // Overwrite an existing entry.
    Container.Insert(FCustomVersion(FGuid{ 100, 0, 0, 0 }, 99, MakeFakeFName(100)));
    Check(Container.Size() == 5,
          "Overwrite Insert changed container size (should be no-op for size)");
    Check(Container.GetVersion(FGuid{ 100, 0, 0, 0 }) == 99,
          "Overwrite did not update Version");
    if (const FCustomVersion* Entry = Container.Find(FGuid{ 100, 0, 0, 0 }))
    {
        Check(Entry->FriendlyName == MakeFakeFName(100),
              "Overwrite did not update FriendlyName");
    }
    else
    {
        Check(false, "Find(100) returned nullptr after overwrite");
    }

    // SetVersion (convenience wrapper for Insert).
    Container.SetVersion(FGuid{ 75, 0, 0, 0 }, 7, MakeFakeFName(70));
    Check(Container.Size() == 6, "SetVersion did not add a new entry");
    Check(Container.GetVersion(FGuid{ 75, 0, 0, 0 }) == 7,
          "SetVersion did not set the Version correctly");

    // Verify sorted order still holds after the SetVersion insert.
    {
        const FGuid* Previous = nullptr;
        for (const FCustomVersion& Entry : Container)
        {
            if (Previous != nullptr)
            {
                Check(*Previous < Entry.Key,
                      "container iteration order broken after SetVersion");
            }
            Previous = &Entry.Key;
        }
    }

    // Remove.
    Check(Container.Remove(FGuid{ 50, 0, 0, 0 }),
          "Remove of existing key returned false");
    Check(Container.Size() == 5, "size after Remove != 5");
    Check(Container.Find(FGuid{ 50, 0, 0, 0 }) == nullptr,
          "Find on removed key did not return nullptr");
    Check(!Container.Contains(FGuid{ 50, 0, 0, 0 }),
          "Contains on removed key returned true");

    // Remove of absent key returns false.
    Check(!Container.Remove(FGuid{ 999, 0, 0, 0 }),
          "Remove of absent key did not return false");
    Check(Container.Size() == 5, "size changed after Remove of absent key");

    // Verify sorted order still holds after Remove.
    {
        const FGuid* Previous = nullptr;
        for (const FCustomVersion& Entry : Container)
        {
            if (Previous != nullptr)
            {
                Check(*Previous < Entry.Key,
                      "container iteration order broken after Remove");
            }
            Previous = &Entry.Key;
        }
    }

    // Empty.
    Container.Empty();
    Check(Container.IsEmpty(), "Empty() did not clear container");
    Check(Container.Size() == 0, "Size after Empty != 0");
    Check(Container.Find(FGuid{ 100, 0, 0, 0 }) == nullptr,
          "Find succeeded on emptied container");

    // GetAllVersions returns the underlying TArray; verify it's empty
    // after Empty().
    Check(Container.GetAllVersions().Num() == 0,
          "GetAllVersions on empty container returned non-empty");

    if (g_FailureCount > 0)
    {
        std::cerr << "FCustomVersion.ContainerInsertFindRemove: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FCustomVersion.ContainerInsertFindRemove: PASS\n";
    return 0;
}
