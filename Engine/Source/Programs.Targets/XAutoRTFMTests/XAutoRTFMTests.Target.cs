// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// XAutoRTFMTests target. Phase 1 Task 1.0a acceptance harness.
// Verifies fallback-mode semantics of the XAutoRTFM runtime under MSVC; when
// verse-clang-cl.exe is vendored, the same suite still exercises the API surface
// and several tests (Test 1, Test 4, Test 5) keep their semantics. Test 2 and
// Test 3 are explicitly labelled as fallback-mode documentation and remain
// passing under both toolchains by asserting on the observable behaviour.

using XBT.Configuration.Rules;

public class XAutoRTFMTestsTarget : TargetRules
{
	public XAutoRTFMTestsTarget(TargetInfo target) : base(target)
	{
		Type = TargetType.Program;
		LaunchModuleName = "XAutoRTFMTests";

		// XAutoRTFMTests doesn't use the shared PCH stub (XCorePCH.Stub.h).
		// Each test TU includes <AutoRTFM.h> directly via XAutoRTFM's public
		// include path; pulling the engine stub PCH on top would only cost
		// compile time for no header reuse.
		bUseSharedPCHs = false;
		bUsePCHFiles = false;

		// bUseAutoRTFMCompiler defaults to true per XPact decision #27a. When the
		// AutoRTFM compiler binary is not vendored, BuildMode logs the advisory
		// line and proceeds with MSVC; under MSVC the tests verify fallback
		// semantics. No additional flag handling needed at the target level.
	}
}
