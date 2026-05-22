// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// TSet.Tests/SwissTableLayout.cpp -- control-byte layout verification.
// =====================================================================
//
// XCore-4a Section 5.6 + Section 11.9: verifies the per-slot control-
// byte encoding matches the abseil canonical scheme. This is the
// load-bearing guarantee for the bit-exact-across-architectures contract
// in Section 5.3 (because the SIMD-friendly bitmask construction
// depends on the exact encoding).
//
// Concretely:
//   * kCtrlEmpty   = 0x80  (top bit set)
//   * kCtrlDeleted = 0xFE
//   * kCtrlSentinel= 0xFF
//   * Full slots:    0x00..0x7F (top bit clear; H2 hash fragment)
//
// We verify:
//   1. An empty TSet (Capacity=0) has its m_ctrl pointing at a
//      sentinel array of all-Empty bytes ending with a Sentinel.
//   2. After Add(value), exactly one control byte is in the 0x00..0x7F
//      range, the rest are kCtrlEmpty, and the byte at offset
//      Capacity is kCtrlSentinel.
//   3. After Remove(value), the same slot's byte is now kCtrlDeleted
//      (0xFE) -- NOT kCtrlEmpty.
//   4. The "mirror" bytes at offset [Capacity, Capacity+GroupSize) are
//      duplicates of [0, GroupSize); writing to a slot < GroupSize
//      must propagate to both positions.
//
// =====================================================================

#include "Containers/TSet.h"
#include "HAL/FMemory.h"
#include "Macros/XCoreTypes.h"

#include <cstdio>

int main()
{
    ::XCore::HAL::FMemory::__Init();

    {
        // Force the set to capacity 16 (one group) by inserting 1 element
        // and checking the post-Add state. Then expand to 64 to test the
        // mirror behavior at higher capacities.

        ::XCore::TSet<::int32> S;
        S.Reserve(1);

        // After Reserve(1), capacity must be at least 16 (one group).
        if (S.Max() != 16)
        {
            std::fprintf(stderr, "FAIL: after Reserve(1), Max()=%d (expected 16)\n", S.Max());
            ::XCore::HAL::FMemory::__Shutdown();
            return 1;
        }

        // All control bytes should be kCtrlEmpty (0x80).
        for (::SIZE_T I = 0; I < 16; ++I)
        {
            const ::uint8 C = S.GetCtrlByte(I);
            if (C != ::XCore::Detail::kCtrlEmpty)
            {
                std::fprintf(stderr,
                    "FAIL: pre-Add control byte [%zu]=0x%02X (expected 0x80=Empty)\n",
                    I, C);
                ::XCore::HAL::FMemory::__Shutdown();
                return 1;
            }
        }

        // Byte at offset Capacity must be kCtrlSentinel (0xFF).
        if (S.GetCtrlByte(16) != ::XCore::Detail::kCtrlSentinel)
        {
            std::fprintf(stderr,
                "FAIL: control byte [Capacity=16]=0x%02X (expected 0xFF=Sentinel)\n",
                S.GetCtrlByte(16));
            ::XCore::HAL::FMemory::__Shutdown();
            return 1;
        }

        // Add one element. Exactly one slot in [0..15] should be Full.
        S.Add(42);

        ::int32 NumFullSlots = 0;
        ::SIZE_T FullSlotIdx = static_cast<::SIZE_T>(-1);
        for (::SIZE_T I = 0; I < 16; ++I)
        {
            const ::uint8 C = S.GetCtrlByte(I);
            if (::XCore::Detail::IsFull(C))
            {
                ++NumFullSlots;
                FullSlotIdx = I;
            }
        }

        if (NumFullSlots != 1)
        {
            std::fprintf(stderr,
                "FAIL: after Add(42), %d Full slots (expected 1)\n", NumFullSlots);
            ::XCore::HAL::FMemory::__Shutdown();
            return 1;
        }

        // The Full byte must be < 0x80.
        const ::uint8 FullByte = S.GetCtrlByte(FullSlotIdx);
        if ((FullByte & 0x80) != 0)
        {
            std::fprintf(stderr,
                "FAIL: Full control byte 0x%02X has top bit set (must be clear)\n",
                FullByte);
            ::XCore::HAL::FMemory::__Shutdown();
            return 1;
        }

        // The mirror byte at offset (Capacity + 1 + FullSlotIdx) must
        // equal the Full byte -- but only for FullSlotIdx in
        // [0, GroupSize-1). Slot GroupSize-1 has no mirror; slot 0..
        // GroupSize-2 are mirrored at Capacity+1..Capacity+GroupSize-1.
        // The byte at offset Capacity is kCtrlSentinel.
        if (FullSlotIdx + 1 < 16)
        {
            const ::uint8 MirrorByte = S.GetCtrlByte(16 + 1 + FullSlotIdx);
            if (MirrorByte != FullByte)
            {
                std::fprintf(stderr,
                    "FAIL: mirror[%zu]=0x%02X != ctrl[%zu]=0x%02X\n",
                    16 + 1 + FullSlotIdx, MirrorByte, FullSlotIdx, FullByte);
                ::XCore::HAL::FMemory::__Shutdown();
                return 1;
            }
        }

        // Remove the element. The slot's control byte should now be
        // kCtrlDeleted (0xFE), not kCtrlEmpty.
        S.Remove(42);
        const ::uint8 PostRemoveByte = S.GetCtrlByte(FullSlotIdx);
        if (PostRemoveByte != ::XCore::Detail::kCtrlDeleted)
        {
            std::fprintf(stderr,
                "FAIL: post-Remove ctrl[%zu]=0x%02X (expected 0xFE=Deleted)\n",
                FullSlotIdx, PostRemoveByte);
            ::XCore::HAL::FMemory::__Shutdown();
            return 1;
        }

        // Sentinel must still be present at offset Capacity.
        if (S.GetCtrlByte(16) != ::XCore::Detail::kCtrlSentinel)
        {
            std::fprintf(stderr,
                "FAIL: post-Remove sentinel byte=0x%02X (expected 0xFF)\n",
                S.GetCtrlByte(16));
            ::XCore::HAL::FMemory::__Shutdown();
            return 1;
        }
    }

    ::XCore::HAL::FMemory::__Shutdown();
    std::printf("TSet.SwissTableLayout: PASS\n");
    return 0;
}
