// Copyright Simgenics. All Rights Reserved.

// XBT.Toolchain.Build.cs
//
// Informational placeholder. See XBT.Core.Build.cs for the rationale --
// XBT itself builds via the .NET SDK at Phase 1; this descriptor is the
// shape XBT will round-trip through ModuleRules once that lands.

#if XBT_HAS_MODULERULES   // never defined

using Simgenics.XPact.XBT.Configuration;

public sealed class XBT_Toolchain : ModuleRules
{
    public XBT_Toolchain(ReadOnlyTargetRules target) : base(target)
    {
        Tier        = ModuleTier.Engine;
        ModuleType  = ModuleType.Programs;
        Languages   = Languages.CSharp;

        PublicDependencyModuleNames.Add("XBT.Core");
        PublicDependencyModuleNames.Add("XBT.Manifest");
        PublicDependencyModuleNames.Add("XBT.ActionGraph");
        PublicDependencyModuleNames.Add("XBT.Configuration");
    }
}

#endif
