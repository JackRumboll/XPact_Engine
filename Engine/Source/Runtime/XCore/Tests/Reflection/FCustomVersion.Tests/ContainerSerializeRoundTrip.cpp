// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FCustomVersion.Tests/ContainerSerializeRoundTrip.cpp -- gate D1.
// =====================================================================
//
// XCore-4b Rev 3, Section 13 Acceptance gate D1:
//
//   "D1. FCustomVersionContainer round-trip via FArchive: round-trip
//    100 random custom versions; output bytes are byte-exact equal to
//    input bytes."
//
// Phase 4b.2 ships the byte-buffer round-trip (the FArchive overload
// lands at XSerialization Layer 9). This test:
//
//   1. Synthesises a container with 100 deterministically-randomised
//      FCustomVersion entries.
//   2. Serialises the container into a uint8 buffer.
//   3. Deserialises the buffer into a second container.
//   4. Serialises the second container into another uint8 buffer.
//   5. Asserts the two buffers are byte-exact equal.
//
// The deterministic-random generator is a fixed-seed linear-congruential
// PRNG (NOT std::mt19937 / std::random_device which would introduce
// platform-specific seed sources). Same input -> same output across
// the determinism matrix.
//
// =====================================================================

#include "Reflection/FCustomVersionContainer.h"
#include "Reflection/FCustomVersion.h"
#include "Reflection/FGuid.h"

#include "Containers/TArray.h"

#include <iostream>

namespace
{
    int g_FailureCount = 0;

    void Check(bool Condition, const char* Diagnostic)
    {
        if (!Condition)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++g_FailureCount;
        }
    }

    using ::XCore::Reflect::FCustomVersion;
    using ::XCore::Reflect::FCustomVersionContainer;
    using ::XCore::Reflect::FGuid;
    using ::XCore::Reflect::FName;

    // Fixed-seed linear-congruential PRNG. Same seed = same sequence
    // on every target.
    class FDeterministicPRNG
    {
    public:
        explicit FDeterministicPRNG(::uint64 Seed) : m_state(Seed) {}

        ::uint32 NextU32()
        {
            // Numerical-Recipes LCG; period 2^64. Bit-exact across all
            // targets because uint64 arithmetic is well-defined.
            m_state = m_state * 6364136223846793005ULL + 1442695040888963407ULL;
            return static_cast<::uint32>(m_state >> 32);
        }

        ::int32 NextI32()
        {
            return static_cast<::int32>(NextU32());
        }

    private:
        ::uint64 m_state;
    };
}

