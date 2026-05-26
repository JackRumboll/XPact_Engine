// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FStruct.Tests/FScriptStructDispatch.cpp -- FScriptStruct's
// ICppStructOps FakeVTable dispatch (XCore-4b §7.2 + FIX-R2-MAJ-2).
// =====================================================================
//
// Constructs a mock FCppStructOpsFakeVTable with two populated slots
// (NetSerialize + GetTypeHash) and an FScriptStruct that points at it;
// verifies:
//
//   1. HasCapability returns true for declared capabilities + false
//      for undeclared.
//   2. GetSlot<T> resolves the correct function pointer; calling it
//      produces the expected output.
//   3. The HasNetSerializer + HasGetTypeHash capability bits map to
//      the right slots (slot 3 + slot 2).
//   4. Capability-only bits (IsPlainOldData, HasIdentical) work
//      without a corresponding slot.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/FCppStructOpsFakeVTable.h"
#include "Reflection/FName.h"
#include "Reflection/FScriptStruct.h"

#include <cstdint>
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

    // -----------------------------------------------------------------
    // Mock dispatch slot bodies.
    // -----------------------------------------------------------------

    // NetSerialize signature: matches a plausible Iris-routed
    // bool(*)(void* Ar, void* Map, void* Value) shape.
    using FNetSerializeFn = bool (*)(void* Ar, void* Map, void* Value);
    bool MockNetSerialize(void* /*Ar*/, void* /*Map*/, void* Value) noexcept
    {
        // Trivial body: write a sentinel into *Value to prove the
        // dispatch reached us.
        if (Value != nullptr)
        {
            *static_cast<int*>(Value) = 0xCAFEBABE;
        }
        return true;
    }

    // GetTypeHash signature: uint64(*)(const void*).
    using FGetTypeHashFn = ::uint64_t (*)(const void* Value);
    ::uint64_t MockGetTypeHash(const void* Value) noexcept
    {
        if (Value == nullptr)
        {
            return 0xDEADBEEFULL;
        }
        return static_cast<::uint64_t>(*static_cast<const int*>(Value)) | 0xCAFE0000ULL;
    }
}

int main()
{
    using namespace ::XCore::Reflect;

    ::XCore::HAL::FMemory::__Init();

    // -----------------------------------------------------------------
    // Build a mock FCppStructOpsFakeVTable in static storage.
    // -----------------------------------------------------------------
    static const FCppStructOpsFakeVTable kMockTable{
        /* Capabilities */ static_cast<::uint32>(
              ECppStructOpsCapability::HasNetSerializer
            | ECppStructOpsCapability::HasGetTypeHash
            | ECppStructOpsCapability::IsPlainOldData
            | ECppStructOpsCapability::HasIdentical),
        /* _reservedHeader */ 0U,
        /* Slots */ {
            nullptr,                                  // 0  PostScriptConstruct
            nullptr,                                  // 1  AddStructReferencedObjects
            reinterpret_cast<void(*)(void)>(&MockGetTypeHash),  // 2  GetTypeHash
            reinterpret_cast<void(*)(void)>(&MockNetSerialize), // 3  NetSerialize
            nullptr, nullptr, nullptr, nullptr,       // 4-7
            nullptr, nullptr, nullptr, nullptr,       // 8-11
            nullptr, nullptr, nullptr, nullptr,       // 12-15
        },
    };

    // -----------------------------------------------------------------
    // Construct an FScriptStruct pointing at the mock table.
    // -----------------------------------------------------------------
    FScriptStruct ScriptStruct(
        FName("TestScriptStruct"), nullptr,
        kMockTable.Capabilities, &kMockTable);

    // -----------------------------------------------------------------
    // Capability probing.
    // -----------------------------------------------------------------
    Check(ScriptStruct.HasCapability(ECppStructOpsCapability::HasNetSerializer),
          "HasNetSerializer capability missing");
    Check(ScriptStruct.HasCapability(ECppStructOpsCapability::HasGetTypeHash),
          "HasGetTypeHash capability missing");
    Check(ScriptStruct.HasCapability(ECppStructOpsCapability::IsPlainOldData),
          "IsPlainOldData capability missing");
    Check(ScriptStruct.HasCapability(ECppStructOpsCapability::HasIdentical),
          "HasIdentical capability missing");
    Check(!ScriptStruct.HasCapability(ECppStructOpsCapability::HasCopy),
          "HasCopy capability erroneously set");
    Check(!ScriptStruct.HasCapability(ECppStructOpsCapability::HasNetDeltaSerializer),
          "HasNetDeltaSerializer capability erroneously set");

    // -----------------------------------------------------------------
    // FCppStructOpsFakeVTable::HasSlot (direct null-check).
    // -----------------------------------------------------------------
    Check(kMockTable.HasSlot(ECppOpSlot::GetTypeHash),
          "Slot 2 GetTypeHash not populated");
    Check(kMockTable.HasSlot(ECppOpSlot::NetSerialize),
          "Slot 3 NetSerialize not populated");
    Check(!kMockTable.HasSlot(ECppOpSlot::PostScriptConstruct),
          "Slot 0 erroneously populated");

    // -----------------------------------------------------------------
    // GetSlot<T> + dispatch round-trip.
    // -----------------------------------------------------------------
    {
        FGetTypeHashFn HashFn = ScriptStruct.GetSlot<FGetTypeHashFn>(ECppOpSlot::GetTypeHash);
        Check(HashFn != nullptr, "GetSlot<FGetTypeHashFn>(GetTypeHash) returned nullptr");
        if (HashFn != nullptr)
        {
            const int Value = 0x1234;
            const ::uint64_t Hash = HashFn(&Value);
            Check(Hash == (0xCAFE0000ULL | 0x1234), "GetTypeHash dispatch produced wrong value");
        }
    }

    {
        FNetSerializeFn NetSerFn = ScriptStruct.GetSlot<FNetSerializeFn>(ECppOpSlot::NetSerialize);
        Check(NetSerFn != nullptr, "GetSlot<FNetSerializeFn>(NetSerialize) returned nullptr");
        if (NetSerFn != nullptr)
        {
            int Buffer = 0;
            const bool OK = NetSerFn(nullptr, nullptr, &Buffer);
            Check(OK, "NetSerialize dispatch returned false");
            Check(Buffer == static_cast<int>(0xCAFEBABE),
                  "NetSerialize dispatch did not write sentinel");
        }
    }

    // -----------------------------------------------------------------
    // GetSlot on an unpopulated slot returns nullptr.
    // -----------------------------------------------------------------
    {
        auto* MissingFn = ScriptStruct.GetSlot<FNetSerializeFn>(ECppOpSlot::PostScriptConstruct);
        Check(MissingFn == nullptr, "GetSlot on unpopulated slot did not return nullptr");
    }

    // -----------------------------------------------------------------
    // The capability bitmask sized correctly (32 bits).
    // -----------------------------------------------------------------
    Check(sizeof(ECppStructOpsCapability) == 4, "ECppStructOpsCapability not uint32");

    if (g_FailureCount > 0)
    {
        std::cerr << "FStruct.FScriptStructDispatch: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FStruct.FScriptStructDispatch: PASS\n";
    return 0;
}
