using System.Buffers.Binary;
using System.IO;
using System.Text;
using Sonator.Services.Sounds;
using Xunit;

namespace Sonator.Tests;

public sealed class NksSoundTests
{
    private static byte[] Str(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var header = bytes.Length < 32 ? new[] { (byte)(0xA0 | bytes.Length) } : new byte[] { 0xD9, (byte)bytes.Length };
        return header.Concat(bytes).ToArray();
    }

    private static byte[] StrArray(params string[] items) =>
        new[] { (byte)(0x90 | items.Length) }.Concat(items.SelectMany(Str)).ToArray();

    private static byte[] Chunk(string id, byte[] body)
    {
        var header = new byte[8];
        Encoding.ASCII.GetBytes(id).CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), body.Length);
        return header.Concat(body).Concat(body.Length % 2 == 1 ? new byte[1] : Array.Empty<byte>()).ToArray();
    }

    // A preset as Komplete Kontrol stores it: RIFF NIKS with NISI (version, MessagePack map) and PCHK (version, state).
    private static string WritePreset(byte[] state)
    {
        var map = new byte[] { 0x84 }
            .Concat(Str("name")).Concat(Str("Strings: Discover - Violins 1"))
            .Concat(Str("vendor")).Concat(Str("Spitfire Audio"))
            .Concat(Str("bankchain")).Concat(StrArray("Spitfire Audio - BBC Symphony Orchestra", "Strings"))
            .Concat(Str("types")).Concat(new byte[] { 0x91 }).Concat(StrArray("Bowed Strings", "Violin"))
            .ToArray();
        var body = Encoding.ASCII.GetBytes("NIKS")
            .Concat(Chunk("NISI", new byte[4].Concat(map).ToArray()))
            .Concat(Chunk("PCHK", new byte[] { 1, 0, 0, 0 }.Concat(state).ToArray()))
            .ToArray();
        var riff = Encoding.ASCII.GetBytes("RIFF").Concat(BitConverter.GetBytes(body.Length)).Concat(body).ToArray();
        var path = Path.Combine(Path.GetTempPath(), $"sonator-nks-{Guid.NewGuid():N}.nksf");
        File.WriteAllBytes(path, riff);
        return path;
    }

    [Fact]
    public void APreset_IsNamedAndFiledFromItsNisiChunk_AndPlayedByItsProductsPlugIn()
    {
        var path = WritePreset(Encoding.ASCII.GetBytes("VC2!state"));
        try
        {
            var info = NksSoundSource.ReadInfo(path);
            var installed = new Dictionary<string, string> { ["BBC Symphony Orchestra (64 Bit)"] = @"C:\VST3\BBC.vst3" };
            var entry = NksSoundSource.ToEntry(path, info, installed);

            Assert.Equal("Strings: Discover - Violins 1", info.Name);
            Assert.NotNull(entry);
            Assert.Equal("BBC Symphony Orchestra (64 Bit)", entry!.PluginName);
            Assert.Equal(SoundCategories.Strings, entry.Category);
            Assert.Equal("Violin", entry.Subcategory);
            Assert.Equal(SoundKind.NksPreset, entry.Kind);
            Assert.Null(NksSoundSource.ToEntry(path, info, new Dictionary<string, string>()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void APresetsState_IsWrappedAsTheJucePlugInsVst3State()
    {
        var chunk = Encoding.ASCII.GetBytes("VC2!<xml name=\"Violins 1\"/>");
        var path = WritePreset(chunk);
        try
        {
            // The plug-in's own empty state: VstW header, CcnK/FBCh bank with its id, then a small chunk.
            var current = new byte[0xAC + 4 + 3];
            Encoding.ASCII.GetBytes("VstW").CopyTo(current, 0);
            Encoding.ASCII.GetBytes("CcnK").CopyTo(current, 0x10);
            Encoding.ASCII.GetBytes("FBCh").CopyTo(current, 0x18);
            Encoding.ASCII.GetBytes("Sant").CopyTo(current, 0x20);

            var state = NksSoundSource.StateFor(path, current);

            Assert.Equal("Sant", Encoding.ASCII.GetString(state, 0x20, 4));
            Assert.Equal(state.Length - 0x18, BinaryPrimitives.ReadInt32BigEndian(state.AsSpan(0x14)));
            Assert.Equal(chunk.Length, BinaryPrimitives.ReadInt32BigEndian(state.AsSpan(0xAC)));
            Assert.Equal(chunk, state.AsSpan(0xB0).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
