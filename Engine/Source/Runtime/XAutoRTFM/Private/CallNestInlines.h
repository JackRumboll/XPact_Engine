// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.

#pragma once

#if (defined(__AUTORTFM) && __AUTORTFM)

#include "CallNest.h"

namespace AutoRTFM
{

template <typename TTryFunctor>
ETransactionStatus FCallNest::Try(const TTryFunctor& TryFunctor)
{
	AbortJump.TryCatch(
		[&]()
		{
			TryFunctor();
			AUTORTFM_ASSERT(Status == ETransactionStatus::Executing);
		},
		[&]() { AUTORTFM_ASSERT(Status != ETransactionStatus::Executing); });
	return Status;
}

}  // namespace AutoRTFM

#endif  // (defined(__AUTORTFM) && __AUTORTFM)
