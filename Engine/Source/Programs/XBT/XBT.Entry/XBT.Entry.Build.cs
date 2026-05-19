// Copyright Simgenics. All Rights Reserved.

// XBT.Entry.Build.cs
//
// Informational placeholder mirroring the descriptor format XBT will
// emit later. XBT itself bootstraps from MSBuild; this file is NOT
// consumed during the Phase 1 build. See XBT.Core's identical
// placeholder for the rationale.

#if XBT_HAS_MODULERULES   // never defined

using Simgenics.XPact.XBT.Configuration;

public sealed class XBT_Entry : ModuleRules
{
    public XBT_Entry(ReadOnlyTargetRules target) : base(target)
    {
        Tier        = ModuleTier.Engine;
        ModuleType  = ModuleType.Programs;
        Languages   = Languages.CSharp;

        PublicDependencyModuleNames.Add("XBT.Core");
        PublicDependencyModuleNames.Add("XBT.Manifest");
    }
}

#endif
