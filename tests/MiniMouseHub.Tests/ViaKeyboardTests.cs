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
        public int Saves;
        public readonly ushort[,,] Keymap = new ushort[4, 11, 8];
        public readonly List<byte[]> Sent = new();

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
                    var code = Keymap[request[1], request[2], request[3]];
                    reply[4] = (byte)(code >> 8);
                    reply[5] = (byte)code;
                    break;
                case 0x05:
                    Keymap[request[1], request[2], request[3]] = (ushort)((request[4] << 8) | request[5]);
                    break;
                case 0x07 when request[1] == 3 && HasRgbMatrix:
                    switch (request[2])
                    {
                        case 1: Brightness = request[3]; break;
                        case 2: Effect = request[3]; break;
                        case 3: Speed = request[3]; break;
                        case 4: Hue = request[3]; Saturation = request[4]; break;
                    }
                    break;
                case 0x08 when request[1] == 3 && HasRgbMatrix:
                    switch (request[2])
                    {
                        case 1: reply[3] = (byte)Brightness; break;
                        case 2: reply[3] = (byte)Effect; break;
                        case 3: reply[3] = (byte)Speed; break;
                        case 4: reply[3] = (byte)Hue; reply[4] = (byte)Saturation; break;
                    }
                    break;
                case 0x09 when request[1] == 3 && HasRgbMatrix:
                    Saves++;
                    break;
                case 0x11:
                    reply[1] = 4;
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
}
