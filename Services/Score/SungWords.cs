using System.IO;
using Whisper.net;
using Whisper.net.Ggml;

namespace Sonator.Services.Score;

// Hears the words of a sung take with Whisper, on this computer: no sound leaves it. The model (Whisper "small",
// about 470 MB, good at Italian and English and at words sung) is downloaded the first time and kept in
// %AppData%\Sonator\Models. Each word comes with when it is sung, for setting it under its notes.
public static class SungWords
{
    public static string ModelPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sonator", "Models", "ggml-small.bin");

    public static bool IsModelReady => File.Exists(ModelPath);

    // Downloads the model, reporting the megabytes received.
    public static async Task DownloadModelAsync(IProgress<string>? progress, CancellationToken cancellation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        var partial = ModelPath + ".part";
        await using (var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.Small, cancellationToken: cancellation))
        await using (var target = File.Create(partial))
        {
            var buffer = new byte[1 << 20];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellation);
                total += read;
                progress?.Report($"Downloading the speech model: {total / (1024 * 1024)} MB");
            }
        }

        File.Move(partial, ModelPath, overwrite: true);
    }

    // The words sung in a mono take, in order with their times. language is "it", "en"... or "auto".
    public static async Task<IReadOnlyList<SungWord>> RecognizeAsync(float[] mono, int sampleRate, string language,
        IProgress<string>? progress, CancellationToken cancellation)
    {
        var samples = Resample(mono, sampleRate, 16000);
        using var factory = WhisperFactory.FromPath(ModelPath);
        using var processor = factory.CreateBuilder()
            .WithLanguage(language)
            .WithTokenTimestamps()
            .SplitOnWord()
            .WithMaxSegmentLength(1)
            .WithProgressHandler(percent => progress?.Report($"Hearing the words: {percent}%"))
            .Build();

        var words = new List<SungWord>();
        await foreach (var segment in processor.ProcessAsync(samples, cancellation))
        {
            var text = segment.Text.Trim();
            if (text.Length > 0 && !text.StartsWith('[') && !text.StartsWith('('))
            {
                words.Add(new SungWord(segment.Start.TotalSeconds, segment.End.TotalSeconds, text));
            }
        }

        return words;
    }

    // The take at another rate, read between its samples.
    internal static float[] Resample(float[] mono, int from, int to)
    {
        if (from == to)
        {
            return mono;
        }

        var result = new float[(int)((long)mono.Length * to / from)];
        var step = from / (double)to;
        for (var i = 0; i < result.Length; i++)
        {
            var position = i * step;
            var index = (int)position;
            var fraction = (float)(position - index);
            var a = mono[Math.Min(index, mono.Length - 1)];
            var b = mono[Math.Min(index + 1, mono.Length - 1)];
            result[i] = a + (b - a) * fraction;
        }

        return result;
    }
}
