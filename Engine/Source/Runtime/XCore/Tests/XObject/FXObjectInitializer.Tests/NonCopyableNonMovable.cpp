// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectInitializer.Tests/NonCopyableNonMovable.cpp
// =====================================================================
//
// XCoreXObject Rev 4 §8.4: compile-time check that FXObjectInitializer
// is non-copyable + non-movable. The deleted copy/move ops are the
// type-system enforcement of the "stack-allocated, non-transferable"
// lifetime discipline.
//
// All checks are static_assert; main() is a no-op that succeeds.
//
// =====================================================================

#include "XObject/FXObjectInitializer.h"

#include <iostream>
#include <type_traits>

int main()
{
    using ::XCore::FXObjectInitializer;

    static_assert(!std::is_copy_constructible_v<FXObjectInitializer>,
                  "FXObjectInitializer must NOT be copy-constructible.");
    static_assert(!std::is_copy_assignable_v<FXObjectInitializer>,
                  "FXObjectInitializer must NOT be copy-assignable.");
    static_assert(!std::is_move_constructible_v<FXObjectInitializer>,
                  "FXObjectInitializer must NOT be move-constructible.");
    static_assert(!std::is_move_assignable_v<FXObjectInitializer>,
                  "FXObjectInitializer must NOT be move-assignable.");
    static_assert(!std::is_polymorphic_v<FXObjectInitializer>,
                  "FXObjectInitializer must NOT be polymorphic.");

    std::cout << "FXObjectInitializer.NonCopyableNonMovable: PASS\n";
    return 0;
}
