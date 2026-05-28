// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FObjectInstancingGraph.Tests/MultipleArchetypes.cpp
// =====================================================================
//
// XCoreXObject Rev 4 §8.4: many archetype pairs in one graph (stress
// the underlying TMap's open-addressing path).
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

    // 64 archetype/instance pairs -- well past TMap's initial
    // capacity; exercises grow + rehash paths.
    constexpr int kCount = 64;
    XObject Archetypes[kCount];
    XObject Instances[kCount];

    FObjectInstancingGraph Graph;

    // Insert.
    for (int i = 0; i < kCount; ++i)
    {
        Graph.RegisterArchetype(&Archetypes[i], &Instances[i]);
    }

    Check(Graph.Num() == kCount,
          "graph Num != kCount after bulk insert");

    // Lookup every pair.
    int LookupFailures = 0;
    for (int i = 0; i < kCount; ++i)
    {
        if (Graph.GetInstance(&Archetypes[i]) != &Instances[i])
        {
            ++LookupFailures;
        }
    }
    Check(LookupFailures == 0,
          "one or more archetype lookups returned wrong instance");

    // Contains every archetype.
    int ContainsFailures = 0;
    for (int i = 0; i < kCount; ++i)
    {
        if (!Graph.Contains(&Archetypes[i]))
        {
            ++ContainsFailures;
        }
    }
    Check(ContainsFailures == 0,
          "one or more archetypes report Contains == false");

    if (g_FailureCount == 0)
    {
        std::cout << "FObjectInstancingGraph.MultipleArchetypes: PASS\n";
        return 0;
    }
    std::cerr << "FObjectInstancingGraph.MultipleArchetypes: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
