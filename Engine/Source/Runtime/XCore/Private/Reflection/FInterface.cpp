// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FInterface.cpp -- XCore-4b §7.5 + §11.3; Phase 4b.5.
// FInterface's lookup bodies + ConstructFn + FFieldClass anchor.
// =====================================================================

#include "Reflection/FInterface.h"

#include "Reflection/EClassCastFlags.h"
#include "Reflection/FField.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FName.h"

#include <new>

namespace XCore::Reflect
{

// =====================================================================
// kFInterfaceStaticClass -- the FFieldClass anchor for FInterface.
//
// Same pattern as FEnum's FFieldClass: kNone CastFlags (not in the
// FProperty hierarchy), SuperClass = kFieldStaticClass, FakeVTable
// nullptr (FInterface has no per-FProperty dispatch surface).
// =====================================================================
constinit FFieldClass kFInterfaceStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kNone,
    /* SuperClass */ &kFieldStaticClass,
    /* Construct  */ &FInterface::ConstructFn,
    /* FakeVTable */ nullptr,
};

// =====================================================================
// FInterface ctor.
// =====================================================================
FInterface::FInterface(FFieldVariant InOwner, FName InName,
                       EInterfaceFlags InFlags) noexcept
    : FField(&kFInterfaceStaticClass, InOwner, InName)
    , InterfaceFunctions()
    , InterfaceFlags(InFlags)
    , _pad(0)
{
}

// =====================================================================
// FInterface::FindFunctionByName -- linear scan by FName.
// =====================================================================
const FFunctionDescriptor*
    FInterface::FindFunctionByName(FName FunctionName) const noexcept
{
    const ::int32 Count = InterfaceFunctions.Num();
    for (::int32 Idx = 0; Idx < Count; ++Idx)
    {
        const FFunctionDescriptor& Entry = InterfaceFunctions[Idx];
        if (Entry.Name == FunctionName)
        {
            return &Entry;
        }
    }
    return nullptr;
}

// =====================================================================
// FInterface::FindFunctionByHash -- linear scan by SignatureHash.
// =====================================================================
const FFunctionDescriptor*
    FInterface::FindFunctionByHash(::uint64 SignatureHash) const noexcept
{
    const ::int32 Count = InterfaceFunctions.Num();
    for (::int32 Idx = 0; Idx < Count; ++Idx)
    {
        const FFunctionDescriptor& Entry = InterfaceFunctions[Idx];
        if (Entry.SignatureHash == SignatureHash)
        {
            return &Entry;
        }
    }
    return nullptr;
}

// =====================================================================
// FInterface::ConstructFn -- placement-new target.
//
// Note: FInterface is NON-COPYABLE / NON-MOVABLE; placement-new
// constructs in-place into OutStorage.
// =====================================================================
void FInterface::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FInterface(Owner, Name);
}

// =====================================================================
// FInterface::StaticClass + GetFInterfaceStaticClass.
// =====================================================================
const FFieldClass* FInterface::StaticClass() noexcept
{
    return &GetFInterfaceStaticClass();
}

const FFieldClass& GetFInterfaceStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFInterfaceStaticClass.Name = FName("FInterface");
        return true;
    }();
    (void)Init;
    return kFInterfaceStaticClass;
}

} // namespace XCore::Reflect
