// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FFieldVariant.h -- LSB-tagged union pointer (XCore-4b §5.1; FIX-4).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.1 ("FField base") + Section 11.2 layout
// row `FFieldVariant: 8 bytes, Storage@0`.
//
// FFieldVariant is the 8-byte sum type that stores either an FField
// pointer or an FStruct pointer with a 1-bit discriminator. It is used
// as the `Owner` field on every FField -- the parent container of a
// reflected field is either:
//
//   * Another FField (the previous sibling in an intra-struct list), or
//   * An FStruct (the enclosing struct/class declaration root).
//
// The discriminator is encoded in the LOW bit (LSB) of the pointer
// payload. Both FField and FStruct allocations are 8-byte-aligned, so
// the LSB of a valid pointer is structurally zero; the engine uses that
// otherwise-unused bit as a kind tag. This is the FIX-4 architectural
// correction over Rev 1's 16-bit high-tag scheme:
//
//   * Faster: one AND vs Rev 1's shift+AND.
//   * Forward-compatible: 8-byte alignment is architecture-independent,
//     unlike Rev 1's assumption that high address bits are unused
//     (which doesn't hold on future ARM64 VA extensions).
//
// ENCODING (XPACT_FPROPERTY_LAYOUT_TAG @ Contract Rev 13.8 §11.2 +
// XCore-4b §5.1):
//
//   bits 0      LSB tag             (0 = FField, 1 = FStruct)
//   bits 1-63   pointer payload     (top 63 bits of an 8-byte-aligned
//                                    pointer; LSB-cleared form recovers
//                                    the raw pointer)
//
// LSB INVERSION FROM UE (Rev 3 FIX-R2-LOW-6 documentation requirement):
// XPact encodes LSB=1 -> FStruct; UE (`Field.h:408 UObjectMask = 0x1`
// + `Field.h:454 (uintptr_t)Container.Object | UObjectMask`) encodes
// LSB=1 -> UObject. Both schemes are equivalent (the choice of which
// kind gets the tag bit is arbitrary); XPact's inversion is DELIBERATE
// per the spec because FField is the more frequent kind in reflection-
// walk hot paths, so a zero-tag bit on FField saves one bitwise-NOT
// instruction in the common case. Anyone porting test vectors between
// UE and XPact must flip the LSB-test polarity.
//
// HOT-RELOAD SAFETY (§5.1):
//
//   * No virtual methods. The struct is trivially copyable; the entire
//     state is a single uint64_t.
//   * Standard layout, so offsetof(FFieldVariant, Storage) is defined
//     and the ABI-lock static_asserts at the bottom of this file pin
//     the byte shape.
//
// FORWARD-DECLARATIONS:
//
//   * FField: declared here; the full definition lives in FField.h
//     and is brought in by clients that need member access. The
//     LSB-tag union only stores a pointer, never dereferences in
//     this header, so a forward declaration suffices.
//   * FStruct: declared here as a forward only; the full definition
//     lands at Phase 4b.5. The LSB-tag union stores the pointer; the
//     `AsStruct()` accessor returns a `const FStruct*` (caller-side
//     dereference is the consumer's responsibility, and that consumer
//     will already have included the FStruct.h header).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include <cstddef>      // offsetof
#include <type_traits>  // is_trivially_copyable etc.

namespace XCore::Reflect
{
    // Forward declarations. The LSB-tag union stores a pointer to either
    // kind without dereferencing in this header, so the forward decls are
    // sufficient. Clients that call `AsField()` / `AsStruct()` and then
    // dereference the returned pointer must include the corresponding
    // full-definition header.
    struct FField;
    struct FStruct;

    // -----------------------------------------------------------------
    // FFieldVariant -- 8-byte LSB-tagged sum type {FField*, FStruct*}.
    //
    // Per spec §5.1 the struct is `alignas(8)` so the LSB-tag scheme is
    // self-consistent: the contained pointer's LSB is structurally zero
    // before tagging, then gets the kFStructTag bit applied for the
    // FStruct case at construction time. The variant itself has no
    // alignment effect on the LSB invariant; the alignas(8) is for the
    // benefit of containers (TArray<FFieldVariant>) and reflection
    // walkers that want a single 64-bit load.
    //
    // NO virtual methods (hot-reload safety; §5.1 + spec-wide).
    // Standard layout + trivially copyable so the variant can flow
    // through C# IL2CPP boundaries by value.
    // -----------------------------------------------------------------
    struct alignas(8) FFieldVariant
    {
        // The 8-byte storage cell. Layout per Contract Rev 13.8 §11.2:
        //
        //   bits 0      LSB tag (0 = FField, 1 = FStruct)
        //   bits 1-63   pointer bits with LSB structurally zero
        //
        // Public so the diagnostic helpers (and the static_asserts at the
        // bottom of this header) can read the raw bytes; user code SHOULD
        // go through the typed accessors below.
        ::uint64 Storage;        //  0  +8   tagged pointer

