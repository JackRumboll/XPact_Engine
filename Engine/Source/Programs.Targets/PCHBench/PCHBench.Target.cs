// XPact Engine - PCHBench Phase-0 .Target.cs. Used for the PCH speedup
// acceptance test (acceptance item #4 of Task 0.2). The test runs a clean
// build with PCHUsage = NoPCHs and again with PCHUsage = UseSharedPCHs,
// then reports the ratio. Toggle via -bench-no-pch / -bench-shared-pch.

using XBT.Configuration.Rules;

public class PCHBenchTarget : TargetRules
{
	public PCHBenchTarget(TargetInfo target) : base(target)
	{
		Type = TargetType.Program;
		LaunchModuleName = "PCHBench";

		bUsePCHFiles = true;
		bUseSharedPCHs = true;
	}
}
