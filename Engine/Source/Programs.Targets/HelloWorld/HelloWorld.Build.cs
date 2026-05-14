// XPact Engine - HelloWorld Phase-0 .Build.cs. Smoke test for XBT Task 0.2.

using XBT.Configuration.Rules;

public class HelloWorld : ModuleRules
{
	public HelloWorld(TargetRules target) : base(target)
	{
		Type = ModuleType.CPlusPlus;
		PCHUsage = PCHUsageMode.UseSharedPCHs;
		SharedPCHHeaderFile = "XCorePCH.Stub.h";
	}
}
