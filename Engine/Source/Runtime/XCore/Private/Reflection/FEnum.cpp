// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FEnum.cpp -- XCore-4b §7.4 + §11.3; Phase 4b.5.
// FEnum's FindByName / FindByValue / ConstructFn / FFieldClass anchor.
// =====================================================================

#include "Reflection/FEnum.h"

#include "Reflection/EClassCastFlags.h"
#include "Reflection/FField.h"
#include "Reflection/FFieldClass.h"
#include "Reflection/FName.h"

#include <new>

namespace XCore::Reflect
{

// =====================================================================
// kFEnumStaticClass -- the FFieldClass anchor for FEnum.
//
// constinit; Name lazy-initialised by GetFEnumStaticClass(). CastFlags
// kNone (FEnum is not part of the FProperty hierarchy; the parent gate
// bit kFProperty does NOT apply). SuperClass points at the base FField
// class (kFieldStaticClass; Phase 4b.3).
//
// FakeVTable nullptr: FEnum has no FFakeVTable dispatch surface. The
// per-FProperty dispatch slots don't apply to FEnum; FEnum is a
// reference-only descriptor type. The FFieldClass FakeVTable slot is
// nullable for non-FProperty FField subclasses (per spec §5.2).
// =====================================================================
constinit FFieldClass kFEnumStaticClass{
    /* Name       */ FName(),
    /* Id         */ ::uint64(0),
    /* CastFlags  */ EClassCastFlags::kNone,
    /* SuperClass */ &kFieldStaticClass,
    /* Construct  */ &FEnum::ConstructFn,
    /* FakeVTable */ nullptr,
};

// =====================================================================
// FEnum ctor.
// =====================================================================
FEnum::FEnum(FFieldVariant InOwner, FName InName, EEnumFlags InEnumFlags) noexcept
    : FField(&kFEnumStaticClass, InOwner, InName)
    , EnumFlags(InEnumFlags)
    , _pad(0)
    , Values()
    , CppForm()
{
}

// =====================================================================
// FEnum::FindByName -- linear scan of Values for an FName match.
// =====================================================================
bool FEnum::FindByName(FName EnumeratorName, ::int64& OutValue) const noexcept
{
    const ::int32 Count = Values.Num();
    for (::int32 Idx = 0; Idx < Count; ++Idx)
    {
        const FEnumValue& Entry = Values[Idx];
        if (Entry.Name == EnumeratorName)
        {
            OutValue = Entry.Value;
            return true;
        }
    }
    return false;
}

// =====================================================================
// FEnum::FindByValue -- linear scan of Values for a numeric match.
// =====================================================================
bool FEnum::FindByValue(::int64 EnumeratorValue, FName& OutName) const noexcept
{
    const ::int32 Count = Values.Num();
    for (::int32 Idx = 0; Idx < Count; ++Idx)
    {
        const FEnumValue& Entry = Values[Idx];
        if (Entry.Value == EnumeratorValue)
        {
            OutName = Entry.Name;
            return true;
        }
    }
    return false;
}

// =====================================================================
// FEnum::ConstructFn -- placement-new target.
//
// Note: FEnum is NON-COPYABLE / NON-MOVABLE (owns the Values TArray).
// The ConstructFn placement-new constructs in-place into OutStorage;
// callers must NOT then memcpy the constructed FEnum elsewhere (the
// Values TArray's heap buffer would be aliased).
// =====================================================================
void FEnum::ConstructFn(FFieldVariant Owner, FName Name, void* OutStorage) noexcept
{
    new (OutStorage) FEnum(Owner, Name);
}

// =====================================================================
// FEnum::StaticClass -- the Cast<FEnum> hook.
// =====================================================================
const FFieldClass* FEnum::StaticClass() noexcept
{
    return &GetFEnumStaticClass();
}

// =====================================================================
// GetFEnumStaticClass -- lazy-init the Name slot.
// =====================================================================
const FFieldClass& GetFEnumStaticClass() noexcept
{
    static const auto Init = []() noexcept {
        kFEnumStaticClass.Name = FName("FEnum");
        return true;
    }();
    (void)Init;
    return kFEnumStaticClass;
}

} // namespace XCore::Reflect
