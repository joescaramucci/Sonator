using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Sonator.Services;

// Separates a song into four stems (drums, bass, other, vocals) with HT-Demucs v4, Meta's music source separation
// model, on this computer: no sound leaves it. The model, an ONNX export of it with the STFT inside (about 166 MB,
// huggingface.co/adowu/htdemucs-onnx), is downloaded the first time, checked against its SHA-256, and kept in
// %AppData%\Sonator\Models. It takes stereo at 44.1 kHz in segments of 7.8 s, which overlap by a quarter and are
// cross-faded, as its reference script does.
public static class StemSeparator
{
    // The stems in the order the model gives them.
    public static IReadOnlyList<string> Stems { get; } = new[] { "Drums", "Bass", "Other", "Vocals" };

    public const int SampleRate = 44100;
    public const int SegmentFrames = 343980; // 7.8 s

    public const string ModelUrl = "https://huggingface.co/adowu/htdemucs-onnx/resolve/main/htdemucs_fp16weights.onnx";
    public const string ModelSha256 = "d05c269d0178d2a72ad484b10b11dd370193fc923201c3b27a99f848745db70a";
    public const long ModelBytes = 165612636;

    public static string ModelPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sonator", "Models", "htdemucs_fp16weights.onnx");

    public static bool IsModelReady => File.Exists(ModelPath);

    // Downloads the model, reporting how much of it has come; a file that is not the model is not kept.
    public static async Task DownloadModelAsync(IProgress<double>? progress, CancellationToken cancellation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        var partial = ModelPath + ".part";
        using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
        using (var response = await client.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, cancellation))
        {
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength ?? ModelBytes;
            await using var source = await response.Content.ReadAsStreamAsync(cancellation);
            await using var target = File.Create(partial);
            var buffer = new byte[1 << 20];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellation);
                total += read;
                progress?.Report(Math.Min(1, total / (double)length));
            }
        }

        string hash;
        await using (var file = File.OpenRead(partial))
        {
            hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation)).ToLowerInvariant();
        }

        if (hash != ModelSha256)
        {
            File.Delete(partial);
            throw new InvalidDataException("The model downloaded is not the one expected (its SHA-256 differs), so it was not kept.");
        }

        File.Move(partial, ModelPath, overwrite: true);
    }

    // The stems of an audio file, as clips at 44.1 kHz stereo, in the order of Stems. progress goes from 0 to 1.
    public static IReadOnlyList<AudioClip> SeparateFile(string path, IProgress<double>? progress, CancellationToken cancellation)
    {
        var mix = ToModelFormat(AudioClipLoader.Load(path));
        using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        using var session = new InferenceSession(ModelPath, options);
        var input = session.InputMetadata.Keys.First();
        return Separate(mix, segment =>
        {
            var tensor = new DenseTensor<float>(segment, new[] { 1, 2, SegmentFrames });
            using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(input, tensor) });
            return results.First().AsTensor<float>().ToArray();
        }, progress, cancellation);
    }

    // Separates a 44.1 kHz stereo clip: runSegment takes a segment, planar (left then right, SegmentFrames each),
    // and gives its stems, planar ([stem][channel][frame]). The segments overlap by a quarter, cross-faded
    // linearly, and the last is padded with silence.
    internal static IReadOnlyList<AudioClip> Separate(AudioClip mix, Func<float[], float[]> runSegment, IProgress<double>? progress,
        CancellationToken cancellation, int segmentFrames = SegmentFrames)
    {
        var frames = mix.FrameCount;
        var overlap = segmentFrames / 4;
        var stride = segmentFrames - overlap;
        var segments = Math.Max(1, (frames + stride - 1) / stride);
        var window = new float[segmentFrames];
        for (var i = 0; i < segmentFrames; i++)
        {
            window[i] = 1;
        }

        for (var i = 0; i < overlap; i++)
        {
            // Never quite silent, so the song's first and last samples, in one segment only, are kept too.
            var fade = (i + 1) / (float)(overlap + 1);
            window[i] = fade;
            window[segmentFrames - 1 - i] = fade;
        }

        var stems = Stems.Select(_ => new float[frames * 2]).ToArray();
        var weight = new float[frames];
        var segment = new float[segmentFrames * 2];
        for (var index = 0; index < segments; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            var start = index * stride;
            var length = Math.Min(segmentFrames, frames - start);
            if (length <= 0)
            {
                break;
            }

            Array.Clear(segment);
            for (var frame = 0; frame < length; frame++)
            {
                segment[frame] = mix.Samples[(start + frame) * 2];
                segment[segmentFrames + frame] = mix.Samples[(start + frame) * 2 + 1];
            }

            var output = runSegment(segment);
            for (var stem = 0; stem < stems.Length; stem++)
            {
                var left = stem * 2 * segmentFrames;
                var right = left + segmentFrames;
                for (var frame = 0; frame < length; frame++)
                {
                    var w = window[frame];
                    stems[stem][(start + frame) * 2] += output[left + frame] * w;
                    stems[stem][(start + frame) * 2 + 1] += output[right + frame] * w;
                }
            }

            for (var frame = 0; frame < length; frame++)
            {
                weight[start + frame] += window[frame];
            }

            progress?.Report((index + 1) / (double)segments);
        }

        foreach (var stem in stems)
        {
            for (var frame = 0; frame < frames; frame++)
            {
                var w = Math.Max(weight[frame], 1e-8f);
                stem[frame * 2] /= w;
                stem[frame * 2 + 1] /= w;
            }
        }

        return stems.Select(samples => new AudioClip(samples, 2, SampleRate)).ToList();
    }

    // A clip as the model takes it: stereo (mono doubled, more channels than two mixed down to their first
    // two), at 44.1 kHz.
    internal static AudioClip ToModelFormat(AudioClip clip)
    {
        if (clip.Channels == 2 && clip.SampleRate == SampleRate)
        {
            return clip;
        }

        ISampleProvider provider = AudioClipLoader.ToPlayable(clip, 0, clip.FrameCount);
        if (clip.Channels == 1)
        {
            provider = new MonoToStereoSampleProvider(provider);
        }
        else if (clip.Channels > 2)
        {
            var multiplexer = new MultiplexingSampleProvider(new[] { provider }, 2);
            multiplexer.ConnectInputToOutput(0, 0);
            multiplexer.ConnectInputToOutput(1, 1);
            provider = multiplexer;
        }

        if (clip.SampleRate != SampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, SampleRate);
        }

        var samples = new List<float>(clip.FrameCount * 2 * SampleRate / Math.Max(1, clip.SampleRate) + 4096);
        var buffer = new float[SampleRate * 2];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, read));
        }

        return new AudioClip(samples.ToArray(), 2, SampleRate);
    }
}
