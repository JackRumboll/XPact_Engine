// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectRefSchema.Tests/WalkMapAndSet.cpp -- exercise the schema-
// vector GC walker against the MapOfObject_KeyValue and SetOfObject
// opcodes (XCoreXObject Rev 4 §7.4 + §7.4.1; Phase 5.g').
// =====================================================================
//
// The Phase 5.g' MVP routes Map / Set opcodes through the strided-
// array helper (the underlying SwissTable storage is a flat array of
// {Hash, Key, Value} entries per XCore-4a §5.1 container layout).
// The walker reads the FIRST 8 bytes of each entry as the XObject*
// handle.
//
// Synthesises:
//   * SetOfObject path: a slot with TArrayCore prefix pointing at a
//     3-element XObject* array (stride 8). Walker visits each.
//   * MapOfObject_KeyValue path: a slot with TArrayCore prefix
//     pointing at a 3-element {XObject*, padding} array (stride 16
//     to model a TMap<XPtr<T>, FNullStorage> entry). Walker visits
//     each entry's first-8-bytes (the XObject* key).
//
// =====================================================================

#include "Reflection/FXObjectRefSchema.h"
#include "XObject/FXObjectSchemaWalker.h"
#include "XObject/XObject.h"

#include <cstdint>
#include <cstring>
#include <iostream>
#include <vector>

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
}

int main()
{
    using namespace ::XCore::Reflect;

    // -----------------------------------------------------------------
    // SetOfObject test.
    // -----------------------------------------------------------------
    auto* S0 = reinterpret_cast<::XCore::XObject*>(static_cast<::std::uintptr_t>(0xB001));
    auto* S1 = reinterpret_cast<::XCore::XObject*>(static_cast<::std::uintptr_t>(0xB002));
    auto* S2 = reinterpret_cast<::XCore::XObject*>(static_cast<::std::uintptr_t>(0xB003));

    alignas(8) ::XCore::XObject* SetBuffer[3] = { S0, S1, S2 };
    alignas(8) ::std::uint8_t SetInstance[32] = {};
    {
        void* DataPtr = static_cast<void*>(SetBuffer);
        ::std::int32_t Num = 3;
        ::std::int32_t Max = 3;
        std::memcpy(SetInstance + 0,  &DataPtr, sizeof(DataPtr));
        std::memcpy(SetInstance + 8,  &Num,     sizeof(Num));
        std::memcpy(SetInstance + 12, &Max,     sizeof(Max));
    }

    static constexpr FXObjectRefSchemaOp SetOps[] =
    {
        { EXObjectRefSchemaOp::SetOfObject, 0, 0, 0, 8, 0, nullptr },
        { EXObjectRefSchemaOp::Terminator,  0, 0, 0, 0, 0, nullptr },
    };
    static constexpr FXObjectRefSchema SetSchema =
    {
        /*NumOps=*/2,
        /*Version=*/kFXObjectRefSchemaCurrentVersion,
        /*Ops=*/SetOps,
        /*_padTail=*/0,
    };

    std::vector<::XCore::XObject*> SetVisited;
    ::XCore::WalkSchemaRefsWithSchema(&SetSchema, SetInstance,
        [&](::XCore::XObject* Ref) noexcept
        {
            SetVisited.push_back(Ref);
        });

    Check(SetVisited.size() == 3, "SetOfObject: visit count != 3");
    if (SetVisited.size() == 3)
    {
        Check(SetVisited[0] == S0, "SetOfObject[0] != S0");
        Check(SetVisited[1] == S1, "SetOfObject[1] != S1");
        Check(SetVisited[2] == S2, "SetOfObject[2] != S2");
    }

    // -----------------------------------------------------------------
    // MapOfObject_KeyValue test.
    //
    // We synthesise an array of 16-byte entries (stride 16) where the
    // first 8 bytes of each entry is the XObject* key. The remaining
    // 8 bytes simulate the value half of a TPair<XPtr, V>.
    // -----------------------------------------------------------------
    auto* M0 = reinterpret_cast<::XCore::XObject*>(static_cast<::std::uintptr_t>(0xC001));
    auto* M1 = reinterpret_cast<::XCore::XObject*>(static_cast<::std::uintptr_t>(0xC002));

    alignas(8) ::std::uint8_t MapBuffer[32] = {};
    std::memcpy(MapBuffer +  0, &M0, sizeof(M0));   // entry 0 key
    // entry 0 value at offset 8 (ignored by walker)
    std::memcpy(MapBuffer + 16, &M1, sizeof(M1));   // entry 1 key
    // entry 1 value at offset 24 (ignored)

    alignas(8) ::std::uint8_t MapInstance[32] = {};
    {
        void* DataPtr = static_cast<void*>(MapBuffer);
        ::std::int32_t Num = 2;
        ::std::int32_t Max = 2;
        std::memcpy(MapInstance + 0,  &DataPtr, sizeof(DataPtr));
        std::memcpy(MapInstance + 8,  &Num,     sizeof(Num));
        std::memcpy(MapInstance + 12, &Max,     sizeof(Max));
    }

    static constexpr FXObjectRefSchemaOp MapOps[] =
    {
        { EXObjectRefSchemaOp::MapOfObject_KeyValue, 0, 0, 0, 16, 0, nullptr },
        { EXObjectRefSchemaOp::Terminator,           0, 0, 0,  0, 0, nullptr },
    };
    static constexpr FXObjectRefSchema MapSchema =
    {
        /*NumOps=*/2,
        /*Version=*/kFXObjectRefSchemaCurrentVersion,
        /*Ops=*/MapOps,
        /*_padTail=*/0,
    };

    std::vector<::XCore::XObject*> MapVisited;
    ::XCore::WalkSchemaRefsWithSchema(&MapSchema, MapInstance,
        [&](::XCore::XObject* Ref) noexcept
        {
            MapVisited.push_back(Ref);
        });

    Check(MapVisited.size() == 2,
          "MapOfObject_KeyValue: visit count != 2 (expected 2 keys)");
    if (MapVisited.size() == 2)
    {
        Check(MapVisited[0] == M0, "MapOfObject_KeyValue[0] != M0");
        Check(MapVisited[1] == M1, "MapOfObject_KeyValue[1] != M1");
    }

    if (g_FailureCount > 0)
    {
        std::cerr << "FXObjectRefSchema.WalkMapAndSet: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FXObjectRefSchema.WalkMapAndSet: PASS\n";
    return 0;
}
