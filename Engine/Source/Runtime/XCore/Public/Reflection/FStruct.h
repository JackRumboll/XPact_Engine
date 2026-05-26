// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FStruct.h -- the 104-byte struct-with-properties descriptor base
// (XCore-4b §7.1 + §11.3).
// =====================================================================
//
// XCore-4b Rev 3, Section 7.1 ("FStruct (base)") + Section 11.3 layout
// row `FStruct: 104 bytes`.
//
// FStruct is the common base for FClass + FScriptStruct. UE's `UStruct`
// (`Class.h:495`) extends UField (which extends UObject); XPact's
// FStruct is itself a TOP-LEVEL type (no XObject inheritance because
// XCore-4b ships before XObject) and is the runtime descriptor for any
// "thing with a property list."
//
// =====================================================================
// SPEC DRIFT NOTICE (Phase 4b.5 audit finding -- escalated per Prime
// Directive's audit standard: "question user-direction decisions that
// look reasonable but a domain expert would do differently").
// =====================================================================
//
// The spec Rev 3 (FIX-R2-CRIT-1) re-derived FStruct = 104 bytes under
// the assumption that XCore-4a TArray sizeof = 16 bytes via EBO of a
// stateless DefaultAllocator. The actual XCore-4a Phase 1c+ ships a
// DefaultAllocator that carries a 2-byte FMemTag for per-container
// memory attribution; the [[msvc::no_unique_address]] attribute on
// TArrayCore::m_alloc has NO PADDING HOLE TO FOLD INTO (the 8+4+4 =
// 16-byte data-member layout is tight) and the trailing alignment-to-8
// forces sizeof(TArray<T>) == 24 on MSVC.
//
// Confirmed at Phase 4b.5 by direct sizeof measurement on MSVC 19.44.
// The spec's TArray = 16 claim is STRUCTURALLY IMPOSSIBLE with the
// shipped DefaultAllocator design.
//
// CONSEQUENCE: every FStruct/FClass/FScriptStruct/FEnum/FInterface
// size in the Rev 3 spec is wrong; the Rev 2 values (before FIX-R2-
// CRIT-1) reflected the actual layouts. Phase 4b.5 ships:
//
//     FStruct          112 bytes (Rev 2 value; spec Rev 3 said 104)
//     FScriptStruct    128 bytes (= 112 + 16; spec Rev 3 said 120)
//     FClass           224 bytes (= 112 + 112; spec Rev 3 said 200)
//     FEnum             72 bytes (Rev 2 value; spec Rev 3 said 64)
//     FInterface        64 bytes (Rev 2 value; spec Rev 3 said 56)
//
// All offsets after the first TArray field shift by +8 (24 vs 16).
//
// RESOLUTION PATHS for the user / spec author:
//
//   (A) Refactor XCore-4a so DefaultAllocator is truly stateless
//       (FMemTag moved to a per-thread default or a sidetable). This
//       is the most spec-aligned path but is XCore-4a-revision work,
//       well beyond Phase 4b.5 scope.
//
//   (B) Amend the spec to acknowledge TArray = 24 (revert FIX-R2-
//       CRIT-1's claim to Rev 2's values). Update the XPACT_F*_LAYOUT_TAG
//       strings accordingly.
//
// Phase 4b.5 ships path (B)-shaped layouts. False static_asserts that
// don't compile are a Prime Directive violation; we pin the ACTUAL
// byte sizes and document the spec drift.
//
// =====================================================================
//
// CRITICAL: FStruct is NOT an FField subclass. The intrusive list of
// properties owned by an FStruct uses the FField::Owner LSB-tag
// mechanism (FFieldVariant) to point AT an FStruct rather than a
// sibling FField -- thus FStruct must be 2-byte-aligned at minimum
// (the spec mandates 8-byte alignas for cache-line friendliness, which
// strictly satisfies the LSB-tag invariant).
//
// LAYOUT (Phase 4b.5 actual: 112 bytes per the SPEC DRIFT NOTICE above;
// per-field offsets shift by +8 after ObjectRefProperties relative to
// the spec Rev 3 published values):
//
//   struct alignas(8) FStruct {
//       FName              NamePrivate;        //  0  +8   struct name
//       const FStruct*     SuperStruct;        //  8  +8   parent struct; null for root
//       FField*            ChildProperties;    // 16  +8   head of intrusive descriptor list
//       int32              PropertiesSize;     // 24  +4   total bytes (excl. tail pad)
//       int16              MinAlignment;       // 28  +2   min alignment for instances
//       EStructFlags       StructFlags;        // 30  +1   8-bit trait bitmask
//       uint8              _padStructFlags;    // 31  +1   pad to 8-byte boundary
//       FProperty*         PropertyLink;       // 32  +8   most-derived-to-base walk head
//       FProperty*         DestructorLink;     // 40  +8   destructor walk head
//       FProperty*         PostConstructLink;  // 48  +8   post-ctor walk head
//       TArray<FProperty*> ObjectRefProperties;// 56 +24   dense GC scan list (FIX-13;
//                                              //         TArray = 24 bytes on MSVC, NOT 16)
//       uint64             SchemaHash;         // 80  +8   BLAKE3-truncated declared-shape hash
//       int32              SchemaVersion;      // 88  +4   monotonic per-type version
//       uint32             _padSchema;         // 92  +4
//       mutable std::atomic<const FUnversionedStructSchema*>
//                          UnversionedSchema;  // 96  +8   lazy unversioned cache
//       void               (*SerializeStructFn)(FArchive&, void*);  // 104  +8
//   };
//
// sizeof(FStruct) == 112.
//
// XPACT_FSTRUCT_LAYOUT_TAG (Rev 3 §11.6 SAID, but Phase 4b.5 audit
// found incorrect):
//   "FStruct-v3: 104 bytes; ObjectRefProperties TArray @ offset 56 =
//    16 bytes; SchemaHash @ 72; SerializeStructFn @ 96"
//
// Phase 4b.5 actually-shippable tag:
//   "FStruct-v3a (audit-corrected): 112 bytes; ObjectRefProperties
//    TArray @ offset 56 = 24 bytes; SchemaHash @ 80; SerializeStructFn
//    @ 104; corrects FIX-R2-CRIT-1's TArray=16 miscalculation"
//
// HOT-RELOAD SAFETY (§7.1):
//
//   * No virtual methods. SerializeStructFn is a function-pointer slot
//     (the only struct-level dispatch surface; the rich-script path
//     uses FCppStructOpsFakeVTable on the FScriptStruct subclass).
//   * Standard-layout for offsetof correctness (every member is
//     public; no virtual functions; the std::atomic member is
//     standard-layout on every supported toolchain).
//   * NOT trivially copyable -- ObjectRefProperties (TArray) owns a
//     heap buffer that must not be aliased; the atomic field is
//     non-copyable. FStruct instances should be referenced by pointer
//     once registered (not value-copied).
//
// RefLink RATIONALE (FIX-13):
//
//   UE's RefLink is an intrusive linked list of FProperty pointers via
//   a per-FProperty `NextRef` field. Linked-list pointer-chasing on
//   the Quest 3 ARM64 cache fails Master Plan §11 Step 5.5 criterion
//   (b) <50ms full-heap-scan over 100k XObjects with 5-10 ObjectRef
//   properties each. XPact replaces with a dense TArray populated at
//   FClass::Link time; the GC scan walks contiguously, prefetch-
//   friendly. The per-FProperty NextRef field is removed.
//
// SCHEMA HASH RATIONALE (FIX-10):
//
//   SchemaHash is a BLAKE3-truncated 64-bit hash over the FStruct's
//   DECLARED shape (member types, names, ArrayDim, replication
//   metadata) computed at XHT emit time. It identifies type identity
//   portable across compilers / packing flags. The full canonical-
//   byte-stream emission ships at Phase 4b.6 (XReflectionRuntime);
//   Phase 4b.5 declares the slot with a deferred-population sentinel
//   (XHT codegen populates at .gen.cpp emit).
//
// UnversionedSchema RATIONALE:
//
//   Mirror of UE's `UStruct.UnversionedGameSchema`: a lazy-built
//   cache of unversioned-property serialization metadata used by
//   XSerialization's compressed-asset path. The cache is built
//   on-demand once per FStruct lifetime; std::atomic ensures the
//   one-shot population is publication-safe across threads. The full
//   schema type ships at XSerialization (Layer 9; post-XCore-4b);
//   Phase 4b.5 forward-declares.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Containers/TArray.h"           // ObjectRefProperties (full type by value)
#include "Reflection/EStructFlags.h"     // EStructFlags strongly-typed enum
#include "Reflection/FName.h"             // FName (by-value member; need full type)

