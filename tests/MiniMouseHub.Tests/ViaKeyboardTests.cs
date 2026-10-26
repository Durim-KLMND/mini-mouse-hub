using MiniMouseHub.Core;
using MiniMouseHub.Core.Via;

namespace MiniMouseHub.Tests;

public class ViaKeyboardTests
{
    /// <summary>Answers like QMK's quantum/via.c: the reply is the request with the data filled in.</summary>
    private sealed class FakeKeyboard
    {
        public int Effect = 1, Brightness = 255, Speed = 127, Hue = 0, Saturation = 255;
        public bool HasRgbMatrix = true;
        public bool HasLedMatrix;
        public int EffectCount = 44;
        public int Saves;
        public byte SavedChannel;
        public int Resets;
        public ushort[,,] Keymap = new ushort[4, 11, 8];
        public readonly List<byte[]> Sent = new();

        private int Layers => Keymap.GetLength(0);
        private int Rows => Keymap.GetLength(1);
        private int Columns => Keymap.GetLength(2);

        private bool HasLighting(byte channel) => (channel == 3 && HasRgbMatrix) || (channel == 5 && HasLedMatrix);

        /// <summary>Like dynamic_keymap_get_keycode: keys outside the matrix are KC_NO.</summary>
        private ushort Key(int layer, int row, int column) =>
            layer < Layers && row < Rows && column < Columns ? Keymap[layer, row, column] : (ushort)0;

        public byte[] Exchange(byte[] request)
        {
            Sent.Add(request);
            var reply = (byte[])request.Clone();
            switch (request[0])
            {
                case 0x01:
                    reply[1] = 0x00;
                    reply[2] = 0x0D;
                    break;
                case 0x04:
                    var code = Key(request[1], request[2], request[3]);
                    reply[4] = (byte)(code >> 8);
                    reply[5] = (byte)code;
                    break;
                case 0x05:
                    Keymap[request[1], request[2], request[3]] = (ushort)((request[4] << 8) | request[5]);
                    break;
                case 0x06:
                    Resets++;
                    break;
                case 0x12:
                    // The keymap as stored: layer by layer, row by row, big-endian, zeros past the end.
                    var offset = (request[1] << 8) | request[2];
                    for (var i = 0; i < request[3]; i++)
                    {
                        var index = (offset + i) / 2;
                        var stored = index < Layers * Rows * Columns ? Keymap[index / (Rows * Columns), index / Columns % Rows, index % Columns] : 0;
                        reply[4 + i] = (offset + i) % 2 == 0 ? (byte)(stored >> 8) : (byte)stored;
                    }
                    break;
                case 0x07 when HasLighting(request[1]):
                    switch (request[2])
                    {
                        case 1: Brightness = request[3]; break;
                        case 2: Effect = request[3] == 0 ? 0 : Math.Min((int)request[3], EffectCount); break;
                        case 3: Speed = request[3]; break;
                        case 4: Hue = request[3]; Saturation = request[4]; break;
                    }
                    break;
                case 0x08 when HasLighting(request[1]):
                    switch (request[2])
                    {
                        case 1: reply[3] = (byte)Brightness; break;
                        case 2: reply[3] = (byte)Effect; break;
                        case 3: reply[3] = (byte)Speed; break;
                        case 4: reply[3] = (byte)Hue; reply[4] = (byte)Saturation; break;
                    }
                    break;
                case 0x09 when HasLighting(request[1]):
                    Saves++;
                    SavedChannel = request[1];
                    break;
                case 0x11:
                    reply[1] = (byte)Layers;
                    break;
                default:
                    reply[0] = 0xFF;
                    break;
            }
            return reply;
        }
    }

    [Fact]
    public void ReadsTheProtocolVersion()
    {
        var fake = new FakeKeyboard();
        Assert.Equal(0x000D, new ViaKeyboard(fake.Exchange).GetProtocolVersion());
    }

    [Fact]
    public void EveryReportIs32Bytes()
    {
        var fake = new FakeKeyboard();
        var keyboard = new ViaKeyboard(fake.Exchange);
        keyboard.GetLighting();
        keyboard.SetLighting(new LightingSettings(5, 100, 50, 10, 200));
        Assert.All(fake.Sent, r => Assert.Equal(32, r.Length));
    }

    [Fact]
    public void ReadsLighting()
    {
        var fake = new FakeKeyboard { Effect = 12, Brightness = 200, Speed = 80, Hue = 170, Saturation = 255 };
        var lighting = new ViaKeyboard(fake.Exchange).GetLighting();
        Assert.Equal(new LightingSettings(12, 200, 80, 170, 255), lighting);
    }

