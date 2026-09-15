namespace NetworkHidBridge.Core.Input;

public static class KeyboardMapper
{
    public static bool TryMap(uint virtualKey, uint scanCode, bool extended, out MappedKey mapped)
    {
        if (TryMapModifier(virtualKey, scanCode, extended, out byte modifier))
        {
            mapped = new(0, modifier);
            return true;
        }

        byte usage = MapUsage(virtualKey, scanCode, extended);
        mapped = new(usage, 0);
        return usage != 0;
    }

    public static bool TryMapConsumer(uint virtualKey, out ushort usage)
    {
        usage = virtualKey switch
        {
            0xAD => ConsumerUsage.Mute,
            0xAE => ConsumerUsage.VolumeDown,
            0xAF => ConsumerUsage.VolumeUp,
            0xB0 => ConsumerUsage.ScanNextTrack,
            0xB1 => ConsumerUsage.ScanPreviousTrack,
            0xB2 => ConsumerUsage.Stop,
            0xB3 => ConsumerUsage.PlayPause,
            _ => 0,
        };
        return usage != 0;
    }

    private static bool TryMapModifier(uint virtualKey, uint scanCode, bool extended, out byte modifier)
    {
        modifier = virtualKey switch
        {
            0xA2 => 0x01,
            0xA0 => 0x02,
            0xA4 => 0x04,
            0x5B => 0x08,
            0xA3 => 0x10,
            0xA1 => 0x20,
            0xA5 => 0x40,
            0x5C => 0x80,
            0x10 when scanCode == 0x2A => 0x02,
            0x10 when scanCode == 0x36 => 0x20,
            0x11 => extended ? (byte)0x10 : (byte)0x01,
            0x12 => extended ? (byte)0x40 : (byte)0x04,
            _ => 0,
        };
        return modifier != 0;
    }

    private static byte MapUsage(uint virtualKey, uint scanCode, bool extended)
    {
        if (virtualKey is >= 0x7C and <= 0x87)
        {
            return (byte)(0x68 + virtualKey - 0x7C);
        }

        if (virtualKey == 0x15)
        {
            return 0x90;
        }

        if (virtualKey == 0x19)
        {
            return 0x91;
        }

        if (virtualKey == 0x13)
        {
            return 0x48;
        }

        if (virtualKey == 0x2C)
        {
            return 0x46;
        }

        if (extended)
        {
            return scanCode switch
            {
                0x1C => 0x58,
                0x35 => 0x54,
                0x37 => 0x46,
                0x47 => 0x4A,
                0x48 => 0x52,
                0x49 => 0x4B,
                0x4B => 0x50,
                0x4D => 0x4F,
                0x4F => 0x4D,
                0x50 => 0x51,
                0x51 => 0x4E,
                0x52 => 0x49,
                0x53 => 0x4C,
                0x5D => 0x65,
                _ => 0,
            };
        }

        return scanCode switch
        {
            0x01 => 0x29,
            >= 0x02 and <= 0x0B => (byte)(0x1E + scanCode - 0x02),
            0x0C => 0x2D,
            0x0D => 0x2E,
            0x0E => 0x2A,
            0x0F => 0x2B,
            0x10 => 0x14,
            0x11 => 0x1A,
            0x12 => 0x08,
            0x13 => 0x15,
            0x14 => 0x17,
            0x15 => 0x1C,
            0x16 => 0x18,
            0x17 => 0x0C,
            0x18 => 0x12,
            0x19 => 0x13,
            0x1A => 0x2F,
            0x1B => 0x30,
            0x1C => 0x28,
            0x1E => 0x04,
            0x1F => 0x16,
            0x20 => 0x07,
            0x21 => 0x09,
            0x22 => 0x0A,
            0x23 => 0x0B,
            0x24 => 0x0D,
            0x25 => 0x0E,
            0x26 => 0x0F,
            0x27 => 0x33,
            0x28 => 0x34,
            0x29 => 0x35,
            0x2B => 0x31,
            0x2C => 0x1D,
            0x2D => 0x1B,
            0x2E => 0x06,
            0x2F => 0x19,
            0x30 => 0x05,
            0x31 => 0x11,
            0x32 => 0x10,
            0x33 => 0x36,
            0x34 => 0x37,
            0x35 => 0x38,
            0x37 => 0x55,
            0x39 => 0x2C,
            0x3A => 0x39,
            >= 0x3B and <= 0x44 => (byte)(0x3A + scanCode - 0x3B),
            0x45 => 0x53,
            0x46 => 0x47,
            0x47 => 0x5F,
            0x48 => 0x60,
            0x49 => 0x61,
            0x4A => 0x56,
            0x4B => 0x5C,
            0x4C => 0x5D,
            0x4D => 0x5E,
            0x4E => 0x57,
            0x4F => 0x59,
            0x50 => 0x5A,
            0x51 => 0x5B,
            0x52 => 0x62,
            0x53 => 0x63,
            0x56 => 0x64,
            0x57 => 0x44,
            0x58 => 0x45,
            _ => 0,
        };
    }
}