        // -------------------------------------------------------------
        // Tag constants. constexpr-available so callers compositing tag
        // values into other bit fields can use them in compile-time
        // expressions.
        // -------------------------------------------------------------

        // The LSB-set bit pattern that indicates "this variant holds an
        // FStruct pointer". Inverse of UE's LSB-tag semantic per Rev 3
        // FIX-R2-LOW-6 (UE: LSB=1 -> UObject; XPact: LSB=1 -> FStruct).
        static constexpr ::uint64 kFStructTag  = ::uint64(0x1);

        // Mask that strips the tag bit, recovering the raw pointer value.
        // Equal to `~kFStructTag` (== 0xFFFFFFFFFFFFFFFE).
        static constexpr ::uint64 kPointerMask = ~::uint64(0x1);

        // -------------------------------------------------------------
        // Construction.
        //
        // All constructors are constexpr-and-noexcept-friendly so the
        // variant can be initialised in constinit data (the XHT-emitted
        // `.gen.cpp` populates static FField instances whose `Owner`
        // member is an FFieldVariant; the consteval-init path requires
        // the constructors be constexpr).
        // -------------------------------------------------------------

        // Default ctor: nullptr (Storage == 0; LSB tag == 0 means "FField"
        // pointer; the FField pointer is nullptr). Predicates:
        //
        //   IsField()  returns true  (LSB=0)
        //   IsStruct() returns false
        //   AsField()  returns nullptr
        //   AsStruct() returns nullptr
        //
        // The zero state is the "no owner" sentinel used by FField until
        // it is linked into an enclosing FStruct / FField chain.
        constexpr FFieldVariant() noexcept : Storage(0) {}

        // Explicit nullptr ctor. Same semantics as default ctor; provided
        // so call sites that want to be explicit (e.g. `FFieldVariant{nullptr}`)
        // do not silently fall through to a wrong-type-overload candidate.
        constexpr FFieldVariant(::std::nullptr_t) noexcept : Storage(0) {}

        // Construct from an FField pointer.
        //
        // The pointer's LSB is structurally zero (FField is 8-byte
        // aligned per §5.1); the LSB tag is set to 0 ("FField") so the
        // result Storage equals the pointer bits verbatim.
        //
        // Const-correctness: the variant stores a const-erased pointer so
        // that both `const FField*` and `FField*` owners can be stored
        // uniformly. The `AsField()` accessor returns `const FField*`;
        // callers needing mutation must const_cast at the call site (and
        // own that mutation discipline themselves -- the reflection
        // runtime publishes descriptors as immutable post-Link).
        //
        // The conversion via `reinterpret_cast` is NOT constexpr (the C++
        // language forbids pointer-to-integer reinterpret_cast in
        // constant expressions), so this constructor is not constexpr.
        // The constinit-friendly path is the (uint64) Storage ctor below.
        explicit FFieldVariant(const FField* InField) noexcept
            : Storage(reinterpret_cast<::uint64>(InField))
        {
            // FField pointers must be 8-byte aligned (the LSB-tag scheme
            // requires it). The pointer's low bit must therefore be zero.
            // In Debug/Dev a check fires; in Shipping the assert compiles
            // out. Misaligned FField pointers indicate a corrupted
            // descriptor and downstream behaviour is undefined.
            //
            // The check is structural: any FField allocation via
            // `alignas(8) struct FField` (§5.1) is guaranteed to honour
            // this invariant. The check exists to catch wild bit patterns
            // (e.g. a misallocated FField from an external module that
            // did not honour the alignment contract).
            XPACT_CHECK((Storage & kFStructTag) == 0);
        }

        // Construct from an FStruct pointer.
        //
        // The pointer's LSB is structurally zero (FStruct is 8-byte
        // aligned per §7.1); the LSB tag is OR'd in so the result
        // Storage equals `pointer_bits | kFStructTag`.
        //
        // Const-correctness: same posture as the FField ctor above.
        explicit FFieldVariant(const FStruct* InStruct) noexcept
            : Storage(reinterpret_cast<::uint64>(InStruct) | kFStructTag)
        {
            // The OR'd-in result must have the LSB set (FStruct tag);
            // before tagging the pointer's LSB must be zero. We check
            // the post-tag form has the LSB set as a sanity gate.
            //
            // If a caller passes a misaligned FStruct pointer (LSB
            // already nonzero), the OR-in is a no-op and we silently
            // accept the value -- but the resulting Storage will not
            // round-trip to the original pointer through `AsStruct()`.
            // The check fires in Debug/Dev only.
            XPACT_CHECK((Storage & kFStructTag) == kFStructTag);
        }

