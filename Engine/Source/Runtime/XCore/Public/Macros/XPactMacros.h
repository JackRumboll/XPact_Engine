// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XPactMacros.h -- the XPACT_* portability + assertion macro suite.
// =====================================================================
//
// XCore-4a Rev 3, Section 13.1 (full body) + Section 1.5 phase-ladder
// cross-reference.
//
// This is the single-source-of-truth for the engine's C++20 portability
// macros. Five families:
//
//   1. Constinit / inline / branch-prediction language constructs
//        (XCONSTINIT, XPACT_FORCEINLINE, XPACT_NOINLINE,
//         XPACT_ALWAYS_INLINE_HARD, XPACT_ALIGNAS, XPACT_RESTRICT,
//         XPACT_TLS, XPACT_NORETURN, XPACT_DEPRECATED,
//         XPACT_FALLTHROUGH, XPACT_LIKELY, XPACT_UNLIKELY,
//         XPACT_NO_UNIQUE_ADDRESS).
//
//   2. ABI tags as inline constexpr std::string_view
//        (XPACT_GC_ROOT_ABI_TAG, XPACT_EXCEPTION_ABI_TAG,
//         XPACT_MANGLING_SCHEME_TAG, XPACT_CVAR_ABI_TAG).
//      Per fix B-MIN4: inline-constexpr-string_view, not #define,
//      because hot-reload patching produces distinct .rdata copies of
//      #define-substituted strings per TU; inline constexpr
//      string_view is linker-deduped and hot-reload-safe.
//
//   3. ConstInit / accessor scaffolding flags
//        (XPACT_WITH_CONSTINIT_XOBJECT,
//         XPACT_PROPERTY_HAS_ACCESSORS).
//      These migrate from the legacy XReflectionRuntime.h stub
//      (Section 13 locked decision 8); the type definitions in that
//      stub stay where they are.
//
//   4. Build-config-aware assertion macros
//        (XPACT_CHECK, XPACT_CHECK_SL, XPACT_ASSUME).
//      Hot path = one UNLIKELY-branch + one cold-call to a
//      [[noreturn]] [[gnu::cold]] helper in XAssertionMacros.h.
//      Per fix M-15 + fix Rev 3 M6.
//
//   5. Cache-line padding constant
//        (XPACT_CACHE_LINE_SIZE).
//      C++17 std::hardware_destructive_interference_size where
//      available; platform-correct fallback otherwise.
//
//   6. Module-safe TLS scaffolding
//        (XPACT_TLS_MODULE_SAFE).
//      Win64 + Android route through XCore::HAL::TModuleSafeThreadLocal
//      (declared here as a forward template; defined by the Platform
//      HAL in XCore-4a Step 2). Linux falls through to native
//      thread_local.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XCoreDefines.h"
#include "Macros/XAssertionMacros.h"
#include "Macros/XErrorTypes.h"  // re-export error enums for downstream convenience
#include "Macros/XResult.h"      // re-export Result<T, E> alias

#include <atomic>
#include <new>            // std::hardware_destructive_interference_size
#include <string_view>
#include <utility>        // std::forward, std::move

// =====================================================================
// 1. ConstInit attribute.
// =====================================================================
//
// XCONSTINIT migrates from XReflectionRuntime.h (Section 13 locked
// decision 8). C++20 has the `constinit` keyword natively; on older
// toolchains (which XCore-4a does not officially support but which
// the legacy XReflectionRuntime.h compatibility path retains) the
// macro degrades to a no-op. The pre-C++20 path exists for backward
// compatibility with .gen.cpp files emitted against the legacy stub;
// new code uses `constinit` directly or this macro indifferently.

#if defined(__cpp_constinit) && __cpp_constinit >= 201907L
    #define XCONSTINIT constinit
#elif __cplusplus >= 202002L
    #define XCONSTINIT constinit
#else
    #define XCONSTINIT
#endif

