using System.Buffers.Binary;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace Sonator.Services.Sounds;

// Sounds in Native Instruments' NKS format, the presets Komplete Kontrol browses: a .nksf file is a RIFF
// "NIKS" container whose NISI chunk names and files the sound and whose PCHK chunk holds the plug-in's own
// state for it. Libraries that ship NKS presets are listed in the registry under Native Instruments, each
// with the folder its content is in.
public static class NksSoundSource
{
    // Plug-ins made with JUCE, such as Spitfire's, keep the PCHK state wrapped as a VST3 state: their sounds
    // are listed. Others are left out until their state is known to load.
    private static readonly string[] JuceVendors = { "Spitfire Audio" };

    public static IReadOnlyList<SoundEntry> Read(IReadOnlyDictionary<string, string> installedPlugins, IEnumerable<string>? folders = null)
    {
        var sounds = new List<SoundEntry>();
        foreach (var folder in folders ?? ContentFolders())
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*.nksf", SearchOption.AllDirectories))
            {
                try
                {
                    if (ToEntry(file, ReadInfo(file), installedPlugins) is { } entry)
                    {
                        sounds.Add(entry);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    // A preset that cannot be read is left out.
                }
            }
        }

        return sounds;
    }

    // The content folders of the Native Instruments products registered on this computer.
    private static IEnumerable<string> ContentFolders()
    {
        using var products = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Native Instruments");
        if (products is null)
        {
            yield break;
        }

        foreach (var name in products.GetSubKeyNames())
        {
            using var product = products.OpenSubKey(name);
            if (product?.GetValue("ContentDir") is string folder && !string.IsNullOrWhiteSpace(folder))
            {
                yield return folder;
            }
        }
    }

    // What a preset's NISI chunk says about it: its name, maker, the bank it is in and its types.
    internal sealed record NksInfo(string Name, string Vendor, IReadOnlyList<string> BankChain, IReadOnlyList<string> Types);

    internal static NksInfo ReadInfo(string path)
    {
        var nisi = ReadChunk(File.ReadAllBytes(path), "NISI") ?? throw new InvalidDataException("No NISI chunk.");
        // The chunk is a version number then a MessagePack map; only its strings and string arrays are needed.
        var map = MessagePack.ReadMap(nisi.AsSpan(4));
        return new NksInfo(
            map.GetValueOrDefault("name") as string ?? Path.GetFileNameWithoutExtension(path),
            map.GetValueOrDefault("vendor") as string ?? string.Empty,
            map.GetValueOrDefault("bankchain") as IReadOnlyList<string> ?? Array.Empty<string>(),
            (map.GetValueOrDefault("types") as IReadOnlyList<object?> ?? Array.Empty<object?>())
                .SelectMany(item => item as IReadOnlyList<string> ?? (item is string type ? new[] { type } : Array.Empty<string>()))
                .ToList());
    }

    // A preset is played by the installed plug-in its bank chain names first ("Spitfire Audio - BBC Symphony
    // Orchestra" is played by "BBC Symphony Orchestra (64 Bit)").
    internal static SoundEntry? ToEntry(string path, NksInfo info, IReadOnlyDictionary<string, string> installedPlugins)
    {
        if (!JuceVendors.Contains(info.Vendor, StringComparer.OrdinalIgnoreCase) || info.BankChain.Count == 0)
        {
            return null;
        }

        var product = info.BankChain[0].Replace($"{info.Vendor} - ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        var plugin = installedPlugins.FirstOrDefault(pair => pair.Key.StartsWith(product, StringComparison.OrdinalIgnoreCase));
        if (plugin.Key is null)
        {
            return null;
        }

        var types = string.Join(' ', info.Types);
        var subtype = info.Types.LastOrDefault() ?? string.Empty;
        return new SoundEntry(
            info.Name,
            SoundCategories.Categorize(info.Name, info.Types.FirstOrDefault() ?? string.Empty, subtype),
            subtype,
            plugin.Key,
            plugin.Value,
            path,
            info.BankChain.Count > 1 ? string.Join(" / ", info.BankChain.Skip(1)) : string.Empty,
            info.Vendor,
            types,
            SoundKind.NksPreset);
    }

    // The plug-in state for a preset: the PCHK chunk's state, after its version number, wrapped as a JUCE
    // plug-in's VST3 state, whose header and fixed fields are taken from the plug-in's own current state.
    public static byte[] StateFor(string presetPath, byte[] currentState)
    {
        var pchk = ReadChunk(File.ReadAllBytes(presetPath), "PCHK") ?? throw new InvalidDataException("No PCHK chunk.");
        var chunk = pchk.AsSpan(4);

        // "VstW" header (16 bytes), then an fxBank: "CcnK", its size, "FBCh", version, plug-in id, plug-in
        // version, programs, 128 reserved bytes, then the chunk's size and the chunk, all big-endian.
        const int chunkSizeAt = 0xAC;
        if (currentState.Length < chunkSizeAt || Encoding.ASCII.GetString(currentState, 0, 4) != "VstW"
            || Encoding.ASCII.GetString(currentState, 0x10, 4) != "CcnK")
        {
            throw new InvalidDataException("The plug-in's state is not a JUCE VST3 state.");
        }

        var state = new byte[chunkSizeAt + 4 + chunk.Length];
        currentState.AsSpan(0, chunkSizeAt).CopyTo(state);
        BinaryPrimitives.WriteInt32BigEndian(state.AsSpan(0x14), state.Length - 0x18);
        BinaryPrimitives.WriteInt32BigEndian(state.AsSpan(chunkSizeAt), chunk.Length);
        chunk.CopyTo(state.AsSpan(chunkSizeAt + 4));
        return state;
    }

    // The body of the first chunk with this id in a RIFF file, or null.
    internal static byte[]? ReadChunk(byte[] riff, string id)
    {
        for (var position = 12; position + 8 <= riff.Length;)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(riff.AsSpan(position + 4));
            if (size < 0 || position + 8 + size > riff.Length)
            {
                return null;
            }

            if (Encoding.ASCII.GetString(riff, position, 4) == id)
            {
                return riff.AsSpan(position + 8, size).ToArray();
            }

            position += 8 + size + (size & 1);
        }

        return null;
    }
}

