using NetworkHidBridge.Core.Input;

namespace NetworkHidBridge.Core.Tests.Input;

public sealed class KeyboardMapperTests
{
    [Theory]
    [InlineData(0x41u, 0x1Eu, false, 0x04)] // A
    [InlineData(0x31u, 0x02u, false, 0x1E)] // 1
    [InlineData(0x70u, 0x3Bu, false, 0x3A)] // F1
    [InlineData(0x7Bu, 0x58u, false, 0x45)] // F12
    [InlineData(0x7Cu, 0x00u, false, 0x68)] // F13
    [InlineData(0x87u, 0x00u, false, 0x73)] // F24
    [InlineData(0x2Du, 0x52u, true, 0x49)]  // Insert
    [InlineData(0x27u, 0x4Du, true, 0x4F)]  // Right
    [InlineData(0x6Fu, 0x35u, true, 0x54)]  // Numpad /
    [InlineData(0x6Du, 0x4Au, false, 0x56)] // Numpad -
    [InlineData(0x13u, 0x45u, false, 0x48)] // Pause
    public void MapsWindowsKeysToHidUsage(uint virtualKey, uint scanCode, bool extended, byte usage)
    {
        Assert.True(KeyboardMapper.TryMap(virtualKey, scanCode, extended, out MappedKey mapped));
        Assert.Equal(usage, mapped.Usage);
        Assert.Equal(0, mapped.ModifierMask);
    }

    [Theory]
    [InlineData(0xA2u, 0x01)]
    [InlineData(0xA0u, 0x02)]
    [InlineData(0xA4u, 0x04)]
    [InlineData(0x5Bu, 0x08)]
    [InlineData(0xA3u, 0x10)]
    [InlineData(0xA1u, 0x20)]
    [InlineData(0xA5u, 0x40)]
    [InlineData(0x5Cu, 0x80)]
    public void MapsAllModifiers(uint virtualKey, byte mask)
    {
        Assert.True(KeyboardMapper.TryMap(virtualKey, 0, false, out MappedKey mapped));
        Assert.Equal(0, mapped.Usage);
        Assert.Equal(mask, mapped.ModifierMask);
    }

    [Theory]
    [InlineData(0x10u, 0x2Au, false, 0x02)] // Generic VK_SHIFT, left scan
    [InlineData(0x10u, 0x36u, false, 0x20)] // Generic VK_SHIFT, right scan
    [InlineData(0x11u, 0x1Du, false, 0x01)] // Generic VK_CONTROL, left
    [InlineData(0x11u, 0x1Du, true, 0x10)]  // Generic VK_CONTROL, right
    [InlineData(0x12u, 0x38u, false, 0x04)] // Generic VK_MENU, left
    [InlineData(0x12u, 0x38u, true, 0x40)]  // Generic VK_MENU, right
    public void ResolvesGenericModifierUsingScanCodeAndExtendedFlag(
        uint virtualKey,
        uint scanCode,
        bool extended,
        byte mask)
    {
        Assert.True(KeyboardMapper.TryMap(virtualKey, scanCode, extended, out MappedKey mapped));
        Assert.Equal(mask, mapped.ModifierMask);
    }

    [Theory]
    [InlineData(0x15u, 0x90)] // VK_HANGUL -> LANG1
    [InlineData(0x19u, 0x91)] // VK_HANJA -> LANG2
    public void MapsKoreanLanguageKeys(uint virtualKey, byte usage)
    {
        Assert.True(KeyboardMapper.TryMap(virtualKey, 0, false, out MappedKey mapped));
        Assert.Equal(usage, mapped.Usage);
    }

    [Theory]
    [InlineData(0xADu, 0x00E2)]
    [InlineData(0xAEu, 0x00EA)]
    [InlineData(0xAFu, 0x00E9)]
    [InlineData(0xB0u, 0x00B5)]
    [InlineData(0xB1u, 0x00B6)]
    [InlineData(0xB2u, 0x00B7)]
    [InlineData(0xB3u, 0x00CD)]
    public void MapsConsumerKeys(uint virtualKey, ushort usage)
    {
        Assert.True(KeyboardMapper.TryMapConsumer(virtualKey, out ushort mapped));
        Assert.Equal(usage, mapped);
    }

    [Fact]
    public void RejectsUnknownKey()
    {
        Assert.False(KeyboardMapper.TryMap(0xFF, 0, false, out _));
        Assert.False(KeyboardMapper.TryMapConsumer(0x41, out _));
    }
}
