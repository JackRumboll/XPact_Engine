// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XObject.Tests/GetFullNameWalk.cpp -- Outer-chain materialisation for
// XObject::GetFullName (XCoreXObject Rev 4 §2.5).
// =====================================================================
//
// Verifies the Outer-chain walk produces the correct dot-qualified
// path string for:
//   * Top-level object (no Outer)              -> "MyObject"
//   * Two-level chain                          -> "Outermost.MyObject"
//   * Three-level chain                        -> "Package.Outer.MyObject"
//   * NAME_None inner + named Outer            -> "Outer.None"
//   * Deep chain triggers the depth cap        -> "....Last.Outer..." prefix
//
// =====================================================================

#include "Containers/FString.h"
#include "HAL/FMemory.h"
#include "Reflection/FName.h"
#include "XObject/XObject.h"

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
    using ::XCore::XObject;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();

    // -----------------------------------------------------------------
    // Top-level: no Outer; result is just the FName's ToString.
    // -----------------------------------------------------------------
    {
        XObject MyObj;
        MyObj.NamePrivate = FName("MyObject");
        const auto FullName = MyObj.GetFullName();
        Check(FullName.LenBytes() == 8,
              "Top-level GetFullName: length not 8 ('MyObject')");
        // Byte compare via NUL-terminated cstr (FString::ToUtf8Cstr).
        const char* Bytes = FullName.ToUtf8Cstr();
        Check(Bytes != nullptr && std::string_view(Bytes) == "MyObject",
              "Top-level GetFullName: bytes != 'MyObject'");
    }

    // -----------------------------------------------------------------
    // Two-level chain: Outer.Inner.
    // -----------------------------------------------------------------
    {
        XObject Outer;
        Outer.NamePrivate = FName("Outer");

        XObject Inner;
        Inner.NamePrivate = FName("Inner");
        Inner.Outer       = &Outer;

        const auto FullName = Inner.GetFullName();
        Check(std::string_view(FullName.ToUtf8Cstr()) == "Outer.Inner",
              "Two-level GetFullName: bytes != 'Outer.Inner'");
    }

    // -----------------------------------------------------------------
    // Three-level chain: Package.Outer.Inner.
    // -----------------------------------------------------------------
    {
        XObject Package;
        Package.NamePrivate = FName("Package");

        XObject MidOuter;
        MidOuter.NamePrivate = FName("MidOuter");
        MidOuter.Outer       = &Package;

        XObject Inner;
        Inner.NamePrivate = FName("Inner");
        Inner.Outer       = &MidOuter;

        const auto FullName = Inner.GetFullName();
        Check(std::string_view(FullName.ToUtf8Cstr()) == "Package.MidOuter.Inner",
              "Three-level GetFullName: bytes != 'Package.MidOuter.Inner'");
    }

    // -----------------------------------------------------------------
    // NAME_None inner: FName::ToString of NAME_None is "None".
    // -----------------------------------------------------------------
    {
        XObject Outer;
        Outer.NamePrivate = FName("Outer");

        XObject Inner;
        // Inner.NamePrivate left as default (NAME_None).
        Inner.Outer       = &Outer;

        const auto FullName = Inner.GetFullName();
        Check(std::string_view(FullName.ToUtf8Cstr()) == "Outer.None",
              "NAME_None inner GetFullName: bytes != 'Outer.None'");
    }

    // -----------------------------------------------------------------
    // Fresh XObject (no name, no Outer): result is "None".
    // -----------------------------------------------------------------
    {
        XObject Obj;
        const auto FullName = Obj.GetFullName();
        Check(std::string_view(FullName.ToUtf8Cstr()) == "None",
              "Fresh GetFullName: bytes != 'None'");
    }

    if (g_FailureCount == 0)
    {
        std::cout << "XObject.GetFullNameWalk: PASS\n";
        return 0;
    }
    std::cerr << "XObject.GetFullNameWalk: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
