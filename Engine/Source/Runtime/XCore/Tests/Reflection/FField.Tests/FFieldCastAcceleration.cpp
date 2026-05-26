// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FField.Tests/FFieldCastAcceleration.cpp -- FField IsA + Cast<T>
// hierarchy walk (XCore-4b §5.1 / §5.2).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.1 ("FField base") + Section 5.2 ("FFieldClass").
//
// Verifies the FFieldClass hierarchy walk:
//
//   1. FField::IsA(SelfClass) returns true for an FField whose
//      ClassPrivate IS SelfClass.
//   2. FField::IsA(ParentClass) returns true for an FField whose
//      ClassPrivate's SuperClass chain reaches ParentClass.
//   3. FField::IsA(ChildClass) returns FALSE for an FField whose
//      ClassPrivate is the parent of ChildClass (not the other way
//      around).
//   4. FField::IsA(UnrelatedClass) returns false for cross-hierarchy
//      queries.
//   5. FField::IsA(nullptr) returns false; FField with nullptr
//      ClassPrivate IsA(_) returns false.
//   6. FFieldClass::IsChildOf walks the chain identically.
//
// To exercise the hierarchy walk we build a synthetic three-class
// chain: Field (base) -> MockProperty -> MockSubProperty. The classes
// are stack-local FFieldClass instances populated with the appropriate
// SuperClass pointers and synthetic CastFlags bits. The Phase 4b.3 IsA
// fast-path is the FFieldClass::IsChildOf SuperClass walk (the
// CastFlags-AND fast path lands at Phase 4b.4 once the per-subclass
// bits are populated; this test exercises the structurally-correct
// walk, not the fast-path optimisation).
//
// Independently, we use a second hierarchy (UnrelatedClass with no
// shared ancestor) to verify cross-hierarchy IsA returns false.
//
// =====================================================================

