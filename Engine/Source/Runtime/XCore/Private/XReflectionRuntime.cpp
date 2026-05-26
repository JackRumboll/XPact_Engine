// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XReflectionRuntime.cpp -- Phase 4b.6 reflection-runtime registry.
// =====================================================================
//
// XCore-4b Rev 4, Section 10. Body of the process-singleton registry
// declared in XReflectionRuntime.h.
//
// DESIGN (spec §10.2):
//
//   Four TMap<FName, const FX*> instances (one per Class / Struct /
//   Enum / Interface), plus two TArray<const FX*> iteration buffers
//   (AllClasses, AllStructs) plus matching buffers for Enums and
//   Interfaces (the spec lists only AllClasses + AllStructs but per
//   the dispatch task we need iteration for all four kinds; the extra
//   buffers cost ~24 bytes each).
//
//   FScriptStruct shares the StructsByName / AllStructs storage with
//   FStruct: an FScriptStruct IS a FStruct via inheritance, so we
//   store the FStruct-base pointer in the map and downcast on
//   FindScriptStruct (verifying through a parallel ScriptStructsByName
//   map that holds only the FScriptStruct-typed slots).
//
//   All four maps share a single FRWLock. Reads acquire shared mode;
//   writes acquire exclusive. The map's mutation cost is bounded by
//   per-DLL-load module-init time (spec §10.4: <50ms for 1000 classes
//   on Win64 desktop).
//
//   OwningModule is tracked via a parallel TMap<FName, FName>
//   (DescriptorName -> ModuleName) for each kind. This avoids
//   ABI-breaking the FStruct/FClass/FEnum/FInterface descriptor
//   layouts (which are locked at Stage B addendum / Contract Rev
//   13.8). Costs ~16 bytes per registered descriptor (FName+FName
//   TPair); acceptable for ~5000 typical descriptor count.
//
//   The thread-local "current module" slot is implemented via the
//   compiler's native `thread_local` storage class. Phase 1g's
//   TModuleSafeThreadLocal would be the principled alternative (per
//   the DLL-reload-safety pattern), but the registry-current-module
//   slot is module-init-only data: it is set in OnModuleLoad and
//   cleared at OnModuleUnload, both of which run BEFORE any DLL
//   reloads from the slot's perspective (a thread that loads a
//   module sets the slot, finishes Register*-sequence, clears the
//   slot; the slot is never touched across a DLL boundary). The
//   native thread_local is sufficient for the use case.
//
// =====================================================================

#include "XReflectionRuntime.h"

#include "Containers/TArray.h"
#include "Containers/TMap.h"
#include "HAL/FRWLock.h"
#include "Macros/XCoreTypes.h"

#include "Reflection/FClass.h"
#include "Reflection/FEnum.h"
#include "Reflection/FInterface.h"
#include "Reflection/FName.h"
#include "Reflection/FScriptStruct.h"
#include "Reflection/FStruct.h"

#include <cstddef>

namespace XCore::Reflect
{

// =====================================================================
// Internal registry storage.
//
// The single FReflectionRegistry instance is a function-local-static
// inside GetRegistry() so the C++11 magic-statics rule guarantees
// thread-safe single initialisation. The instance lives until process
// exit; we do not explicitly destroy it (the FRWLock destructor is
// benign but the order-of-destruction lottery is best avoided).
//
// The struct is NOT exposed in the header -- it is an implementation
// detail. The spec §10.2 declares an `extern FReflectionRegistry
// g_ReflectionRegistry`, but in practice the only consumers of the
// internal data are this .cpp (every API is a thin shim over the
// internal state). Hiding the struct keeps the ABI surface clean.
// =====================================================================

struct FReflectionRegistry
{
    // ---- Per-kind name->pointer maps (FName-indexed O(1) lookup) ----
    ::XCore::TMap<FName, const FClass*>        ClassesByName;
    ::XCore::TMap<FName, const FStruct*>       StructsByName;
    ::XCore::TMap<FName, const FScriptStruct*> ScriptStructsByName;
    ::XCore::TMap<FName, const FEnum*>         EnumsByName;
    ::XCore::TMap<FName, const FInterface*>    InterfacesByName;