    [Fact]
    public void SetsAndSavesLighting()
    {
        var fake = new FakeKeyboard();
        var keyboard = new ViaKeyboard(fake.Exchange);
        keyboard.SetLighting(new LightingSettings(Effect: 0, Brightness: 64, Speed: 10, Hue: 85, Saturation: 128));

        Assert.Equal((0, 64, 10, 85, 128), (fake.Effect, fake.Brightness, fake.Speed, fake.Hue, fake.Saturation));
        Assert.Equal(1, fake.Saves);
        Assert.Equal([0x09, 3], fake.Sent[^1][..2]);
    }

    [Fact]
    public void KeyboardsWithoutRgbThrow()
    {
        var fake = new FakeKeyboard { HasRgbMatrix = false };
        Assert.Throws<ViaException>(() => new ViaKeyboard(fake.Exchange).GetLighting());
    }

    [Fact]
    public void ReadsAndWritesKeycodes()
    {
        var fake = new FakeKeyboard();
        var keyboard = new ViaKeyboard(fake.Exchange);
        keyboard.SetKeycode(1, 3, 5, 0x7C77);
        Assert.Equal(0x7C77, keyboard.GetKeycode(1, 3, 5));
        Assert.Equal(4, keyboard.GetLayerCount());
    }

    [Fact]
    public void RejectsAReplyToAnotherCommand()
    {
        var keyboard = new ViaKeyboard(request => [0x02, .. new byte[31]]);
        Assert.Throws<ViaException>(() => keyboard.GetProtocolVersion());
    }

    [Fact]
    public void GmmkProHasItsEffectNames()
    {
        var names = ViaEffects.For(0x320F, 0x5044);
        Assert.Equal(44, names.Count);
        Assert.Equal("Solid color", names[0]);
        Assert.Equal("Solid multi-splash", names[^1]);
    }

    [Fact]
    public void LedMatrixKeyboardsHaveNoColor()
    {
        var fake = new FakeKeyboard { HasRgbMatrix = false, HasLedMatrix = true, Effect = 3, Brightness = 90 };
        var keyboard = new ViaKeyboard(fake.Exchange);
        Assert.Throws<ViaException>(() => keyboard.GetLighting());
        Assert.Equal(new LightingSettings(3, 90, 127, 0, 0), keyboard.GetLighting(LightingChannel.LedMatrix));

        keyboard.SetLighting(new LightingSettings(2, 40, 10, 99, 99), LightingChannel.LedMatrix);
        Assert.Equal((2, 40, 10), (fake.Effect, fake.Brightness, fake.Speed));
        Assert.Equal(5, fake.SavedChannel);
        Assert.DoesNotContain(fake.Sent, r => r[0] == 0x07 && r[2] == 4); // no color sent
    }

    [Fact]
    public void CountsEffectsAndPutsTheCurrentOneBack()
    {
        var fake = new FakeKeyboard { EffectCount = 22, Effect = 7 };
        var keyboard = new ViaKeyboard(fake.Exchange);
        Assert.Equal(22, keyboard.CountEffects());
        Assert.Equal(7, fake.Effect);
        Assert.Equal(0, fake.Saves);
    }

    [Fact]
    public void DoesNotCountEffectsWhileTheLightsAreOff()
    {
        var fake = new FakeKeyboard { Effect = 0 };
        Assert.Null(new ViaKeyboard(fake.Exchange).CountEffects());
        Assert.Equal(0, fake.Effect);
    }

    [Fact]
    public void KeychronEffectNamesOnlyWhenTheCountMatches()
    {
        Assert.Same(ViaEffects.Keychron, ViaEffects.For(0x3434, 0x0110, 22));
        Assert.Equal("Effect 9", ViaEffects.For(0x3434, 0x0110, 9)[^1]);
        Assert.Equal(44, ViaEffects.For(0x1234, 0x0001).Count);
    }

    /// <summary>A small Keychron-style keymap: Mac, Mac Fn, Windows, Windows Fn.</summary>
    private static ushort[,,] KeychronKeymap(int rows, int columns)
    {
        var keymap = new ushort[4, rows, columns];
        for (var layer = 0; layer < 4; layer++)
            for (var r = 0; r < rows; r++)
                for (var c = 0; c < columns; c++)
                    keymap[layer, r, c] = layer % 2 == 0 ? (ushort)(0x04 + (r * columns + c) % 26) : QmkKeycodes.Transparent;
        // Some empty spots in the matrix, including the last column of the first row.
        keymap[0, 0, columns - 1] = 0;
        keymap[2, 0, columns - 1] = 0;
        keymap[0, rows - 1, 2] = 0;
        return keymap;
    }

