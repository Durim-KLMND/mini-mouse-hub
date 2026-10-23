using MiniMouseHub.Core;

namespace MiniMouseHub.Tests;

public class OnboardProfileTests
{
    // Profile 1 as read from a PRO X Superlight 2 (profile format 7, 255-byte sectors).
    private const string SuperlightProfileHex =
        "030300002003200302B004B0040240064006026009600902800C800C0200000000FF00FFFFFFFFFFFFFFFFFF3C002C018001" +
        "000180010002800100048001000880010010FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF" +
        "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF" +
        "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF" +
        "FFFFFFFFFFFFFFFF0300000000001F400000000300000000001F400000000300000000001F403200000300000000001F4032" +
        "00000384DB";

    private static byte[] SuperlightProfile()
    {
        var bytes = Convert.FromHexString(SuperlightProfileHex);
        Assert.Equal(255, bytes.Length);
        return bytes;
    }

    private static OnboardProfile Decode(byte[] raw) =>
        new(new ProfileHeader(1, 1, true), ProfileLayout.For(7), raw, 5, 0);

    [Fact]
    public void DecodesFormat7DpiSlotsAndButtons()
    {
        var profile = Decode(SuperlightProfile());

        Assert.True(profile.CrcIsValid);
        Assert.Equal(new[] { 800, 1200, 1600, 2400, 3200 }, profile.DpiSlots);
        Assert.Equal(new[] { "mouse button left", "mouse button right", "mouse button middle", "mouse button back", "mouse button forward" },
            profile.Buttons.Select(b => b.ToString()));
    }

    [Fact]
    public void SettingADpiSlotChangesOnlyThatSlotAndTheChecksum()
    {
        var original = SuperlightProfile();
        var profile = Decode((byte[])original.Clone());

        profile.SetDpiSlot(0, 1000);

        Assert.True(profile.CrcIsValid);
        Assert.Equal(new[] { 1000, 1200, 1600, 2400, 3200 }, profile.DpiSlots);
        var changed = Enumerable.Range(0, original.Length).Where(i => original[i] != profile.Raw[i]).ToArray();
        Assert.Equal(new[] { 4, 6, 253, 254 }, changed); // low bytes of X and Y (800 -> 1000 keeps the 0x03 high byte), plus CRC
        Assert.Equal(0x02, profile.Raw[8]); // lift-off distance untouched
    }

    [Fact]
    public void ParsesChainedDpiRanges()
    {
        var caps = DpiCapabilities.Parse([100, 0xE001, 200, 0xE002, 500, 0xE005, 1000, 0xE0C8, 44000]);

        Assert.Equal(100, caps.Min);
        Assert.Equal(44000, caps.Max);
        Assert.True(caps.Accepts(150));
        Assert.True(caps.Accepts(1600)); // 1000 + 3 * 200
        Assert.False(caps.Accepts(1001));
    }
}
