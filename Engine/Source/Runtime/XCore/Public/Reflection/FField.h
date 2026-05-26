// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FField.h -- polymorphism-free reflection-descriptor anchor (§5.1).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.1 ("FField base") + Section 11.2 layout row
// `FField: 32 bytes, ClassPrivate@0, Owner@8, Next@16, NamePrivate@24`.
//
// FField is the smallest possible reflection-runtime anchor. Every
// reflected field-or-property descriptor in the engine is an FField
// (FProperty subclasses extend FField; FFunctionDescriptor / other
// non-FProperty reflected fields planned for post-MVP also extend
// FField).
//
// LAYOUT (locked at 32 bytes per Contract Rev 13.8 §11.2):
//
//   struct alignas(8) FField {
//       const FFieldClass* ClassPrivate;  //  0  +8  type descriptor
//       FFieldVariant      Owner;         //  8  +8  parent FStruct or sibling FField
//       FField*            Next;          // 16  +8  intra-struct linked-list next
//       FName              NamePrivate;   // 24  +8  field name (e.g. "Health")
//   };
//
// XPact's FField strips out UE's EObjectFlags-derived flags slot (UE
// deprecated it in 5.8 anyway; `Field.h:655-659`) and keeps only the
// four load-bearing fields. The 32-byte total is half a 64-byte cache
// line on every target platform; two FFields fit per line.
//
// HOT-RELOAD SAFETY (§5.1):
//
//   * No virtual methods. The dispatch surface is the per-FProperty-
//     subclass FFakeVTable (§5.4; Phase 4b.4) -- a function-pointer
//     table in .rodata, not a vtable.
//   * Standard-layout struct so offsetof is well-defined.
//   * Trivially destructible (no per-instance teardown).
//   * Constructors are simple member init; constinit-friendly.
//
// ALIGNMENT INVARIANT (FFieldVariant LSB-tag dependency):
//
//   alignas(8) FField means every FField pointer's LSB is structurally
//   zero. The FFieldVariant LSB-tag scheme (§5.1 + FFieldVariant.h)
//   depends on this. The static_assert at the bottom of this file
//   pins alignof(FField) >= 2 -- which is the minimum requirement for
//   the LSB-tag invariant -- alongside the actual alignment of 8.
//
// USAGE FROM SUBCLASSES (Phase 4b.4):
//
//   FProperty (and every FProperty subclass) extends FField via plain
//   single inheritance. Subclasses populate the FField base via the
//   protected constructor that takes (ClassPrivate, Owner, Name); the
//   Next field is set later by `FClass::Link` when the FStruct's
//   property list is built.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Reflection/EClassCastFlags.h"   // EClassCastFlags + HasAnyCastFlags helpers
#include "Reflection/FFieldClass.h"        // FFieldClass; FField stores a const FFieldClass*
#include "Reflection/FFieldVariant.h"      // FFieldVariant; FField stores Owner by value
#include "Reflection/FName.h"              // FName; FField stores NamePrivate by value

