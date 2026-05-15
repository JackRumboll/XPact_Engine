// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.
//
// XAutoRTFMTests - fallback-semantics acceptance suite for Phase 1 Task 1.0a.
// Mirrors the test-driver shape of XPact.Core.Tests/Program.cs: each test prints
// PASS / FAIL with a description; exit code is 0 on full pass, otherwise the
// number of failing tests.
//
// Path-2 reality:
//   * Under MSVC fallback (current state - verse-clang-cl.exe not vendored)
//     AutoRTFM macros UE_AUTORTFM / UE_AUTORTFM_ENABLED are zero, so all of
//     XAutoRTFM's runtime .cpp files compile to empty TUs and the public-header
//     inline fallbacks define the API. The contract those fallbacks promise:
//       - Transact(lambda)         -> runs lambda; returns ETransactionResult::Committed
//       - AbortTransaction()       -> no-op (the lambda continues running)
//       - CascadingAbortTransaction() -> no-op
//       - IsTransactional()        -> always false
//       - IsClosed()               -> always false (constant-folded only under real compiler)
//       - Open(lambda)             -> runs lambda directly
//       - RecordOpenWrite(...)     -> no-op
//   * Under verse-clang-cl.exe (future) tests 1, 4, 5 keep semantics. Tests 2
//     and 3 are labelled fallback-mode documentation; their pass criteria
//     intentionally encode the MSVC-fallback observable behavior. See
//     Engine/Documentation/AUTORTFM_INTEGRATION.md for the broader picture.

#include "AutoRTFM.h"

#include <cstdio>
#include <cstring>

namespace
{
	int g_failures = 0;

	void Check(const char* name, bool condition, const char* detail = nullptr)
	{
		if (condition)
		{
			std::printf("PASS  %s\n", name);
		}
		else
		{
			++g_failures;
			if (detail != nullptr)
			{
				std::fprintf(stderr, "FAIL  %s: %s\n", name, detail);
			}
			else
			{
				std::fprintf(stderr, "FAIL  %s\n", name);
			}
		}
	}

	// ----- 1. CommitTest_FallbackOrCompiled ---------------------------------------------------------
	// Equivalent under MSVC fallback and real AutoRTFM compiler: Transact returns
	// Committed when the lambda exits normally; the lambda's side effects are
	// visible on return.
	void CommitTest_FallbackOrCompiled()
	{
		int sideEffect = 0;
		AutoRTFM::ETransactionResult result = AutoRTFM::Transact([&]()
		{
			sideEffect = 42;
		});
		Check("CommitTest_FallbackOrCompiled: Transact returns Committed",
			result == AutoRTFM::ETransactionResult::Committed,
			"Expected ETransactionResult::Committed");
		Check("CommitTest_FallbackOrCompiled: Side-effect persists",
			sideEffect == 42,
			"Expected sideEffect == 42");
	}

	// ----- 2. AbortFallback_Documented --------------------------------------------------------------
	// Under MSVC fallback, AbortTransaction() is a no-op; Transact returns Committed;
	// and the side effect set before AbortTransaction persists. Test asserts the
	// fallback-mode behavior explicitly and prints a documenting log line so the
	// fact that this is *not* an end-to-end rollback test is plainly visible.
	void AbortFallback_Documented()
	{
		std::printf("[XAutoRTFMTests] FALLBACK: AbortTransaction is no-op under MSVC; real semantics requires verse-clang-cl.exe per AUTORTFM_INTEGRATION.md\n");

		int sideEffect = 0;
		AutoRTFM::ETransactionResult result = AutoRTFM::Transact([&]()
		{
			sideEffect = 1;
			AutoRTFM::AbortTransaction();
			// Under fallback, AbortTransaction is a no-op; control falls through.
			sideEffect = 2;
		});
		Check("AbortFallback_Documented: Transact returns Committed under MSVC fallback",
			result == AutoRTFM::ETransactionResult::Committed);
		Check("AbortFallback_Documented: post-Abort code ran (fallback no-op)",
			sideEffect == 2,
			"Under MSVC fallback the abort is a no-op; control falls through");
	}