// Just enough of MessagePack to read the NISI chunk: maps, arrays, strings, numbers, booleans and nil.
internal static class MessagePack
{
    public static Dictionary<string, object?> ReadMap(ReadOnlySpan<byte> data)
    {
        var position = 0;
        return Read(data, ref position) as Dictionary<string, object?> ?? new Dictionary<string, object?>();
    }

    private static object? Read(ReadOnlySpan<byte> data, ref int position)
    {
        var tag = data[position++];
        switch (tag)
        {
            case <= 0x7F: return (long)tag;
            case >= 0x80 and <= 0x8F: return Map(data, ref position, tag & 0x0F);
            case >= 0x90 and <= 0x9F: return Array(data, ref position, tag & 0x0F);
            case >= 0xA0 and <= 0xBF: return Text(data, ref position, tag & 0x1F);
            case 0xC0: return null;
            case 0xC2: return false;
            case 0xC3: return true;
            case 0xCA: position += 4; return 0.0;
            case 0xCB: position += 8; return 0.0;
            case 0xCC: return (long)data[position++];
            case 0xCD: position += 2; return (long)BinaryPrimitives.ReadUInt16BigEndian(data[(position - 2)..]);
            case 0xCE: position += 4; return (long)BinaryPrimitives.ReadUInt32BigEndian(data[(position - 4)..]);
            case 0xCF: position += 8; return (long)BinaryPrimitives.ReadUInt64BigEndian(data[(position - 8)..]);
            case 0xD0: return (long)(sbyte)data[position++];
            case 0xD1: position += 2; return (long)BinaryPrimitives.ReadInt16BigEndian(data[(position - 2)..]);
            case 0xD2: position += 4; return (long)BinaryPrimitives.ReadInt32BigEndian(data[(position - 4)..]);
            case 0xD3: position += 8; return BinaryPrimitives.ReadInt64BigEndian(data[(position - 8)..]);
            case 0xD9: return Text(data, ref position, data[position++]);
            case 0xDA: { var length = BinaryPrimitives.ReadUInt16BigEndian(data[position..]); position += 2; return Text(data, ref position, length); }
            case 0xDB: { var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data[position..]); position += 4; return Text(data, ref position, length); }
            case 0xDC: { var count = BinaryPrimitives.ReadUInt16BigEndian(data[position..]); position += 2; return Array(data, ref position, count); }
            case 0xDD: { var count = (int)BinaryPrimitives.ReadUInt32BigEndian(data[position..]); position += 4; return Array(data, ref position, count); }
            case 0xDE: { var count = BinaryPrimitives.ReadUInt16BigEndian(data[position..]); position += 2; return Map(data, ref position, count); }
            case 0xDF: { var count = (int)BinaryPrimitives.ReadUInt32BigEndian(data[position..]); position += 4; return Map(data, ref position, count); }
            case >= 0xE0: return (long)(sbyte)tag;
            default: throw new InvalidDataException($"Unsupported MessagePack tag 0x{tag:X2}.");
        }
    }

    private static string Text(ReadOnlySpan<byte> data, ref int position, int length)
    {
        var text = Encoding.UTF8.GetString(data.Slice(position, length));
        position += length;
        return text;
    }

    // An array of strings comes back as a list of strings; any other array as a list of objects.
    private static object Array(ReadOnlySpan<byte> data, ref int position, int count)
    {
        var items = new List<object?>(count);
        for (var i = 0; i < count; i++)
        {
            items.Add(Read(data, ref position));
        }

        return items.All(item => item is string) ? items.Cast<string>().ToList() : items;
    }

    private static Dictionary<string, object?> Map(ReadOnlySpan<byte> data, ref int position, int count)
    {
        var map = new Dictionary<string, object?>(count);
        for (var i = 0; i < count; i++)
        {
            var key = Read(data, ref position)?.ToString() ?? string.Empty;
            map[key] = Read(data, ref position);
        }

        return map;
    }
}