#include <atomic>
#include <cstddef>      // offsetof
#include <type_traits>  // is_standard_layout etc.

// Forward declaration: FArchive lands in XSerialization (Layer 9;
// post-XCore-4b). SerializeStructFn slot signature references the
// type by reference; consumers needing to dereference must include
// the full XSerialization header when it ships.
namespace XCore::Serialization
{
    class FArchive;
}

namespace XCore::Reflect
{
    // Forward declarations.

    // FField -- the polymorphism-free reflection anchor (Phase 4b.3).
    // ChildProperties stores the head of the intra-struct linked
    // list; the list nodes are FFields (subclasses thereof, mostly
    // FProperty derivatives). Phase 4b.3 supplies the full type.
    struct FField;

    // FProperty -- the FField subclass that carries property-shape
    // metadata (Phase 4b.4). FStruct's PropertyLink / DestructorLink /
    // PostConstructLink chains thread through FProperty::
    // PropertyLinkNext / DestructorLinkNext. Forward-declared here
    // because the chains are pointer-only.
    struct FProperty;

    // FUnversionedStructSchema -- the lazy unversioned-serialization
    // cache built by XSerialization. Phase 4b.5 forward-declares; the
    // full type ships at Layer 9.
    struct FUnversionedStructSchema;

    // -----------------------------------------------------------------
    // FStruct -- 104-byte struct-with-properties descriptor base.
    //
    // Per spec §7.1: alignas(8). NO virtual methods.
    //
    // The struct is NOT trivially copyable (TArray has a destructor;
    // std::atomic is non-copyable). FStruct instances should be
    // referenced by pointer once registered.
    //
    // FStruct is INTENDED to be used as a base for FScriptStruct +
    // FClass (Phase 4b.5 land). Direct instantiation of FStruct is
    // valid for non-CppStructOps reflected structs (the "plain
    // struct" case) -- in that case SerializeStructFn carries the
    // bytewise serializer (Phase 4b.6 owns the default), the FStruct
    // has no FCppStructOps dispatch, and the GC walk uses
    // ObjectRefProperties exclusively.
    // -----------------------------------------------------------------
    struct alignas(8) FStruct
    {
        // ---- Identity (offsets 0-15) ----

