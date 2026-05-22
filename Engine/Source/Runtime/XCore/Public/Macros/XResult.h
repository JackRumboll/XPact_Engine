// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XResult.h -- Result<T, E> alias (Section 13.1 / fix B-M6).
// =====================================================================
//
// XCore-4a Rev 3, Section 13.1.
//
// The user-facing error-returning type. Every error-returning surface
// in XCore-4a (TArray::At, FString::ToInt32, FMemory::Malloc-with-
// policy, FDateTime::FromUnixTimestamp, etc.) returns Result<T, E>.
//
// Aliasing strategy:
//   * C++23 path: `using Result<T, E> = std::expected<T, E>;` -- pure
//                 alias to the C++ standard type. A future C++23-
//                 returning third-party library can compose with
//                 XCore-4a's Result without conversion adapters.
//   * C++20 path: `using Result<T, E> = tl::expected<T, E>;` --
//                 vendored polyfill at
//                 Engine/Source/ThirdParty/tl_expected/expected.hpp
//                 (TartanLlama/expected; CC0 / MIT dual; vendored at
//                 Phase 1d Subagent A). The header is fully
//                 constexpr-compatible and ABI-identical to
//                 std::expected for trivially-destructible T + E,
//                 which is the case for every XCore-4a Result
//                 instantiation (every E is one of the uint8-backed
//                 enums in XErrorTypes.h; every T is either a value
//                 type or a XObject pointer).
//
// The C++23 detection is via `__cpp_lib_expected >= 202202L` (the
// feature-test macro defined when <expected> ships with std::expected
// at the locked Rev 13.7 / Rev 3 wording). GCC 12+, Clang 16+, MSVC
// 19.33+ ship it; most current build profiles are C++20 + polyfill.
//
// API SURFACE -- IDENTICAL ON BOTH BRANCHES.
//
// User code writes `XCore::Result<T, E>` regardless of branch. The
// polyfill mirrors std::expected's value() / error() / operator* /
// operator-> / has_value() / and_then() / or_else() / transform()
// surface. The XCore::Unexpected<E>() factory wraps the branch-
// specific `unexpected<E>` constructor.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XErrorTypes.h"  // brings in FParseError, FBoundsError, etc., so consumers can declare Result<T, FBoundsError> without an extra include
#include <utility>               // std::forward used by the Unexpected<E>() factory

#if defined(__cpp_lib_expected) && __cpp_lib_expected >= 202202L
    #include <expected>

    namespace XCore
    {
        // -------------------------------------------------------------
        // Result<T, E> -- C++23 std::expected alias.
        //
        // Pure type alias; no behaviour wrapping. Consumers may use
        // .value(), .error(), operator*, operator->, has_value(),
        // and(), or(), transform(), and the rest of the
        // std::expected surface freely.
        // -------------------------------------------------------------
        template<typename T, typename E>
        using Result = ::std::expected<T, E>;

        // -------------------------------------------------------------
        // Convenience helpers mirroring std::expected's `unexpected`
        // factory. Result<T, E> callers wishing to return an error
        // typically write `return XCore::Unexpected(FBoundsError::...);`
        // which is more readable than the std::unexpected{...} ctor.
        // -------------------------------------------------------------
        template<typename E>
        [[nodiscard]] constexpr ::std::unexpected<typename ::std::decay<E>::type> Unexpected(E&& Err) noexcept
        {
            return ::std::unexpected<typename ::std::decay<E>::type>{ ::std::forward<E>(Err) };
        }

    } // namespace XCore

#else  // C++20 fallback path -- vendored tl::expected polyfill

    // The vendored header lives at
    //     /Engine/Source/ThirdParty/tl_expected/expected.hpp
    // and is reachable as `expected.hpp` because the tl_expected
    // module (a ModuleType.ThirdParty pseudomodule declared by
    // /Engine/Source/ThirdParty/tl_expected/tl_expected.Build.toml)
    // exposes its own directory as a public include path. XCore
    // depends on tl_expected via `public_dependency_modules` so
    // the include search path is propagated transitively to every
    // XCore consumer.
    //
    // The header is self-contained, header-only, fully constexpr,
    // and ABI-compatible with std::expected for the trivially-
    // destructible T + E case used throughout XCore-4a (every error
    // E is a uint8-backed enum, every value T is a primitive or a
    // value-type aggregate).
    //
    // Per the spec Section 2 exception-lowering row: "Result<T, E>
    // is a type alias for C++23 std::expected<T, E>; on C++20 builds
    // the engine vendors tl::expected (header-only MIT) as polyfill
    // at XCore/Public/Polyfill/TLExpected.h." The spec quotes a
    // longer placement hint and a longer include form; the actually
    // landed location follows the ThirdParty conventions (one
    // subdirectory per vendored project) and the include form is
    // the shorter `expected.hpp` -- the XBT TOML parser (Toolchain
    // Contract Rev 13 Section 2.1) bans `..` traversal in
    // include-path declarations, so the engine-source-root cannot
    // be made a global include root. Trade-off documented here for
    // the architecture review.
    #include "expected.hpp"

    namespace XCore
    {
        // -------------------------------------------------------------
        // Result<T, E> -- C++20 polyfill alias to tl::expected.
        //
        // ABI lock: sizeof(Result<int32_t, FParseError>) is
        // platform-dependent (tl::expected's discriminator + alignment
        // padding produces 8 bytes on x86_64 for trivially-destructible
        // T + E pairs, matching std::expected's canonical layout).
        // Tests in FString.Tests assert the layout matches expectations
        // at instantiation time rather than asserting a fixed sizeof
        // value -- the underlying polyfill's choice is the contract,
        // not a XCore-4a-imposed shape.
        // -------------------------------------------------------------
        template<typename T, typename E>
        using Result = ::tl::expected<T, E>;

        // -------------------------------------------------------------
        // Unexpected factory -- mirrors the C++23 std::unexpected
        // factory; wraps tl::unexpected on the C++20 path.
        //
        // `decay` matches std::unexpected's CTAD-decay shape so a
        // value-category-preserving call (rvalue passed -> rvalue
        // stored; lvalue passed -> value copied) is exact.
        // -------------------------------------------------------------
        template<typename E>
        [[nodiscard]] constexpr ::tl::unexpected<typename ::std::decay<E>::type> Unexpected(E&& Err) noexcept
        {
            return ::tl::unexpected<typename ::std::decay<E>::type>{ ::std::forward<E>(Err) };
        }
    } // namespace XCore

#endif  // __cpp_lib_expected
