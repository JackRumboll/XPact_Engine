// Copyright Simgenics. All Rights Reserved.

// XBT.ActionGraph.Build.cs
//
// Informational placeholder. XBT.ActionGraph (like every XBT.* C# project)
// is built by the .NET SDK via XBT.sln at Phase 1; this descriptor is the
// shape XBT will round-trip once ModuleRules lands. Not consumed during
// the build. See XBT.Core.Build.cs for the full rationale.

#if XBT_HAS_MODULERULES   // never defined; placeholder only

using Simgenics.XPact.XBT.Configuration;

public sealed class XBT_ActionGraph : ModuleRules
{
    public XBT_ActionGraph(ReadOnlyTargetRules target) : base(target)
    {
        Tier        = ModuleTier.Engine;
        ModuleType  = ModuleType.Programs;
        Languages   = Languages.CSharp;

        PublicDependencyModuleNames.Add("XBT.Core");
        PublicDependencyModuleNames.Add("XBT.Manifest");
    }
}

#endif