#include "Reflection/EClassCastFlags.h"
#include "Reflection/FField.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FFieldVariant.h"
#include "Reflection/FName.h"

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
    using ::XCore::Reflect::EClassCastFlags;
    using ::XCore::Reflect::FField;
    using ::XCore::Reflect::FFieldClass;
    using ::XCore::Reflect::FFieldVariant;
    using ::XCore::Reflect::FName;
    using ::XCore::Reflect::GetFieldStaticClass;

    // Force lazy-init of kFieldStaticClass.Name.
    const FFieldClass& BaseFieldClass = GetFieldStaticClass();

    // -----------------------------------------------------------------
    // Build a synthetic FFieldClass hierarchy.
    //
    //   BaseFieldClass (kFieldStaticClass)
    //       ^
    //       |
    //   MockPropertyClass
    //       ^
    //       |
    //   MockSubPropertyClass
    //
    //   UnrelatedClass (no parent -- a separate root)
    //
    // The CastFlags bits are synthetic (Phase 4b.4 will assign per-
    // subclass bits per §5.5); for Phase 4b.3 the walk works on any
    // bit pattern (the walk goes through SuperClass; CastFlags is
    // inspected by HasAnyCastFlags but not by IsChildOf in Phase 4b.3).
    //
    // We do still set distinct CastFlags so the HasAnyCastFlags /
    // HasAllCastFlags predicates can be exercised below.
    // -----------------------------------------------------------------
    constexpr EClassCastFlags kMockPropertyBit    = static_cast<EClassCastFlags>(0x100ull);
    constexpr EClassCastFlags kMockSubPropertyBit = static_cast<EClassCastFlags>(0x200ull);
    constexpr EClassCastFlags kUnrelatedBit       = static_cast<EClassCastFlags>(0x400ull);

    FFieldClass MockPropertyClass{
        /* Name       */ FName(),  // FName("MockProperty") would touch the pool;
                                    // not needed for the IsA test (the walk uses
                                    // SuperClass / pointer identity, not Name).
        /* Id         */ ::std::uint64_t(0x1001),
        /* CastFlags  */ kMockPropertyBit,
        /* SuperClass */ &BaseFieldClass,
        /* Construct  */ nullptr,
        /* FakeVTable */ nullptr,
    };

    FFieldClass MockSubPropertyClass{
        FName(),
        ::std::uint64_t(0x1002),
        kMockSubPropertyBit | kMockPropertyBit,  // CastFlags union-of-self-and-ancestors
        &MockPropertyClass,
        nullptr,
        nullptr,
    };

    FFieldClass UnrelatedClass{
        FName(),
        ::std::uint64_t(0x2001),
        kUnrelatedBit,
        nullptr,  // standalone root, no parent
        nullptr,
        nullptr,
    };

    // -----------------------------------------------------------------
    // FFieldClass::IsChildOf hierarchy walk.
    // -----------------------------------------------------------------

    // Identity: every class is a child of itself.
    Check(MockSubPropertyClass.IsChildOf(&MockSubPropertyClass),
          "MockSubPropertyClass.IsChildOf(self) returned false");
    Check(MockPropertyClass.IsChildOf(&MockPropertyClass),
          "MockPropertyClass.IsChildOf(self) returned false");
    Check(BaseFieldClass.IsChildOf(&BaseFieldClass),
          "BaseFieldClass.IsChildOf(self) returned false");

    // Direct parent: MockSubPropertyClass is a child of MockPropertyClass.
    Check(MockSubPropertyClass.IsChildOf(&MockPropertyClass),
          "MockSubPropertyClass.IsChildOf(MockPropertyClass) returned false");

    // Transitive parent: MockSubPropertyClass is a child of BaseFieldClass.
    Check(MockSubPropertyClass.IsChildOf(&BaseFieldClass),
          "MockSubPropertyClass.IsChildOf(BaseFieldClass) returned false");

    // Direct parent (one level): MockPropertyClass is a child of BaseFieldClass.
    Check(MockPropertyClass.IsChildOf(&BaseFieldClass),
          "MockPropertyClass.IsChildOf(BaseFieldClass) returned false");

    // Inverse: parent is NOT a child of its own child.
    Check(!MockPropertyClass.IsChildOf(&MockSubPropertyClass),
          "MockPropertyClass.IsChildOf(MockSubPropertyClass) returned TRUE "
          "(parent must NOT be child of child)");
    Check(!BaseFieldClass.IsChildOf(&MockPropertyClass),
          "BaseFieldClass.IsChildOf(MockPropertyClass) returned TRUE "
          "(parent must NOT be child of child)");

    // Cross-hierarchy: UnrelatedClass shares no ancestor.
    Check(!MockSubPropertyClass.IsChildOf(&UnrelatedClass),
          "MockSubPropertyClass.IsChildOf(UnrelatedClass) returned TRUE "
          "(cross-hierarchy IsA must be false)");
    Check(!UnrelatedClass.IsChildOf(&BaseFieldClass),
          "UnrelatedClass.IsChildOf(BaseFieldClass) returned TRUE "
          "(no shared ancestor)");

    // nullptr: nothing is a child of nullptr.
    Check(!MockSubPropertyClass.IsChildOf(nullptr),
          "MockSubPropertyClass.IsChildOf(nullptr) returned true");
    Check(!BaseFieldClass.IsChildOf(nullptr),
          "BaseFieldClass.IsChildOf(nullptr) returned true");

    // -----------------------------------------------------------------
    // FField::IsA(target) wraps ClassPrivate->IsChildOf(target).
    //
    // Construct FFields tagged with each class and verify IsA mirrors
    // the IsChildOf results.
    // -----------------------------------------------------------------
    {
        FField BaseInstance{&BaseFieldClass, FFieldVariant{}, FName()};
        FField MockPropInstance{&MockPropertyClass, FFieldVariant{}, FName()};
        FField MockSubInstance{&MockSubPropertyClass, FFieldVariant{}, FName()};
        FField UnrelatedInstance{&UnrelatedClass, FFieldVariant{}, FName()};

        // Each instance IsA its own ClassPrivate.
        Check(BaseInstance.IsA(&BaseFieldClass),
              "BaseInstance.IsA(BaseFieldClass) returned false");
        Check(MockPropInstance.IsA(&MockPropertyClass),
              "MockPropInstance.IsA(MockPropertyClass) returned false");
        Check(MockSubInstance.IsA(&MockSubPropertyClass),
              "MockSubInstance.IsA(MockSubPropertyClass) returned false");

        // MockSubInstance IsA every ancestor.
        Check(MockSubInstance.IsA(&MockPropertyClass),
              "MockSubInstance.IsA(MockPropertyClass) returned false");
        Check(MockSubInstance.IsA(&BaseFieldClass),
              "MockSubInstance.IsA(BaseFieldClass) returned false");

        // MockPropInstance IsA BaseFieldClass but NOT MockSubPropertyClass.
        Check(MockPropInstance.IsA(&BaseFieldClass),
              "MockPropInstance.IsA(BaseFieldClass) returned false");
        Check(!MockPropInstance.IsA(&MockSubPropertyClass),
              "MockPropInstance.IsA(MockSubPropertyClass) returned TRUE "
              "(parent must NOT IsA its child)");

        // Cross-hierarchy IsA returns false.
        Check(!MockSubInstance.IsA(&UnrelatedClass),
              "MockSubInstance.IsA(UnrelatedClass) returned TRUE");
        Check(!UnrelatedInstance.IsA(&BaseFieldClass),
              "UnrelatedInstance.IsA(BaseFieldClass) returned TRUE");

        // nullptr-target IsA returns false.
        Check(!MockSubInstance.IsA(nullptr),
              "MockSubInstance.IsA(nullptr) returned true");

        // FField with nullptr ClassPrivate IsA(_) returns false.
        FField NoClassInstance;  // default ctor; ClassPrivate is nullptr.
        Check(!NoClassInstance.IsA(&BaseFieldClass),
              "FField with nullptr ClassPrivate IsA(_) returned true");
    }

    // -----------------------------------------------------------------
    // HasAnyCastFlags / HasAllCastFlags.
    //
    // MockSubPropertyClass.CastFlags is (kMockSubPropertyBit | kMockPropertyBit).
    // HasAnyCastFlags(kMockSubPropertyBit) -> true.
    // HasAnyCastFlags(kMockPropertyBit)    -> true.
    // HasAnyCastFlags(kUnrelatedBit)       -> false.
    // HasAllCastFlags(kMockSubPropertyBit | kMockPropertyBit) -> true.
    // HasAllCastFlags(kMockSubPropertyBit | kUnrelatedBit)    -> false.
    // -----------------------------------------------------------------
    {
        Check(MockSubPropertyClass.HasAnyCastFlags(kMockSubPropertyBit),
              "MockSubPropertyClass.HasAnyCastFlags(SubBit) returned false");
        Check(MockSubPropertyClass.HasAnyCastFlags(kMockPropertyBit),
              "MockSubPropertyClass.HasAnyCastFlags(PropBit) returned false");
        Check(!MockSubPropertyClass.HasAnyCastFlags(kUnrelatedBit),
              "MockSubPropertyClass.HasAnyCastFlags(UnrelatedBit) returned true");

        Check(MockSubPropertyClass.HasAllCastFlags(
                  kMockSubPropertyBit | kMockPropertyBit),
              "MockSubPropertyClass.HasAllCastFlags(Sub|Prop) returned false");
        Check(!MockSubPropertyClass.HasAllCastFlags(
                  kMockSubPropertyBit | kUnrelatedBit),
              "MockSubPropertyClass.HasAllCastFlags(Sub|Unrelated) returned true");
    }

    if (g_FailureCount > 0)
    {
        std::cerr << "FField.FFieldCastAcceleration: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FField.FFieldCastAcceleration: PASS\n";
    return 0;
}