#include <cstddef>      // offsetof
#include <type_traits>  // is_trivially_copyable etc.

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // FField -- the 32-byte polymorphism-free reflection anchor.
    //
    // Per spec §5.1: alignas(8), no virtual methods, 32-byte total.
    //
    // The struct IS trivially copyable (every member is POD: pointer,
    // FFieldVariant {one uint64}, pointer, FName {two uint32}). The
    // defaulted copy/move constructors are bitwise. This is required
    // for memcpy-able storage in TArray<FField> and for the constinit
    // aggregator path (XHT-emitted descriptor arrays).
    //
    // SEMANTIC NOTE: although the struct is bitwise-copyable, FField is
    // INTENDED to be referenced by stable pointer once registered into
    // the FStruct's linked list (its address lives in another FField's
    // Next slot). Copying an FField is bitwise-safe but logically
    // suspect because the copy would leak a Next pointer into a list
    // it doesn't belong to. Consumers SHOULD pass `const FField*` /
    // `FField*` rather than by value at the API boundary.
    //
    // The struct IS standard-layout (all members are public, all
    // members have the same access, no virtual functions, no virtual
    // bases). This satisfies the offsetof requirements at the
    // static_assert site below.
    // -----------------------------------------------------------------
    struct alignas(8) FField
    {
        const FFieldClass* ClassPrivate;  //  0  +8   FFieldClass descriptor (e.g. &FBoolPropertyClass)
        FFieldVariant      Owner;         //  8  +8   parent FStruct or sibling FField (LSB-tagged)
        FField*            Next;          // 16  +8   intra-struct linked-list next; nullptr at construction
        FName              NamePrivate;   // 24  +8   field name (e.g. FName("Health"))

        // -------------------------------------------------------------
        // Construction.
        //
        // Two constructors are provided:
        //
        //   (a) Default ctor: every field zero / nullptr. Allows array
        //       allocation of FField storage that is later populated
        //       in-place by `FFieldClass::Construct`. constexpr so the
        //       constinit aggregator path can use it.
        //
        //   (b) Explicit ctor: takes ClassPrivate, Owner, Name. Next is
        //       left as nullptr (the property-list linking happens at
        //       FClass::Link time in Phase 4b.5). The constructor is
        //       NOT constexpr because the FFieldVariant ctor that takes
        //       a pointer is not constexpr (the pointer-to-uint64
        //       reinterpret_cast is not allowed in constant
        //       expressions). For constinit data, the consteval path
        //       in Phase 4b.4 will use brace-init with a pre-tagged
        //       FFieldVariant (constructed via the uint64-Storage
        //       overload).
        // -------------------------------------------------------------

        // Default ctor: nullptr ClassPrivate, nullptr-tagged-as-Field Owner,
        // nullptr Next, NAME_None NamePrivate.
        constexpr FField() noexcept
            : ClassPrivate(nullptr)
            , Owner()              // default-constructed (storage == 0; IsField() true)
            , Next(nullptr)
            , NamePrivate()
        {
        }

        // Explicit ctor. Per spec §5.1's example:
        //   explicit FField(const FFieldClass* cls, FFieldVariant owner, FName name)
        //       : ClassPrivate(cls), Owner(owner), Next(nullptr), NamePrivate(name) {}
        //
        // `explicit` so an FFieldClass* alone cannot implicitly convert
        // to an FField (the construction requires Owner and Name too).
        FField(const FFieldClass* InClass, FFieldVariant InOwner, FName InName) noexcept
            : ClassPrivate(InClass)
            , Owner(InOwner)
            , Next(nullptr)
            , NamePrivate(InName)
        {
        }

        // -------------------------------------------------------------
        // Copy + move.
        //
        // Defaulted but NOT trivially copyable -- the FField is intended
        // to be referenced by stable pointer (its address lives in the
        // FStruct's linked list), so copying an FField is rare and
        // suspect. We default the copy/move so language-level
        // requirements (TArray-element move, constinit initialiser)
        // still hold, but consumers should generally pass by `const
        // FField*` rather than by value.
        //
        // The Next pointer is copied verbatim. The caller is responsible
        // for ensuring the copy doesn't leak a pointer into a list it
        // doesn't belong to.
        // -------------------------------------------------------------
        constexpr FField(const FField&) noexcept            = default;
        constexpr FField(FField&&) noexcept                 = default;
        constexpr FField& operator=(const FField&) noexcept = default;
        constexpr FField& operator=(FField&&) noexcept      = default;
        ~FField() noexcept                                  = default;

        // -------------------------------------------------------------
        // Accessors. Conventional Get-prefixed accessors mirror UE's
        // FField surface (`Field.h:GetClass`, `:GetOwner`, `:GetNext`,
        // `:GetFName`).
        // -------------------------------------------------------------

        // The FFieldClass descriptor (e.g. `&FBoolPropertyClass`).
        [[nodiscard]] XPACT_FORCEINLINE constexpr const FFieldClass* GetClass() const noexcept
        {
            return ClassPrivate;
        }

        // The owner (parent FStruct or sibling FField).
        [[nodiscard]] XPACT_FORCEINLINE constexpr FFieldVariant GetOwner() const noexcept
        {
            return Owner;
        }

        // The next FField in the intra-struct linked list. nullptr at
        // construction; populated by FClass::Link (Phase 4b.5).
        [[nodiscard]] XPACT_FORCEINLINE constexpr FField* GetNext() const noexcept
        {
            return Next;
        }

        // The field name (FName handle).
        [[nodiscard]] XPACT_FORCEINLINE constexpr FName GetFName() const noexcept
        {
            return NamePrivate;
        }

        // -------------------------------------------------------------
        // Mutators. Used by the linker (FClass::Link) to wire up the
        // intra-struct linked list. Public so the linker doesn't need
        // friendship; the discipline is enforced by the calling-
        // convention (FField is only mutated during Link, which runs
        // single-threaded at module init).
        // -------------------------------------------------------------

        XPACT_FORCEINLINE void SetNext(FField* InNext) noexcept
        {
            Next = InNext;
        }

        // -------------------------------------------------------------
        // IsA -- cast-flag-based acceleration.
        //
        // `IsA(TargetClass)` returns true iff `this` is an instance of
        // TargetClass or any subclass. The implementation walks the
        // ClassPrivate->SuperClass chain and short-circuits if any
        // FFieldClass on the chain matches TargetClass.
        //
        // For FProperty subclasses (Phase 4b.4+), the fast path will
        // use `ClassPrivate->CastFlags & TargetClass->CastFlags` --
        // every FProperty subclass has a dedicated cast bit, so the
        // hierarchy walk reduces to a single AND. The fast path lands
        // in Phase 4b.4 when the per-subclass cast bits are populated;
        // Phase 4b.3 ships the hierarchy-walk slow path.
        //
        // O(N) in chain depth; the chain is bounded (spec §13 gate C1:
        // typical <= 3, max <= 10).
        //
        // nullptr TargetClass returns false (nothing is an instance of
        // "no class").
        // -------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool IsA(const FFieldClass* TargetClass) const noexcept
        {
            if (TargetClass == nullptr || ClassPrivate == nullptr)
            {
                return false;
            }
            return ClassPrivate->IsChildOf(TargetClass);
        }

        // -------------------------------------------------------------
        // ConstructField -- the FConstructFn target for the base
        // FFieldClass (kFieldStaticClass).
        //
        // Placement-new constructs an FField at OutStorage with the
        // given Owner and Name. The ClassPrivate slot is set to
        // &kFieldStaticClass.
        //
        // The function is declared here and defined in FField.cpp so the
        // kFieldStaticClass static-init can take its address without
        // including the full implementation in this header.
        // -------------------------------------------------------------
        static void ConstructField(FFieldVariant Owner, FName Name, void* OutStorage) noexcept;
    };

    // ---------------------------------------------------------------------
    // ABI locks (per Contract Rev 13.8 §11.2). The static_asserts here ARE
    // the ABI contract. Any layout change breaks every FField subclass
    // engine-wide (FProperty extends FField; FProperty layout assumes
    // FField at offsets 0..31 with the next field starting at offset 32).
    // ---------------------------------------------------------------------
    static_assert(sizeof(FField)  == 32,
                  "FField ABI lock: must be exactly 32 bytes "
                  "(ClassPrivate 8 + Owner 8 + Next 8 + NamePrivate 8). "
                  "See XCore-4b §5.1.");
    static_assert(alignof(FField) == 8,
                  "FField ABI lock: 8-byte alignment per §5.1's alignas(8)");

    // The LSB-tag invariant: FFieldVariant requires alignof(FField) >= 2
    // so the LSB is naturally zero. We assert >= 2 here as the structural
    // dependency, and 8 above as the spec-mandated value.
    static_assert(alignof(FField) >= 2,
                  "FField alignment must be >= 2 for the FFieldVariant LSB-tag "
                  "invariant (a valid FField* must have LSB == 0)");

    // Member offsets locked to the bytes the Stage B addendum names.
    static_assert(offsetof(FField, ClassPrivate) ==  0,
                  "FField ABI lock: ClassPrivate at offset 0");
    static_assert(offsetof(FField, Owner)        ==  8,
                  "FField ABI lock: Owner at offset 8");
    static_assert(offsetof(FField, Next)         == 16,
                  "FField ABI lock: Next at offset 16");
    static_assert(offsetof(FField, NamePrivate)  == 24,
                  "FField ABI lock: NamePrivate at offset 24");

    // Type traits.
    static_assert(::std::is_standard_layout_v<FField>,
                  "FField must be standard layout (so offsetof is well-defined; "
                  "all members public + same access + no virtual functions)");
    static_assert(::std::is_trivially_copyable_v<FField>,
                  "FField must be trivially copyable (memcpy-able in TArray; "
                  "every member is POD and the copy/move ctors are defaulted)");
    static_assert(::std::is_trivially_destructible_v<FField>,
                  "FField must be trivially destructible (no per-instance teardown)");

    // ---------------------------------------------------------------------
    // Cast<T> -- typed conversion helper.
    //
    // Usage: `T* Result = Cast<T>(Field)` returns Field as a `T*` if Field
    // is an instance of T (or a subclass), else nullptr.
    //
    // The implementation goes through IsA which uses the FFieldClass
    // hierarchy walk. Phase 4b.4 will add the cast-flag fast path.
    //
    // Requirements on T:
    //   * T must declare a static member `StaticClass()` returning
    //     `const FFieldClass*` -- the descriptor for T's class. Phase 4b.3
    //     does not ship StaticClass on any subclass (only FField is
    //     defined); Phase 4b.4 introduces it on FProperty and its
    //     subclasses. Calling Cast<FField>(some_field) at Phase 4b.3
    //     is valid only if the test fixture provides its own mock
    //     FFieldClass instance for the test's hierarchy.
    //
    // The free function form (rather than a member function) follows UE's
    // `Cast<T>(FFieldVariant)` discipline and avoids requiring every
    // subclass to expose a `Cast<T>()` method.
    //
    // Const-overloaded: passing `const FField*` returns `const T*`.
    // ---------------------------------------------------------------------

    template <typename T>
    [[nodiscard]] XPACT_FORCEINLINE T* Cast(FField* Field) noexcept
    {
        static_assert(sizeof(T) > 0, "T must not be an incomplete type");
        if (Field == nullptr)
        {
            return nullptr;
        }
        if (Field->IsA(T::StaticClass()))
        {
            return static_cast<T*>(Field);
        }
        return nullptr;
    }

    template <typename T>
    [[nodiscard]] XPACT_FORCEINLINE const T* Cast(const FField* Field) noexcept
    {
        static_assert(sizeof(T) > 0, "T must not be an incomplete type");
        if (Field == nullptr)
        {
            return nullptr;
        }
        if (Field->IsA(T::StaticClass()))
        {
            return static_cast<const T*>(Field);
        }
        return nullptr;
    }

    // ---------------------------------------------------------------------
    // ExactCast<T> -- like Cast<T> but requires an exact-class match
    // (rejects subclass instances).
    //
    // Useful for code paths that need to distinguish "this is exactly an
    // FProperty" from "this is some FProperty subclass". Mirrors UE's
    // `ExactCast<T>(UObject*)` discipline.
    // ---------------------------------------------------------------------

    template <typename T>
    [[nodiscard]] XPACT_FORCEINLINE T* ExactCast(FField* Field) noexcept
    {
        static_assert(sizeof(T) > 0, "T must not be an incomplete type");
        if (Field == nullptr || Field->GetClass() != T::StaticClass())
        {
            return nullptr;
        }
        return static_cast<T*>(Field);
    }

    template <typename T>
    [[nodiscard]] XPACT_FORCEINLINE const T* ExactCast(const FField* Field) noexcept
    {
        static_assert(sizeof(T) > 0, "T must not be an incomplete type");
        if (Field == nullptr || Field->GetClass() != T::StaticClass())
        {
            return nullptr;
        }
        return static_cast<const T*>(Field);
    }

} // namespace XCore::Reflect
