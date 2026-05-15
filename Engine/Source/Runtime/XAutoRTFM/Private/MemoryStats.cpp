// Copyright Epic Games, Inc. All Rights Reserved.
// Modified by Simgenics for XPact Engine.

#if (defined(__AUTORTFM) && __AUTORTFM)

#include "MemoryStats.h"
#include "Utils.h"

namespace AutoRTFM
{

std::atomic<size_t> FMemoryStats::BytesAllocated = 0;

#if AUTORTFM_CHECK_FOR_ALLOCATION_UNDERFLOW
void FMemoryStats::FatalErrorBytesAllocatedUnderflow()
{
	AUTORTFM_FATAL("FMemoryStats::BytesAllocated has underflowed");
}
#endif

}  // namespace AutoRTFM

#endif  // defined(__AUTORTFM) && __AUTORTFM
