// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FObjectInstancingGraph.Tests/RegisterAndGet.cpp
// =====================================================================
//
// XCoreXObject Rev 4 §8.4: RegisterArchetype + GetInstance round-trip.
//
// =====================================================================

#include "XObject/FObjectInstancingGraph.h"
#include "XObject/XObject.h"

#include "HAL/FMemory.h"

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
    using ::XCore::FObjectInstancingGraph;
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    XObject Archetype1, Archetype2, Archetype3;
    XObject Instance1,  Instance2,  Instance3;

    FObjectInstancingGraph Graph;

    Check(Graph.IsEmpty(), "fresh graph not empty");
    Check(Graph.Num() == 0, "fresh graph Num != 0");

    // Register 1 pair.
    Graph.RegisterArchetype(&Archetype1, &Instance1);
    Check(!Graph.IsEmpty(), "graph empty after 1 register");
    Check(Graph.Num() == 1, "graph Num != 1 after 1 register");
    Check(Graph.GetInstance(&Archetype1) == &Instance1,
          "GetInstance(Archetype1) != Instance1");
    Check(Graph.Contains(&Archetype1),
          "Contains(Archetype1) false after register");

    // Register 2 more pairs.
    Graph.RegisterArchetype(&Archetype2, &Instance2);
    Graph.RegisterArchetype(&Archetype3, &Instance3);
    Check(Graph.Num() == 3, "graph Num != 3 after 3 registers");

    Check(Graph.GetInstance(&Archetype1) == &Instance1, "Archetype1 lookup wrong after 3");
    Check(Graph.GetInstance(&Archetype2) == &Instance2, "Archetype2 lookup wrong after 3");
    Check(Graph.GetInstance(&Archetype3) == &Instance3, "Archetype3 lookup wrong after 3");

    // Lookup for unregistered archetype returns nullptr.
    XObject UnregisteredArchetype;
    Check(Graph.GetInstance(&UnregisteredArchetype) == nullptr,
          "GetInstance(unregistered) != nullptr");
    Check(!Graph.Contains(&UnregisteredArchetype),
          "Contains(unregistered) true");

    // nullptr Archetype returns nullptr.
    Check(Graph.GetInstance(nullptr) == nullptr,
          "GetInstance(nullptr) != nullptr");
    Check(!Graph.Contains(nullptr),
          "Contains(nullptr) true");

    // Re-register OVERWRITES (per RegisterArchetype contract).
    Graph.RegisterArchetype(&Archetype1, &Instance2);
    Check(Graph.GetInstance(&Archetype1) == &Instance2,
          "re-register did not overwrite");
    Check(Graph.Num() == 3, "Num changed after overwrite (expected 3)");

    if (g_FailureCount == 0)
    {
        std::cout << "FObjectInstancingGraph.RegisterAndGet: PASS\n";
        return 0;
    }
    std::cerr << "FObjectInstancingGraph.RegisterAndGet: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