        // Internal constructor that stores a pre-tagged uint64 verbatim.
        //
        // PUBLIC so XHT-emitted `.gen.cpp` aggregators that already
        // computed the tagged-pointer value at compile time can populate
        // a constinit FFieldVariant via brace-init. The C++ language
        // does not permit reinterpret_cast in constexpr contexts, so the
        // pointer-taking constructors above cannot be constexpr; this
        // uint64-taking constructor IS constexpr and is the path the
        // XHT codegen uses for constinit-ready data.
        //
        // The InRaw value MUST be either:
        //   * 0 (the nullptr sentinel), OR
        //   * `pointer_bits | 0x1` for an FStruct (LSB set, top 63 bits
        //     are an 8-byte-aligned FStruct pointer), OR
        //   * `pointer_bits | 0x0` for an FField (LSB clear, top 63 bits
        //     are an 8-byte-aligned FField pointer).
        //
        // Direct callers MUST construct the uint64 themselves with the
        // correct tag bit; the constructor performs no transformation.
        // Test code that round-trips an FField*->FFieldVariant->AsField
        // does NOT use this path -- it uses the typed ctors above.
        explicit constexpr FFieldVariant(::uint64 InRaw, ::int32 /*RawTagDisambiguator*/) noexcept
            : Storage(InRaw) {}

        // -------------------------------------------------------------
        // Predicates.
        //
        // IsField() / IsStruct() inspect the LSB tag. They are
        // structurally O(1) (a single AND + compare) and safe to call
        // even when Storage == 0 (nullptr default ctor): IsField() will
        // return true and AsField() will return nullptr, which is the
        // "no owner" sentinel.
        //
        // Spec naming note (§5.1): the spec calls the FField predicate
        // `IsField()` and the FStruct predicate `IsStruct()`. UE's
        // `Field.h:489 IsUObject()` is the (LSB=1 -> UObject) version
        // of this same test. XPact's predicate names match the XPact
        // tag semantic (LSB=1 -> FStruct).
        // -------------------------------------------------------------

        // True iff this variant holds an FField pointer (or nullptr).
        // Bytewise: `(Storage & kFStructTag) == 0`.
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool IsField() const noexcept
        {
            return (Storage & kFStructTag) == 0;
        }

        // True iff this variant holds an FStruct pointer.
        // Bytewise: `(Storage & kFStructTag) != 0`.
        // Note: a nullptr-default-constructed variant returns false here.
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool IsStruct() const noexcept
        {
            return (Storage & kFStructTag) != 0;
        }

        // True iff this variant is the nullptr sentinel (Storage == 0).
        // Equivalent to `AsField() == nullptr && !IsStruct()`.
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool IsNull() const noexcept
        {
            return Storage == 0;
        }

        // -------------------------------------------------------------
        // Typed accessors.
        //
        // AsField() returns the FField pointer (nullptr-safe; null result
        // if the variant holds an FStruct or is nullptr).
        // AsStruct() returns the FStruct pointer (nullptr-safe; null
        // result if the variant holds an FField or is nullptr).
        //
        // The accessors are NOT constexpr (they use reinterpret_cast,
        // which C++ forbids in constant expressions). They are
        // FORCEINLINE-hinted because they appear on reflection hot paths.
        // -------------------------------------------------------------

        // Return the held FField pointer, or nullptr if the variant
        // holds an FStruct.
        [[nodiscard]] XPACT_FORCEINLINE const FField* AsField() const noexcept
        {
            if (IsStruct())
            {
                return nullptr;
            }
            // LSB is already 0 (IsField() returned true), so Storage IS
            // the pointer bits. The mask is for paranoia / robustness
            // against a hand-constructed Storage value with stray
            // high-bit garbage; on every supported target this is a no-op.
            return reinterpret_cast<const FField*>(Storage & kPointerMask);
        }

        // Return the held FStruct pointer, or nullptr if the variant
        // holds an FField (or is nullptr).
        [[nodiscard]] XPACT_FORCEINLINE const FStruct* AsStruct() const noexcept
        {
            if (!IsStruct())
            {
                return nullptr;
            }
            // Strip the LSB tag to recover the raw pointer.
            return reinterpret_cast<const FStruct*>(Storage & kPointerMask);
        }

