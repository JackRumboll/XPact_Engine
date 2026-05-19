// Copyright Simgenics. All Rights Reserved.

// XBT.Discovery.Build.cs
//
// Informational placeholder mirroring the descriptor format XBT will emit
// later. XBT itself is built by the .NET SDK at Phase 1 -- this file is
// NOT consumed during the build. See XBT.Core's identical placeholder for
// the full rationale.

#if XBT_HAS_MODULERULES   // never defined

using Simgenics.XPact.XBT.Configuration;

public sealed class XBT_Discovery : ModuleRules
{
    public XBT_Discovery(ReadOnlyTargetRules target) : base(target)
    {
        Tier        = ModuleTier.Engine;
        ModuleType  = ModuleType.Programs;
        Languages   = Languages.CSharp;

        PublicDependencyModuleNames.Add("XBT.Core");
        PublicDependencyModuleNames.Add("XBT.Configuration");
        PublicDependencyModuleNames.Add("XBT.Manifest");
    }
}

#endif
