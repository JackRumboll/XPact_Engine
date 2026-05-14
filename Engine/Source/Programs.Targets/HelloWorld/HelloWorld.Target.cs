// XPact Engine - HelloWorld Phase-0 .Target.cs. Smoke test for XBT Task 0.2.

using XBT.Configuration.Rules;

public class HelloWorldTarget : TargetRules
{
	public HelloWorldTarget(TargetInfo target) : base(target)
	{
		Type = TargetType.Program;
		LaunchModuleName = "HelloWorld";

		bUseSharedPCHs = true;
		bUsePCHFiles = true;

		// bUseAutoRTFMCompiler defaults to true per XPact decision #27a.
		// HelloWorld's .cpp doesn't use AutoRTFM macros yet but the flag is honoured.
	}
}
