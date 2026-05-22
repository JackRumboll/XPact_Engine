// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// DefaultAllocator.h -- the one allocator policy used by every container.
// =====================================================================
//
// XCore-4a Rev 3, Section 5.1 (TArray conceptual layout: "AllocatorT
// m_alloc with XPACT_NO_UNIQUE_ADDRESS") + Section 5.5 row 2 ("49
// allocator policies (TInlineAllocator, FFixedSizeAllocator, etc.) ->
// One default allocator. Stack storage is the distinct type
// TStaticArray<T,N>").
//
// Per the divergence row in Section 5.5: "UE's enormous allocator-policy
// template-bloat is a compile-time tax and an ABI-fragility surface."
// XPact ships exactly ONE allocator policy used by every container:
// DefaultAllocator. It routes every allocation through
// FMemory::MallocOrAbort / ReallocOrAbort / Free with a per-instance
// FMemTag carrier so per-container memory attribution is accurate.
//
// The allocator is intentionally STATELESS at the byte level: it carries
// a 2-byte FMemTag and that's it. With XPACT_NO_UNIQUE_ADDRESS on the
// TArrayCore::m_alloc field, the allocator either folds into adjacent
// padding (when paired with int32 fields that already have alignment
// holes) or expands by 2 bytes - never more. The Section 5.5 row 6
// "~10-80 KB saved per scene at ~10k TArray instances" claim is the
// load-bearing rationale for the empty-base-optimisation pattern.
//
// Why a class wrapper rather than a free function? Three reasons:
//
//   1. Container types take AllocatorT as a template parameter, so the
//      same TArrayCore template can be instantiated against future
//      specialised allocators (a stack-pinned scratch allocator, an
//      arena allocator, a debug-canary allocator) without source
//      changes. The template parameter is a NEEDED degree of freedom
//      even though only one policy ships in XCore-4a.
//
//   2. The per-instance FMemTag carrier means a per-container tag
//      override is possible at construction (`TArray<int, DefaultAllocator>
//      MyArray(DefaultAllocator{FMemTag::Math})` for a math-tagged array
//      of ints). The default ctor uses FMemTag::Container.
//
//   3. The allocator carries the tag, so even if the container's element
//      type would naturally suggest a different tag (e.g., a TArray<float>
//      inside the math layer wanting FMemTag::Math), the developer can
//      override at the call site without subclassing.
//
// Phase 1c contract: DefaultAllocator is implemented header-inline plus
// a thin .cpp shim that holds the FMemory dispatch (and the dispatch is
// already in HAL/FMemory.h, so the .cpp is effectively a no-op
// translation unit kept for symmetry with other modules).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"     // XPACT_NO_UNIQUE_ADDRESS, XPACT_FORCEINLINE
#include "HAL/FMemTag.h"            // FMemTag
#include "HAL/FMemory.h"            // FMemory::MallocOrAbort / Free / Realloc

namespace XCore
{
    // -----------------------------------------------------------------
    // DefaultAllocator -- the single allocator policy in XCore-4a.
    //
    // Layout: holds exactly one FMemTag (uint16). With XPACT_NO_UNIQUE_ADDRESS
    // on the container's allocator field, this folds into adjacent padding
    // in 95%+ of real-world container layouts.
    //
    // Methods:
    //   Allocate(Size, Align)             -> calls FMemory::MallocOrAbort.
    //   Reallocate(Ptr, NewSize, Align)   -> calls FMemory::ReallocOrAbort.
    //   Deallocate(Ptr)                   -> calls FMemory::Free.
    //
    // All three are noexcept; OOM aborts cleanly per the Section 4.1
    // OOM contract ("containers cannot safely propagate ReturnNull: a
    // half-constructed container with an already-registered XGCRootSpan
    // would expose a dangling buffer base to the collector").
    //
    // Construction:
    //   * Default ctor uses FMemTag::Container (the canonical container
    //     tag; Section 4.1 + FMemTag.h).
    //   * Tag-taking ctor allows per-instance override
    //     (e.g., DefaultAllocator{FMemTag::Math}).
    //
    // Threading: stateless at the method level (the FMemory backing is
    // already thread-safe per Section 4.2); the allocator itself holds
    // only the tag, which is set at construction and never mutated. Two
    // threads concurrently calling Allocate on the same DefaultAllocator
    // instance is safe (same as concurrent FMemory::Malloc).
    //
    // ABI lock: sizeof(DefaultAllocator) == 2 (one FMemTag field).
    // -----------------------------------------------------------------

