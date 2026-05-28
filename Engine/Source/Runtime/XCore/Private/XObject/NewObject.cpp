// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// NewObject.cpp -- NewObject<T> hot-path body + Try* fallible variant
// (XCoreXObject Rev 4 §3.5 + Phase 5.d).
// =====================================================================
//
// Two non-template bodies live in this TU:
//
//   * NewObjectImpl -- the load-bearing hot-path implementation. Per
//     spec §3.5: allocate raw storage; reserve FXObjectArray slot;
//     construct FXObjectInitializer; placement-new T via
//     ClassConstructorFn; fill XObject header; bind FXObjectArray;
//     dispatch PostInitProperties via ~FXObjectInitializer at scope
//     exit; clear NeedInitialization; return T*.
//
//   * TryNewObjectImpl -- fallible-style entry point. Surfaces
//     pre-condition failures as ENewObjectError values instead of
//     aborting via XPACT_CHECK. The Phase 5.d body shares the
//     happy-path with NewObjectImpl (both route through a single
//     internal helper).
//
// =====================================================================

#include "XObject/NewObject.h"

#include "HAL/FMemory.h"                       // FMemTag::XObject
#include "HAL/XInitPhase.h"                    // EInitPhase / EngineInitPhase
#include "Reflection/EClassFlags.h"
#include "Reflection/FClass.h"
#include "Reflection/FFieldVariant.h"
#include "Reflection/FName.h"
#include "XObject/FXObjectAllocator.h"
#include "XObject/FXObjectArray.h"
#include "XObject/FXObjectInitializer.h"
#include "XObject/XObject.h"

#include "Macros/XAssertionMacros.h"

#include <atomic>
#include <cstdint>
#include <new>

namespace XCore
{

