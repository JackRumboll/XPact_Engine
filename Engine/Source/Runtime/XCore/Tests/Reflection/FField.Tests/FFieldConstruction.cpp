// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FField.Tests/FFieldConstruction.cpp -- FField ctor + accessor surface
// (XCore-4b §5.1; acceptance gate B1 coverage).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.1 ("FField base").
//
// Verifies:
//
//   1. Default-constructed FField: every field is zero-initialised
//      (ClassPrivate nullptr, Owner null, Next nullptr, NamePrivate
//      NAME_None).
//   2. Explicit-ctor FField(class*, owner, name) populates the four
//      fields as passed. Next is left as nullptr.
//   3. Accessors (GetClass, GetOwner, GetNext, GetFName) return the
//      stored values.
//   4. SetNext sets the Next field; subsequent GetNext returns the new
//      value.
//   5. The kFieldStaticClass instance is reachable via
//      GetFieldStaticClass() and has the expected shape:
//        * Name == FName("Field") (post-init).
//        * SuperClass == nullptr.
//        * CastFlags == EClassCastFlags::kNone.
//        * Construct != nullptr (points at FField::ConstructField).
//        * FakeVTable == nullptr.
//   6. FField::ConstructField placement-new-creates a valid FField at
//      caller-provided storage with the expected ClassPrivate, Owner,
//      Name, and nullptr Next.
//
// =====================================================================

#include "Reflection/EClassCastFlags.h"
#include "Reflection/FField.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FFieldVariant.h"
#include "Reflection/FName.h"