	// ----- 3. IsTransactional_Returns_Documented_Value ----------------------------------------------
	// Outside Transact, IsTransactional() is false (both modes).
	// Inside Transact under MSVC fallback, IsTransactional() is also false because
	// the fallback never enters transactional context. Documents the gap.
	void IsTransactional_Returns_Documented_Value()
	{
		const bool outsideTxn = AutoRTFM::IsTransactional();
		Check("IsTransactional_Returns_Documented_Value: false outside Transact",
			outsideTxn == false);

		bool insideTxn = true;  // initialise to opposite of expected so a no-op lambda would fail
		AutoRTFM::Transact([&]()
		{
			insideTxn = AutoRTFM::IsTransactional();
		});
		// Under MSVC fallback, IsTransactional() returns false because
		// autortfm_is_context_status(autortfm_status_on_track) is implemented
		// inline as "Status == autortfm_status_idle" (see CAPI.h:240-243) when
		// called with autortfm_status_on_track => returns false.
		// Under verse-clang-cl.exe instrumentation, IsTransactional() is
		// constant-folded to true in closed code. Both modes are valid; this
		// test encodes the fallback-mode expectation.
		Check("IsTransactional_Returns_Documented_Value: fallback false inside Transact",
			insideTxn == false,
			"Under MSVC fallback IsTransactional() inside a Transact is false");
	}

	// ----- 4. OpenWrite_Compiles_And_Runs -----------------------------------------------------------
	// Exercise AutoRTFM::Open and AutoRTFM::RecordOpenWrite (the upstream "Write"
	// family). Under MSVC fallback Open just calls the lambda; RecordOpenWrite
	// calls autortfm_record_open_write_with_flags which is a no-op. The test
	// asserts the lambda executed and the program did not crash.
	void OpenWrite_Compiles_And_Runs()
	{
		int openExecuted = 0;
		AutoRTFM::Transact([&]()
		{
			AutoRTFM::Open([&]()
			{
				openExecuted = 1;
			});
		});
		Check("OpenWrite_Compiles_And_Runs: Open() ran its lambda",
			openExecuted == 1);

		// RecordOpenWrite: scalar overload and sized overload. Both are no-ops
		// under fallback but must compile and not crash.
		int target = 0;
		AutoRTFM::RecordOpenWrite(&target);                     // scalar overload (size from type)
		AutoRTFM::RecordOpenWrite(&target, sizeof(int));        // sized overload
		Check("OpenWrite_Compiles_And_Runs: RecordOpenWrite overloads link and run",
			true);
	}

	// ----- 5. API_Surface_Present -------------------------------------------------------------------
	// Verify the major API surface compiles, links, and runs without crashing.
	// Catches link errors that would indicate the runtime port is incomplete.
	void API_Surface_Present()
	{
		// Transact, AbortTransaction, CascadingAbortTransaction
		(void)AutoRTFM::Transact([]() { /* empty */ });
		AutoRTFM::AbortTransaction();
		AutoRTFM::CascadingAbortTransaction();

		// IsTransactional, IsClosed, IsCommittingOrAborting
		(void)AutoRTFM::IsTransactional();
		(void)AutoRTFM::IsClosed();
		(void)AutoRTFM::IsCommittingOrAborting();

		// Open
		int x = 0;
		AutoRTFM::Open([&]() { x = 1; });
		(void)x;

		// RecordOpenWrite scalar + sized
		int y = 0;
		AutoRTFM::RecordOpenWrite(&y);
		AutoRTFM::RecordOpenWrite(&y, sizeof(int));

		// CurrentTransactionID
		(void)AutoRTFM::CurrentTransactionID();

		Check("API_Surface_Present: all listed entry points link and run", true);
	}
}

int main()
{
	std::printf("[XAutoRTFMTests] Phase 1 Task 1.0a - fallback-semantics acceptance suite\n");
	std::printf("[XAutoRTFMTests] Under MSVC fallback (current state): exercising public-header inline fallback paths.\n");
	std::printf("[XAutoRTFMTests] When verse-clang-cl.exe is vendored, see AUTORTFM_INTEGRATION.md for the full transactional semantics this suite will then exercise.\n\n");

	CommitTest_FallbackOrCompiled();
	AbortFallback_Documented();
	IsTransactional_Returns_Documented_Value();
	OpenWrite_Compiles_And_Runs();
	API_Surface_Present();

	if (g_failures == 0)
	{
		std::printf("\nAll XAutoRTFMTests fallback-mode tests passed.\n");
		return 0;
	}
	std::fprintf(stderr, "\n%d test(s) failed.\n", g_failures);
	return g_failures;
}
