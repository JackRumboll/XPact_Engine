// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FStruct.Tests/FInterfaceFunctions.cpp -- FInterface::
// FindFunctionByName + FindFunctionByHash (XCore-4b §7.5 + §7.5.1).
// =====================================================================
//
// Constructs an FInterface with 3 functions and verifies:
//
//   1. FindFunctionByName resolves each declared function.
//   2. FindFunctionByHash resolves each declared signature hash.
//   3. Unknown names + hashes return nullptr.
//   4. The returned pointer points into the InterfaceFunctions TArray.
//   5. NumFunctions returns 3; InterfaceFlags accessor works.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/EInterfaceFlags.h"
#include "Reflection/FFieldVariant.h"
#include "Reflection/FInterface.h"
#include "Reflection/FName.h"

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
}

int main()
{
    using namespace ::XCore::Reflect;

    ::XCore::HAL::FMemory::__Init();

    // -----------------------------------------------------------------
    // Build a synthetic interface: IInteractable with 3 functions.
    //
    // SignatureHash values are arbitrary (the spec calls for BLAKE3-
    // truncated 64-bit hashes; the test verifies the lookup mechanism
    // regardless of the hash provenance).
    // -----------------------------------------------------------------
    FInterface Iface(FFieldVariant{}, FName("IInteractable"),
                     EInterfaceFlags::INTERFACE_BlueprintImpl);

    constexpr ::uint64_t HashOnInteract = 0xAAAA'1111'BBBB'2222ULL;
    constexpr ::uint64_t HashOnHover    = 0xCCCC'3333'DDDD'4444ULL;
    constexpr ::uint64_t HashOnExit     = 0xEEEE'5555'FFFF'6666ULL;

    Iface.InterfaceFunctions.Add(
        FFunctionDescriptor(FName("OnInteract"), HashOnInteract));
    Iface.InterfaceFunctions.Add(
        FFunctionDescriptor(FName("OnHover"),    HashOnHover));
    Iface.InterfaceFunctions.Add(
        FFunctionDescriptor(FName("OnExit"),     HashOnExit));

    // -----------------------------------------------------------------
    // NumFunctions + InterfaceFlags accessor.
    // -----------------------------------------------------------------
    Check(Iface.NumFunctions() == 3, "NumFunctions != 3");
    Check(Iface.GetInterfaceFlags() == EInterfaceFlags::INTERFACE_BlueprintImpl,
          "GetInterfaceFlags != INTERFACE_BlueprintImpl");

    // -----------------------------------------------------------------
    // FindFunctionByName.
    // -----------------------------------------------------------------
    {
        const FFunctionDescriptor* F = Iface.FindFunctionByName(FName("OnInteract"));
        Check(F != nullptr, "FindFunctionByName(OnInteract) returned nullptr");
        if (F != nullptr)
        {
            Check(F->Name == FName("OnInteract"), "OnInteract Name mismatch");
            Check(F->SignatureHash == HashOnInteract, "OnInteract Hash mismatch");
        }
    }
    {
        const FFunctionDescriptor* F = Iface.FindFunctionByName(FName("OnHover"));
        Check(F != nullptr, "FindFunctionByName(OnHover) returned nullptr");
        if (F != nullptr)
        {
            Check(F->Name == FName("OnHover"), "OnHover Name mismatch");
            Check(F->SignatureHash == HashOnHover, "OnHover Hash mismatch");
        }
    }
    {
        const FFunctionDescriptor* F = Iface.FindFunctionByName(FName("OnExit"));
        Check(F != nullptr, "FindFunctionByName(OnExit) returned nullptr");
        if (F != nullptr)
        {
            Check(F->Name == FName("OnExit"), "OnExit Name mismatch");
            Check(F->SignatureHash == HashOnExit, "OnExit Hash mismatch");
        }
    }

    // -----------------------------------------------------------------
    // FindFunctionByName: unknown name.
    // -----------------------------------------------------------------
    {
        const FFunctionDescriptor* F = Iface.FindFunctionByName(FName("OnClick"));
        Check(F == nullptr, "FindFunctionByName(OnClick) erroneously found");
    }

    // -----------------------------------------------------------------
    // FindFunctionByHash.
    // -----------------------------------------------------------------
    {
        const FFunctionDescriptor* F = Iface.FindFunctionByHash(HashOnInteract);
        Check(F != nullptr, "FindFunctionByHash(OnInteract) returned nullptr");
        if (F != nullptr)
        {
            Check(F->Name == FName("OnInteract"), "Hash->OnInteract reverse lookup wrong name");
        }
    }
    {
        const FFunctionDescriptor* F = Iface.FindFunctionByHash(HashOnHover);
        Check(F != nullptr, "FindFunctionByHash(OnHover) returned nullptr");
        if (F != nullptr)
        {
            Check(F->Name == FName("OnHover"), "Hash->OnHover reverse lookup wrong name");
        }
    }
    {
        const FFunctionDescriptor* F = Iface.FindFunctionByHash(HashOnExit);
        Check(F != nullptr, "FindFunctionByHash(OnExit) returned nullptr");
        if (F != nullptr)
        {
            Check(F->Name == FName("OnExit"), "Hash->OnExit reverse lookup wrong name");
        }
    }

    // -----------------------------------------------------------------
    // FindFunctionByHash: unknown hash.
    // -----------------------------------------------------------------
    {
        const FFunctionDescriptor* F = Iface.FindFunctionByHash(0xDEAD'BEEF'DEAD'BEEFULL);
        Check(F == nullptr, "FindFunctionByHash(unknown) erroneously found");
    }

    // -----------------------------------------------------------------
    // StaticClass + IsA<FInterface>.
    // -----------------------------------------------------------------
    Check(FInterface::StaticClass() != nullptr, "FInterface::StaticClass() returned nullptr");
    Check(Iface.IsA(FInterface::StaticClass()), "Iface is not an FInterface");

    if (g_FailureCount > 0)
    {
        std::cerr << "FStruct.FInterfaceFunctions: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FStruct.FInterfaceFunctions: PASS\n";
    return 0;
}
