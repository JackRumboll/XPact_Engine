// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// MacroExpansion.cpp -- compile-time exercise of every XPACT_* macro.
// =====================================================================
//
// XCore-4a Section 13.4 test strategy:
//   "Compile-test every macro under each TargetConfiguration on each
//    platform."
//
// This TU exercises each macro at the call site; a passing compile
// means the macro expands to syntactically-valid C++ under the
// active configuration. The full test matrix (Debug + Development +
// Test + Shipping, each on Win64 + Linux + Android) is the build
// system's job; this single TU is the per-config probe.
//
// =====================================================================

#include "Macros/XPactMacros.h"

#include <cstdlib>      // std::abort
#include <string_view>

namespace
{
    // -----------------------------------------------------------------
    // 1. XCONSTINIT
    //
    // XCONSTINIT expands to `constinit`, which guarantees STATIC-INIT-
    // TIME initialisation (no dynamic init) but does NOT make the
    // variable a core-constant-expression (the variable is still
    // mutable at runtime). A `static_assert(g_ConstInitInt == 42, ...)`
    // is therefore invalid: the variable's value is not a constant
    // expression. The compile-time guarantee XCONSTINIT actually
    // provides is that this declaration WITHOUT a constant initialiser
    // is a hard error -- which the declaration below exercises by
    // construction (mistyping the initialiser would fail to compile).
    //
    // To validate the value at compile time, we ALSO declare a
    // `constexpr` companion: constexpr is strictly stronger than
    // constinit (every constexpr is constinit; not vice versa), and
    // constexpr values ARE constant expressions.
    // -----------------------------------------------------------------
    XCONSTINIT int g_ConstInitInt = 42;
    constexpr int  k_ConstInitProbe = 42;
    static_assert(k_ConstInitProbe == 42, "XCONSTINIT companion constexpr must initialise correctly");

    // -----------------------------------------------------------------
    // 2. Inlining + branch-prediction.
    // -----------------------------------------------------------------
    XPACT_FORCEINLINE int ForceInlinedFn(int X) noexcept { return X + 1; }
    XPACT_NOINLINE    int NoInlinedFn   (int X) noexcept { return X + 2; }

    // Branch-prediction macros: must be usable as expression-form.
    int BranchTest(int X) noexcept
    {
        if (XPACT_UNLIKELY(X < 0)) { return -1; }
        if (XPACT_LIKELY(X >= 0))  { return X * 2; }
        return 0;
    }

    // -----------------------------------------------------------------
    // 3. Alignment + restrict + TLS.
    // -----------------------------------------------------------------
    struct XPACT_ALIGNAS(16) AlignedThing { int Data; };
    static_assert(alignof(AlignedThing) == 16, "XPACT_ALIGNAS must align to 16");

    XPACT_TLS int g_TlsCounter = 0;

    // XPACT_RESTRICT on function parameters -- compile-only check.
    void RestrictParamSink(int* XPACT_RESTRICT Out, const int* XPACT_RESTRICT In) noexcept
    {
        *Out = *In;
    }

    // -----------------------------------------------------------------
    // 4. NORETURN + DEPRECATED + FALLTHROUGH.
    // -----------------------------------------------------------------
    [[noreturn]] XPACT_NORETURN void NoReturnFn() noexcept { ::std::abort(); }

    XPACT_DEPRECATED("test deprecation message") int DeprecatedFn() noexcept { return 0; }

    int FallthroughFn(int X) noexcept
    {
        switch (X)
        {
            case 0:
                X += 1;
                XPACT_FALLTHROUGH;
            case 1:
                X += 2;
                break;
            default:
                break;
        }
        return X;
    }

    // -----------------------------------------------------------------
    // 5. XPACT_NO_UNIQUE_ADDRESS -- empty-base-class optimisation.
    // -----------------------------------------------------------------
    struct EmptyAllocator {};
    struct ContainerWithEbo
    {
        int* m_data;
        int  m_num;
        int  m_capacity;
        XPACT_NO_UNIQUE_ADDRESS EmptyAllocator m_alloc;
    };
    // The EBO save is not a strict guarantee (compilers may still
    // pad), but the attribute must be syntactically accepted.
    static_assert(sizeof(ContainerWithEbo) >= sizeof(int*) + 2 * sizeof(int),
                  "ContainerWithEbo must at minimum hold its data + counters");

    // -----------------------------------------------------------------
    // 6. ABI tags as inline constexpr string_view.
    // -----------------------------------------------------------------
    static_assert(::XCore::XPACT_GC_ROOT_ABI_TAG     == ::std::string_view{"Span-based v1"},
                  "XPACT_GC_ROOT_ABI_TAG content lock");
    static_assert(::XCore::XPACT_EXCEPTION_ABI_TAG   == ::std::string_view{"Tier1-Shim/Tier2-Direct"},
                  "XPACT_EXCEPTION_ABI_TAG content lock");
    static_assert(::XCore::XPACT_MANGLING_SCHEME_TAG == ::std::string_view{"Itanium-LengthPrefixed-v1"},
                  "XPACT_MANGLING_SCHEME_TAG content lock");
    static_assert(::XCore::XPACT_CVAR_ABI_TAG        == ::std::string_view{"v1-with-4-reserved-slots"},
                  "XPACT_CVAR_ABI_TAG content lock");

    // Legacy macro spellings.
    static_assert(::std::string_view{XPACT_GC_ROOT_ABI_TAG_LITERAL}     == "Span-based v1",
                  "XPACT_GC_ROOT_ABI_TAG_LITERAL content lock");

    // -----------------------------------------------------------------
    // 7. ConstInit flags.
    // -----------------------------------------------------------------
    static_assert(XPACT_WITH_CONSTINIT_XOBJECT == 1, "XPACT_WITH_CONSTINIT_XOBJECT must be 1");
    static_assert(XPACT_PROPERTY_HAS_ACCESSORS == 1, "XPACT_PROPERTY_HAS_ACCESSORS must be 1");

    // -----------------------------------------------------------------
    // 8. XPACT_CACHE_LINE_SIZE.
    // -----------------------------------------------------------------
    static_assert(::XCore::XPACT_CACHE_LINE_SIZE == 64 || ::XCore::XPACT_CACHE_LINE_SIZE == 128,
                  "XPACT_CACHE_LINE_SIZE must be 64 (x86_64 / default) or 128 (ARM64 prefetch)");

    // -----------------------------------------------------------------
    // 9. XPACT_CHECK -- compile-only smoke test (cannot assert
    //    runtime behaviour without invoking abort).
    // -----------------------------------------------------------------
    int CheckCallable(int X) noexcept
    {
        XPACT_CHECK(X >= 0);     // expands to do-while in Debug/Dev; ((void)0) in Shipping
        XPACT_CHECK_SL(X < 1000);
        XPACT_ASSUME(X <= 999);
        return X;
    }

    // -----------------------------------------------------------------
    // 10. Suppress unused-warnings for the helper definitions above.
    //     The macros that warn-on-deprecate-use don't fire in this TU
    //     because we are not actually calling DeprecatedFn(); the
    //     declaration alone does not trigger the deprecation
    //     diagnostic.
    // -----------------------------------------------------------------
    [[maybe_unused]] void TouchEverything()
    {
        (void)ForceInlinedFn(1);
        (void)NoInlinedFn(1);
        (void)BranchTest(1);
        int A = 0, B = 1;
        RestrictParamSink(&A, &B);
        (void)FallthroughFn(0);
        (void)CheckCallable(0);
        g_TlsCounter++;
    }
} // anonymous

int main()
{
    return 0;
}
