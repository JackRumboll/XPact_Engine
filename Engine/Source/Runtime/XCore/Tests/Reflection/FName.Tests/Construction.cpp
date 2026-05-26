// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FName.Tests/Construction.cpp -- ctor surface + NAME_None sentinel.
// =====================================================================
//
// XCore-4b Rev 3 §4.1. Covers:
//   * Default ctor produces NAME_None (Index = 0, SerialNumber = 0).
//   * FName(const char*) constructs from a NUL-terminated UTF-8 C-string.
//   * FName(const char*, int32) constructs from a byte range.
//   * FName(FString) constructs from an FString.
//   * NAME_None constexpr global is constructible and equals default-ctor.
//   * Empty input ("" or null) produces NAME_None.
//
// Acceptance gates: A1 (NAME_None), constructor coverage, large-name
// boundary (1024 bytes).
// =====================================================================

#include "Reflection/FName.h"
#include "HAL/FMemory.h"
#include "Containers/FString.h"

#include <cstdio>
#include <cstring>

int main()
{
    ::XCore::HAL::FMemory::__Init();

    using ::XCore::Reflect::FName;
    using ::XCore::Reflect::NAME_None;
    using ::XCore::Reflect::kNoneIndex;

    // -----------------------------------------------------------------
    // Default ctor: NAME_None sentinel.
    // -----------------------------------------------------------------
    {
        FName N;
        if (N.GetIndex() != kNoneIndex)
        {
            std::fprintf(stderr, "FAIL: default ctor Index != kNoneIndex (got %u)\n", N.GetIndex());
            return 1;
        }
        if (N.GetSerialNumber() != 0)
        {
            std::fprintf(stderr, "FAIL: default ctor SerialNumber != 0 (got %u)\n", N.GetSerialNumber());
            return 1;
        }
        if (!N.IsNone())
        {
            std::fprintf(stderr, "FAIL: default-ctor FName.IsNone() returned false\n");
            return 1;
        }
        if (N.IsNumbered())
        {
            std::fprintf(stderr, "FAIL: default-ctor FName.IsNumbered() returned true\n");
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // NAME_None constexpr equals default-constructed FName.
    // -----------------------------------------------------------------
    {
        if (!(NAME_None == FName()))
        {
            std::fprintf(stderr, "FAIL: NAME_None != FName()\n");
            return 1;
        }
        if (NAME_None.GetIndex() != 0 || NAME_None.GetSerialNumber() != 0)
        {
            std::fprintf(stderr, "FAIL: NAME_None has unexpected fields (Index=%u, Serial=%u)\n",
                         NAME_None.GetIndex(), NAME_None.GetSerialNumber());
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // FName(const char*) construction.
    // -----------------------------------------------------------------
    {
        FName N("Actor");
        if (N.IsNone())
        {
            std::fprintf(stderr, "FAIL: FName(\"Actor\") IsNone()\n");
            return 1;
        }
        if (N.GetIndex() == 0)
        {
            std::fprintf(stderr, "FAIL: FName(\"Actor\").Index == 0 (should be non-zero)\n");
            return 1;
        }
        if (N.GetSerialNumber() != 0)
        {
            std::fprintf(stderr, "FAIL: FName(\"Actor\").SerialNumber != 0 (got %u)\n", N.GetSerialNumber());
            return 1;
        }
        if (N.GetBaseLength() != 5)
        {
            std::fprintf(stderr, "FAIL: FName(\"Actor\").GetBaseLength() = %d (expected 5)\n", N.GetBaseLength());
            return 1;
        }
        if (std::memcmp(N.GetBaseBytes(), "Actor", 5) != 0)
        {
            std::fprintf(stderr, "FAIL: FName(\"Actor\").GetBaseBytes() byte mismatch\n");
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // FName(const char*, int32) construction.
    // -----------------------------------------------------------------
    {
        const char* Buf = "abcdefgh";
        FName N(Buf, 3);  // intern only "abc"
        if (N.GetBaseLength() != 3)
        {
            std::fprintf(stderr, "FAIL: FName(buf,3).GetBaseLength() = %d (expected 3)\n", N.GetBaseLength());
            return 1;
        }
        if (std::memcmp(N.GetBaseBytes(), "abc", 3) != 0)
        {
            std::fprintf(stderr, "FAIL: FName(buf,3) byte mismatch\n");
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // FName(FString) construction.
    // -----------------------------------------------------------------
    {
        ::XCore::FString S("Hello");
        FName N(S);
        if (N.GetBaseLength() != 5)
        {
            std::fprintf(stderr, "FAIL: FName(FString(\"Hello\")) length = %d (expected 5)\n", N.GetBaseLength());
            return 1;
        }
        if (std::memcmp(N.GetBaseBytes(), "Hello", 5) != 0)
        {
            std::fprintf(stderr, "FAIL: FName(FString) byte mismatch\n");
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // Empty input produces NAME_None.
    // -----------------------------------------------------------------
    {
        FName N("");
        if (!N.IsNone())
        {
            std::fprintf(stderr, "FAIL: FName(\"\") not NAME_None\n");
            return 1;
        }

        FName N2(static_cast<const char*>(nullptr));
        if (!N2.IsNone())
        {
            std::fprintf(stderr, "FAIL: FName(nullptr) not NAME_None\n");
            return 1;
        }

        FName N3("abc", 0);
        if (!N3.IsNone())
        {
            std::fprintf(stderr, "FAIL: FName(buf, 0) not NAME_None\n");
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // Large-name boundary (1024 bytes).
    // -----------------------------------------------------------------
    {
        char Buf[1024];
        for (int I = 0; I < 1024; ++I)
        {
            Buf[I] = static_cast<char>('a' + (I % 26));
        }
        FName N(Buf, 1024);
        if (N.IsNone())
        {
            std::fprintf(stderr, "FAIL: FName(1024-byte input) returned NAME_None\n");
            return 1;
        }
        if (N.GetBaseLength() != 1024)
        {
            std::fprintf(stderr, "FAIL: FName(1024).GetBaseLength() = %d (expected 1024)\n", N.GetBaseLength());
            return 1;
        }
        if (std::memcmp(N.GetBaseBytes(), Buf, 1024) != 0)
        {
            std::fprintf(stderr, "FAIL: FName(1024-byte) byte mismatch\n");
            return 1;
        }
    }

    return 0;
}
