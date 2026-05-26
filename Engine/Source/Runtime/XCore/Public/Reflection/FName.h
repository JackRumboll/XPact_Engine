// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FName.h -- the case-sensitive UTF-8 interned-name handle (XCore-4b §4).
// =====================================================================
//
// XCore-4b Rev 3, Section 4 ("FName Design") + Section 11.1 ABI tag
// `XPACT_FNAME_LAYOUT_TAG = "FName-v1: 4+4 / Index+SerialNumber / 8-byte
// total / 4-byte aligned"`.
//
// FName is the 8-byte interned-name handle every reflected name in the
// engine flows through. The 8-byte handle layout was forward-declared at
// XCore-4a §11.8 (XCoreFwd.h) so containers (TMap, TSet) could compile
// against it without a circular dependency on this XCore-4b runtime.
// THIS header ships the COMPLETE struct with the spec §4.1 public API.
//
// HANDLE LAYOUT (locked at XCore-4a §11.8 + Contract Rev 13.7 §6; the
// static_asserts at the bottom of this file ARE the ABI contract):
//
//   bytes 0-3  Index         (uint32) intern-table entry index
//                                     encoding: `(shard_id << 24) | shard_local_entry_id`
//                                     Index == 0  ==>  NAME_None
//   bytes 4-7  SerialNumber  (uint32) numbered-name suffix
//                                     0  ==>  no suffix
//                                     N  ==>  "{base}_{N}"
//
// SEMANTICS (per §4.5):
//   * Case-sensitive bytewise comparison and bytewise hashing.
//   * NO Unicode normalization (no NFC; no case folding).
//   * Equality compares the two uint32 fields as a single 8-byte handle.
//   * GetTypeHash(FName) is FXxh3-64 over the (Index, SerialNumber) bytes.
//
// CONSTRUCTION:
//   * FName()                                 ==> NAME_None
//   * FName(const char* utf8)                  ==> intern the bytes
//   * FName(const char* utf8, int32 ByteLen)   ==> intern the byte range
//   * FName(FStringView)                       == FName(const char*, len)
//                                                  (XPact lacks FStringView in
//                                                   XCore-4a; the spec's FStringView
//                                                   overload is provided as the
//                                                   `(const char*, int32)` form.)
//   * FName(FString)                           ==> FName(s.ToUtf8Ptr(), s.LenBytes())
//   * FName::WithNumber(name, n)               ==> same Index, SerialNumber=n
//
// NUMBERED-NAME PRODUCTION (§4.4):
//   * The base name's bytes ("Actor") are interned exactly once.
//   * Subsequent numbered instances re-use the same Index; only the
//     SerialNumber field differs.
//   * SerialNumber == 0 means "no suffix" (the plain base name "Actor").
//   * SerialNumber == 1 means "Actor_1", etc.
//   * No internal/external offset hack (XPact rejects UE's
//     NAME_NO_NUMBER_INTERNAL == 0 quirk per §4.10).
//
// HOT-RELOAD SURVIVAL (§4.7):
//   * The FNamePool is a process-singleton; entries are never relocated.
//   * An FName created before a DLL hot-patch resolves to the same Index
//     after the patch.
//   * A patched DLL creating an FName with the same bytes as an existing
//     entry resolves to the existing Index (no duplicate-entry allocation).
//
// CROSS-PROCESS PERSISTENCE: NOT a goal. FName.Index is unstable across
// runs; only valid within a single process lifetime. On-disk persistence
// is XSerialization's responsibility (string-table-indexed; resolves to
// fresh Index at load time).
//
// THREAD SAFETY (§4.3): the intern-table read path acquires the per-shard
// FRWLock in shared mode (multiple readers concurrent); writes acquire
// exclusive. The FName handle itself is a trivially-copyable POD.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"
#include "Macros/XCoreFwd.h"               // Forward decl + GetTypeHash signature

#include <bit>                              // std::bit_cast (FIX-A6 fast-equal)
#include <cstddef>                          // offsetof
#include <cstdint>                          // std::uint64_t (FIX-A6 fast-equal)
#include <type_traits>                      // is_trivially_copyable etc.

