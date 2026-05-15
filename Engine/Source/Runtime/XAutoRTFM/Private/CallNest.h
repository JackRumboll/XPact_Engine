// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.

#pragma once

#if (defined(__AUTORTFM) && __AUTORTFM)

#include "AutoRTFM.h"
#include "LongJump.h"

namespace AutoRTFM
{

class FContext;

class AUTORTFM_DISABLE FCallNest final
{
public:
	FCallNest(FCallNest* Parent, TransactionID Transaction) : Parent(Parent), Transaction(Transaction) {}

	FCallNest* const Parent;
	TransactionID const Transaction;
	FLongJump AbortJump;
	ETransactionStatus Status = ETransactionStatus::Executing;

	// Whether this succeeded or not is reflected in Context::GetStatus().
	template <typename TTryFunctor>
	ETransactionStatus Try(const TTryFunctor& TryFunctor);
};

}  // namespace AutoRTFM

#endif  // (defined(__AUTORTFM) && __AUTORTFM)
