// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FProperty.Tests/FBoolPropertyBitfield.cpp -- bitfield-bool path
// correctness (XCore-4b §5.5 row `FBoolProperty`).
// =====================================================================
//
// Verifies that an FBoolProperty configured for a C-bitfield (e.g.,
// `uint32 bFlag : 1;` at bit 5 of byte 2 of a uint32 field) correctly
// reads and writes the single bit without disturbing neighbouring bits.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/FBoolProperty.h"
#include "Reflection/FName.h"

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
    ::XCore::HAL::FMemory::__Init();

    using ::XCore::Reflect::FBoolProperty;
    using ::XCore::Reflect::FFieldVariant;
    using ::XCore::Reflect::FName;

    // -----------------------------------------------------------------
    // Construct a bitfield FBoolProperty:
    //   FieldSize  = 4 (containing uint32 field)
    //   ByteOffset = 2 (third byte of the field)
    //   ByteMask   = 0x20 (bit 5 of that byte)
    //   FieldMask  = 0x20
    // -----------------------------------------------------------------
    FBoolProperty Prop(FFieldVariant{}, FName("bBitfield"),
                       /*FieldSize*/ 4, /*ByteOffset*/ 2,
                       /*ByteMask*/  0x20, /*FieldMask*/ 0x20);
    Check(Prop.IsBitfield(), "Bitfield FBoolProperty IsBitfield() returned false");
    Check(Prop.GetFieldSize() == 4, "Bitfield FieldSize != 4");
    Check(Prop.GetByteOffset() == 2, "Bitfield ByteOffset != 2");
    Check(Prop.GetByteMask()  == 0x20, "Bitfield ByteMask != 0x20");

    // -----------------------------------------------------------------
    // Build a mock 4-byte field with all bits initially set EXCEPT the
    // target bit. SetBoolValue(true) must set the bit. SetBoolValue(false)
    // must clear it. The OTHER bits in the field MUST be preserved.
    // -----------------------------------------------------------------

    // Mock owner: just a uint32 field at offset 0 of the struct.
    // The FBoolProperty's Offset is 0 (the field is at the start of
    // the owner struct).
    struct MockOwner
    {
        ::uint32 BitfieldStorage;
    };
    Prop.Offset      = static_cast<::int32>(offsetof(MockOwner, BitfieldStorage));
    Prop.ElementSize = 4;

    MockOwner Instance{};
    Instance.BitfieldStorage = 0xFFDFFFFFu;  // all bits set EXCEPT byte 2 bit 5

    // GetBoolValue: bit is clear (BitfieldStorage byte 2 = 0xDF, mask
    // 0x20 not set).
    Check(!Prop.GetBoolValue(&Instance),
          "GetBoolValue: bit 5 of byte 2 should be clear; got true");

    // SetBoolValue(true): bit should set.
    Prop.SetBoolValue(&Instance, true);
    Check(Instance.BitfieldStorage == 0xFFFFFFFFu,
          "SetBoolValue(true) did not set bit 5 of byte 2 "
          "(or disturbed other bits)");
    Check(Prop.GetBoolValue(&Instance),
          "GetBoolValue after SetBoolValue(true) returned false");

    // SetBoolValue(false): bit should clear; others preserved.
    Instance.BitfieldStorage = 0xFFFFFFFFu;
    Prop.SetBoolValue(&Instance, false);
    Check(Instance.BitfieldStorage == 0xFFDFFFFFu,
          "SetBoolValue(false) did not clear bit 5 of byte 2 "
          "(or disturbed other bits)");
    Check(!Prop.GetBoolValue(&Instance),
          "GetBoolValue after SetBoolValue(false) returned true");

    // -----------------------------------------------------------------
    // Toggle bits in a single byte (FieldSize=1, ByteOffset=0,
    // ByteMask=0x04 -- bit 2 of byte 0).
    // -----------------------------------------------------------------
    FBoolProperty Prop2(FFieldVariant{}, FName("bByteBitfield"),
                        /*FieldSize*/ 1, /*ByteOffset*/ 0,
                        /*ByteMask*/  0x04, /*FieldMask*/ 0x04);
    Check(Prop2.IsBitfield(), "Prop2 IsBitfield() returned false");

    struct ByteOwner
    {
        ::uint8 Byte;
    };
    Prop2.Offset      = 0;
    Prop2.ElementSize = 1;

    ByteOwner BO{};
    BO.Byte = 0xFBu;  // all bits set EXCEPT bit 2

    Check(!Prop2.GetBoolValue(&BO), "Byte-bitfield bit 2 should be clear");
    Prop2.SetBoolValue(&BO, true);
    Check(BO.Byte == 0xFFu, "Byte-bitfield SetBoolValue(true) failed");
    Prop2.SetBoolValue(&BO, false);
    Check(BO.Byte == 0xFBu, "Byte-bitfield SetBoolValue(false) failed");

    if (g_FailureCount > 0)
    {
        std::cerr << "FProperty.FBoolPropertyBitfield: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FProperty.FBoolPropertyBitfield: PASS\n";
    return 0;
}