        FName              NamePrivate;        //  0  +8   struct name (e.g. "FVector", "AXActor")
        const FStruct*     SuperStruct;        //  8  +8   parent struct; nullptr for root

        // ---- Property list (intrusive linked list of FField descriptors) ----

        FField*            ChildProperties;    // 16  +8   head of FField linked list
                                                //         (FFields live in .rodata for XHT-emitted
                                                //          types or on the heap for runtime-
                                                //          constructed reflected types)

        // ---- Size + alignment (offsets 24-31) ----

        ::int32            PropertiesSize;     // 24  +4   total size of all properties
                                                //         (excluding tail pad to natural alignment)
        ::int16            MinAlignment;       // 28  +2   minimum alignment for instances
        EStructFlags       StructFlags;        // 30  +1   8-bit struct-trait bitmask
        ::uint8            _padStructFlags;    // 31  +1   pad to 8-byte boundary

        // ---- Property walks (offsets 32-55; 3 x 8-byte pointers) ----

        // Most-derived-to-base linked list used for replication walks
        // and the unversioned serializer (mirrors UE's
        // UStruct::PropertyLink).
        FProperty*         PropertyLink;       // 32  +8

        // Subset linked list of properties whose destruction is non-
        // trivial (any FProperty whose destroy slot does meaningful
        // work; mirrors UE's UStruct::DestructorLink).
        FProperty*         DestructorLink;     // 40  +8