// =====================================================================
// 2. Inlining + branch-prediction language constructs (fix M-14 / B-M5).
// =====================================================================
//
// XPACT_FORCEINLINE -- portable always-inline HINT. Communicates
// intent; neither MSVC nor Clang/GCC actually guarantees inlining
// for any attribute (they all reserve the right to refuse for
// cost-model reasons).
//
// XPACT_ALWAYS_INLINE_HARD -- the hard variant (Clang treats as a
// hard error if it cannot inline). MSVC has no equivalent; the
// macro expands to a static_assert(false, ...) on MSVC so the
// build fails at the call site with a clear "refactor or move out
// of MSVC TU" diagnostic.
//
// XPACT_NOINLINE -- the opposite hint; suppresses inlining.
//
// Per fix B-M5 the FORCEINLINE / ALWAYS_INLINE_HARD split is
// deliberate: most call sites just want a hint, but the rare SIMD
// intrinsic wrapper whose unused-argument elimination depends on
// inlining needs the hard variant. Code is expected to default to
// XPACT_FORCEINLINE.

#if defined(_MSC_VER)
    #define XPACT_FORCEINLINE         __forceinline
    #define XPACT_NOINLINE            __declspec(noinline)
    // MSVC has no clang::always_inline equivalent. Expanding to a
    // static_assert(false, ...) at the call site forces the
    // developer to refactor (move the function to a Clang TU, or
    // drop the hard-inline requirement).
    #define XPACT_ALWAYS_INLINE_HARD  static_assert(false, "XPACT_ALWAYS_INLINE_HARD is Clang/GCC only; refactor or move out of MSVC TU")
#elif defined(__clang__) || defined(__GNUC__)
    #define XPACT_FORCEINLINE         [[gnu::always_inline]] inline
    #define XPACT_NOINLINE            [[gnu::noinline]]
    #define XPACT_ALWAYS_INLINE_HARD  [[clang::always_inline]] inline
#else
    #define XPACT_FORCEINLINE         inline
    #define XPACT_NOINLINE            /* nothing */
    #define XPACT_ALWAYS_INLINE_HARD  static_assert(false, "XPACT_ALWAYS_INLINE_HARD requires Clang or GCC")
#endif

#define XPACT_ALIGNAS(N)              alignas(N)

// MSVC: __restrict; Clang/GCC: __restrict__ (both spellings work in
// GCC, but __restrict__ is the documented one for C++).
#if defined(_MSC_VER)
    #define XPACT_RESTRICT            __restrict
#else
    #define XPACT_RESTRICT            __restrict__
#endif

#define XPACT_TLS                     thread_local
#define XPACT_NORETURN                [[noreturn]]
#define XPACT_DEPRECATED(Msg)         [[deprecated(Msg)]]
#define XPACT_FALLTHROUGH             [[fallthrough]]

// Branch-prediction hints. GCC's __builtin_expect is the canonical
// spelling. MSVC does not have a direct equivalent; the C++20
// [[likely]] / [[unlikely]] attributes are the standardized
// replacement and MSVC supports them since 19.26. Since XCore-4a is
// C++20, we use the attributes natively rather than the builtin
// wrapper -- they compose better with control-flow statements.
//
// However the spec body (Section 13.1) names the macros
// XPACT_LIKELY(x) / XPACT_UNLIKELY(x) as expression-form macros
// (i.e., `if (XPACT_UNLIKELY(!Expr))`). The C++20 [[likely]] /
// [[unlikely]] attributes are statement-form ONLY -- they attach
// to the if/else or the case label, not to the expression. So we
// keep the expression-form macros backed by __builtin_expect (which
// MSVC has shimmed via __builtin_expect_with_probability since
// 19.30); for older MSVC the macros fall through to the bare
// expression. This is acceptable because the optimiser still
// inlines correctly without the hint, just without the cold-branch
// reordering benefit.
#if defined(__clang__) || defined(__GNUC__)
    #define XPACT_LIKELY(x)           (__builtin_expect(!!(x), 1))
    #define XPACT_UNLIKELY(x)         (__builtin_expect(!!(x), 0))
