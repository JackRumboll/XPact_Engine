// Copyright Simgenics. All Rights Reserved.

// XBT.Core.Build.cs
//
// XBT bootstraps from MSBuild. Once XBT exists it can build other modules
// from .Build.toml or .Build.cs (per Toolchain Contract Section 9.6); but
// XBT itself is built by the .NET SDK via the XBT.sln solution and its
// .csproj files. This .Build.cs is therefore INFORMATIONAL ONLY at
// Phase 1 -- it mirrors the descriptor format XBT will emit later for
// self-documentation, but is not consumed during XBT's own build.
//
// When ModuleRules is implemented (Phase 1.2 / 1.3), this file can be
// activated to round-trip XBT through its own descriptor format as a
// dogfooding test. Until then it lives here as a placeholder of the
// shape per /Documents/XToolchainContract.html Section 9 and
// /Documents/XBT.html Section 4.

#if XBT_HAS_MODULERULES   // never defined; placeholder only

using Simgenics.XPact.XBT.Configuration;

public sealed class XBT_Core : ModuleRules
{
    public XBT_Core(ReadOnlyTargetRules target) : base(target)
    {
        Tier        = ModuleTier.Engine;
        ModuleType  = ModuleType.Programs;
        Languages   = Languages.CSharp;

        PublicDependencyModuleNames.Clear();
        // Blake3 NuGet is a leaf dependency; not represented in the
        // XPact module graph because XPact's module graph models XPact
        // modules only.
    }
}

#endif
