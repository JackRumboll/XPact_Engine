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
//                 vendored polyfill at ThirdParty/tl_expected/expected.hpp
//                 (license: CC0 / MIT dual; per spec Section 2 exception
//                 lowering row "the engine vendors `tl::expected`
//                 header-only MIT as polyfill").
//
// The vendoring step for tl::expected is a follow-up Phase 1b/c task;
// for Phase 1a the C++20 branch is a static_assert(false, ...)
// placeholder with a TODO marker. This is documented in the dispatch:
// "for Phase 1a, the `#else` branch can be a static_assert(false,
// 'C++20 fallback pending vendor of tl::expected') placeholder with
// a clear TODO."
//
// The C++23 detection is via `__cpp_lib_expected >= 202202L` (the
// feature-test macro defined when <expected> ships with std::expected
// at the locked Rev 13.7 / Rev 3 wording). GCC 12+, Clang 16+, MSVC
// 19.33+ ship it; for now most build profiles are C++20 + polyfill.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XErrorTypes.h"  // brings in FParseError, FBoundsError, etc., so consumers can declare Result<T, FBoundsError> without an extra include
#include <utility>               // std::forward used by the Unexpected<E>() factory below (and by tl::expected's polyfill path); Phase-1b errata fix

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
        [[nodiscard]] constexpr ::std::unexpected<E> Unexpected(E&& Err) noexcept
        {
            return ::std::unexpected<E>{ ::std::forward<E>(Err) };
        }

    } // namespace XCore

#else  // C++20 fallback path

    // TODO(Phase 1b/c): vendor tl::expected from
    // https://github.com/TartanLlama/expected at the locked commit
    // and place the amalgamated header at
    // Engine/Source/ThirdParty/tl_expected/expected.hpp. License: CC0
    // dual MIT. The header is fully constexpr-compatible and ABI-
    // identical to std::expected for trivially-destructible T + E,
    // which is the case for every XCore-4a Result instantiation
    // (every E is one of the uint8-backed enums in XErrorTypes.h;
    // every T is either a value type or a XObject pointer).
    //
    // The placeholder static_assert below is the explicit
    // engineering-principles-compliant marker: this code WILL NOT
    // compile to a USABLE Result<T, E> until the vendoring step
    // lands. The static_assert is INSIDE the template body so it
    // fires only on instantiation; merely including XResult.h on a
    // pre-C++23 toolchain is fine (downstream headers that forward-
    // declare Result<T, E> by name still compile).
    namespace XCore
    {
        namespace Detail
        {
            // Two-step `false` dependent on the template parameter so
            // the static_assert is delayed until instantiation. A
            // bare `static_assert(false, ...)` at namespace scope or
            // at non-dependent template scope fires unconditionally,
            // which would make XResult.h un-includable on C++20
            // toolchains -- not the intended behaviour.
            template<typename> inline constexpr bool ResultUnvendoredFallback = false;
        } // namespace Detail

        // Result<T, E> -- C++20 polyfill placeholder.
        //
        // The template body is empty; any instantiation triggers the
        // dependent static_assert that names the vendoring TODO.
        // Downstream headers may forward-declare or name-mention
        // Result<T, E> freely without triggering it; only an actual
        // use (default-construct, return, value-access) fires the
        // diagnostic.
        template<typename T, typename E>
        class Result
        {
            static_assert(Detail::ResultUnvendoredFallback<T>,
                "XCore-4a Result<T, E> C++20 fallback requires tl::expected "
                "vendoring (TODO Phase 1b/c). Either upgrade to a toolchain "
                "that ships std::expected (GCC 12+, Clang 16+, MSVC 19.33+) "
                "or land the ThirdParty/tl_expected/expected.hpp polyfill "
                "per spec Section 13.1.");
        };

        // Unexpected factory -- placeholder. Mirrors the C++23 path
        // signature so callers can compile against both branches.
        // Body would forward to tl::unexpected once the polyfill is
        // vendored; the current return-type-deduced form is empty
        // and instantiation triggers the same Detail diagnostic via
        // its declarator.
        template<typename E>
        [[nodiscard]] auto Unexpected(E&& Err) noexcept
        {
            static_assert(Detail::ResultUnvendoredFallback<E>,
                "XCore-4a Unexpected<E> C++20 fallback requires tl::expected "
                "vendoring (TODO Phase 1b/c).");
            // Unreachable; the static_assert fires first. Body kept
            // so the auto return-type deduction is well-formed.
            return ::std::forward<E>(Err);
        }
    } // namespace XCore

#endif  // __cpp_lib_expected
