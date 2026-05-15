// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// XAutoRTFMTests module rules. The .cpp test driver under Private/ depends on
// XAutoRTFM's public headers; XBT BuildMode resolves the dependency, compiles
// XAutoRTFM separately, archives the result via lib.exe into XAutoRTFM.lib,
// and links that into the test executable.

using XBT.Configuration.Rules;

public class XAutoRTFMTests : ModuleRules
{
	public XAutoRTFMTests(TargetRules target) : base(target)
	{
		Type = ModuleType.CPlusPlus;
		PCHUsage = PCHUsageMode.NoPCHs;

		PublicDependencyModuleNames.Add("XAutoRTFM");
	}
}
