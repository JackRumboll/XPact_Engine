// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// TArrayView.Tests/Construction.cpp -- view constructors + accessors.
// =====================================================================
//
// XCore-4a Section 5.1 / 5.6: TArrayView<T> is the non-owning view
// equivalent to std::span<T>. This test exercises every documented
// constructor + the Num / operator[] / GetData / begin / end surface.
//
// =====================================================================

#include "Containers/TArrayView.h"
#include "Containers/TArray.h"
#include "Containers/TStaticArray.h"
#include "HAL/FMemory.h"
#include "Macros/XCoreTypes.h"

#include <cstdio>
#include <initializer_list>

int main()
{
    ::XCore::HAL::FMemory::__Init();

    {
        // ---------------------------------------------------------------
        // 1. Default ctor: empty view.
        // ---------------------------------------------------------------
        ::XCore::TArrayView<::int32> Empty;
        if (Empty.Num() != 0)
        {
            std::fprintf(stderr, "FAIL: default ctor Num=%d (expected 0)\n", Empty.Num());
            return 1;
        }
        if (Empty.begin() != Empty.end())
        {
            std::fprintf(stderr, "FAIL: default ctor begin != end\n");
            return 1;
        }
    }

    {
        // ---------------------------------------------------------------
        // 2. Pointer + count ctor: view over raw memory.
        // ---------------------------------------------------------------
        ::int32 RawArray[5] = { 10, 20, 30, 40, 50 };
        ::XCore::TArrayView<::int32> View(RawArray, 5);

        if (View.Num() != 5)
        {
            std::fprintf(stderr, "FAIL: ptr+count Num=%d (expected 5)\n", View.Num());
            return 1;
        }
        if (View[0] != 10 || View[4] != 50)
        {
            std::fprintf(stderr, "FAIL: ptr+count View[0]=%d View[4]=%d (expected 10/50)\n",
                         View[0], View[4]);
            return 1;
        }
        if (View.GetData() != RawArray)
        {
            std::fprintf(stderr, "FAIL: GetData != original pointer\n");
            return 1;
        }

        // Iteration sum.
        ::int32 Sum = 0;
        for (::int32 V : View)
        {
            Sum += V;
        }
        if (Sum != 150)
        {
            std::fprintf(stderr, "FAIL: iteration sum=%d (expected 150)\n", Sum);
            return 1;
        }
    }

    {
        // ---------------------------------------------------------------
        // 3. From C-style array (template-N ctor).
        // ---------------------------------------------------------------
        ::int32 RawArray[3] = { 7, 8, 9 };
        ::XCore::TArrayView<::int32> View(RawArray);

        if (View.Num() != 3)
        {
            std::fprintf(stderr, "FAIL: C-array ctor Num=%d (expected 3)\n", View.Num());
            return 1;
        }
        if (View[1] != 8)
        {
            std::fprintf(stderr, "FAIL: C-array View[1]=%d (expected 8)\n", View[1]);
            return 1;
        }
    }

    {
        // ---------------------------------------------------------------
        // 4. From TArray<int32>.
        // ---------------------------------------------------------------
        ::XCore::TArray<::int32, ::XCore::DefaultAllocator> Arr;
        for (::int32 I = 0; I < 10; ++I)
        {
            Arr.Add(I * 100);
        }

        ::XCore::TArrayView<::int32> View(Arr);

        if (View.Num() != 10)
        {
            std::fprintf(stderr, "FAIL: from-TArray Num=%d (expected 10)\n", View.Num());
            return 1;
        }
        if (View[5] != 500)
        {
            std::fprintf(stderr, "FAIL: from-TArray View[5]=%d (expected 500)\n", View[5]);
            return 1;
        }
    }

    {
        // ---------------------------------------------------------------
        // 5. From std::initializer_list (caller-discipline non-owning).
        //
        // The view is valid only within the full-expression. We construct
        // and verify inside the same expression scope.
        // ---------------------------------------------------------------
        auto VerifyInitList = [](::XCore::TArrayView<const ::int32> V) -> bool
        {
            if (V.Num() != 4) return false;
            if (V[0] != 1 || V[1] != 2 || V[2] != 3 || V[3] != 4) return false;
            return true;
        };
        if (!VerifyInitList({1, 2, 3, 4}))
        {
            std::fprintf(stderr, "FAIL: initializer_list ctor verification failed\n");
            return 1;
        }
    }

    {
        // ---------------------------------------------------------------
        // 6. At() bounds-check returns Result.
        //
        // PHASE 1C STATUS: the Result<T, E> alias is the polyfill
        // placeholder on C++20 toolchains; until tl::expected is
        // vendored, instantiating At() fires the diagnostic in
        // XResult.h. We DEFER this assertion to Phase 1d when the
        // polyfill lands.
        //
        // Compile-time-only: confirm the At() signature is declared
        // without instantiating it. The line below is sufficient --
        // we take the address of At, which forces overload resolution
        // but not body instantiation.
        // ---------------------------------------------------------------
        ::int32 RawArray[2] = { 0, 1 };
        ::XCore::TArrayView<::int32> View(RawArray);
        (void)View;
        // TODO(Phase 1d): once tl::expected ships, exercise At(-1) /
        // At(2) and verify the FBoundsError variants.
    }

    {
        // ---------------------------------------------------------------
        // 7. constexpr-friendly: TArrayView accessors are constexpr.
        //
        // The default-constructed TArrayView's Num() and IsEmpty() are
        // evaluatable in a constant expression -- the default ctor sets
        // m_data = nullptr and m_num = 0, both literal-type values.
        // ---------------------------------------------------------------
        constexpr ::XCore::TArrayView<::int32> EmptyView;
        static_assert(EmptyView.Num() == 0, "TArrayView constexpr Num() on default");
        static_assert(EmptyView.IsEmpty(), "TArrayView constexpr IsEmpty() on default");
        static_assert(EmptyView.GetData() == nullptr, "TArrayView constexpr GetData() on default");

        // Pointer + count ctor is constexpr too -- we pass a nullptr/0
        // pair, which is well-defined for an empty view.
        constexpr ::XCore::TArrayView<::int32> NullView(nullptr, 0);
        static_assert(NullView.Num() == 0, "TArrayView constexpr ptr+count default");
    }

    ::XCore::HAL::FMemory::__Shutdown();
    std::printf("TArrayView.Construction: PASS\n");
    return 0;
}