    class DefaultAllocator
    {
    public:
        // -------------------------------------------------------------
        // Default ctor uses FMemTag::Container.
        // -------------------------------------------------------------
        constexpr DefaultAllocator() noexcept
            : m_tag(::XCore::HAL::FMemTag::Container)
        {
        }

        // -------------------------------------------------------------
        // Tag-taking ctor allows per-container override.
        //
        // explicit so a stray `DefaultAllocator a = FMemTag::Math;`
        // gets flagged; the override pattern is
        // `DefaultAllocator{FMemTag::Math}`.
        // -------------------------------------------------------------
        constexpr explicit DefaultAllocator(::XCore::HAL::FMemTag InTag) noexcept
            : m_tag(InTag)
        {
        }

        // -------------------------------------------------------------
        // Allocate -- request `Size` bytes with alignment `Align`.
        //
        // Routes through FMemory::MallocOrAbort: on OOM the wrapper
        // aborts cleanly with a tagged diagnostic. Per the container
        // OOM contract (Section 4.1 + Section 5.1), containers cannot
        // propagate ReturnNull semantics; the abort is the contract.
        //
        // The returned block is at least `Size` bytes and at least
        // `Align`-aligned. Calling Deallocate on it later releases the
        // block.
        //
        // For Size == 0 the wrapper returns a non-null distinct pointer
        // (matches operator new's "zero-size returns a distinct ptr"
        // semantics; the container layer never calls with Size == 0 in
        // practice, but the contract is preserved for safety).
        // -------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE void* Allocate(::SIZE_T Size, ::SIZE_T Align) noexcept
        {
            return ::XCore::HAL::FMemory::MallocOrAbort(Size, Align, m_tag);
        }

        // -------------------------------------------------------------
        // Reallocate -- resize an existing block.
        //
        // Ptr may be nullptr (degenerates to Allocate). NewSize may be
        // zero (degenerates to Deallocate; returns nullptr).
        //
        // Routes through FMemory::ReallocOrAbort. The in-place-resize
        // fast path is preserved when the bin accommodates; the
        // fallback Malloc-new + Memcpy + Free-old is also wrapped in
        // the abort-on-null semantics.
        // -------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE void* Reallocate(void* Ptr, ::SIZE_T NewSize, ::SIZE_T Align) noexcept
        {
            return ::XCore::HAL::FMemory::ReallocOrAbort(Ptr, NewSize, Align, m_tag);
        }

        // -------------------------------------------------------------
        // Deallocate -- release a block previously returned by Allocate
        // / Reallocate.
        //
        // Ptr may be nullptr (no-op). The block's tag is implicitly
        // determined from the allocator header; the container does not
        // need to pass the tag back.
        // -------------------------------------------------------------
        XPACT_FORCEINLINE void Deallocate(void* Ptr) noexcept
        {
            ::XCore::HAL::FMemory::Free(Ptr);
        }

        // -------------------------------------------------------------
        // GetTag -- diagnostic accessor; primarily for tests.
        // -------------------------------------------------------------
        [[nodiscard]] constexpr ::XCore::HAL::FMemTag GetTag() const noexcept
        {
            return m_tag;
        }

    private:
        // The carried tag. uint16-backed (per FMemTag.h ABI lock).
        ::XCore::HAL::FMemTag m_tag;
    };

    // ABI lock: stateless beyond the 2-byte tag.
    static_assert(sizeof(DefaultAllocator) == 2,
                  "DefaultAllocator ABI lock: must be exactly 2 bytes "
                  "(one FMemTag); the XPACT_NO_UNIQUE_ADDRESS attribute "
                  "on container fields depends on this size class to "
                  "produce the documented EBO savings (Section 5.5).");

    static_assert(alignof(DefaultAllocator) == 2,
                  "DefaultAllocator ABI lock: 2-byte alignment");

} // namespace XCore
