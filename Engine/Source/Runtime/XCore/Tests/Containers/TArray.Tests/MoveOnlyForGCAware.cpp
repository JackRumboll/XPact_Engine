// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// TArray.Tests/MoveOnlyForGCAware.cpp -- TArray<XObject*> is move-only.
// =====================================================================
//
// XCore-4a Section 5.4 + Contract Rev 13.7 Section 3.4 M1: "GC-aware
// containers are move-only". The GC-aware partial specialization
// TArray<T*, AllocatorT> where T : XCore::Reflect::XObject deletes
// copy ctor + copy-assign; this test verifies that with a stub
// XObject-derived type.
//
// Since XObject ships in XCore-4b (master plan Section 4 Step 5), this
// test defines a local class XObject in namespace XCore::Reflect that
// satisfies the forward declaration in TArray.h. The fake-XObject is
// scope-local to this TU (anonymous namespace), so it does not
// conflict with the XCore-4b version when both land.
//
// NB: this test only validates COMPILE-TIME properties (move-only,
// span layout). The runtime XGC_RegisterRootSpan / XGC_UpdateRootSpan
// extern "C" symbols are NOT linked here -- the test does NOT
// instantiate a TArray<XActor*> at runtime (it would link-fail
// because those symbols have no Phase 1c body). Phase 1c-A landed
// the XGCDeclarations.h with a TODO for the no-op weak-symbol stub;
// until that stub lands, GC-aware containers are
// compile-only-instantiable.
//
// The test thus verifies:
//   1. TArray<MockXObject*> selects the GC-aware specialization
//      (via std::derived_from constraint).
//   2. The specialization is move-only at compile time
//      (static_assert).
//   3. The X_DECLARE_GC_AWARE_CONTAINER sentinel is present.
//
// =====================================================================

#include "Containers/TArray.h"
#include "Macros/XCoreTypes.h"

#include <cstdio>
#include <type_traits>

// Provide a stub XCore::Reflect::XObject so the GC-aware partial
// specialization's std::derived_from<T, XObject> constraint can be
// satisfied at compile time. The forward declaration in TArray.h is
// `class XObject;`; this completes it for the TU.
//
// Anonymous namespace would not work because the spec's forward decl
// is at namespace scope; we instead provide the definition at the
// matching namespace scope, with the understanding that this TU is
// the ONLY TU in the test binary that provides this definition
// (single-TU rule respected).
namespace XCore::Reflect
{
    class XObject
    {
    public:
        virtual ~XObject() = default;
    };
}

namespace
{
    class MockXObject : public ::XCore::Reflect::XObject
    {
    public:
        ::int32 Id = 0;
    };
}

int main()
{
    // ---------------------------------------------------------------
    // Verify that TArray<MockXObject*> is move-only.
    //
    // Per Section 5.4 / Contract Section 3.4 M1, the GC-aware partial
    // specialization deletes copy ctor + copy-assign. The std
    // type-trait queries below test compile-time properties; they do
    // NOT instantiate the type (so we do not need the XGC_* extern
    // "C" symbols to link).
    // ---------------------------------------------------------------
    using GCAwareArray = ::XCore::TArray<MockXObject*, ::XCore::DefaultAllocator>;

    static_assert(!::std::is_copy_constructible_v<GCAwareArray>,
                  "GC-aware TArray<MockXObject*> must NOT be copy-constructible "
                  "(Contract Section 3.4 M1 move-only).");

    static_assert(!::std::is_copy_assignable_v<GCAwareArray>,
                  "GC-aware TArray<MockXObject*> must NOT be copy-assignable "
                  "(Contract Section 3.4 M1 move-only).");

    static_assert(::std::is_move_constructible_v<GCAwareArray>,
                  "GC-aware TArray<MockXObject*> must be move-constructible.");

    static_assert(::std::is_move_assignable_v<GCAwareArray>,
                  "GC-aware TArray<MockXObject*> must be move-assignable.");

    // ---------------------------------------------------------------
    // Verify that the X_DECLARE_GC_AWARE_CONTAINER sentinel is present.
    // ---------------------------------------------------------------
    static_assert(GCAwareArray::x_container_layout_frozen,
                  "GC-aware container must carry x_container_layout_frozen = true");

    // ---------------------------------------------------------------
    // Verify that non-GC TArray<int> remains copyable.
    //
    // This is the dual property: only the XObject-derived pointer
    // specialization is move-only; the primary template is fully
    // copyable (delegates to TArrayCore which preserves copyability).
    // ---------------------------------------------------------------
    using NonGCArray = ::XCore::TArray<::int32, ::XCore::DefaultAllocator>;

    static_assert(::std::is_copy_constructible_v<NonGCArray>,
                  "Non-GC TArray<int32> MUST be copy-constructible.");

    static_assert(::std::is_copy_assignable_v<NonGCArray>,
                  "Non-GC TArray<int32> MUST be copy-assignable.");

    static_assert(::std::is_move_constructible_v<NonGCArray>,
                  "Non-GC TArray<int32> MUST be move-constructible.");

    // ---------------------------------------------------------------
    // Verify that a non-XObject-derived pointer (e.g., int*) goes to
    // the PRIMARY template, not the GC-aware specialization. Such an
    // array IS copyable (no GC integration).
    // ---------------------------------------------------------------
    using RawPtrArray = ::XCore::TArray<::int32*, ::XCore::DefaultAllocator>;

    static_assert(::std::is_copy_constructible_v<RawPtrArray>,
                  "TArray<int32*> (non-XObject) must be copyable -- it should "
                  "select the primary template, not the GC-aware specialization.");

    std::printf("TArray.MoveOnlyForGCAware: PASS (compile-time properties verified)\n");
    return 0;
}
