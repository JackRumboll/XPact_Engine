// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XObject.Tests/Construction.cpp -- default-ctor + consteval-ctor
// behaviour for XObject (XCoreXObject Rev 4 §2.2).
// =====================================================================
//
// Verifies the default-ctor zero-initialisation of every XObject
// field + the consteval CDO bootstrap ctor's constant-evaluated
// construction path.
//
// =====================================================================

#include "Reflection/FName.h"
#include "XObject/EObjectFlags.h"
#include "XObject/XObject.h"

#include "HAL/FMemory.h"                    // FMemory::__Init() for FName intern table

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
}

int main()
{
    using ::XCore::EObjectFlags;
    using ::XCore::XObject;
    using ::XCore::Reflect::FName;

    // The FName intern table requires FMemory live (matches the FName
    // test discipline at Tests/Reflection/FName.Tests/*).
    ::XCore::HAL::FMemory::__Init();

    // -----------------------------------------------------------------
    // Default-ctor zero-initialisation (per spec §2.2 + Phase 5.a
    // header implementation).
    // -----------------------------------------------------------------
    {
        XObject Obj;
        Check(Obj.GetClass()              == nullptr,
              "default-ctor: GetClass() != nullptr");
        Check(Obj.GetOuter()              == nullptr,
              "default-ctor: GetOuter() != nullptr");
        Check(Obj.GetFName()              == FName(),
              "default-ctor: GetFName() != NAME_None");
        Check(Obj.GetName()               == FName(),
              "default-ctor: GetName() != NAME_None");
        Check(Obj.GetInternalIndex()      == ::INDEX_NONE,
              "default-ctor: GetInternalIndex() != INDEX_NONE");
        Check(Obj.GetSerialNumber()       == 0u,
              "default-ctor: GetSerialNumber() != 0");
        Check(Obj.GetObjectFlags()        == EObjectFlags::None,
              "default-ctor: GetObjectFlags() != None");
        Check(!Obj.IsMarkedAsGarbage(),
              "default-ctor: IsMarkedAsGarbage() == true");
        Check(!Obj.HasAnyFlags(EObjectFlags::Transient),
              "default-ctor: HasAnyFlags(Transient) == true");
        Check(Obj.HasAllFlags(EObjectFlags::None),
              "default-ctor: HasAllFlags(None) == false (should be vacuously true)");

        // ReachabilityFlag should be zero (Rev 3 per FIX-M-R2-3; not
        // marked by any cycle yet).
        Check(Obj.ReachabilityFlag.load(::std::memory_order_acquire) == 0u,
              "default-ctor: ReachabilityFlag != 0");

        // Cluster reservation should be zero (Phase 2 not active).
        Check(Obj._reservedCluster0 == 0ull, "default-ctor: _reservedCluster0 != 0");
        Check(Obj._reservedCluster1 == 0ull, "default-ctor: _reservedCluster1 != 0");
    }

    // -----------------------------------------------------------------
    // Consteval CDO bootstrap ctor: constant-evaluated XObject
    // construction with an explicit (nullptr cls, nullptr outer,
    // NAME_None, flags=0) tuple. The consteval qualifier guarantees
    // the call happens at compile time.
    //
    // NOTE: we cannot use a real FClass* here (none exist at Phase 5.a;
    // the FClass static initialisers are XHT-emitted at .gen.cpp time).
    // The consteval ctor with nullptr cls is the unit-test exercise of
    // the constant-evaluation path itself.
    // -----------------------------------------------------------------
    {
        // The XObject must be constructed at constant-evaluation time
        // for the consteval ctor to be valid. We exercise this via a
        // constexpr lambda (the lambda body is constant-evaluated at
        // the call-site if all inputs are constants).
        //
        // Since FName(uint32, uint32) IS constexpr (per FName.h Phase
        // 4b.1 signature), constructing NAME_None as the FName input is
        // allowed in the consteval context.

        // We construct a constinit XObject directly to exercise the
        // consteval ctor; the variable lives in static storage and the
        // initialiser must be constant-evaluated.
        //
        // NOTE: the XObject is non-copyable + non-movable (deleted
        // copy/move ops), so we cannot return one from a constexpr
        // function. The constinit-storage form is the canonical
        // exercise.

        static constinit XObject CDOExemplar{
            /*cls=*/   static_cast<const ::XCore::Reflect::FClass*>(nullptr),
            /*outer=*/ static_cast<XObject*>(nullptr),
            /*name=*/  FName(),
            /*flags=*/ ::std::uint32_t(0)
        };

        // The fields should match what the consteval ctor body set.
        Check(CDOExemplar.GetClass()         == nullptr,
              "consteval ctor: GetClass() != nullptr");
        Check(CDOExemplar.GetOuter()         == nullptr,
              "consteval ctor: GetOuter() != nullptr");
        Check(CDOExemplar.GetFName()         == FName(),
              "consteval ctor: GetFName() != NAME_None");
        Check(CDOExemplar.GetInternalIndex() == ::INDEX_NONE,
              "consteval ctor: GetInternalIndex() != INDEX_NONE");
        Check(CDOExemplar.GetSerialNumber()  == 0u,
              "consteval ctor: GetSerialNumber() != 0");
        Check(CDOExemplar.GetObjectFlags()   == EObjectFlags::None,
              "consteval ctor: GetObjectFlags() != None");
        Check(CDOExemplar.ReachabilityFlag.load(::std::memory_order_acquire) == 0u,
              "consteval ctor: ReachabilityFlag != 0");
    }

    // -----------------------------------------------------------------
    // Field write + accessor read round-trip (the public fields are
    // intentionally writeable so XHT-emitted .gen.cpp can populate
    // CDOs via designated initialisers and FXObjectAllocator can fill
    // the header directly during NewObject).
    // -----------------------------------------------------------------
    {
        XObject Obj;
        Obj.InternalIndex = 5;
        Obj.SerialNumber  = 7;
        Obj.NamePrivate   = FName(99u, 0u);  // arbitrary Index for test
        Check(Obj.GetInternalIndex() == 5,
              "field-write round-trip: InternalIndex");
        Check(Obj.GetSerialNumber()  == 7u,
              "field-write round-trip: SerialNumber");
        Check(Obj.GetFName().GetIndex() == 99u,
              "field-write round-trip: NamePrivate.Index");
    }

    if (g_FailureCount == 0)
    {
        std::cout << "XObject.Construction: PASS\n";
        return 0;
    }
    std::cerr << "XObject.Construction: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