    namespace
    {
        // -------------------------------------------------------------
        // Internal helper: shared allocation + construction path for
        // both the infallible NewObjectImpl and the fallible
        // TryNewObjectImpl. Returns nullptr on any pre-condition or
        // allocation failure; the caller is responsible for either
        // aborting (infallible) or surfacing the error (fallible).
        //
        // The OutError parameter lets the caller distinguish "OOM" from
        // "kInvalidClass" / "kAbstractClass" / "kInvalidPhase". When the
        // function returns non-null, *OutError is left unmodified.
        // -------------------------------------------------------------
        XObject* NewObjectInternal(
            const ::XCore::Reflect::FClass* Class,
            XObject* Outer,
            ::XCore::Reflect::FName Name,
            EObjectFlags Flags,
            XObject* Archetype,
            ENewObjectError* OutError) noexcept
        {
            // ----- Pre-condition: Class non-null -----
            if (Class == nullptr)
            {
                if (OutError != nullptr) *OutError = ENewObjectError::kInvalidClass;
                return nullptr;
            }

            // ----- Pre-condition: phase gate (FIX-A-CRIT-5) -----
            if (::XCore::HAL::EngineInitPhase() < ::XCore::HAL::EInitPhase::PostStaticInit)
            {
                if (OutError != nullptr) *OutError = ENewObjectError::kInvalidPhase;
                return nullptr;
            }

            // ----- Pre-condition: not abstract -----
            //
            // EClassFlags::CLASS_Abstract on Class means "this class
            // cannot be directly instantiated". The Phase 5.d body
            // rejects the call before allocation; the caller (the
            // infallible NewObject) surfaces this as an XPACT_CHECK
            // abort via the post-call check; the fallible variant
            // returns the error code.
            const ::XCore::Reflect::EClassFlags ClassFlagsValue = Class->GetClassFlags();
            if ((ClassFlagsValue & ::XCore::Reflect::EClassFlags::CLASS_Abstract)
                != ::XCore::Reflect::EClassFlags::CLASS_None)
            {
                if (OutError != nullptr) *OutError = ENewObjectError::kAbstractClass;
                return nullptr;
            }

            // ----- Step 1: derive Size + Align from FClass -----
            //
            // Per FStruct: PropertiesSize is the total size in bytes of
            // the class instance (excluding tail padding); MinAlignment
            // is the alignment requirement. For XObject-derived classes
            // PropertiesSize is at least 56 (the XObject header). For
            // a hand-rolled FClass with PropertiesSize == 0 (no XHT-
            // emit yet for the test classes), we substitute the bare
            // XObject footprint so allocation does not size-zero.
            ::SIZE_T Size = static_cast<::SIZE_T>(Class->GetPropertiesSize());
            if (Size < sizeof(XObject))
            {
                // Defensive minimum: at least an XObject header. This
                // covers the Phase 5.d test-harness path where FClass
                // is hand-constructed with PropertiesSize = 0. Real
                // XHT-emitted FClasses always carry the correct
                // PropertiesSize.
                Size = sizeof(XObject);
            }

            ::SIZE_T Align = static_cast<::SIZE_T>(Class->GetMinAlignment());
            if (Align < alignof(XObject))
            {
                Align = alignof(XObject);
            }

            // ----- Step 2: AllocateRaw via FXObjectAllocator -----
            //
            // Per spec §3.5 step 1: route through FXObjectAllocator's
            // 3-arg AllocateRaw (Size, Align, FClass*). The allocator
            // returns nullptr on OOM (its body routes through
            // FMemory::MallocOrAbort which aborts on OOM by default;
            // the fallible path is a Phase 5.h+ deliverable per the
            // spec trailing prose).
            void* const Storage = ::XCore::FXObjectAllocator::Get().AllocateRaw(
                Size, Align, Class);
            if (Storage == nullptr)
            {
                if (OutError != nullptr) *OutError = ENewObjectError::kOutOfMemory;
                return nullptr;
            }

            // ----- Step 3: Reserve an FXObjectArray slot -----
            //
            // Per spec §3.5 step 2: ReserveSlot returns the assigned
            // InternalIndex + captures the SerialNumber. The XObject's
            // own SerialNumber field will match the captured value
            // after the header fill.
            ::uint32 SerialNumber = 0u;
            const ::int32 InternalIndex =
                ::XCore::FXObjectArray::Get().ReserveSlot(&SerialNumber);

            // ----- Step 4: Placement-new the XObject + invoke
            //             ClassConstructorFn (per spec §3.5 step 3) -----
            //
            // For Phase 5.d the ClassConstructorFn signature in
            // XCore-4b is `void(*)(void*, FFieldVariant)` (no
            // Initializer parameter). The Phase 5.d Initializer is
            // constructed AROUND the ClassConstructorFn call so the
            // Initializer's lifetime brackets construction; the
            // user-class ctor that wants Initializer access uses the
            // canonical const-ref signature documented at spec §8.4.1.
            //
            // XHT's emit (Phase 5.f+) will widen the
            // ClassConstructorFn signature to thread the Initializer
            // through; the Phase 5.d posture works for hand-rolled
            // XObject subclasses (the test harness) and the
            // PostInitProperties dispatch at ~FXObjectInitializer
            // scope exit is the structural integration point.
            //
            // RF_NeedLoad bypass (Rev 3 FIX-M-R2-4 + §10.6.1): if
            // RF_NeedLoad is set on entry, the loader is responsible
            // for invoking PostInitProperties later (after Serialize
            // completes); skip the FXObjectInitializer construction
            // to avoid firing PostInitProperties twice.
            //
            // First: placement-new an XObject into the storage. This
            // initialises the XObject header to default-state (all
            // zeros). The subsequent ClassConstructorFn (if non-null)
            // overwrites class-specific fields; we then fill the
            // header in step 5.
            XObject* const Obj = ::new (Storage) XObject();

            const bool bSkipInitializer =
                HasAnyObjectFlags(Flags, EObjectFlags::NeedLoad);

            if (bSkipInitializer)
            {
                // Loader path: skip the FXObjectInitializer + the
                // PostInitProperties dispatch. The loader will fire
                // PostInitProperties (then PostLoad) in batch after
                // Serialize completes (per spec §10.6.1).
                if (Class->ClassConstructorFn != nullptr)
                {
                    // FFieldVariant carries an FField* OR FStruct* (per
                    // the LSB-tag scheme in FFieldVariant.h). The
                    // ClassConstructorFn's Owner argument names the
                    // OWNING reflection node, NOT the XObject Outer.
                    // For Phase 5.d the most general posture is a
                    // default-constructed (null) FFieldVariant; XHT-
                    // emitted user-class ctors that need the Outer
                    // read it from the Initializer's GetTarget()
                    // accessor or the XObject's Outer field directly.
                    Class->ClassConstructorFn(
                        Storage, ::XCore::Reflect::FFieldVariant());
                }

                // ----- Step 5 (header fill) for the loader path -----
                Obj->ClassPrivate  = Class;
                Obj->InternalIndex = InternalIndex;
                Obj->SerialNumber  = SerialNumber;
                Obj->Outer         = Outer;
                Obj->NamePrivate   = Name;
                Obj->ObjectFlags.store(
                    ToUnderlying(Flags | EObjectFlags::NeedInitialization),
                    ::std::memory_order_release);

                // ----- Step 6: Bind the slot -----
                ::XCore::FXObjectArray::Get().BindObject(InternalIndex, Obj);

                // NeedInitialization stays set; the loader clears it
                // after PostInitProperties + PostLoad fire in batch.
                return Obj;
            }

            // Non-loader path: construct the Initializer, invoke the
            // ClassConstructorFn (which may run user-class ctor bodies
            // that call CreateDefaultSubobject through the Initializer),
            // then fill the header, then bind, then let the Initializer
            // dtor fire PostInitProperties at scope exit.
            //
            // The Initializer is constructed BEFORE the
            // ClassConstructorFn call so user-class ctors that capture
            // the Initializer by const-ref via the canonical signature
            // (spec §8.4.1) have a live Initializer to reference.
            //
            // PER-SCOPE PostInitProperties FIRE: the Initializer's
            // destructor at the end of the enclosing { ... } block
            // dispatches PostInitProperties via the LifecycleTable.
            // The block scope is the rest of this function up to
            // `return Obj;` below.
            {
                FXObjectInitializer Initializer(Obj, Class, Archetype);

                // Invoke the ClassConstructorFn if present. For Phase
                // 5.d hand-rolled FClass instances (test harness)
                // ClassConstructorFn may be null; the placement-new
                // of XObject above is sufficient to produce a valid
                // empty XObject.
                if (Class->ClassConstructorFn != nullptr)
                {
                    // FFieldVariant carries an FField* OR FStruct* (per
                    // the LSB-tag scheme in FFieldVariant.h). The
                    // ClassConstructorFn's Owner argument names the
                    // OWNING reflection node, NOT the XObject Outer.
                    // For Phase 5.d the most general posture is a
                    // default-constructed (null) FFieldVariant; XHT-
                    // emitted user-class ctors that need the Outer
                    // read it from the Initializer's GetTarget()
                    // accessor or the XObject's Outer field directly.
                    Class->ClassConstructorFn(
                        Storage, ::XCore::Reflect::FFieldVariant());
                }

                // ----- Step 5: fill the XObject header -----
                Obj->ClassPrivate  = Class;
                Obj->InternalIndex = InternalIndex;
                Obj->SerialNumber  = SerialNumber;
                Obj->Outer         = Outer;
                Obj->NamePrivate   = Name;
                Obj->ObjectFlags.store(
                    ToUnderlying(Flags | EObjectFlags::NeedInitialization),
                    ::std::memory_order_release);

                // ----- Step 6: Bind the slot -----
                ::XCore::FXObjectArray::Get().BindObject(InternalIndex, Obj);

                // Step 7 fires here at scope exit:
                //   ~FXObjectInitializer dispatches PostInitProperties
                //   via Class->LifecycleTable->Slots[PostInitProperties]
                //   (if the slot is populated).
            }

            // ----- Step 8: Clear NeedInitialization -----
            //
            // PostInitProperties has fired (or no-op'd for classes with
            // no hook). The object is now fully constructed; clear the
            // initialisation-in-progress bit so external observers see
            // the object as "ready".
            Obj->ClearFlags(EObjectFlags::NeedInitialization);

            return Obj;
        }
    } // namespace