#else
    // MSVC: no expression-form __builtin_expect. The macros degrade
    // to bare expressions. Call sites that need the cold-branch
    // reorder benefit on MSVC can wrap their if/else in [[likely]]
    // / [[unlikely]] attributes separately.
    #define XPACT_LIKELY(x)           (!!(x))
    #define XPACT_UNLIKELY(x)         (!!(x))
#endif

// C++20 attribute for empty-base-class optimisation of stateless
// allocators and other empty members (fix Rev 3 m6).
//
// MSVC supports the attribute under [[msvc::no_unique_address]] for
// ABI-stability reasons (default-naming would break vtable layouts
// in pre-existing ABIs); both spellings collapse to the same effect
// in practice.

#if defined(_MSC_VER)
    #define XPACT_NO_UNIQUE_ADDRESS   [[msvc::no_unique_address]]
#else
    #define XPACT_NO_UNIQUE_ADDRESS   [[no_unique_address]]
#endif

// =====================================================================
// 3. ABI tags as inline constexpr std::string_view (fix B-MIN4).
// =====================================================================
//
// Per fix B-MIN4: these are inline constexpr std::string_view rather
// than #define macros so they are well-defined under linker dedup and
// hot-reload-safe; macros are textual substitution and produce
// distinct .rdata copies per TU which then differ subtly under
// hot-reload patching.
//
// The four tags (Sections 13.1 + 15):
//
//   XPACT_GC_ROOT_ABI_TAG          "Span-based v1"
//   XPACT_EXCEPTION_ABI_TAG        "Tier1-Shim/Tier2-Direct"
//   XPACT_MANGLING_SCHEME_TAG      "Itanium-LengthPrefixed-v1"
//   XPACT_CVAR_ABI_TAG             "v1-with-4-reserved-slots"
//
// XHT emits static_asserts against these tags at every .gen.cpp;
// drift between the manifest and the live runtime fails at compile
// time, not at hot-reload time.
//
// IMPORTANT: the legacy XReflectionRuntime.h stub defines these
// names as #defines at global scope. If a TU includes both this
// header and XReflectionRuntime.h, the textual macro substitution
// would corrupt the namespace-qualified declarations below
// (turning `XPACT_GC_ROOT_ABI_TAG` inside the namespace into the
// literal "Span-based v1"). We #undef the legacy macros here so
// the namespace declarations are well-formed; the
// XPACT_*_ABI_TAG_LITERAL spellings below preserve the macro-
// literal accessor for any .gen.cpp file that still expects the
// #define form.
//
// Per Section 13 locked decision 8, the eventual deprecation path
// is: remove the #define from XReflectionRuntime.h once all .gen.cpp
// consumers have switched to the namespace XCore:: constexpr form.
// Until then, the #undef + redirect-to-literal pattern below is the
// migration shim.

#ifdef XPACT_GC_ROOT_ABI_TAG
    #undef XPACT_GC_ROOT_ABI_TAG
#endif
#ifdef XPACT_EXCEPTION_ABI_TAG
    #undef XPACT_EXCEPTION_ABI_TAG
#endif
#ifdef XPACT_MANGLING_SCHEME_TAG
    #undef XPACT_MANGLING_SCHEME_TAG
#endif
#ifdef XPACT_CVAR_ABI_TAG
    #undef XPACT_CVAR_ABI_TAG
#endif

namespace XCore
{
    inline constexpr ::std::string_view XPACT_GC_ROOT_ABI_TAG     = "Span-based v1";
    inline constexpr ::std::string_view XPACT_EXCEPTION_ABI_TAG   = "Tier1-Shim/Tier2-Direct";
    inline constexpr ::std::string_view XPACT_MANGLING_SCHEME_TAG = "Itanium-LengthPrefixed-v1";
    inline constexpr ::std::string_view XPACT_CVAR_ABI_TAG        = "v1-with-4-reserved-slots";
} // namespace XCore