        // Subset linked list of properties requiring post-construction
        // initialisation (default values populated after the bytewise
        // construction; mirrors UE's UStruct::PostConstructLink).
        FProperty*         PostConstructLink;  // 48  +8

        // ---- GC scan acceleration (offset 56; 16 bytes) ----
        //
        // Per FIX-13: dense TArray<FProperty*> populated at FClass::
        // Link by walking the property list and probing each
        // FProperty::DispatchTable->ContainsObjectReferenceFn. The
        // contiguous array is prefetch-friendly and meets Foundation
        // Prototype criterion (b) <50ms full-heap-scan on Quest 3
        // for 100k XObjects.
        //
        // Each element is a non-owning pointer to a property
        // descriptor stored elsewhere (in the FStruct's own
        // ChildProperties chain, or in a parent struct's chain when
        // an inherited XObject reference is exposed via FClass).

        ::XCore::TArray<FProperty*> ObjectRefProperties;  // 56 +16

        // ---- Schema versioning (offsets 72-87; per FIX-10) ----

        ::uint64           SchemaHash;          // 72  +8   BLAKE3-truncated declared-shape hash
        ::int32            SchemaVersion;       // 80  +4   monotonic per-type version int
        ::uint32           _padSchema;          // 84  +4   pad to 8-byte boundary

        // ---- Unversioned-schema cache (offset 88; 8 bytes) ----
        //
        // Mirrors CoreUObject's `UStruct.UnversionedGameSchema`. Lazy-
        // populated by XSerialization on first use; subsequent reads
        // observe the cached pointer. The atomic-load is a single
        // 8-byte read on every supported platform; the write happens
        // exactly once per FStruct lifetime.

        mutable ::std::atomic<const FUnversionedStructSchema*> UnversionedSchema;  // 88 +8

        // ---- Function-pointer slots (no virtual methods) ----
        //
        // SerializeStructFn is the FStruct-level bytewise serializer
        // (the "plain struct" path; FScriptStruct's CppStructOps
        // overrides via the FakeVTable for rich-script structs).
        // Forward-declared FArchive; consumers needing to dispatch
        // through this slot must include the XSerialization header.

        void               (*SerializeStructFn)(
            ::XCore::Serialization::FArchive& Ar, void* Instance);  // 96  +8

        // -------------------------------------------------------------
        // Construction.
        //
        // The default ctor zero-initialises every field except
        // ObjectRefProperties (TArray default-constructs to empty)
        // and UnversionedSchema (std::atomic default-constructs to
        // nullptr storage on every supported platform).
        //
        // The default ctor is NOT constexpr -- std::atomic's default
        // ctor is constexpr-only in C++20 with the value-initialisation
        // form; the safer posture is to make the FStruct ctor a
        // run-time-only init. XHT-emitted constinit FStructs use the
        // brace-init form below (every member explicitly named).
        // -------------------------------------------------------------

        FStruct() noexcept
            : NamePrivate()
            , SuperStruct(nullptr)
            , ChildProperties(nullptr)
            , PropertiesSize(0)
            , MinAlignment(1)
            , StructFlags(EStructFlags::STRUCT_None)
            , _padStructFlags(0)
            , PropertyLink(nullptr)
            , DestructorLink(nullptr)
            , PostConstructLink(nullptr)
            , ObjectRefProperties()
            , SchemaHash(0)
            , SchemaVersion(0)
            , _padSchema(0)
            , UnversionedSchema(nullptr)
            , SerializeStructFn(nullptr)
        {
        }

        // Explicit identity-only ctor for programmatic construction
        // in tests and runtime-built reflected types. Mutators below
        // populate the rest.
        FStruct(FName InName, const FStruct* InSuper) noexcept
            : NamePrivate(InName)
            , SuperStruct(InSuper)
            , ChildProperties(nullptr)
            , PropertiesSize(0)
            , MinAlignment(1)
            , StructFlags(EStructFlags::STRUCT_None)
            , _padStructFlags(0)
            , PropertyLink(nullptr)
            , DestructorLink(nullptr)
            , PostConstructLink(nullptr)
            , ObjectRefProperties()
            , SchemaHash(0)
            , SchemaVersion(0)
            , _padSchema(0)
            , UnversionedSchema(nullptr)
            , SerializeStructFn(nullptr)
        {
        }