    // ---- Per-kind iteration buffers (registration-order) ----
    ::XCore::TArray<const FClass*>             AllClasses;
    ::XCore::TArray<const FStruct*>            AllStructs;
    ::XCore::TArray<const FEnum*>              AllEnums;
    ::XCore::TArray<const FInterface*>         AllInterfaces;

    // ---- Per-kind OwningModule maps (FName -> FName) ----
    // Tracks which module registered each descriptor; OnModuleUnload
    // consults these to identify the entries to purge. Storing
    // OwningModule out-of-band (not as a per-descriptor field)
    // preserves the descriptor ABI lock from Stage B addendum.
    ::XCore::TMap<FName, FName>                ClassOwningModule;
    ::XCore::TMap<FName, FName>                StructOwningModule;
    ::XCore::TMap<FName, FName>                ScriptStructOwningModule;
    ::XCore::TMap<FName, FName>                EnumOwningModule;
    ::XCore::TMap<FName, FName>                InterfaceOwningModule;

    // ---- Global lock (single FRWLock for all four maps) ----
    mutable ::XCore::HAL::FRWLock              Lock;

    FReflectionRegistry() noexcept = default;
    ~FReflectionRegistry() noexcept = default;

    FReflectionRegistry(const FReflectionRegistry&)            = delete;
    FReflectionRegistry(FReflectionRegistry&&)                 = delete;
    FReflectionRegistry& operator=(const FReflectionRegistry&) = delete;
    FReflectionRegistry& operator=(FReflectionRegistry&&)      = delete;
};

namespace
{
    // -----------------------------------------------------------------
    // Singleton accessor.
    //
    // Function-local-static guarantees C++11 magic-statics initialisation:
    // the first caller constructs the instance; subsequent callers see
    // the fully-initialised state.
    // -----------------------------------------------------------------
    FReflectionRegistry& GetRegistry() noexcept
    {
        static FReflectionRegistry Instance;
        return Instance;
    }