        // Return the raw 64-bit Storage. Diagnostic / hashing only;
        // user code SHOULD go through AsField()/AsStruct().
        [[nodiscard]] XPACT_FORCEINLINE constexpr ::uint64 GetRaw() const noexcept
        {
            return Storage;
        }

        // -------------------------------------------------------------
        // Equality. Bytewise on Storage. Two variants are equal iff
        // they hold the same kind of pointer at the same address.
        //
        // Note: a nullptr-default-constructed variant compares unequal
        // to a default-constructed FFieldVariant{(const FStruct*)nullptr}
        // because the latter has the LSB tag set (Storage == 0x1) while
        // the former has Storage == 0. This is intentional -- a
        // null-tagged-as-struct variant is structurally different from
        // a null-tagged-as-field variant.
        // -------------------------------------------------------------

        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator==(FFieldVariant Other) const noexcept
        {
            return Storage == Other.Storage;
        }

        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator!=(FFieldVariant Other) const noexcept
        {
            return !(*this == Other);
        }
    };

    // ---------------------------------------------------------------------
    // ABI locks (per Contract Rev 13.8 §11.2 layout table:
    // `FFieldVariant: 8 bytes, Storage@0`). The static_asserts here ARE
    // the ABI contract. Any layout change breaks every FField instance
    // engine-wide (FField carries an FFieldVariant by value as the
    // Owner field at offset 8).
    // ---------------------------------------------------------------------
    static_assert(sizeof(FFieldVariant)  == 8,
                  "FFieldVariant ABI lock: must be exactly 8 bytes "
                  "(XPACT_FPROPERTY_LAYOUT_TAG XCore-4b §5.1)");
    static_assert(alignof(FFieldVariant) == 8,
                  "FFieldVariant ABI lock: 8-byte alignment (the LSB-tag "
                  "scheme requires 8-byte-aligned pointed-to objects, which "
                  "implies 8-byte storage alignment for the variant itself)");
    static_assert(offsetof(FFieldVariant, Storage) == 0,
                  "FFieldVariant ABI lock: Storage must be at offset 0");
    static_assert(sizeof(FFieldVariant::Storage) == 8,
                  "FFieldVariant ABI lock: Storage must be uint64 (8 bytes)");

    static_assert(::std::is_standard_layout_v<FFieldVariant>,
                  "FFieldVariant must be standard layout "
                  "(so offsetof is defined and C# interop is byte-compatible)");
    static_assert(::std::is_trivially_copyable_v<FFieldVariant>,
                  "FFieldVariant must be trivially copyable "
                  "(memcpy-safe; required by reflection containers)");
    static_assert(::std::is_trivially_destructible_v<FFieldVariant>,
                  "FFieldVariant must be trivially destructible "
                  "(no per-instance teardown; constinit-friendly)");

    // ---------------------------------------------------------------------
    // Compile-time invariant: the LSB-tag scheme requires
    // alignof(FField) >= 2 so the LSB is naturally zero for any valid
    // FField pointer. The structural guarantee is alignof(FField) == 8
    // per §5.1's `alignas(8)` declaration on the struct; we assert >= 2
    // here as the minimum LSB-tag invariant.
    //
    // The FField alignment cannot be checked HERE (FField is forward-
    // declared; alignof on an incomplete type is ill-formed). The check
    // lives in FField.h alongside the FField static_asserts.
    //
    // Similarly the kFStructTag = 0x1 bit-positioning is verified by
    // round-trip: if a callable accepts an FField* and immediately reads
    // it back as `AsField()`, the LSB-cleared form recovers the original
    // pointer. The .cpp tests (`FFieldVariantLSBTag.cpp`) verify this.
    // ---------------------------------------------------------------------
    static_assert((FFieldVariant::kFStructTag & FFieldVariant::kPointerMask) == 0,
                  "FFieldVariant ABI lock: tag bit and pointer mask must "
                  "be mutually exclusive (kFStructTag & kPointerMask == 0)");
    static_assert((FFieldVariant::kFStructTag | FFieldVariant::kPointerMask) == ~::uint64(0),
                  "FFieldVariant ABI lock: tag bit and pointer mask must "
                  "cover the entire 64-bit Storage cell");
    static_assert(FFieldVariant::kFStructTag == 0x1ull,
                  "FFieldVariant ABI lock: kFStructTag must be the LSB (0x1)");

} // namespace XCore::Reflect