#include <cstddef>
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

    // -----------------------------------------------------------------
    // (1) Default-constructed FField.
    // -----------------------------------------------------------------
    {
        FField F;

        Check(F.GetClass() == nullptr,
              "Default-ctor FField GetClass() != nullptr");
        Check(F.GetOwner().IsNull(),
              "Default-ctor FField GetOwner() is not null");
        Check(F.GetOwner().AsField() == nullptr,
              "Default-ctor FField GetOwner().AsField() != nullptr");
        Check(F.GetOwner().AsStruct() == nullptr,
              "Default-ctor FField GetOwner().AsStruct() != nullptr");
        Check(F.GetNext() == nullptr,
              "Default-ctor FField GetNext() != nullptr");
        Check(F.GetFName().IsNone(),
              "Default-ctor FField GetFName() != NAME_None");
    }

    // -----------------------------------------------------------------
    // (2) Explicit-ctor FField populated with class + owner + name.
    //
    // Use the base kFieldStaticClass as the ClassPrivate (the only
    // FFieldClass available at Phase 4b.3); construct a synthetic
    // owner (a stack-local FField that we tag as the Owner).
    // -----------------------------------------------------------------
    {
        const FFieldClass& BaseClass = GetFieldStaticClass();

        FField Owner;
        FFieldVariant OwnerVariant{&Owner};
        FName FieldName{"TestField"};

        FField F{&BaseClass, OwnerVariant, FieldName};

        Check(F.GetClass() == &BaseClass,
              "Explicit-ctor FField GetClass() != &BaseClass");
        Check(F.GetOwner() == OwnerVariant,
              "Explicit-ctor FField GetOwner() != passed owner");
        Check(F.GetOwner().AsField() == &Owner,
              "Explicit-ctor FField GetOwner().AsField() != owner ptr");
        Check(F.GetNext() == nullptr,
              "Explicit-ctor FField GetNext() != nullptr (must be null at ctor)");
        Check(F.GetFName() == FieldName,
              "Explicit-ctor FField GetFName() != passed name");
    }

    // -----------------------------------------------------------------
    // (3) SetNext mutator.
    // -----------------------------------------------------------------
    {
        FField F;
        FField NextSibling;

        // Pre: Next is nullptr.
        Check(F.GetNext() == nullptr, "Pre-condition: default FField Next != nullptr");

        F.SetNext(&NextSibling);
        Check(F.GetNext() == &NextSibling,
              "SetNext + GetNext round-trip failed");

        // Set back to nullptr (the unlink operation).
        F.SetNext(nullptr);
        Check(F.GetNext() == nullptr,
              "SetNext(nullptr) did not clear Next");
    }

    // -----------------------------------------------------------------
    // (4) kFieldStaticClass shape verification.
    //
    // The base "Field" descriptor. Reached via GetFieldStaticClass()
    // which lazy-initialises the Name slot to FName("Field") on first
    // call. Subsequent calls see the cached value.
    // -----------------------------------------------------------------
    {
        const FFieldClass& BaseClass = GetFieldStaticClass();

        // Name is FName("Field") post-init.
        FName ExpectedName{"Field"};
        Check(BaseClass.Name == ExpectedName,
              "GetFieldStaticClass().Name != FName(\"Field\")");

        // SuperClass is nullptr (base of the hierarchy).
        Check(BaseClass.SuperClass == nullptr,
              "GetFieldStaticClass().SuperClass != nullptr");

        // CastFlags == kNone (no dedicated bit for the base).
        Check(BaseClass.CastFlags == EClassCastFlags::kNone,
              "GetFieldStaticClass().CastFlags != EClassCastFlags::kNone");

        // Construct is non-null (points at FField::ConstructField).
        Check(BaseClass.Construct != nullptr,
              "GetFieldStaticClass().Construct is null");

        // FakeVTable is nullptr (no dispatch surface on the base).
        Check(BaseClass.FakeVTable == nullptr,
              "GetFieldStaticClass().FakeVTable != nullptr");

        // GetFName / GetSuperClass / GetCastFlags / GetConstructFn /
        // GetFakeVTable accessors mirror the direct field reads.
        Check(BaseClass.GetFName() == ExpectedName,
              "GetFieldStaticClass().GetFName() != FName(\"Field\")");
        Check(BaseClass.GetSuperClass() == nullptr,
              "GetFieldStaticClass().GetSuperClass() != nullptr");
        Check(BaseClass.GetCastFlags() == EClassCastFlags::kNone,
              "GetFieldStaticClass().GetCastFlags() != kNone");
        Check(BaseClass.GetConstructFn() != nullptr,
              "GetFieldStaticClass().GetConstructFn() is null");
        Check(BaseClass.GetFakeVTable() == nullptr,
              "GetFieldStaticClass().GetFakeVTable() != nullptr");
    }

    // -----------------------------------------------------------------
    // (5) FField::ConstructField placement-new.
    //
    // Allocate a sizeof(FField)-byte buffer with 8-byte alignment;
    // invoke ConstructField; verify the resulting FField has the
    // expected ClassPrivate / Owner / Name / Next.
    // -----------------------------------------------------------------
    {
        const FFieldClass& BaseClass = GetFieldStaticClass();

        // Force the lazy-init at least once so the Name slot is set.
        (void)BaseClass;

        alignas(alignof(FField)) std::byte Buffer[sizeof(FField)] = {};

        FField OwnerStack;
        FFieldVariant OwnerVariant{&OwnerStack};
        FName ConstructedName{"PlacementNewField"};

        // ConstructField is the FConstructFn for kFieldStaticClass.
        // It placement-new's an FField into Buffer.
        FField::ConstructField(OwnerVariant, ConstructedName, Buffer);

        const FField* Constructed = reinterpret_cast<const FField*>(Buffer);

        Check(Constructed->GetClass() == &BaseClass,
              "ConstructField result GetClass() != &kFieldStaticClass");
        Check(Constructed->GetOwner() == OwnerVariant,
              "ConstructField result GetOwner() != passed owner");
        Check(Constructed->GetFName() == ConstructedName,
              "ConstructField result GetFName() != passed name");
        Check(Constructed->GetNext() == nullptr,
              "ConstructField result GetNext() != nullptr");

        // Destroy explicitly (the placement-new'd object is trivially-
        // destructible per the FField static_assert; this is just a
        // posture-correctness statement).
        Constructed->~FField();
    }

    // -----------------------------------------------------------------
    // (6) Round-trip ctor via FConstructFn pointer in kFieldStaticClass.
    //
    // The FFieldClass exposes a Construct function pointer; invoking it
    // through the FFieldClass (rather than directly) verifies the
    // function-pointer storage round-trips correctly.
    // -----------------------------------------------------------------
    {
        const FFieldClass& BaseClass = GetFieldStaticClass();
        auto ConstructFn = BaseClass.GetConstructFn();
        Check(ConstructFn != nullptr, "kFieldStaticClass.Construct is nullptr");

        alignas(alignof(FField)) std::byte Buffer[sizeof(FField)] = {};

        FName FnPtrName{"FnPtrField"};
        ConstructFn(FFieldVariant{}, FnPtrName, Buffer);

        const FField* Constructed = reinterpret_cast<const FField*>(Buffer);
        Check(Constructed->GetClass() == &BaseClass,
              "FnPtr-invoked Construct result GetClass() != &kFieldStaticClass");
        Check(Constructed->GetFName() == FnPtrName,
              "FnPtr-invoked Construct result GetFName() != passed name");
        Constructed->~FField();
    }

    if (g_FailureCount > 0)
    {
        std::cerr << "FField.FFieldConstruction: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FField.FFieldConstruction: PASS\n";
    return 0;
}