        // FStruct is non-copyable + non-movable: the std::atomic
        // member is non-copyable; the TArray owns its buffer; the
        // intra-struct pointer chains assume a stable address. We
        // delete the copy + move surface explicitly so misuse is a
        // compile error rather than silent corruption.
        FStruct(const FStruct&)            = delete;
        FStruct(FStruct&&)                 = delete;
        FStruct& operator=(const FStruct&) = delete;
        FStruct& operator=(FStruct&&)      = delete;

        // The destructor is non-trivial because TArray frees its
        // buffer. No virtual qualifier; FStruct is a value-by-pointer
        // type referenced via stable address.
        ~FStruct() noexcept = default;

        // -------------------------------------------------------------
        // Accessors (mirroring UE UStruct surface).
        // -------------------------------------------------------------

        [[nodiscard]] XPACT_FORCEINLINE FName GetFName() const noexcept
        {
            return NamePrivate;
        }

        [[nodiscard]] XPACT_FORCEINLINE const FStruct* GetSuperStruct() const noexcept
        {
            return SuperStruct;
        }

        [[nodiscard]] XPACT_FORCEINLINE FField* GetChildProperties() const noexcept
        {
            return ChildProperties;
        }

        [[nodiscard]] XPACT_FORCEINLINE ::int32 GetPropertiesSize() const noexcept
        {
            return PropertiesSize;
        }

        [[nodiscard]] XPACT_FORCEINLINE ::int16 GetMinAlignment() const noexcept
        {
            return MinAlignment;
        }

        [[nodiscard]] XPACT_FORCEINLINE EStructFlags GetStructFlags() const noexcept
        {
            return StructFlags;
        }

        [[nodiscard]] XPACT_FORCEINLINE FProperty* GetPropertyLink() const noexcept
        {
            return PropertyLink;
        }

        [[nodiscard]] XPACT_FORCEINLINE FProperty* GetDestructorLink() const noexcept
        {
            return DestructorLink;
        }

        [[nodiscard]] XPACT_FORCEINLINE FProperty* GetPostConstructLink() const noexcept
        {
            return PostConstructLink;
        }

        [[nodiscard]] XPACT_FORCEINLINE const ::XCore::TArray<FProperty*>&
            GetObjectRefProperties() const noexcept
        {
            return ObjectRefProperties;
        }

        [[nodiscard]] XPACT_FORCEINLINE ::uint64 GetSchemaHash() const noexcept
        {
            return SchemaHash;
        }

        [[nodiscard]] XPACT_FORCEINLINE ::int32 GetSchemaVersion() const noexcept
        {
            return SchemaVersion;
        }

        // -------------------------------------------------------------
        // IsChildOf -- walk the SuperStruct chain.
        //
        // Returns true iff `this` is `Other` or descends from `Other`
        // via the SuperStruct chain. O(depth); the chain is bounded
        // (typical depth <=3, max <=10 per spec §13 gate C1).
        //
        // `Other == nullptr` returns false (nothing is a child of
        // "no struct"); `Other == this` returns true (a struct IS a
        // child of itself).
        //
        // The slow path (chain walk) is used because FStruct has no
        // CastFlags accelerator like FFieldClass does for FProperty
        // subclasses (a parallel hierarchy could be added post-MVP
        // if the population grows to need it; current MVP does not
        // need it).
        // -------------------------------------------------------------
        [[nodiscard]] bool IsChildOf(const FStruct* Other) const noexcept
        {
            if (Other == nullptr)
            {
                return false;
            }
            for (const FStruct* Walker = this; Walker != nullptr; Walker = Walker->SuperStruct)
            {
                if (Walker == Other)
                {
                    return true;
                }
            }
            return false;
        }

