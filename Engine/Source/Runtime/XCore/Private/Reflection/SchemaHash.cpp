// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// SchemaHash.cpp -- ComputeSchemaHashImpl body.
// =====================================================================
//
// XCore-4b Rev 3, Section 8.4. Thin wrapper around XCore-4a's BLAKE3
// streaming implementation; truncates the 256-bit digest to its lower
// 64 bits.
//
// =====================================================================

#include "Reflection/SchemaHash.h"

#include "Hash/FBlake3.h"
#include "Macros/XCoreTypes.h"

namespace XCore::Reflect
{

::uint64 ComputeSchemaHashImpl(const ::uint8* CanonicalBytes,
                               ::uint64 ByteCount) noexcept
{
    // FBlake3::Compute accepts a SIZE_T length. ByteCount is uint64;
    // SIZE_T is 64-bit on every supported target so the narrowing
    // never loses information. The explicit static_cast is the
    // engineering-principles-correct path (no implicit narrowing
    // warnings).
    const ::XCore::Hash::FBlake3Digest Digest =
        ::XCore::Hash::FBlake3::Compute(CanonicalBytes,
                                        static_cast<::SIZE_T>(ByteCount));

    return TruncateBlake3ToUInt64(Digest);
}

} // namespace XCore::Reflect
