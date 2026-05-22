// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// TArrayCore.cpp -- explicit-template-instantiation TU + ABI locks.
// =====================================================================
//
// XCore-4a Rev 3, Section 5.1 (fix C-3 internal primitive) + Section 5
// test strategy / dependency-graph step 7.
//
// Most of TArrayCore is header-only (the template body is inline in
// TArrayCore.h). This .cpp file does two things:
//
//   1. Static asserts for the ABI lock on the common instantiations
//      (sizeof / alignof). Any future drift in the conceptual layout
//      ("T* m_data + int32 m_num + int32 m_max + EBO allocator") would
//      land here as a compile-time stop.
//
//   2. Explicit template instantiations for the common primitive
//      element types. These are NOT strictly required (the header
//      provides the implementations and any TU that uses a particular
//      instantiation will get it via implicit instantiation); they
//      ship here as a fail-fast unit -- if any of the common
//      instantiations stops compiling, the failure surfaces in THIS
//      TU rather than in some downstream consumer's TU where the
//      cause is harder to diagnose.
//
// The common types instantiated:
//   * int32   -- TArray<int32> (the canonical "int array" used by
//                gameplay code, container-of-IDs, etc.).
//   * int64   -- TArray<int64> (TimeStamps, FName SerialNumber sums).
//   * uint8   -- TArray<uint8> (byte buffers; TBitArray backing via
//                uint64 also tested below).
//   * uint64  -- TArray<uint64> (TBitArray backing storage; bitmask
//                tables in CVar / Stat).
//   * char    -- TArray<char> (the FString backbone; Phase 1d FString
//                will declare TArrayCore<char> as its storage).
//
// =====================================================================

#include "Containers/TArrayCore.h"
#include "Containers/DefaultAllocator.h"
#include "Macros/XCoreTypes.h"
#include "HAL/FMemTag.h"

#include <cstddef>          // offsetof
#include <type_traits>

namespace
{
    // -----------------------------------------------------------------
    // ABI locks for the common TArrayCore instantiations.
    //
    // The conceptual layout per Section 5.1:
    //   T*       m_data    (8 bytes on 64-bit)
    //   int32    m_num     (4 bytes)
    //   int32    m_max     (4 bytes)
    //   AllocatorT m_alloc (2 bytes for DefaultAllocator)
    //
    // Without XPACT_NO_UNIQUE_ADDRESS the struct sums to 8 + 4 + 4 + 2
    // = 18 bytes, rounded up to 24 by the 8-byte struct alignment
    // (max alignof of m_data = 8). With XPACT_NO_UNIQUE_ADDRESS the
    // 2-byte allocator MAY overlap with the trailing padding behind
    // m_max -- if the compiler chooses to fold, sizeof drops to 16.
    //
    // Both 16 and 24 are valid outcomes per the C++ standard's wording
    // for [[no_unique_address]]; the attribute is a HINT, not a
    // mandate. The static asserts below accept either size -- the
    // engineering-principles-correct constraint is "the struct is no
    // larger than 24 bytes", which catches a future drift (e.g.,
    // adding a third pointer field) without locking the EBO outcome
    // to a single compiler's behaviour.
    //
    // Per Section 5.5 row 6's wording ("~10-80 KB saved per scene at
    // ~10k TArray instances"), the spec ANTICIPATES the EBO outcome
    // varies across compilers; the saving is per-instance 0-8 bytes,
    // with 8 bytes on compilers that fold and 0 bytes on those that
    // don't. The TArray contract doesn't depend on the outcome.
    // -----------------------------------------------------------------

    using ::XCore::DefaultAllocator;

    template<typename T>
    using ArrayCore = ::XCore::Detail::TArrayCore<T, DefaultAllocator>;

    // The struct alignment is 8 (max of m_data's pointer-alignment).
    // After the explicit fields sum to 18, the compiler must round up
    // to a multiple of 8 -- so the minimum legal sizeof is 24. With
    // [[no_unique_address]] elision the compiler may shrink to 16.
    // We accept either outcome.
    static_assert(sizeof(ArrayCore<::int32>) == 16 || sizeof(ArrayCore<::int32>) == 24,
                  "TArrayCore<int32, DefaultAllocator> ABI lock: 16 (EBO folded) or 24 (no EBO)");