    [Theory]
    [InlineData(6, 16)]
    [InlineData(6, 15)]
    [InlineData(5, 21)]
    [InlineData(2, 4)]
    public void WorksOutTheMatrixSize(int rows, int columns)
    {
        var fake = new FakeKeyboard { Keymap = KeychronKeymap(rows, columns) };
        var keymap = new ViaKeyboard(fake.Exchange).ReadKeymap();
        Assert.Equal((4, rows, columns), (keymap.Layers, keymap.Rows, keymap.Columns));
        for (var layer = 0; layer < 4; layer++)
            for (var r = 0; r < rows; r++)
                for (var c = 0; c < columns; c++)
                    Assert.Equal(fake.Keymap[layer, r, c], keymap[layer, r, c]);
    }

    [Fact]
    public void ReadsASingleLayerKeymap()
    {
        var fake = new FakeKeyboard { Keymap = new ushort[1, 3, 3] };
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
                fake.Keymap[0, r, c] = (ushort)(0x1E + r * 3 + c);
        var keymap = new ViaKeyboard(fake.Exchange).ReadKeymap();
        Assert.Equal((1, 3, 3), (keymap.Layers, keymap.Rows, keymap.Columns));
    }

    [Fact]
    public void FindsLightingKeysOnEveryLayerAndTheWindowsKey()
    {
        var map = KeychronKeymap(6, 16);
        map[0, 0, 15] = map[2, 0, 15] = 0x7821; // the lighting key, on both the Mac and Windows layers
        map[1, 1, 1] = map[3, 1, 1] = 0x7820;   // Fn + the key at row 1, column 1: lighting on/off
        map[0, 1, 1] = map[2, 1, 1] = 0x14;     // which is Q
        map[2, 5, 0] = 0xE3;                    // Windows key on the Windows layer only
        var keymap = new Keymap(4, 6, 16, Flatten(map));
        var state = new KeymapState("3434:0110", keymap, HardwareKeys.DescribeLayers(keymap, macWindowsPairs: true));

        var keys = HardwareKeys.Find(state, new Dictionary<KeyPosition, ushort>());

        Assert.Collection(keys,
            k =>
            {
                Assert.Equal("Lighting key", k.Title);
                Assert.Equal("Next lighting effect · Mac and Windows layers", k.Detail);
                Assert.Equal([new KeyPosition(0, 0, 15), new KeyPosition(2, 0, 15)], k.Positions);
            },
            k =>
            {
                Assert.Equal("Fn + Q", k.Title);
                Assert.Equal("Lighting on/off · Mac and Windows layers", k.Detail);
            },
            k =>
            {
                Assert.Equal("Left Windows key", k.Title);
                Assert.Equal("Windows layer", k.Detail);
                Assert.False(k.IsLighting);
            });
    }

    [Fact]
    public void KeysTheAppTurnedOffAreStillListed()
    {
        var map = KeychronKeymap(6, 16);
        map[2, 0, 15] = QmkKeycodes.None;
        var keymap = new Keymap(4, 6, 16, Flatten(map));
        var state = new KeymapState("3434:0110", keymap, HardwareKeys.DescribeLayers(keymap, macWindowsPairs: true));

        var keys = HardwareKeys.Find(state, new Dictionary<KeyPosition, ushort> { [new KeyPosition(2, 0, 15)] = 0x7821 });

        var key = Assert.Single(keys);
        Assert.False(key.Enabled);
        Assert.Equal(0x7821, key.Keycode);
        Assert.Equal("Next lighting effect · Windows layer", key.Detail);
    }

    [Fact]
    public void TheLayerHeldByAKeyIsFn()
    {
        var map = new ushort[3, 2, 2];
        map[0, 1, 0] = 0x5221; // MO(1)
        var keymap = new Keymap(3, 2, 2, Flatten(map));
        var layers = HardwareKeys.DescribeLayers(keymap, macWindowsPairs: false);
        Assert.Equal([null, "Fn", "Layer 2"], layers.Select(l => l.Prefix));
    }

    [Fact]
    public void KeyPositionsRoundTrip()
    {
        var key = new KeyPosition(2, 0, 15);
        Assert.Equal(key, KeyPosition.Parse(key.ToString()));
        Assert.Null(KeyPosition.Parse("nonsense"));
    }

    private static ushort[] Flatten(ushort[,,] map) => map.Cast<ushort>().ToArray();
}
