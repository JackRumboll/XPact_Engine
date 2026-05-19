// Copyright Simgenics. All Rights Reserved.

// XBT.Configuration.Build.cs
//
// Informational placeholder mirroring the descriptor format XBT will emit
// later. XBT itself is built by the .NET SDK at Phase 1 -- this file is
// NOT consumed during the build. See XBT.Core's identical placeholder for
// the full rationale.

#if XBT_HAS_MODULERULES   // never defined

using Simgenics.XPact.XBT.Configuration;

public sealed class XBT_Configuration : ModuleRules
{
    public XBT_Configuration(ReadOnlyTargetRules target) : base(target)
    {
        Tier        = ModuleTier.Engine;
        ModuleType  = ModuleType.Programs;
        Languages   = Languages.CSharp;

        PublicDependencyModuleNames.Add("XBT.Core");
        PublicDependencyModuleNames.Add("XBT.Manifest");
        // Tomlyn NuGet is a leaf dependency; not represented in the XPact
        // module graph because XPact's module graph models XPact modules only.
    }
}

#endif
