// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XObject.Tests/EObjectFlagsBitwise.cpp -- bitwise operator surface
// for the strongly-typed EObjectFlags enum (XCoreXObject Rev 4 §2.3).
// =====================================================================
//
// Verifies the per-bit position locks + the operator| / operator& /
// operator^ / operator~ / operator|= / operator&= / operator^=
// round-trip + the HasAnyObjectFlags / HasAllObjectFlags predicate
// helpers. The bit positions are part of the Stage B addendum ABI
// lock (XPACT_XOBJECT_LAYOUT_TAG); any drift breaks XHT-emitted
// .gen.cpp's Capabilities bitmask probe.
//
// =====================================================================

#include "XObject/EObjectFlags.h"

#include <cstdint>
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
}

int main()
{
    using ::XCore::EObjectFlags;
    using ::XCore::HasAllObjectFlags;
    using ::XCore::HasAnyObjectFlags;
    using ::XCore::ToUnderlying;

    // -----------------------------------------------------------------
    // Bit-position locks (Rev 2 expanded + Rev 3 plugin range per
    // FIX-M-R2-2). Spec §2.3.
    // -----------------------------------------------------------------

    Check(ToUnderlying(EObjectFlags::None)               == 0u,
          "EObjectFlags::None != 0");
    Check(ToUnderlying(EObjectFlags::NeedInitialization) == (1u <<  0),
          "EObjectFlags::NeedInitialization not at bit 0");
    Check(ToUnderlying(EObjectFlags::ClassDefaultObject) == (1u <<  1),
          "EObjectFlags::ClassDefaultObject not at bit 1");
    Check(ToUnderlying(EObjectFlags::ArchetypeObject)    == (1u <<  2),
          "EObjectFlags::ArchetypeObject not at bit 2");
    Check(ToUnderlying(EObjectFlags::Transient)          == (1u <<  3),
          "EObjectFlags::Transient not at bit 3");
    Check(ToUnderlying(EObjectFlags::NeedLoad)           == (1u <<  4),
          "EObjectFlags::NeedLoad not at bit 4");
    Check(ToUnderlying(EObjectFlags::NeedPostLoad)       == (1u <<  5),
          "EObjectFlags::NeedPostLoad not at bit 5");
    Check(ToUnderlying(EObjectFlags::Standalone)         == (1u <<  6),
          "EObjectFlags::Standalone not at bit 6");
    Check(ToUnderlying(EObjectFlags::BeginDestroyed)     == (1u <<  7),
          "EObjectFlags::BeginDestroyed not at bit 7");
    Check(ToUnderlying(EObjectFlags::FinishDestroyed)    == (1u <<  8),
          "EObjectFlags::FinishDestroyed not at bit 8");
    Check(ToUnderlying(EObjectFlags::MarkAsRootSet)      == (1u <<  9),
          "EObjectFlags::MarkAsRootSet not at bit 9");
    Check(ToUnderlying(EObjectFlags::MarkAsNative)       == (1u << 10),
          "EObjectFlags::MarkAsNative not at bit 10");
    Check(ToUnderlying(EObjectFlags::DefaultSubObject)   == (1u << 11),
          "EObjectFlags::DefaultSubObject not at bit 11");
    Check(ToUnderlying(EObjectFlags::MarkedAsGarbage)    == (1u << 12),
          "EObjectFlags::MarkedAsGarbage not at bit 12");
    Check(ToUnderlying(EObjectFlags::BeingReplaced)      == (1u << 13),
          "EObjectFlags::BeingReplaced not at bit 13");
    Check(ToUnderlying(EObjectFlags::HotReloadReplaced)  == (1u << 14),
          "EObjectFlags::HotReloadReplaced not at bit 14");
    Check(ToUnderlying(EObjectFlags::Public)             == (1u << 15),
          "EObjectFlags::Public not at bit 15");
    Check(ToUnderlying(EObjectFlags::HasExternalPackage) == (1u << 16),
          "EObjectFlags::HasExternalPackage not at bit 16");
    Check(ToUnderlying(EObjectFlags::LoadCompleted)      == (1u << 17),
          "EObjectFlags::LoadCompleted not at bit 17");
    Check(ToUnderlying(EObjectFlags::NonPIETransient)    == (1u << 18),
          "EObjectFlags::NonPIETransient not at bit 18");
    Check(ToUnderlying(EObjectFlags::DebugLogged)        == (1u << 19),
          "EObjectFlags::DebugLogged not at bit 19");

    // Reserved slots are placeholders for future engine extensions;
    // their positions are part of the ABI lock so they stay reserved.
    Check(ToUnderlying(EObjectFlags::_Reserved20) == (1u << 20),
          "_Reserved20 not at bit 20");
    Check(ToUnderlying(EObjectFlags::_Reserved21) == (1u << 21),
          "_Reserved21 not at bit 21");
    Check(ToUnderlying(EObjectFlags::_Reserved22) == (1u << 22),
          "_Reserved22 not at bit 22");
    Check(ToUnderlying(EObjectFlags::_Reserved23) == (1u << 23),
          "_Reserved23 not at bit 23");

    // Plugin range (Rev 3 per FIX-M-R2-2).
    Check(ToUnderlying(EObjectFlags::UserFlag_Begin) == (1u << 24),
          "UserFlag_Begin not at bit 24");
    Check(ToUnderlying(EObjectFlags::UserFlag_1)     == (1u << 24),
          "UserFlag_1 not at bit 24");
    Check(ToUnderlying(EObjectFlags::UserFlag_2)     == (1u << 25),
          "UserFlag_2 not at bit 25");
    Check(ToUnderlying(EObjectFlags::UserFlag_3)     == (1u << 26),
          "UserFlag_3 not at bit 26");
    Check(ToUnderlying(EObjectFlags::UserFlag_4)     == (1u << 27),
          "UserFlag_4 not at bit 27");
    Check(ToUnderlying(EObjectFlags::UserFlag_5)     == (1u << 28),
          "UserFlag_5 not at bit 28");
    Check(ToUnderlying(EObjectFlags::UserFlag_6)     == (1u << 29),
          "UserFlag_6 not at bit 29");
    Check(ToUnderlying(EObjectFlags::UserFlag_7)     == (1u << 30),
          "UserFlag_7 not at bit 30");
    Check(ToUnderlying(EObjectFlags::UserFlag_8)     == (1u << 31),
          "UserFlag_8 not at bit 31");
    Check(ToUnderlying(EObjectFlags::UserFlag_End)   == (1u << 31),
          "UserFlag_End not at bit 31");

    // -----------------------------------------------------------------
    // operator| round-trip.
    // -----------------------------------------------------------------
    {
        const EObjectFlags AB =
            EObjectFlags::Transient | EObjectFlags::Public;
        Check(ToUnderlying(AB) == ((1u << 3) | (1u << 15)),
              "Transient | Public OR not correct");
        Check(HasAllObjectFlags(AB, EObjectFlags::Transient),
              "HasAll(Transient) failed on Transient|Public");
        Check(HasAllObjectFlags(AB, EObjectFlags::Public),
              "HasAll(Public) failed on Transient|Public");
        Check(HasAllObjectFlags(AB,
                  EObjectFlags::Transient | EObjectFlags::Public),
              "HasAll(Transient|Public) failed on Transient|Public");
    }

    // -----------------------------------------------------------------
    // operator& round-trip (mask isolation).
    // -----------------------------------------------------------------
    {
        const EObjectFlags Mixed =
            EObjectFlags::NeedLoad | EObjectFlags::NeedPostLoad |
            EObjectFlags::Standalone;
        Check((Mixed & EObjectFlags::NeedLoad) == EObjectFlags::NeedLoad,
              "AND-mask isolation failed for NeedLoad");
        Check((Mixed & EObjectFlags::Public) == EObjectFlags::None,
              "AND-mask isolation produced unexpected Public bit");
        Check(HasAnyObjectFlags(Mixed, EObjectFlags::Public) == false,
              "HasAny(Public) should be false on Mixed");
        Check(HasAnyObjectFlags(Mixed, EObjectFlags::Standalone) == true,
              "HasAny(Standalone) should be true on Mixed");
    }

    // -----------------------------------------------------------------
    // operator^ (XOR) round-trip.
    // -----------------------------------------------------------------
    {
        const EObjectFlags A = EObjectFlags::Transient | EObjectFlags::Public;
        const EObjectFlags B = EObjectFlags::Public   | EObjectFlags::Standalone;
        const EObjectFlags X = A ^ B;
        // Public cancels out; Transient and Standalone remain.
        Check(ToUnderlying(X) ==
                  ((1u << 3) /* Transient */ | (1u << 6) /* Standalone */),
              "XOR cancellation incorrect");
    }

    // -----------------------------------------------------------------
    // operator~ (bitwise NOT) basic check.
    // -----------------------------------------------------------------
    {
        const EObjectFlags X = ~EObjectFlags::None;
        Check(ToUnderlying(X) == ::std::uint32_t(~0u),
              "~None should be all-ones uint32");
    }

    // -----------------------------------------------------------------
    // Compound assignment operators.
    // -----------------------------------------------------------------
    {
        EObjectFlags Acc = EObjectFlags::None;
        Acc |= EObjectFlags::Transient;
        Acc |= EObjectFlags::Public;
        Check(HasAllObjectFlags(Acc,
                  EObjectFlags::Transient | EObjectFlags::Public),
              "|= compound failed");

        Acc &= EObjectFlags::Transient;
        Check(Acc == EObjectFlags::Transient, "&= compound failed");

        Acc ^= EObjectFlags::Transient;
        Check(Acc == EObjectFlags::None, "^= self-cancel failed");
    }

    // -----------------------------------------------------------------
    // Equality / inequality (comparing enum class values).
    // -----------------------------------------------------------------
    Check(EObjectFlags::None != EObjectFlags::Transient,
          "enum class != failed for distinct values");
    Check(EObjectFlags::Transient == EObjectFlags::Transient,
          "enum class == failed for identical values");

    // -----------------------------------------------------------------
    // HasAnyObjectFlags / HasAllObjectFlags edge cases.
    // -----------------------------------------------------------------
    Check(HasAnyObjectFlags(EObjectFlags::None, EObjectFlags::None) == false,
          "HasAny(None, None) should be false (no bits set)");
    Check(HasAllObjectFlags(EObjectFlags::None, EObjectFlags::None) == true,
          "HasAll(None, None) should be true (empty mask vacuously satisfied)");

    if (g_FailureCount == 0)
    {
        std::cout << "XObject.EObjectFlagsBitwise: PASS\n";
        return 0;
    }
    std::cerr << "XObject.EObjectFlagsBitwise: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