    // -----------------------------------------------------------------
    // Thread-local current-module slot.
    //
    // Set by OnModuleLoad; consulted by Register* to stamp each
    // registration's OwningModule; cleared by OnModuleUnload.
    //
    // FName default-constructs to NAME_None (Index=0, SerialNumber=0)
    // via its constexpr default ctor, so the thread_local can use
    // a constexpr-initialised default.
    //
    // Storage is the compiler's native thread_local. The slot is
    // process-local + thread-local: a thread that does not call
    // OnModuleLoad sees NAME_None throughout (registration's
    // OwningModule will be NAME_None, which is the documented
    // "engine-core pre-registration" sentinel).
    // -----------------------------------------------------------------
    thread_local FName tls_CurrentModule = FName{};
} // anonymous namespace

// =====================================================================
// Find* (read path: shared-lock).
// =====================================================================

const FClass* XReflectionRuntime::FindClass(FName Name) noexcept
{
    if (Name.IsNone())
    {
        return nullptr;
    }
    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedReadLock Read(Reg.Lock);
    if (const FClass* const* Found = Reg.ClassesByName.Find(Name))
    {
        return *Found;
    }
    return nullptr;
}

const FStruct* XReflectionRuntime::FindStruct(FName Name) noexcept
{
    if (Name.IsNone())
    {
        return nullptr;
    }
    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedReadLock Read(Reg.Lock);
    if (const FStruct* const* Found = Reg.StructsByName.Find(Name))
    {
        return *Found;
    }
    return nullptr;
}

const FScriptStruct* XReflectionRuntime::FindScriptStruct(FName Name) noexcept
{
    if (Name.IsNone())
    {
        return nullptr;
    }
    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedReadLock Read(Reg.Lock);
    if (const FScriptStruct* const* Found = Reg.ScriptStructsByName.Find(Name))
    {
        return *Found;
    }
    return nullptr;
}

const FEnum* XReflectionRuntime::FindEnum(FName Name) noexcept
{
    if (Name.IsNone())
    {
        return nullptr;
    }
    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedReadLock Read(Reg.Lock);
    if (const FEnum* const* Found = Reg.EnumsByName.Find(Name))
    {
        return *Found;
    }
    return nullptr;
}

const FInterface* XReflectionRuntime::FindInterface(FName Name) noexcept
{
    if (Name.IsNone())
    {
        return nullptr;
    }
    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedReadLock Read(Reg.Lock);
    if (const FInterface* const* Found = Reg.InterfacesByName.Find(Name))
    {
        return *Found;
    }
    return nullptr;
}

// =====================================================================
// Register* (write path: exclusive-lock).
//
// Each Register* follows the same shape:
//   * Reject nullptr inputs (defensive).
//   * Reject NAME_None descriptor names (no-named-types contract).
//   * Acquire exclusive lock.
//   * Probe the matching map for the FName.
//     - If found AND existing pointer == new pointer -> idempotent
//       re-register; return true with no state change.
//     - If found AND existing pointer != new pointer -> name collision;
//       return false (caller logs the diagnostic).
//     - If not found -> insert into map, append to AllX, stamp the
//       OwningModule from the thread-local slot, return true.
// =====================================================================

bool XReflectionRuntime::RegisterClass(const FClass* Class) noexcept
{
    if (Class == nullptr)
    {
        return false;
    }
    const FName Name = Class->GetFName();
    if (Name.IsNone())
    {
        return false;
    }

    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedWriteLock Write(Reg.Lock);

    if (const FClass* const* Existing = Reg.ClassesByName.Find(Name))
    {
        // Idempotent re-register accepted; collision with a different
        // pointer is rejected.
        return *Existing == Class;
    }

    Reg.ClassesByName.Add(Name, Class);
    Reg.AllClasses.Add(Class);
    Reg.ClassOwningModule.Add(Name, tls_CurrentModule);
    return true;
}

bool XReflectionRuntime::RegisterStruct(const FStruct* Struct) noexcept
{
    if (Struct == nullptr)
    {
        return false;
    }
    const FName Name = Struct->GetFName();
    if (Name.IsNone())
    {
        return false;
    }

    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedWriteLock Write(Reg.Lock);

    if (const FStruct* const* Existing = Reg.StructsByName.Find(Name))
    {
        return *Existing == Struct;
    }

    Reg.StructsByName.Add(Name, Struct);
    Reg.AllStructs.Add(Struct);
    Reg.StructOwningModule.Add(Name, tls_CurrentModule);
    return true;
}

bool XReflectionRuntime::RegisterScriptStruct(const FScriptStruct* ScriptStruct) noexcept
{
    if (ScriptStruct == nullptr)
    {
        return false;
    }
    // An FScriptStruct IS a FStruct (subclass). The Name comes through
    // FStruct's GetFName.
    const FName Name = static_cast<const FStruct*>(ScriptStruct)->GetFName();
    if (Name.IsNone())
    {
        return false;
    }

    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedWriteLock Write(Reg.Lock);

    // Probe BOTH the FScriptStruct map AND the FStruct map; both must
    // either be absent (insert into both) or both contain the same
    // pointer (idempotent re-register).
    const FScriptStruct* const* ExistingScript = Reg.ScriptStructsByName.Find(Name);
    const FStruct*       const* ExistingStruct = Reg.StructsByName.Find(Name);

    if (ExistingScript != nullptr || ExistingStruct != nullptr)
    {
        // Idempotency check: both must point at our descriptor for the
        // re-register to be accepted.
        const bool ScriptOk = (ExistingScript != nullptr) && (*ExistingScript == ScriptStruct);
        const bool StructOk = (ExistingStruct != nullptr) &&
                              (*ExistingStruct == static_cast<const FStruct*>(ScriptStruct));
        return ScriptOk && StructOk;
    }

    Reg.ScriptStructsByName.Add(Name, ScriptStruct);
    Reg.StructsByName.Add(Name, static_cast<const FStruct*>(ScriptStruct));
    Reg.AllStructs.Add(static_cast<const FStruct*>(ScriptStruct));
    Reg.ScriptStructOwningModule.Add(Name, tls_CurrentModule);
    Reg.StructOwningModule.Add(Name, tls_CurrentModule);
    return true;
}

bool XReflectionRuntime::RegisterEnum(const FEnum* Enum) noexcept
{
    if (Enum == nullptr)
    {
        return false;
    }
    // FEnum extends FField; the Name lives in FField::NamePrivate.
    const FName Name = Enum->NamePrivate;
    if (Name.IsNone())
    {
        return false;
    }

    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedWriteLock Write(Reg.Lock);

    if (const FEnum* const* Existing = Reg.EnumsByName.Find(Name))
    {
        return *Existing == Enum;
    }

    Reg.EnumsByName.Add(Name, Enum);
    Reg.AllEnums.Add(Enum);
    Reg.EnumOwningModule.Add(Name, tls_CurrentModule);
    return true;
}

bool XReflectionRuntime::RegisterInterface(const FInterface* Interface) noexcept
{
    if (Interface == nullptr)
    {
        return false;
    }
    const FName Name = Interface->NamePrivate;
    if (Name.IsNone())
    {
        return false;
    }

    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedWriteLock Write(Reg.Lock);

    if (const FInterface* const* Existing = Reg.InterfacesByName.Find(Name))
    {
        return *Existing == Interface;
    }

    Reg.InterfacesByName.Add(Name, Interface);
    Reg.AllInterfaces.Add(Interface);
    Reg.InterfaceOwningModule.Add(Name, tls_CurrentModule);
    return true;
}

// =====================================================================
// Unregister* (write path: exclusive-lock).
//
// Each Unregister* removes both the (Name -> Descriptor) entry from
// the matching map AND the corresponding entry in the AllX TArray.
// The AllX removal is O(N) (linear scan to find the slot) -- bounded
// by the registered-type count (typically <5000); acceptable for the
// rare hot-reload module-unload path.
// =====================================================================

namespace
{
    template <typename TDesc>
    void RemoveFromAll(::XCore::TArray<const TDesc*>& All, const TDesc* Target) noexcept
    {
        const ::int32 Count = All.Num();
        for (::int32 I = 0; I < Count; ++I)
        {
            if (All[I] == Target)
            {
                All.RemoveAtSwap(I);
                return;
            }
        }
    }
} // anonymous namespace

void XReflectionRuntime::UnregisterClass(FName Name) noexcept
{
    if (Name.IsNone())
    {
        return;
    }
    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedWriteLock Write(Reg.Lock);

    if (const FClass* const* Found = Reg.ClassesByName.Find(Name))
    {
        const FClass* Target = *Found;
        Reg.ClassesByName.Remove(Name);
        Reg.ClassOwningModule.Remove(Name);
        RemoveFromAll(Reg.AllClasses, Target);
    }
}

void XReflectionRuntime::UnregisterStruct(FName Name) noexcept
{
    if (Name.IsNone())
    {
        return;
    }
    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedWriteLock Write(Reg.Lock);

    if (const FStruct* const* Found = Reg.StructsByName.Find(Name))
    {
        const FStruct* Target = *Found;
        Reg.StructsByName.Remove(Name);
        Reg.StructOwningModule.Remove(Name);
        RemoveFromAll(Reg.AllStructs, Target);
        // If the struct was also registered as an FScriptStruct, purge
        // that slot too (the StructsByName probe finds the same FName
        // either way).
        if (Reg.ScriptStructsByName.Contains(Name))
        {
            Reg.ScriptStructsByName.Remove(Name);
            Reg.ScriptStructOwningModule.Remove(Name);
        }
    }
}

void XReflectionRuntime::UnregisterScriptStruct(FName Name) noexcept
{
    if (Name.IsNone())
    {
        return;
    }
    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedWriteLock Write(Reg.Lock);

    if (const FScriptStruct* const* Found = Reg.ScriptStructsByName.Find(Name))
    {
        const FStruct* Target = static_cast<const FStruct*>(*Found);
        Reg.ScriptStructsByName.Remove(Name);
        Reg.ScriptStructOwningModule.Remove(Name);
        // Symmetrically purge the FStruct view (an FScriptStruct
        // registration created both slots; unregistering the
        // FScriptStruct must clear both).
        Reg.StructsByName.Remove(Name);
        Reg.StructOwningModule.Remove(Name);
        RemoveFromAll(Reg.AllStructs, Target);
    }
}

void XReflectionRuntime::UnregisterEnum(FName Name) noexcept
{
    if (Name.IsNone())
    {
        return;
    }
    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedWriteLock Write(Reg.Lock);

    if (const FEnum* const* Found = Reg.EnumsByName.Find(Name))
    {
        const FEnum* Target = *Found;
        Reg.EnumsByName.Remove(Name);
        Reg.EnumOwningModule.Remove(Name);
        RemoveFromAll(Reg.AllEnums, Target);
    }
}

void XReflectionRuntime::UnregisterInterface(FName Name) noexcept
{
    if (Name.IsNone())
    {
        return;
    }
    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedWriteLock Write(Reg.Lock);

    if (const FInterface* const* Found = Reg.InterfacesByName.Find(Name))
    {
        const FInterface* Target = *Found;
        Reg.InterfacesByName.Remove(Name);
        Reg.InterfaceOwningModule.Remove(Name);
        RemoveFromAll(Reg.AllInterfaces, Target);
    }
}

// =====================================================================
// Hot-reload module-load / module-unload hooks.
// =====================================================================

void XReflectionRuntime::OnModuleLoad(FName ModuleName) noexcept
{
    if (ModuleName.IsNone())
    {
        // Reject NAME_None modules: the OwningModule tracking encodes
        // "engine-core pre-registration" as NAME_None; permitting a
        // NAME_None OnModuleLoad would conflate the two.
        return;
    }
    // Set the thread-local current-module slot. Subsequent Register*
    // calls on this thread stamp the registration with this FName.
    tls_CurrentModule = ModuleName;
}

void XReflectionRuntime::OnModuleUnload(FName ModuleName) noexcept
{
    if (ModuleName.IsNone())
    {
        return;
    }

    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedWriteLock Write(Reg.Lock);

    // Purge every descriptor whose OwningModule matches the unloading
    // module. Iterate over the OwningModule maps; collect the FNames
    // to purge; then purge in a separate pass (mutating the maps
    // during iteration is unsafe).
    //
    // The four kinds are handled by four inline blocks rather than a
    // templated lambda: TArray<const FX*>::ElementType resolution
    // through `auto&` in a generic lambda is brittle on MSVC's two-
    // phase lookup, and the four blocks are short enough that the
    // unrolling is clearer than the abstraction.

    // ---- Classes ----
    {
        ::XCore::TArray<FName> ToPurge;
        for (const auto& Pair : Reg.ClassOwningModule)
        {
            if (Pair.Value == ModuleName)
            {
                ToPurge.Add(Pair.Key);
            }
        }
        for (::int32 I = 0; I < ToPurge.Num(); ++I)
        {
            const FName N = ToPurge[I];
            if (const FClass* const* Found = Reg.ClassesByName.Find(N))
            {
                const FClass* Target = *Found;
                Reg.ClassesByName.Remove(N);
                Reg.ClassOwningModule.Remove(N);
                RemoveFromAll(Reg.AllClasses, Target);
            }
        }
    }

    // ---- Structs ----
    {
        ::XCore::TArray<FName> ToPurge;
        for (const auto& Pair : Reg.StructOwningModule)
        {
            if (Pair.Value == ModuleName)
            {
                ToPurge.Add(Pair.Key);
            }
        }
        for (::int32 I = 0; I < ToPurge.Num(); ++I)
        {
            const FName N = ToPurge[I];
            if (const FStruct* const* Found = Reg.StructsByName.Find(N))
            {
                const FStruct* Target = *Found;
                Reg.StructsByName.Remove(N);
                Reg.StructOwningModule.Remove(N);
                RemoveFromAll(Reg.AllStructs, Target);
            }
        }
    }

    // ---- Enums ----
    {
        ::XCore::TArray<FName> ToPurge;
        for (const auto& Pair : Reg.EnumOwningModule)
        {
            if (Pair.Value == ModuleName)
            {
                ToPurge.Add(Pair.Key);
            }
        }
        for (::int32 I = 0; I < ToPurge.Num(); ++I)
        {
            const FName N = ToPurge[I];
            if (const FEnum* const* Found = Reg.EnumsByName.Find(N))
            {
                const FEnum* Target = *Found;
                Reg.EnumsByName.Remove(N);
                Reg.EnumOwningModule.Remove(N);
                RemoveFromAll(Reg.AllEnums, Target);
            }
        }
    }

    // ---- Interfaces ----
    {
        ::XCore::TArray<FName> ToPurge;
        for (const auto& Pair : Reg.InterfaceOwningModule)
        {
            if (Pair.Value == ModuleName)
            {
                ToPurge.Add(Pair.Key);
            }
        }
        for (::int32 I = 0; I < ToPurge.Num(); ++I)
        {
            const FName N = ToPurge[I];
            if (const FInterface* const* Found = Reg.InterfacesByName.Find(N))
            {
                const FInterface* Target = *Found;
                Reg.InterfacesByName.Remove(N);
                Reg.InterfaceOwningModule.Remove(N);
                RemoveFromAll(Reg.AllInterfaces, Target);
            }
        }
    }

    // ScriptStructs: their Owning entries are tracked separately AND in
    // StructOwningModule (because every FScriptStruct registration
    // populates BOTH maps). The Struct purge above already removed the
    // StructsByName + AllStructs entries; we now clean the
    // ScriptStructsByName + ScriptStructOwningModule maps.
    {
        ::XCore::TArray<FName> ToPurgeScript;
        for (const auto& Pair : Reg.ScriptStructOwningModule)
        {
            if (Pair.Value == ModuleName)
            {
                ToPurgeScript.Add(Pair.Key);
            }
        }
        for (::int32 I = 0; I < ToPurgeScript.Num(); ++I)
        {
            const FName N = ToPurgeScript[I];
            Reg.ScriptStructsByName.Remove(N);
            Reg.ScriptStructOwningModule.Remove(N);
        }
    }

    // Clear the thread-local current-module slot if it matches the
    // unloading module (defensive; the OnModuleLoad/OnModuleUnload
    // bracket should always be properly nested on the same thread).
    if (tls_CurrentModule == ModuleName)
    {
        tls_CurrentModule = FName{};
    }
}

FName XReflectionRuntime::GetCurrentModuleName() noexcept
{
    return tls_CurrentModule;
}

// =====================================================================
// OwningModule introspection (read path: shared-lock).
// =====================================================================

FName XReflectionRuntime::GetClassOwningModule(FName ClassName) noexcept
{
    if (ClassName.IsNone())
    {
        return FName{};
    }
    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedReadLock Read(Reg.Lock);
    if (const FName* Found = Reg.ClassOwningModule.Find(ClassName))
    {
        return *Found;
    }
    return FName{};
}

FName XReflectionRuntime::GetStructOwningModule(FName StructName) noexcept
{
    if (StructName.IsNone())
    {
        return FName{};
    }
    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedReadLock Read(Reg.Lock);
    if (const FName* Found = Reg.StructOwningModule.Find(StructName))
    {
        return *Found;
    }
    return FName{};
}

FName XReflectionRuntime::GetEnumOwningModule(FName EnumName) noexcept
{
    if (EnumName.IsNone())
    {
        return FName{};
    }
    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedReadLock Read(Reg.Lock);
    if (const FName* Found = Reg.EnumOwningModule.Find(EnumName))
    {
        return *Found;
    }
    return FName{};
}

FName XReflectionRuntime::GetInterfaceOwningModule(FName InterfaceName) noexcept
{
    if (InterfaceName.IsNone())
    {
        return FName{};
    }
    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedReadLock Read(Reg.Lock);
    if (const FName* Found = Reg.InterfaceOwningModule.Find(InterfaceName))
    {
        return *Found;
    }
    return FName{};
}

// =====================================================================
// Diagnostic counts.
// =====================================================================

::int32 XReflectionRuntime::GetClassCount() noexcept
{
    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedReadLock Read(Reg.Lock);
    return Reg.AllClasses.Num();
}

::int32 XReflectionRuntime::GetStructCount() noexcept
{
    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedReadLock Read(Reg.Lock);
    return Reg.AllStructs.Num();
}

::int32 XReflectionRuntime::GetScriptStructCount() noexcept
{
    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedReadLock Read(Reg.Lock);
    return Reg.ScriptStructsByName.Num();
}

::int32 XReflectionRuntime::GetEnumCount() noexcept
{
    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedReadLock Read(Reg.Lock);
    return Reg.AllEnums.Num();
}

::int32 XReflectionRuntime::GetInterfaceCount() noexcept
{
    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedReadLock Read(Reg.Lock);
    return Reg.AllInterfaces.Num();
}

// =====================================================================
// Iteration snapshots (called by the templated Iterate* bodies).
//
// Returns the count of items in the AllX TArray; if OutCapacity is
// at least that count, fills OutBuffer with the pointers. If
// OutCapacity is too small, returns the required count WITHOUT
// touching OutBuffer; the templated body retries with a heap buffer
// of the indicated size.
//
// Acquires the shared lock for the duration of the copy.
// =====================================================================

::int32 XReflectionRuntime::GetClassSnapshot(const FClass** OutBuffer, ::int32 OutCapacity) noexcept
{
    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedReadLock Read(Reg.Lock);
    const ::int32 Count = Reg.AllClasses.Num();
    if (Count > OutCapacity)
    {
        return Count;
    }
    for (::int32 I = 0; I < Count; ++I)
    {
        OutBuffer[I] = Reg.AllClasses[I];
    }
    return Count;
}

::int32 XReflectionRuntime::GetStructSnapshot(const FStruct** OutBuffer, ::int32 OutCapacity) noexcept
{
    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedReadLock Read(Reg.Lock);
    const ::int32 Count = Reg.AllStructs.Num();
    if (Count > OutCapacity)
    {
        return Count;
    }
    for (::int32 I = 0; I < Count; ++I)
    {
        OutBuffer[I] = Reg.AllStructs[I];
    }
    return Count;
}

::int32 XReflectionRuntime::GetEnumSnapshot(const FEnum** OutBuffer, ::int32 OutCapacity) noexcept
{
    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedReadLock Read(Reg.Lock);
    const ::int32 Count = Reg.AllEnums.Num();
    if (Count > OutCapacity)
    {
        return Count;
    }
    for (::int32 I = 0; I < Count; ++I)
    {
        OutBuffer[I] = Reg.AllEnums[I];
    }
    return Count;
}

::int32 XReflectionRuntime::GetInterfaceSnapshot(const FInterface** OutBuffer, ::int32 OutCapacity) noexcept
{
    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedReadLock Read(Reg.Lock);
    const ::int32 Count = Reg.AllInterfaces.Num();
    if (Count > OutCapacity)
    {
        return Count;
    }
    for (::int32 I = 0; I < Count; ++I)
    {
        OutBuffer[I] = Reg.AllInterfaces[I];
    }
    return Count;
}

// =====================================================================
// EmptyForTesting.
// =====================================================================

void XReflectionRuntime::EmptyForTesting() noexcept
{
    FReflectionRegistry& Reg = GetRegistry();
    ::XCore::HAL::FScopedWriteLock Write(Reg.Lock);

    Reg.ClassesByName.Reset();
    Reg.StructsByName.Reset();
    Reg.ScriptStructsByName.Reset();
    Reg.EnumsByName.Reset();
    Reg.InterfacesByName.Reset();

    Reg.AllClasses.Reset();
    Reg.AllStructs.Reset();
    Reg.AllEnums.Reset();
    Reg.AllInterfaces.Reset();

    Reg.ClassOwningModule.Reset();
    Reg.StructOwningModule.Reset();
    Reg.ScriptStructOwningModule.Reset();
    Reg.EnumOwningModule.Reset();
    Reg.InterfaceOwningModule.Reset();

    // Reset the thread-local slot for THIS thread; other threads'
    // slots are not reachable from here, but tests run their fixtures
    // single-threaded so this is sufficient for the test reset
    // contract.
    tls_CurrentModule = FName{};
}

} // namespace XCore::Reflect