int main()
{
    constexpr ::int32 kEntryCount = 100;
    constexpr ::uint64 kSeed = 0xCAFEBABEDEADBEEFULL;

    FCustomVersionContainer Original;
    FDeterministicPRNG Rng(kSeed);

    // Build 100 entries with random Keys (and ensure no duplicates by
    // using Insert's idempotent-update path -- duplicates would simply
    // collapse, but for this test we generate distinct keys by
    // construction: the deterministic PRNG produces unique uint32
    // sequences with overwhelming probability over 100 draws).
    for (::int32 I = 0; I < kEntryCount; ++I)
    {
        FGuid Key;
        Key.A = Rng.NextU32();
        Key.B = Rng.NextU32();
        Key.C = Rng.NextU32();
        Key.D = Rng.NextU32();

        // Force Key non-zero (zero GUID is reserved as the sentinel;
        // the random sequence will almost never produce zero, but the
        // guard makes the test robust regardless).
        if (Key.IsZero())
        {
            Key.A = 1;
        }

        const ::int32 Version = static_cast<::int32>(Rng.NextU32() & 0x7FFFFFFFu);
        const FName FriendlyName(Rng.NextU32() & 0x00FFFFFFu, Rng.NextU32() & 0x000000FFu);

        Original.Insert(FCustomVersion(Key, Version, FriendlyName));
    }

    Check(Original.Size() == kEntryCount,
          "container size after 100 inserts != 100 (possible duplicate-key collision in PRNG)");

    // Serialise.
    const ::SIZE_T SerializedSize = Original.GetSerializedByteCount();
    Check(SerializedSize == static_cast<::SIZE_T>(4 + 32 * kEntryCount),
          "GetSerializedByteCount mismatch (expected 4 + 32 * 100 = 3204)");

    ::XCore::TArray<::uint8> Buffer1;
    Buffer1.Reserve(static_cast<::int32>(SerializedSize));
    for (::SIZE_T I = 0; I < SerializedSize; ++I)
    {
        Buffer1.Add(0);
    }
    Original.WriteToBuffer(Buffer1.GetData());

    // Deserialise.
    FCustomVersionContainer Roundtrip;
    const bool bReadOk = Roundtrip.ReadFromBuffer(Buffer1.GetData(), SerializedSize);
    Check(bReadOk, "ReadFromBuffer failed");
    Check(Roundtrip.Size() == kEntryCount, "round-trip size mismatch");

    // Serialise the roundtrip container into a second buffer.
    const ::SIZE_T SerializedSize2 = Roundtrip.GetSerializedByteCount();
    Check(SerializedSize2 == SerializedSize,
          "round-trip serialized byte count != original");

    ::XCore::TArray<::uint8> Buffer2;
    Buffer2.Reserve(static_cast<::int32>(SerializedSize2));
    for (::SIZE_T I = 0; I < SerializedSize2; ++I)
    {
        Buffer2.Add(0);
    }
    Roundtrip.WriteToBuffer(Buffer2.GetData());

    // Byte-exact compare.
    bool bByteExact = true;
    for (::SIZE_T I = 0; I < SerializedSize; ++I)
    {
        if (Buffer1[static_cast<::int32>(I)] != Buffer2[static_cast<::int32>(I)])
        {
            bByteExact = false;
            std::cerr << "FAIL: byte mismatch at offset " << I
                      << ": original=0x"
                      << static_cast<int>(Buffer1[static_cast<::int32>(I)])
                      << " round-trip=0x"
                      << static_cast<int>(Buffer2[static_cast<::int32>(I)])
                      << "\n";
            break;
        }
    }
    Check(bByteExact, "round-trip byte-exact comparison failed (gate D1)");

    // Bonus: per-entry equality check. The two containers should have
    // bit-identical entries because the serialisation preserves every
    // field of FCustomVersion.
    Check(Original.Size() == Roundtrip.Size(),
          "Size mismatch between original and roundtrip");
    for (::int32 I = 0; I < Original.Size(); ++I)
    {
        Check(Original.GetAllVersions()[I] == Roundtrip.GetAllVersions()[I],
              "FCustomVersion entry mismatch between original and round-trip");
    }

    // Verify truncated buffer is rejected.
    {
        FCustomVersionContainer Truncated;
        const bool bRejected = !Truncated.ReadFromBuffer(Buffer1.GetData(), 3);
        Check(bRejected, "ReadFromBuffer did not reject 3-byte truncated buffer");
    }

    // Verify zero-length buffer is rejected (header too short).
    {
        FCustomVersionContainer Empty;
        const bool bRejected = !Empty.ReadFromBuffer(nullptr, 0);
        Check(bRejected, "ReadFromBuffer did not reject 0-byte buffer");
    }

    // Verify wrong-size buffer is rejected.
    {
        FCustomVersionContainer Wrong;
        const bool bRejected = !Wrong.ReadFromBuffer(Buffer1.GetData(), SerializedSize - 1);
        Check(bRejected, "ReadFromBuffer did not reject undersized buffer");
    }

    if (g_FailureCount > 0)
    {
        std::cerr << "FCustomVersion.ContainerSerializeRoundTrip: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FCustomVersion.ContainerSerializeRoundTrip: PASS\n";
    return 0;
}