// Forward-declare FString (full include avoided to keep this header lean;
// FName.h is consumed extensively by reflection-heavy translation units
// and dragging FString.h's <format>/<string> includes into every one is
// avoidable). The FString-returning ToFString() body lives in FName.cpp
// where FString.h is fully included.
namespace XCore { class FString; }

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // Sentinel index value for NAME_None. Per §4.10 ("SerialNumber == 0
    // directly means 'no suffix'; Index == 0 directly means NAME_None").
    //
    // The intern-table reserves Index == 0 as the NAME_None slot; no
    // string is ever interned at Index 0. The shard count is 256 with
    // shard 0 being the only one that "owns" entry 0 (NAME_None).
    // -----------------------------------------------------------------
    inline constexpr ::uint32 kNoneIndex = 0;

    // -----------------------------------------------------------------
    // FName -- the 8-byte case-sensitive interned-name handle.
    //
    // Layout-locked at XCore-4a §11.8 + Contract Rev 13.7 §6
    // (XPACT_FNAME_LAYOUT_TAG). Any change to this struct's byte layout
    // ABI-breaks every TMap<FName, V> instantiation engine-wide.
    //
    // NO virtual methods (hot-reload safety; XCore-4b §4.7).
    // Standard-layout + trivially-copyable so the type can flow through
    // C# IL2CPP boundaries by value.
    // -----------------------------------------------------------------
    struct FName
    {
        ::uint32 Index;         //  0  +4   intern-table entry index (0 = NAME_None)
        ::uint32 SerialNumber;  //  4  +4   numbered-name suffix (0 = no suffix)

        // -------------------------------------------------------------
        // Construction.
        //
        // The default ctor is constexpr so NAME_None is constexpr-
        // available from any EInitPhase (no allocator dependency for
        // the sentinel).
        //
        // String-taking ctors require EInitPhase >= PostStaticInit
        // (the allocator must be live). A debug-only XPACT_CHECK
        // inside the .cpp body enforces this; in Shipping the check
        // compiles out and an early-phase call is UB (mirrors the
        // FString discipline).
        // -------------------------------------------------------------

        // Default ctor: NAME_None. Constexpr; usable from any phase.
        constexpr FName() noexcept
            : Index(kNoneIndex), SerialNumber(0) {}

        // Internal ctor used by the intern-table to construct a known
        // (Index, SerialNumber) pair without going through interning
        // (e.g., when applying WithNumber to an existing FName). Public
        // so the FNamePool can construct results; do not call from
        // user code -- the Index must already be a valid intern-table
        // slot, otherwise IsValid() / ToString() are UB.
        constexpr FName(::uint32 InIndex, ::uint32 InSerialNumber) noexcept
            : Index(InIndex), SerialNumber(InSerialNumber) {}

        // Signed-integer overload of the (uint32, uint32) ctor. Exists so
        // brace-list-initialization with integer literals (e.g.,
        // `FName{0, 0}` for NAME_None in a constinit context) prefers
        // THIS overload over the otherwise-better-match
        // `FName(const char*, ::int32)` (which would lose constexpr).
        // Per C++ overload-resolution: `int` literal -> `::int32` is
        // identity (since int32_t == int on the supported targets),
        // beating `int` -> `::uint32` (integral conversion).
        //
        // Rev 3 Round 2 audit FIX-R2-MIN-11: previously, negative
        // values silently cast to large unsigned values (UB-like
        // pollution of the FName.Index encoding which cannot represent
        // negative ids). The XPACT_CHECK guards convert the UB into
        // a clean assertion under Debug/Development. The check
        // compiles out in Shipping; constinit callers like
        // `FName{0, 0}` are unaffected (Index/SerialNumber both >= 0
        // satisfy the predicate at compile time).
        constexpr FName(::int32 InIndex, ::int32 InSerialNumber) noexcept
            : Index(static_cast<::uint32>(InIndex))
            , SerialNumber(static_cast<::uint32>(InSerialNumber))
        {
            XPACT_CHECK(InIndex >= 0);
            XPACT_CHECK(InSerialNumber >= 0);
        }

        // Construct from a NUL-terminated UTF-8 C-string.
        //
        // The byte sequence is interned in the FNamePool; the returned
        // FName's Index points at the interned entry. A trailing `_N`
        // suffix is recognised and split off into SerialNumber per §4.4
        // ("FromString parses trailing `_N` automatically"); a name
        // like "Actor_5" produces FName with Index pointing at "Actor"
        // and SerialNumber == 5.
        //
        // `Utf8` may not be null (XPACT_CHECK enforces). An empty
        // string ("") produces NAME_None.
        //
        // The construction is intentionally NOT `constexpr` -- it
        // touches the intern-table runtime state.
        explicit FName(const char* Utf8) noexcept;

        // Construct from a UTF-8 byte buffer + explicit byte length.
        //
        // The byte sequence is interned verbatim. `Utf8` may not be
        // null if `ByteLen > 0`. `ByteLen == 0` produces NAME_None.
        // `ByteLen > kFNameMaxLength` aborts with a clean diagnostic.
        //
        // Numbered-suffix splitting (per §4.4) is applied: a trailing
        // `_N` is recognised and routed into SerialNumber.
        FName(const char* Utf8, ::int32 ByteLen) noexcept;

        // Construct from an FString. Equivalent to
        // FName(s.ToUtf8Ptr(), s.LenBytes()). Body in FName.cpp so this
        // header does not need to include FString.h.
        explicit FName(const ::XCore::FString& Source) noexcept;

        // -------------------------------------------------------------
        // Numbered-name production (§4.4).
        //
        // WithNumber(name, n) yields an FName with the same Index as
        // `name` and SerialNumber == n. The base name's bytes are NOT
        // re-interned -- this is the load-bearing optimisation that
        // makes 10000 numbered actors consume one intern entry, not
        // 10000.
        //
        // `Suffix == 0` is the "no suffix" case; the result equals
        // `name` exactly (the optimisation is structurally identical
        // to passing the FName through, but the explicit form
        // documents intent).
        // -------------------------------------------------------------
        [[nodiscard]] static constexpr FName WithNumber(FName BaseName, ::uint32 Suffix) noexcept
        {
            return FName(BaseName.Index, Suffix);
        }

        // Construct from a (UTF-8 base name, SerialNumber) pair.
        // Equivalent to WithNumber(FName(Utf8, ByteLen), Suffix).
        FName(const char* Utf8, ::int32 ByteLen, ::uint32 Suffix) noexcept;

        // -------------------------------------------------------------
        // Conversion / accessors.
        // -------------------------------------------------------------

        // Materialise the (base + suffix) string. Body in FName.cpp.
        //
        // SerialNumber == 0 returns the interned bytes verbatim;
        // SerialNumber > 0 returns "{base}_{N}" formatted via
        // FString::Append.
        [[nodiscard]] ::XCore::FString ToString() const;

        // Synonym for ToString(); the spec lists both names because
        // some call sites prefer the explicit "to FString" form.
        [[nodiscard]] ::XCore::FString ToFString() const;

        // Append the (base + suffix) representation to an existing
        // FString. Avoids one heap allocation when the caller already
        // owns a builder. Body in FName.cpp.
        void AppendString(::XCore::FString& Out) const;

        // Raw UTF-8 byte pointer to the interned base name (NUL-terminated;
        // length excluded from the Length field). Does NOT include the
        // numbered suffix; callers needing the full "{base}_{N}" form must
        // call ToString().
        //
        // The pointer is stable for the lifetime of the process (intern-
        // table entries are never relocated). Returns the literal "None"
        // bytes for IsNone() FNames (no allocation; static constexpr).
        [[nodiscard]] const char* GetBaseBytes() const noexcept;

        // Byte length of the interned base name (excludes NUL terminator
        // and excludes the numbered suffix).
        [[nodiscard]] ::int32 GetBaseLength() const noexcept;

        // The serial-number / suffix value. 0 means "no suffix".
        [[nodiscard]] XPACT_FORCEINLINE ::uint32 GetSerialNumber() const noexcept
        {
            return SerialNumber;
        }

        // The intern-table entry index. Process-local; not stable across runs.
        [[nodiscard]] XPACT_FORCEINLINE ::uint32 GetIndex() const noexcept
        {
            return Index;
        }

        // -------------------------------------------------------------
        // Predicates.
        // -------------------------------------------------------------

        // True iff this FName is NAME_None (Index == 0, SerialNumber == 0).
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool IsNone() const noexcept
        {
            return Index == kNoneIndex && SerialNumber == 0;
        }

        // True iff a numbered suffix is present (SerialNumber > 0).
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool IsNumbered() const noexcept
        {
            return SerialNumber != 0;
        }

        // True iff Index points at a valid intern-table entry.
        //
        // For Index == 0 (NAME_None) returns true unconditionally.
        // For Index > 0 consults the FNamePool to verify the entry
        // exists. Used by diagnostic paths and the assertion harness;
        // NOT cheap (touches the pool's slot table). Body in FName.cpp.
        [[nodiscard]] bool IsValid() const noexcept;

        // -------------------------------------------------------------
        // Equality + ordering.
        //
        // Equality is byte-exact over the 8-byte handle. Two FNames
        // referring to identical UTF-8 bytes with identical suffixes
        // MUST share the same Index (because the intern-table is the
        // single source of truth) so byte-equality of the handle is
        // equivalent to byte-equality of the underlying strings.
        //
        // Ordering is index-then-serial; intended for sort-stable use
        // (e.g., FName-keyed maps that need a deterministic iteration
        // order independent of insertion order). The ordering is NOT
        // lexicographic over the underlying string bytes -- it is
        // ordering over the intern-table-allocated indices.
        // -------------------------------------------------------------

        // XCore-4b Subagent A FIX-A6: single 64-bit-load equality.
        //
        // The two-field compare (`Index == Other.Index && SerialNumber
        // == Other.SerialNumber`) is structurally identical to a single
        // 8-byte compare (FName is 8 bytes; standard-layout; trivially-
        // copyable per the static_asserts below). The bit_cast form
        // expresses the intent so the compiler reliably emits ONE
        // 64-bit cmp instead of two 32-bit cmps + a boolean AND. UE
        // does the same at NameTypes.h (FName::CompareIndexes uint64
        // load pattern).
        //
        // bit_cast is constexpr-safe (C++20 [bit.cast]/2: trivially-
        // copyable + same-sizeof source + destination) so operator==
        // remains usable in constant-evaluated contexts.
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator==(FName Other) const noexcept
        {
            return ::std::bit_cast<::std::uint64_t>(*this)
                == ::std::bit_cast<::std::uint64_t>(Other);
        }

        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator!=(FName Other) const noexcept
        {
            return ::std::bit_cast<::std::uint64_t>(*this)
                != ::std::bit_cast<::std::uint64_t>(Other);
        }

        // Rev 3 Round 2 audit FIX-R2-MIN-6: single 64-bit compare
        // paralleling the operator== bit_cast pattern (Subagent A
        // FIX-A6). The spec contract is "Index then SerialNumber"
        // ordering (§4.5); for a single 64-bit compare to produce that
        // ordering, Index must occupy the HIGH 32 bits of the composed
        // uint64.
        //
        // Why not std::bit_cast directly. On little-endian targets the
        // bit_cast<uint64>(FName) places the low-address bytes (Index)
        // into the LOW 32 bits of the uint64, with SerialNumber in
        // the high 32 bits. Comparing such uint64s would order by
        // SerialNumber first -- the opposite of the spec. The
        // compose-via-shift-and-or pattern below is endianness-
        // independent: Index is placed in the high 32 bits explicitly,
        // so the compare matches the spec on every architecture. The
        // ALU sequence is one shift + one or + one cmp per side --
        // identical to what a hypothetical (correct) bit_cast variant
        // would produce after the byte-swap fixup, and one branch
        // fewer than the prior `if (Index != Other.Index)` form.
        //
        // Verification: the test
        // Tests/Reflection/FName.Tests/Equality.cpp covers the spec
        // ordering invariant (SerialNumber tie-break under equal
        // Index) and is extended in Rev 3 to also cover the
        // Index-differs branch (Index majoring over SerialNumber).
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator<(FName Other) const noexcept
        {
            const ::std::uint64_t Lhs =
                (static_cast<::std::uint64_t>(Index)        << 32) |
                 static_cast<::std::uint64_t>(SerialNumber);
            const ::std::uint64_t Rhs =
                (static_cast<::std::uint64_t>(Other.Index)        << 32) |
                 static_cast<::std::uint64_t>(Other.SerialNumber);
            return Lhs < Rhs;
        }

        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator<=(FName Other) const noexcept
        {
            return !(Other < *this);
        }

        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator>(FName Other) const noexcept
        {
            return Other < *this;
        }

        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator>=(FName Other) const noexcept
        {
            return !(*this < Other);
        }
    };

    // ---------------------------------------------------------------------
    // ABI locks (per Contract Rev 13.7 §6 + Rev 13.8 addendum tag
    // XPACT_FNAME_LAYOUT_TAG). The static_asserts here ARE the ABI contract.
    // Any layout change breaks every TMap<FName, V> in the engine.
    // ---------------------------------------------------------------------
    static_assert(sizeof(FName)  == 8,
                  "FName ABI lock: must be exactly 8 bytes (XPACT_FNAME_LAYOUT_TAG)");
    static_assert(alignof(FName) == 4,
                  "FName ABI lock: must be 4-byte aligned (XPACT_FNAME_LAYOUT_TAG)");
    static_assert(offsetof(FName, Index)        == 0,
                  "FName ABI lock: Index must be at offset 0");
    static_assert(offsetof(FName, SerialNumber) == 4,
                  "FName ABI lock: SerialNumber must be at offset 4");
    static_assert(sizeof(FName::Index)        == 4,
                  "FName ABI lock: Index must be uint32 (4 bytes)");
    static_assert(sizeof(FName::SerialNumber) == 4,
                  "FName ABI lock: SerialNumber must be uint32 (4 bytes)");
    static_assert(::std::is_standard_layout_v<FName>,
                  "FName must be standard layout (so offsetof is defined and C# interop is byte-compatible)");
    static_assert(::std::is_trivially_copyable_v<FName>,
                  "FName must be trivially copyable (for memcpy / C# byval marshalling)");
    static_assert(::std::is_trivially_destructible_v<FName>,
                  "FName must be trivially destructible (no per-instance teardown allowed)");

    // ---------------------------------------------------------------------
    // NAME_None sentinel.
    //
    // Constexpr-available from any EInitPhase. Per §4.10 ("Single sentinel
    // NAME_None; every other 'well-known FName' is a static constinit FName
    // initialised at EInitPhase::PostStaticInit via FromString").
    // ---------------------------------------------------------------------
    inline constexpr FName NAME_None{};

    // ---------------------------------------------------------------------
    // Hash function. Declared (forward) in XCoreFwd.h so XCore-4a's TMap
    // and TSet could compile against FName; defined in FName.cpp where the
    // XCore::Hash::FXxh3 implementation is available.
    //
    // Hashes the 8-byte handle (Index + SerialNumber) via FXxh3-64.
    //
    // NOTE: the FName intern-table's per-shard slot table uses a DIFFERENT
    // hash (FXxh3-64 over the UTF-8 byte sequence) for the FromString
    // lookup. The two-hash split is intentional (§4.5):
    //
    //   * Byte-hash       : keys the intern table; reflects string identity.
    //   * Handle-hash     : keys TMap<FName, V>; reflects FName identity
    //                       (which IS string identity, but hashing the bytes
    //                        again would leak entropy between the two layers).
    //
    // Declared in XCoreFwd.h:
    //     [[nodiscard]] ::uint64 GetTypeHash(FName Name) noexcept;
    // ---------------------------------------------------------------------

    // ---------------------------------------------------------------------
    // Diagnostic accessors. Exposed so test harnesses and inspection tools
    // (e.g., a future `stat fname` console command) can query the shard
    // discipline without pulling in the private FNamePool header.
    // ---------------------------------------------------------------------

    // -----------------------------------------------------------------
    // GetFNameShardCount -- always returns 256 per §4.2 + Rev 3 §14
    // OPEN-1 RECOMMENDED. constexpr so callers can use it in array
    // sizes and template parameters.
    // -----------------------------------------------------------------
    [[nodiscard]] constexpr ::SIZE_T GetFNameShardCount() noexcept
    {
        return 256;
    }

    // -----------------------------------------------------------------
    // GetFNameShardIdFromIndex -- the high 8 bits of FName.Index.
    //
    // Encoding contract (§4.6): FName.Index packs
    // `(shard_id << 24) | shard_local_entry_id`. The shard id is the
    // top byte of the 32-bit Index.
    // -----------------------------------------------------------------
    [[nodiscard]] constexpr ::uint8 GetFNameShardIdFromIndex(::uint32 Index) noexcept
    {
        return static_cast<::uint8>(Index >> 24);
    }

    // -----------------------------------------------------------------
    // GetFNameShardIdFromByteHash -- the high 8 bits of the XXH3-64 byte
    // hash. The Intern() shard-selection function. constexpr (the input
    // is the precomputed hash).
    // -----------------------------------------------------------------
    [[nodiscard]] constexpr ::uint8 GetFNameShardIdFromByteHash(::uint64 ByteHash) noexcept
    {
        return static_cast<::uint8>(ByteHash >> 56);
    }

    // ---------------------------------------------------------------------
    // Engine init hooks. The FNamePool is constructed once at engine
    // bootstrap. Per §3 cyclic-risk resolution: "the first FName ever
    // created (NAME_None == ID 0) is a static constexpr sentinel that
    // requires no heap allocation; subsequent FName creations require
    // EInitPhase::PostStaticInit (the allocator is fully live by then)."
    //
    // __Init initialises the 256-shard pool and reserves the NAME_None
    // sentinel entry. __Shutdown releases all 64 KB shard blocks. The
    // double-underscore prefix flags these as engine-internal bootstrap
    // calls; user-tier modules MUST NOT invoke them.
    //
    // First-use Intern() lazily runs Init under a double-checked locking
    // pattern, so test harnesses that omit the bootstrap calls still
    // work; the explicit __Init exists for engines that prefer an
    // eager-init posture.
    // ---------------------------------------------------------------------
    void __InitFNamePool() noexcept;
    void __ShutdownFNamePool() noexcept;

} // namespace XCore::Reflect