// =====================================================================
// LEGACY-SURFACE shims (global scope).
//
// The Stage-A stub's class-method bodies. Phase 4b.6 implements them
// as deferred-no-ops (the legacy XClassDescriptor records do not carry
// enough information to build a full FClass / FStruct / FEnum /
// FInterface).
//
// The legacy surface is opaque-pointer-typed at the API; the
// implementation does NOT bridge legacy XClass* to namespaced FClass*.
// Production registration flows are expected to switch to the
// namespaced surface in Phase 4b.7+ (XHT regeneration).
//
// The legacy RegisterType overloads accept the calls and route to a
// diagnostic-only path: if the legacy pointer is nullptr (the Stage-A
// Get*FromConstInit pathway), the call is a successful no-op; a
// non-nullptr legacy pointer is treated as a structural error (the
// caller obtained the pointer from outside the supported Stage-A
// path) and is silently ignored.
// =====================================================================

const XClass* XReflectionRuntime::GetXClassFromConstInit(const XClassDescriptor* /*desc*/)
{
    // Stage-A compatibility: nullptr indicates "construct an XClass
    // from the legacy descriptor is not supported in Phase 4b.6".
    // Phase 4b.7+ XHT regeneration produces FClassDescriptor records
    // and bypasses this shim entirely.
    return nullptr;
}