    static_assert(sizeof(ArrayCore<::int64>) == 16 || sizeof(ArrayCore<::int64>) == 24,
                  "TArrayCore<int64, DefaultAllocator> ABI lock: 16 or 24");

    static_assert(sizeof(ArrayCore<::uint8>) == 16 || sizeof(ArrayCore<::uint8>) == 24,
                  "TArrayCore<uint8, DefaultAllocator> ABI lock: 16 or 24");

    static_assert(sizeof(ArrayCore<::uint64>) == 16 || sizeof(ArrayCore<::uint64>) == 24,
                  "TArrayCore<uint64, DefaultAllocator> ABI lock: 16 or 24");

    static_assert(sizeof(ArrayCore<char>)    == 16 || sizeof(ArrayCore<char>)    == 24,
                  "TArrayCore<char, DefaultAllocator> ABI lock: 16 or 24 "
                  "(FString backbone; Phase 1d FString relies on this size class)");

    static_assert(sizeof(ArrayCore<void*>)   == 16 || sizeof(ArrayCore<void*>)   == 24,
                  "TArrayCore<void*, DefaultAllocator> ABI lock: 16 or 24 "
                  "(GC-aware TArray<T*> backing storage)");

    static_assert(alignof(ArrayCore<::int32>) == 8,
                  "TArrayCore alignment lock: 8-byte (pointer alignment)");
} // anonymous namespace

// =====================================================================
// Explicit template instantiations.
//
// Per the C++ standard (chapter "Explicit instantiation"), an explicit
// instantiation definition `template class Foo<T>;` instantiates EVERY
// member of the class template. This is the canonical pattern for
// header-mostly templates: keep the implementations in the header so
// implicit instantiation works at any call site, then explicit-
// instantiate the common types in a single TU so the codegen is
// produced exactly once for the common cases.
//
// XCORE-4A PHASE 1C GATE: the explicit instantiation is GATED on
// `__cpp_lib_expected >= 202202L` (i.e., the C++23 std::expected
// feature). The reason: TArrayCore::At returns Result<T, E> which is
// the C++23 std::expected on toolchains that ship it, or a
// static_assert-placeholder on C++20 toolchains until tl::expected is
// vendored (see XResult.h). The whole-class explicit instantiation
// would force the placeholder's static_assert to fire because it
// instantiates At() unconditionally.
//
// On C++23 toolchains (GCC 12+, Clang 16+, MSVC 19.33+ with
// /std:c++latest or /std:c++23): the whole-class instantiation fires
// and emits codegen for every method once per common T.
//
// On C++20 toolchains (the current default at Phase 1c): the explicit
// instantiation is SKIPPED. Each consumer of TArrayCore<T> gets
// implicit instantiation of the methods it calls; At() remains
// uninstantiated until a caller tries to use it, at which point the
// XResult.h placeholder fires its TODO diagnostic.
//
// Once tl::expected is vendored (Phase 1d), this gate can be widened
// to always-on -- the polyfill will fully implement Result<T, E> on
// C++20 toolchains and the whole-class instantiation will succeed
// without the C++23-only check.
//
// Note: explicit instantiation also means that if any member of the
// template fails to compile for the given T, the failure surfaces in
// this TU. That is the fail-fast contract for the C++23 path.
// =====================================================================

#if defined(__cpp_lib_expected) && __cpp_lib_expected >= 202202L

namespace XCore::Detail
{
    template class TArrayCore<::int32,  ::XCore::DefaultAllocator>;
    template class TArrayCore<::int64,  ::XCore::DefaultAllocator>;
    template class TArrayCore<::uint8,  ::XCore::DefaultAllocator>;
    template class TArrayCore<::uint64, ::XCore::DefaultAllocator>;
    template class TArrayCore<char,     ::XCore::DefaultAllocator>;
    template class TArrayCore<void*,    ::XCore::DefaultAllocator>;
} // namespace XCore::Detail

#endif  // __cpp_lib_expected
// TODO(Phase 1d): once tl::expected is vendored, remove the
// __cpp_lib_expected gate. The polyfill will make Result<T, E> work
// on C++20 toolchains and the whole-class explicit instantiation
// will be valid on every supported configuration.
