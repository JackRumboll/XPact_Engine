// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FObjectInstancingGraph.Tests/IsEmpty.cpp
// =====================================================================
//
// XCoreXObject Rev 4 §8.4: IsEmpty predicate.
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

    // Empty graph reports IsEmpty true + Num zero.
    {
        FObjectInstancingGraph Graph;
        Check(Graph.IsEmpty(), "freshly-constructed graph IsEmpty == false");
        Check(Graph.Num() == 0, "freshly-constructed graph Num != 0");
    }

    // After one register, IsEmpty false.
    {
        XObject Archetype, Instance;
        FObjectInstancingGraph Graph;
        Graph.RegisterArchetype(&Archetype, &Instance);
        Check(!Graph.IsEmpty(), "graph with 1 register IsEmpty == true");
        Check(Graph.Num() == 1, "graph Num != 1");
    }

    // Register with nullptr Instance is supported; the IsEmpty
    // predicate still reports non-empty.
    {
        XObject Archetype;
        FObjectInstancingGraph Graph;
        Graph.RegisterArchetype(&Archetype, nullptr);
        Check(!Graph.IsEmpty(),
              "graph with 1 register (nullptr Instance) IsEmpty == true");
        Check(Graph.Num() == 1,
              "graph Num != 1 (nullptr-Instance register)");
        Check(Graph.Contains(&Archetype),
              "Contains(Archetype) false after nullptr-Instance register");
        Check(Graph.GetInstance(&Archetype) == nullptr,
              "GetInstance != nullptr for nullptr-Instance register");
    }

    if (g_FailureCount == 0)
    {
        std::cout << "FObjectInstancingGraph.IsEmpty: PASS\n";
        return 0;
    }
    std::cerr << "FObjectInstancingGraph.IsEmpty: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