const XStruct* XReflectionRuntime::GetXStructFromConstInit(const XStructDescriptor* /*desc*/)
{
    return nullptr;
}

const XEnum* XReflectionRuntime::GetXEnumFromConstInit(const XEnumDescriptor* /*desc*/)
{
    return nullptr;
}

const XInterface* XReflectionRuntime::GetXInterfaceFromConstInit(const XInterfaceDescriptor* /*desc*/)
{
    return nullptr;
}

const XDelegateFunction* XReflectionRuntime::GetXDelegateFunctionFromConstInit(const XDelegateFunctionDescriptor* /*desc*/)
{
    return nullptr;
}

void XReflectionRuntime::RegisterType(const XClass* /*xclass*/)
{
    // No-op: the legacy XClass* is opaque and carries no FClass
    // identity. Phase 4b.7+ XHT regeneration uses the namespaced
    // RegisterClass API directly.
}

void XReflectionRuntime::RegisterType(const XStruct* /*xstruct*/)
{
}

void XReflectionRuntime::RegisterType(const XEnum* /*xenum*/)
{
}

void XReflectionRuntime::RegisterType(const XInterface* /*xinterface*/)
{
}

void XReflectionRuntime::RegisterType(const XDelegateFunction* /*xdelegate*/)
{
}