// Legacy macro spellings -- migrate from XReflectionRuntime.h stub
// (Section 13 locked decision 8). These are kept as #define so the
// existing .gen.cpp files that pin them via static_assert continue
// to link; new code should consume the namespace XCore:: constexpr
// values above.
//
// Per Section 13.1 fix B-MIN4 these SHOULD be migrated away from
// #define everywhere; the macro spellings are a transitional shim.
// The names are intentionally suffixed _LITERAL to distinguish them
// from the namespace XCore:: constexpr forms above -- a .gen.cpp
// that wants the bare string literal writes
// `XPACT_GC_ROOT_ABI_TAG_LITERAL`; a .gen.cpp that wants the
// constexpr-string_view writes `::XCore::XPACT_GC_ROOT_ABI_TAG`.

#ifndef XPACT_GC_ROOT_ABI_TAG_LITERAL
    #define XPACT_GC_ROOT_ABI_TAG_LITERAL     "Span-based v1"
#endif
#ifndef XPACT_EXCEPTION_ABI_TAG_LITERAL
    #define XPACT_EXCEPTION_ABI_TAG_LITERAL   "Tier1-Shim/Tier2-Direct"
#endif
#ifndef XPACT_MANGLING_SCHEME_TAG_LITERAL
    #define XPACT_MANGLING_SCHEME_TAG_LITERAL "Itanium-LengthPrefixed-v1"
#endif
#ifndef XPACT_CVAR_ABI_TAG_LITERAL
    #define XPACT_CVAR_ABI_TAG_LITERAL        "v1-with-4-reserved-slots"
#endif

// =====================================================================
// 4. ConstInit / accessor scaffolding flags.
// =====================================================================
//
// Migrated from XReflectionRuntime.h (Section 13 locked decision 8).
// The .gen.cpp files static_assert these flags at emit-site; the
// type definitions in XReflectionRuntime.h stay where they are.

#ifndef XPACT_WITH_CONSTINIT_XOBJECT
    #define XPACT_WITH_CONSTINIT_XOBJECT 1
#endif

#ifndef XPACT_PROPERTY_HAS_ACCESSORS
    #define XPACT_PROPERTY_HAS_ACCESSORS 1
#endif

// =====================================================================
// 5. Build-config-aware assertion macros (fix M-15 + fix Rev 3 M6).
// =====================================================================
//
// Per Section 13.1:
//
//   XPACT_CHECK (Debug/Dev):     do { if (XPACT_UNLIKELY(!(Expr)))
//                                       ::XCore::HAL::CheckFailed(
//                                           #Expr, __FILE__, __LINE__);
//                                   } while (0)
//   XPACT_CHECK (Shipping/Test): ((void)0)
//
//   XPACT_CHECK_SL is the std::source_location overload (Rev 3 fix M6).
//   Preferred for new code; the per-call .rdata duplicate-string
//   emission cost is borne once at the linker rather than once per
//   check site per patched DLL revision. See XAssertionMacros.h.
//
//   XPACT_ASSUME -- "the compiler may assume this holds". Debug/Dev
//   degrades to XPACT_CHECK (so a developer testing the assertion
//   sees a clean abort); Shipping uses __assume(Expr) on MSVC and
//   __builtin_assume(Expr) on Clang/GCC so the optimiser can rely on
//   the invariant. The cost / benefit is documented in Section 13.3
//   ("XPACT_ASSUME is the explicit branch-pruning macro for the rare
//   site where we genuinely know the invariant").
//
// In Shipping XPACT_CHECK is `((void)0)`, NOT `__assume(Expr)`. Per
// Section 13.3: "The Prime Directive forbids silent corruption that
// would arise from a check expression getting wrong-branch-pruned
// (UE's checkSlow uses __assume; XPact does not)."

