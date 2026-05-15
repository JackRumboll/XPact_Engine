// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// XAutoRTFM module build rules. Mirrors Engine/Source/Runtime/AutoRTFM/AutoRTFM.Build.cs
// with paths retargeted to the XPact tree.
//
// Path-2 reality (Phase 1 Task 1.0a): under MSVC fallback all Private/*.cpp files
// reduce to empty translation units because of their `#if UE_AUTORTFM` /
// `#if (defined(__AUTORTFM) && __AUTORTFM)` guards. Public headers ship inline
// `!UE_AUTORTFM_ENABLED` fallback implementations so the API surface compiles
// and links unchanged. When a verse-clang-cl.exe binary is vendored into
// Engine/Source/ThirdParty/UnrealInstrumentation/bin, XBT's
// VCEnvironment.TryGetAutoRTFMCompilerPath flips the toolchain swap on,
// AutoRTFMExternalMappingFiles entries below start being emitted via
// `-Xclang -autortfm-mappings`, and full transactional semantics activate.

using XBT.Configuration.Rules;

public class XAutoRTFM : ModuleRules
{
	public XAutoRTFM(TargetRules target) : base(target)
	{
		Type = ModuleType.CPlusPlus;

		// No PCH for this module - matches the original AutoRTFM module's lack of
		// SharedPCHHeaderFile and the fact that the module's headers don't pull
		// any Engine PCH.
		PCHUsage = PCHUsageMode.NoPCHs;

		// Make the public + private trees visible. Public headers live under
		// Public/ (master AutoRTFM.h) and Public/AutoRTFM/ (the .h subset); Private
		// headers and .cpp's live under Private/.
		PublicIncludePaths.Add("Public");
		PrivateIncludePaths.Add("Private");

		// Mirror UE 5.9 AutoRTFM.Build.cs:
		//   PrivateIncludePathModuleNames.Add("Core") so we can include HAL/Platform.h
		//   for DLLEXPORT / DLLIMPORT. XPact has no Core module yet (Phase 1 Task 1.1+),
		//   so we instead define UE_AUTORTFM_DO_NOT_INCLUDE_PLATFORM_H=1 to skip the
		//   HAL/Platform.h include in Public/AutoRTFM/CAPI.h. The resulting
		//   UE_AUTORTFM_API macro is empty (matching a non-DLL static-lib build),
		//   which is appropriate for XAutoRTFM compiled as a static .lib.
		PrivateDefinitions.Add("UE_AUTORTFM_DO_NOT_INCLUDE_PLATFORM_H=1");
		PublicDefinitions.Add("UE_AUTORTFM_DO_NOT_INCLUDE_PLATFORM_H=1");

		// SUPPRESS_PER_MODULE_INLINE_FILE - matches the upstream comment that
		// this module does not use Core's standard operator new/delete overloads.
		PrivateDefinitions.Add("SUPPRESS_PER_MODULE_INLINE_FILE");

		// AUTORTFM_PLATFORM_WINDOWS / AUTORTFM_BUILD_* are derived from PLATFORM_WINDOWS
		// and UE_BUILD_DEVELOPMENT etc. in Private/BuildMacros.h. We have no Unreal
		// platform headers wired up, so set them ourselves. Win64 only for Phase 1;
		// other platforms are deferred.
		PrivateDefinitions.Add("PLATFORM_WINDOWS=1");
		PrivateDefinitions.Add("UE_BUILD_DEVELOPMENT=1");

		// AutoRTFM mapping files - the AutoRTFM compiler consumes these via
		// `-Xclang -autortfm-mappings <abs path>`. Under MSVC fallback these are
		// logged but otherwise ignored (the linker has nothing to link them to).
		// Paths are relative to Engine/Source; absolute would also work.
		AutoRTFMExternalMappingFiles.Add("Runtime/XAutoRTFM/Public/Internal.aem");
		AutoRTFMExternalMappingFiles.Add("Runtime/XAutoRTFM/Public/StdLib.Common.aem");
		AutoRTFMExternalMappingFiles.Add("Runtime/XAutoRTFM/Public/StdLib.Windows.aem");
		// Sanitizer-conditional mappings (ASAN.aem, UBSAN.aem, MSAN.aem,
		// NewDelete.Windows.aem) are deferred to future tasks when sanitizer
		// flags are wired through TargetRules. The upstream AutoRTFM.Build.cs
		// conditioned them on Target.WindowsPlatform.bEnableAddressSanitizer
		// etc.; we do not yet have that surface on the XBT TargetRules.

		// Warnings-as-errors policy - mirrors AutoRTFM.Build.cs verbatim. XPact's
		// XBT ModuleRules does not yet surface CppCompileWarningSettings (those
		// fields land in Phase 1 Task 1.2 with the wider WarningLevel system),
		// so the upstream `CppCompileWarningSettings.*WarningLevel = WarningLevel.Error;`
		// block is intentionally omitted at this layer. The compiler-driven
		// /W3 + /WX-style enforcement happens at the XBTWindows level, which
		// already passes /W3; tightening to /WX is a follow-up.
		// TODO(Phase 1.2): port the 70+ CppCompileWarningSettings entries from
		// AutoRTFM.Build.cs as soon as CppCompileWarningSettings is wired on
		// XBT.ModuleRules. Under MSVC fallback most of these clang-cl-specific
		// warnings don't fire anyway, so behavior parity is preserved.
	}
}
