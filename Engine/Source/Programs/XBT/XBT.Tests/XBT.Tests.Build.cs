// Copyright Simgenics. All Rights Reserved.

// XBT.Tests.Build.cs
//
// Informational placeholder mirroring the descriptor format XBT will emit
// later. XBT itself is built by the .NET SDK at Phase 1 -- this file is
// NOT consumed during the build. See XBT.Core's identical placeholder for
// the full rationale.
//
// Marked bIsTestModule = true so the action graph (when implemented) will
// filter this module out of Game / Server target builds per Toolchain
// Contract Rev 13 Section 9.1.

#if XBT_HAS_MODULERULES   // never defined

using Simgenics.XPact.XBT.Configuration;

public sealed class XBT_Tests : ModuleRules
{
    public XBT_Tests(ReadOnlyTargetRules target) : base(target)
    {
        Tier         = ModuleTier.Engine;
        ModuleType   = ModuleType.Programs;
        Languages    = Languages.CSharp;
        bIsTestModule = true;

        PublicDependencyModuleNames.Add("XBT.Core");
        PublicDependencyModuleNames.Add("XBT.Manifest");
    }
}

#endif