#if XPACT_DEBUG
    #define XPACT_CHECK(Expr) \
        do { if (XPACT_UNLIKELY(!(Expr))) ::XCore::HAL::CheckFailed(#Expr, __FILE__, __LINE__); } while (0)
    #if XPACT_HAS_SOURCE_LOCATION
        #define XPACT_CHECK_SL(Expr) \
            do { if (XPACT_UNLIKELY(!(Expr))) ::XCore::HAL::CheckFailedSL(); } while (0)
    #else
        #define XPACT_CHECK_SL(Expr) XPACT_CHECK(Expr)
    #endif
    #define XPACT_ASSUME(Expr) XPACT_CHECK(Expr)

#elif XPACT_DEVELOPMENT
    // Same body as Debug. The split exists so a future revision can
    // diverge if needed (e.g., emit a soft-warning rather than an
    // abort in Development), without disturbing the Debug path.
    #define XPACT_CHECK(Expr) \
        do { if (XPACT_UNLIKELY(!(Expr))) ::XCore::HAL::CheckFailed(#Expr, __FILE__, __LINE__); } while (0)
    #if XPACT_HAS_SOURCE_LOCATION
        #define XPACT_CHECK_SL(Expr) \
            do { if (XPACT_UNLIKELY(!(Expr))) ::XCore::HAL::CheckFailedSL(); } while (0)
    #else
        #define XPACT_CHECK_SL(Expr) XPACT_CHECK(Expr)
    #endif
    // Per Section 13.1: XPACT_ASSUME in Development is `((void)0)`,
    // NOT XPACT_CHECK. The wording is "Debug = CheckFailed; Dev =
    // ((void)0) for ASSUME; Shipping = __assume". The Dev split
    // exists so the ASSUME-marked invariant does not pay the check
    // cost in Dev binaries (which run under perf-tuned settings).
    #define XPACT_ASSUME(Expr) ((void)0)

#else  // Shipping / Test
    #define XPACT_CHECK(Expr)    ((void)0)
    #define XPACT_CHECK_SL(Expr) ((void)0)
    #if defined(_MSC_VER)
        #define XPACT_ASSUME(Expr) __assume(Expr)
    #elif defined(__clang__)
        #define XPACT_ASSUME(Expr) __builtin_assume(Expr)
    #elif defined(__GNUC__)
        // GCC has no __builtin_assume; the canonical pattern is
        // if (!Expr) __builtin_unreachable(). Expressed as a void-
        // returning expression to match the macro contract.
        #define XPACT_ASSUME(Expr) ((Expr) ? ((void)0) : __builtin_unreachable())
    #else
        #define XPACT_ASSUME(Expr) ((void)0)
    #endif
#endif

// =====================================================================
// 6. Cache-line padding constant (fix B-M3).
// =====================================================================
//
// C++17 std::hardware_destructive_interference_size where available;
// platform-correct fallback otherwise. On x86_64 the fallback is 64
// (matches the L1 cache line size); on ARM64 (Snapdragon XR2 Gen 2)
// the fallback is 128 to match the destructive prefetch unit (the L1
// line is 64 bytes but the prefetch unit pulls 128).
//
// Used throughout XCore-4a wherever cross-thread-shared data is
// padded: TMpscQueue head + tail, atomic counters, FStatShard arrays.

namespace XCore
{
#if defined(__cpp_lib_hardware_interference_size) && __cpp_lib_hardware_interference_size >= 201703L
    inline constexpr ::SIZE_T XPACT_CACHE_LINE_SIZE = ::std::hardware_destructive_interference_size;
#elif defined(__aarch64__) || defined(_M_ARM64)
    inline constexpr ::SIZE_T XPACT_CACHE_LINE_SIZE = 128;
#else
    inline constexpr ::SIZE_T XPACT_CACHE_LINE_SIZE = 64;
#endif
} // namespace XCore

// =====================================================================
// 7. Module-safe TLS scaffolding (fix B-C1).
// =====================================================================
//
// Win64 (hot-reloadable DLL host) + Android (hot-reload-capable
// variant TBD) route TLS through a slot-allocator pattern. Linux
// server (no DLL hot-reload at MVP) falls through to native
// thread_local.
//
// Raw thread_local in a DLL that is unloaded leaves fiber-local-
// storage entries pointing at freed code; the slot-allocator pattern
// (UE's TModuleSafeThreadLocal equivalent) routes through a globally
// allocated TLS slot whose destruction is sequenced with module
// unload.
//
// TModuleSafeThreadLocal<T> is forward-declared here; the
// implementation lives in the Platform HAL (XCore-4a Step 2). The
// forward declaration is enough for the XPACT_TLS_MODULE_SAFE macro
// to expand at every TU that consumes it; the type is defined when
// the consumer also includes the Platform HAL header.

namespace XCore::HAL
{
    template<typename T>
    class TModuleSafeThreadLocal;  // defined in XCore-4a Step 2 Platform HAL
} // namespace XCore::HAL

// Platform flag detection. XCore-4a Step 2 will ship
// XPlatformProperties.h which defines XPACT_PLATFORM_WIN64 etc.
// flags. Until then, sentry via raw _WIN64 / __ANDROID__ / __linux__
// macros so the TLS macro expansion is correct from day one.
#if !defined(XPACT_PLATFORM_WIN64) && (defined(_WIN64) || defined(_WIN32))
    #define XPACT_PLATFORM_WIN64 1
#endif
#if !defined(XPACT_PLATFORM_ANDROID) && defined(__ANDROID__)
    #define XPACT_PLATFORM_ANDROID 1
#endif
#if !defined(XPACT_PLATFORM_LINUX) && defined(__linux__) && !defined(__ANDROID__)
    #define XPACT_PLATFORM_LINUX 1
#endif
// Defaults so the conditional below is well-defined.
#ifndef XPACT_PLATFORM_WIN64
    #define XPACT_PLATFORM_WIN64 0
#endif
#ifndef XPACT_PLATFORM_ANDROID
    #define XPACT_PLATFORM_ANDROID 0
#endif
#ifndef XPACT_PLATFORM_LINUX
    #define XPACT_PLATFORM_LINUX 0
#endif

#if XPACT_PLATFORM_WIN64 || XPACT_PLATFORM_ANDROID
    // Module-safe path: forward-declared template type. The
    // implementation (XCore-4a Step 2) provides operator-> and a
    // lazy-init Get() so call sites can write
    //     g_shard.Get().counter++;
    // The macro emits a declaration; the type's storage is owned by
    // the Platform HAL's TLS-slot table.
    #define XPACT_TLS_MODULE_SAFE(Type, Name) \
        ::XCore::HAL::TModuleSafeThreadLocal<Type> Name
#else  // Linux server (no DLL hot-reload in MVP)
    #define XPACT_TLS_MODULE_SAFE(Type, Name) \
        thread_local Type Name
#endif

// =====================================================================
// 8. GC-aware container layout-frozen sentinel (Section 13 / fix M-12).
// =====================================================================
//
// Every GC-aware container type carries
//     X_DECLARE_GC_AWARE_CONTAINER(T)
// inside its class body. The macro injects:
//   * static constexpr bool x_container_layout_frozen = true (sentinel
//     XLiveCoding's patch-validation pass reads on patch)
//   * static_assert(sizeof(T::m_rootSpan) == 32, ...) (layout drift
//     guard against the fix M-12 32-byte XGCRootSpan).
//
// Note that this macro intentionally references T::m_rootSpan, which
// must exist as a private XGCRootSpan field in every GC-aware
// container. The constraint is documented; container authors who
// forget it get a compile error pointing at the missing field.
// `sizeof(T::m_rootSpan)` is well-formed for non-static data members
// because the operand of `sizeof` is unevaluated; no instance of T
// is required.

#define X_DECLARE_GC_AWARE_CONTAINER(T)                                                 \
    static constexpr bool x_container_layout_frozen = true;                             \
    static_assert(sizeof(T::m_rootSpan) == 32,                                          \
                  "XGCRootSpan layout drift; expected 32 bytes per fix M-12")

// =====================================================================
// 9. XPACT_SIMPATH (Section 6.3 / Section 13).
// =====================================================================
//
// Defined to 1 by `XSimPathMathOverrides.h` when included by a
// sim-path TU. The default (0) is provided by XCoreDefines.h; the
// macro is documented here for cross-reference but no further work
// is done at this layer. Math + container code consults XPACT_SIMPATH
// directly via `#if XPACT_SIMPATH` blocks.
