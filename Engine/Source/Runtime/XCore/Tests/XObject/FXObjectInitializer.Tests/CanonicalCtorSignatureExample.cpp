// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectInitializer.Tests/CanonicalCtorSignatureExample.cpp
// =====================================================================
//
// XCoreXObject Rev 4 §8.4.1: the canonical XObject subclass ctor
// signature is `T(const FXObjectInitializer& Initializer) :
// XObject(Initializer)`. This test demonstrates a hand-rolled XActor /
// XCharacter-style inheritance chain compiles + constructs.
//
// Phase 5.d test discipline: XHT-emitted StaticClass() is not
// available; we use a per-test FClass instance + bypass NewObject<T>
// in favor of direct constructor invocation. The test validates the
// SHAPE of the canonical signature (it accepts the Initializer by
// const-ref AND can call CreateDefaultSubobject through it).
//
// =====================================================================

#include "XObject/FXObjectInitializer.h"
#include "XObject/FXObjectArray.h"
#include "XObject/NewObject.h"
#include "XObject/XObject.h"
#include "Reflection/FClass.h"
#include "Reflection/FName.h"

#include "HAL/FMemory.h"
#include "HAL/XInitPhase.h"

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

    // Per-test FClass slots. These are constructed at main entry; their
    // FName is set in main() because FName(const char*) is not
    // constexpr (it touches the FNamePool intern table).
    ::XCore::Reflect::FClass* g_XComponentClass = nullptr;
    ::XCore::Reflect::FClass* g_XMeshComponentClass = nullptr;
    ::XCore::Reflect::FClass* g_XActorClass = nullptr;
    ::XCore::Reflect::FClass* g_XCharacterClass = nullptr;
}

namespace
{
    // -----------------------------------------------------------------
    // Hand-rolled XObject subclass hierarchy demonstrating the
    // canonical const-ref Initializer signature.
    //
    // NOTE: real XHT-emitted user classes derive from XObject
    // transitively (XCharacter : XActor : XObject) AND implement
    // StaticClass() / ClassConstructorFn / etc. Phase 5.d ships the
    // initializer surface; this test shows the canonical signature
    // compiles + the const-ref pass-through is functional.
    // -----------------------------------------------------------------

    class XComponent : public ::XCore::XObject
    {
    public:
        explicit XComponent(const ::XCore::FXObjectInitializer& /*Initializer*/) noexcept
        {
            // No member init beyond the XObject default ctor (which
            // the C++ language calls implicitly here).
        }
    };

    class XMeshComponent : public XComponent
    {
    public:
        explicit XMeshComponent(const ::XCore::FXObjectInitializer& Initializer) noexcept
            : XComponent(Initializer)
        {
        }
    };

    class XActor : public ::XCore::XObject
    {
    public:
        explicit XActor(const ::XCore::FXObjectInitializer& Initializer) noexcept
        {
            // XActor would call CreateDefaultSubobject<XSomeComponent>()
            // here in a real implementation. The test exercises the
            // SHAPE of the ctor; the sub-object spawning is covered by
            // CreateDefaultSubobject.cpp.
            (void)Initializer;
        }
    };

    class XCharacter : public XActor
    {
    public:
        ::XCore::XObject* MeshComponent;

        explicit XCharacter(const ::XCore::FXObjectInitializer& Initializer) noexcept
            : XActor(Initializer)         // canonical: pass Initializer to base by const-ref
            , MeshComponent(nullptr)
        {
            // CreateDefaultSubobject via the type-erased path (Phase
            // 5.d posture; XHT-emit will route through the typed
            // template). We invoke the impl directly to demonstrate
            // the const-ref Initializer enables sub-object creation
            // from the user-class ctor body.
            MeshComponent = Initializer.CreateDefaultSubobjectImpl(
                g_XMeshComponentClass,
                ::XCore::Reflect::FName("Mesh"),
                /*bTransient=*/false);
        }
    };
}

int main()
{
    using ::XCore::FXObjectInitializer;
    using ::XCore::FXObjectArray;
    using ::XCore::EObjectFlags;
    using ::XCore::XObject;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();
    ::XCore::HAL::__AdvanceInitPhase(::XCore::HAL::EInitPhase::PostStaticInit);
    FXObjectArray::Get().__ResetForTests();

    // Set up the per-test FClass slots.
    FClass ComponentClass(FName("XComponent"), nullptr);
    FClass MeshComponentClass(FName("XMeshComponent"), &ComponentClass);
    FClass ActorClass(FName("XActor"), nullptr);
    FClass CharacterClass(FName("XCharacter"), &ActorClass);

    g_XComponentClass     = &ComponentClass;
    g_XMeshComponentClass = &MeshComponentClass;
    g_XActorClass         = &ActorClass;
    g_XCharacterClass     = &CharacterClass;

    // Construct an XCharacter via the Phase 5.d path:
    //   1. Allocate raw storage (uninitialised bytes).
    //   2. Placement-new an XCharacter (the C++ ctor signature is
    //      `XCharacter(const FXObjectInitializer&)` per spec §8.4.1).
    //   3. Construct the Initializer FIRST so the XCharacter ctor body
    //      can call CreateDefaultSubobjectImpl through it.
    //
    // For the test we use a stack-allocated buffer (in production
    // FXObjectAllocator + NewObjectImpl wraps this).
    alignas(::XCore::XObject) unsigned char Storage[sizeof(XCharacter)];

    // Pre-initialise the XObject header so the Initializer's ctor
    // (which XPACT_CHECKs non-null Target) sees a valid XObject. The
    // placement-new of XObject is layout-compatible with XCharacter
    // (XCharacter starts with its XActor base which starts with its
    // XObject base; XObject's bytes are the first sizeof(XObject)).
    XObject* AsXObject = new (Storage) XObject();
    AsXObject->ClassPrivate = &CharacterClass;

    XCharacter* Character = nullptr;
    {
        FXObjectInitializer Initializer(AsXObject, &CharacterClass);

        // Placement-new the XCharacter on top of the same storage.
        // First we must end the XObject lifetime (in-place dtor; XObject
        // is trivially destructible per its ABI lock, so the dtor is a
        // no-op + the lifetime ends per [basic.life]/1).
        AsXObject->~XObject();

        // Now placement-new the XCharacter via the canonical signature.
        Character = new (Storage) XCharacter(Initializer);

        Check(Character != nullptr, "XCharacter placement-new returned nullptr");
        Check(Character->MeshComponent != nullptr,
              "XCharacter::Mesh sub-object not created");
        if (Character->MeshComponent != nullptr)
        {
            // The sub-object's Outer is the Initializer's Target -- the
            // XObject we constructed before the Initializer was made.
            Check(Character->MeshComponent->GetClass() == &MeshComponentClass,
                  "Mesh sub-object Class != MeshComponentClass");
            Check(Character->MeshComponent->GetFName() == FName("Mesh"),
                  "Mesh sub-object Name != Mesh");
        }
    }

    // The XCharacter destructor (implicit) does NOT touch the
    // sub-object; clean teardown is the FXObjectArray's concern.
    Character->~XCharacter();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectInitializer.CanonicalCtorSignatureExample: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectInitializer.CanonicalCtorSignatureExample: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