    // =================================================================
    // NewObjectImpl -- the infallible-style entry point.
    //
    // Asserts on every pre-condition failure (kInvalidClass,
    // kInvalidPhase, kAbstractClass). Returns the constructed
    // XObject* on success.
    //
    // Per spec §3.5: OOM is a programmer error in trainee builds (the
    // underlying FMemory::Malloc aborts via the abort-policy); the
    // fallible TryNewObject is the recoverable path for loader code.
    // =================================================================
    XObject* NewObjectImpl(const ::XCore::Reflect::FClass* Class,
                           XObject* Outer,
                           ::XCore::Reflect::FName Name,
                           EObjectFlags Flags,
                           XObject* Archetype) noexcept
    {
        ENewObjectError Error = ENewObjectError::kOutOfMemory;
        XObject* const Obj = NewObjectInternal(
            Class, Outer, Name, Flags, Archetype, &Error);

        // The infallible path asserts on any pre-condition failure.
        // The error code is captured for the diagnostic; the
        // XPACT_CHECK fires in Dev/Debug only (Shipping compiles out).
        XPACT_CHECK(Obj != nullptr);
        return Obj;
    }

    // =================================================================
    // TryNewObjectImpl -- the fallible-style entry point.
    //
    // Returns Result<XObject*, ENewObjectError>: XObject* on success,
    // an error code on failure.
    // =================================================================
    ::XCore::Result<XObject*, ENewObjectError> TryNewObjectImpl(
        const ::XCore::Reflect::FClass* Class,
        XObject* Outer,
        ::XCore::Reflect::FName Name,
        EObjectFlags Flags,
        XObject* Archetype) noexcept
    {
        ENewObjectError Error = ENewObjectError::kOutOfMemory;
        XObject* const Obj = NewObjectInternal(
            Class, Outer, Name, Flags, Archetype, &Error);

        if (Obj != nullptr)
        {
            return Obj;
        }
        return ::XCore::Unexpected(Error);
    }

} // namespace XCore