        // -------------------------------------------------------------
        // FindPropertyByName -- linear scan of the ChildProperties
        // linked list for a property with the given FName.
        //
        // Returns the FProperty* (cast via the FField subclass test)
        // or nullptr if not found. Body lives in FStruct.cpp to avoid
        // pulling FField + FProperty headers into FStruct.h.
        //
        // The lookup walks ONLY this struct's ChildProperties (not
        // parent structs). Callers wanting hierarchical lookup must
        // walk SuperStruct themselves; FClass::FindPropertyByName
        // (Phase 4b.5) handles the hierarchical variant for the FClass
        // override.
        //
        // O(N) in this struct's property count; bounded by §13 gate
        // C2 (200+ properties across 20 test types feasible).
        // -------------------------------------------------------------
        [[nodiscard]] FProperty* FindPropertyByName(FName PropertyName) const noexcept;

        // -------------------------------------------------------------
        // Link -- rebuild PropertyLink / DestructorLink /
        // PostConstructLink chains and populate ObjectRefProperties
        // from the ChildProperties list.
        //
        // Walks ChildProperties (head of the FField linked list),
        // filters to FProperty descriptors, and:
        //
        //   1. Threads PropertyLink as most-derived-to-base order
        //      (mirrors UE's UStruct::Link convention; the list is
        //      head=most-recent for replication-walk traversal).
        //   2. Threads DestructorLink with the subset of properties
        //      whose CPF_NoDestructor flag is clear (default: every
        //      property needs destruction unless explicitly marked).
        //      Phase 4b.5 uses a conservative-all-properties policy
        //      because the CPF_NoDestructor probe lives in
        //      EPropertyFlags (already declared).
        //   3. Threads PostConstructLink with properties carrying
        //      CPF_NeedCtorLink (Phase 4b.5: conservative-all because
        //      the FPropertyFlags-CPF_NeedCtorLink bit is forward-
        //      declared; the precise per-property filter ships at
        //      Phase 4b.6 alongside XReflectionRuntime).
        //   4. Populates ObjectRefProperties with the subset of
        //      properties whose ContainsObjectReference dispatch
        //      returns true (per FIX-13; the dense GC scan list).
        //
        // The link operation is idempotent: calling Link a second
        // time clears the chains and rebuilds. This is the contract
        // FClass::Link relies on when hot-reload swaps a patch DLL
        // (the patch may re-link with a new ChildProperties list).
        //
        // O(N) in this struct's property count. Bounded by §13 gate
        // C2 + G3 (<50ms for 1000-property class hierarchy on Win64
        // desktop).
        //
        // Body lives in FStruct.cpp.
        // -------------------------------------------------------------
        void Link() noexcept;

        // -------------------------------------------------------------
        // ForEachProperty -- visit every FProperty in the
        // PropertyLink chain.
        //
        // The visitor is `void (FProperty*)`; called once per property
        // in PropertyLink order. The header inlines a templated
        // overload accepting any invocable; the body remains in the
        // header so the per-property callback is monomorphised at
        // each call site (no virtual or std::function indirection).
        //
        // Walks PropertyLink (not ChildProperties): Link MUST have
        // been called first.
        // -------------------------------------------------------------
        template <typename FnT>
        XPACT_FORCEINLINE void ForEachProperty(FnT&& Visitor) const noexcept;
    };

    // ---------------------------------------------------------------------
    // ABI locks (Phase 4b.5 audit-corrected per SPEC DRIFT NOTICE above).
    //
    // Spec Rev 3 §11.3 declared FStruct = 104 under the TArray=16 EBO
    // assumption. Real XCore-4a TArray = 24 bytes (DefaultAllocator
    // carries a 2-byte FMemTag with no padding hole to fold into).
    // The asserts pin the ACTUAL sizes; the field offsets after
    // ObjectRefProperties shift by +8 relative to the spec.
    // ---------------------------------------------------------------------
    static_assert(sizeof(FStruct)  == 112,
                  "FStruct ABI lock (audit-corrected): 112 bytes. "
                  "Spec Rev 3 §7.1 declared 104 assuming TArray=16 EBO; "
                  "actual XCore-4a TArray=24 (DefaultAllocator carries "
                  "2-byte FMemTag with no padding hole). Phase 4b.5 ships "
                  "the actual size; spec amendment requested.");
    static_assert(alignof(FStruct) == 8,
                  "FStruct ABI lock: 8-byte alignment per §7.1 alignas(8)");

    // Member offsets locked per the audit-corrected layout. Offsets
    // up to and including ObjectRefProperties match the spec; offsets
    // after ObjectRefProperties shift by +8 (TArray=24 vs spec's
    // assumed TArray=16).
    static_assert(offsetof(FStruct, NamePrivate)        ==  0,
                  "FStruct ABI lock: NamePrivate at offset 0");
    static_assert(offsetof(FStruct, SuperStruct)        ==  8,
                  "FStruct ABI lock: SuperStruct at offset 8");
    static_assert(offsetof(FStruct, ChildProperties)    == 16,
                  "FStruct ABI lock: ChildProperties at offset 16");
    static_assert(offsetof(FStruct, PropertiesSize)     == 24,
                  "FStruct ABI lock: PropertiesSize at offset 24");
    static_assert(offsetof(FStruct, MinAlignment)       == 28,
                  "FStruct ABI lock: MinAlignment at offset 28");
    static_assert(offsetof(FStruct, StructFlags)        == 30,
                  "FStruct ABI lock: StructFlags at offset 30");
    static_assert(offsetof(FStruct, PropertyLink)       == 32,
                  "FStruct ABI lock: PropertyLink at offset 32");
    static_assert(offsetof(FStruct, DestructorLink)     == 40,
                  "FStruct ABI lock: DestructorLink at offset 40");
    static_assert(offsetof(FStruct, PostConstructLink)  == 48,
                  "FStruct ABI lock: PostConstructLink at offset 48");
    static_assert(offsetof(FStruct, ObjectRefProperties)== 56,
                  "FStruct ABI lock: ObjectRefProperties at offset 56");
    static_assert(offsetof(FStruct, SchemaHash)         == 80,
                  "FStruct ABI lock (audit-corrected): SchemaHash at "
                  "offset 80 (spec said 72; +8 shift from TArray=24)");
    static_assert(offsetof(FStruct, SchemaVersion)      == 88,
                  "FStruct ABI lock (audit-corrected): SchemaVersion at "
                  "offset 88 (spec said 80; +8 shift from TArray=24)");
    static_assert(offsetof(FStruct, UnversionedSchema)  == 96,
                  "FStruct ABI lock (audit-corrected): UnversionedSchema "
                  "at offset 96 (spec said 88; +8 shift from TArray=24)");
    static_assert(offsetof(FStruct, SerializeStructFn)  == 104,
                  "FStruct ABI lock (audit-corrected): SerializeStructFn "
                  "at offset 104 (spec said 96; +8 shift from TArray=24)");

    // Type traits: FStruct is NOT trivially copyable (TArray + atomic)
    // and is NOT standard-layout because the std::atomic member has
    // potentially-non-standard-layout depending on toolchain; we still
    // pin the size + alignment as the ABI contract.

    // Free function helper used by ForEachProperty's templated body.
    // The body lives in FStruct.cpp where FProperty is fully included.
    // Allows the templated ForEachProperty to stay header-only without
    // requiring callers to include FProperty.h transitively.
    //
    // Declared BEFORE the template body so MSVC's two-phase lookup
    // finds it (the function call inside the template references a
    // non-dependent name and must be visible at template-definition
    // time per [temp.dep.candidate]).
    FProperty* ForEachPropertyAdvance(FProperty* Current) noexcept;

    // -------------------------------------------------------------
    // ForEachProperty template body.
    //
    // Header-inline so the visitor lambda is monomorphised at each
    // call site. The chain walk is O(N) in property count; the only
    // overhead is the indirect call through the FProperty::
    // PropertyLinkNext chain (and the visitor invocation).
    //
    // ForEachProperty assumes Link() has been called -- the
    // PropertyLink chain is populated only after Link().
    // -------------------------------------------------------------
    template <typename FnT>
    XPACT_FORCEINLINE void FStruct::ForEachProperty(FnT&& Visitor) const noexcept
    {
        // The advance helper indirects through FStruct.cpp where
        // FProperty is fully included; callers passing a visitor
        // that uses FProperty members must include FProperty.h
        // themselves at the call site.
        FProperty* Walker = PropertyLink;
        while (Walker != nullptr)
        {
            Visitor(Walker);
            Walker = ForEachPropertyAdvance(Walker);
        }
    }

} // namespace XCore::Reflect
