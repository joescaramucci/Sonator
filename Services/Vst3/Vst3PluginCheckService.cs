using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Sonator.Services.Vst3;

// Finds out which plug-ins Sonator can use, by loading each one in the separate VstThumbnailHost
// process: a plug-in that fails, crashes or hangs there cannot take Sonator down. Results are kept
// per plug-in file and its date, in %LocalAppData%\Sonator\plugin-check.json, so a plug-in is checked
// again only when it is new or updated.
public sealed class Vst3PluginCheckService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly SemaphoreSlim CheckSlots = new(3);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _cachePath;
    private readonly Func<string, Task<bool?>> _runCheck;
    private readonly object _sync = new();
    private Dictionary<string, bool> _results;

    public Vst3PluginCheckService(string? cachePath = null, Func<string, Task<bool?>>? runCheck = null)
    {
        _cachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sonator", "plugin-check.json");
        _runCheck = runCheck ?? RunHelperAsync;
        _results = Load(_cachePath);
    }

    // The known result for the plug-in as it is on disk now, or null when it has not been checked.
    public bool? KnownResult(string pluginPath)
    {
        lock (_sync)
        {
            return _results.TryGetValue(Identity(pluginPath), out var works) ? works : null;
        }
    }

    // Whether the plug-in works, checking it when its result is not known yet.
    // Null when the check could not run at all (the helper is missing): such plug-ins are kept.
    public async Task<bool?> WorksAsync(string pluginPath)
    {
        if (KnownResult(pluginPath) is { } known)
        {
            return known;
        }

        await CheckSlots.WaitAsync().ConfigureAwait(false);
        bool? works;
        try
        {
            works = await _runCheck(pluginPath).ConfigureAwait(false);
        }
        finally
        {
            CheckSlots.Release();
        }

        if (works is { } result)
        {
            lock (_sync)
            {
                _results[Identity(pluginPath)] = result;
                Save();
            }
        }

        return works;
    }

    // Forgets every result, so all plug-ins are checked again.
    public void Forget()
    {
        lock (_sync)
        {
            _results = new Dictionary<string, bool>();
            Save();
        }
    }

    // The plug-in's path and date: a plug-in replaced by a new version is checked again.
    private static string Identity(string pluginPath)
    {
        var lastWrite = Directory.Exists(pluginPath)
            ? Directory.GetLastWriteTimeUtc(pluginPath)
            : File.Exists(pluginPath) ? File.GetLastWriteTimeUtc(pluginPath) : DateTime.MinValue;
        return $"{pluginPath.ToUpperInvariant()}|{lastWrite.Ticks}";
    }

    private static async Task<bool?> RunHelperAsync(string pluginPath)
    {
        var helperPath = Path.Combine(AppContext.BaseDirectory, "VstThumbnailHost", "Sonator.VstThumbnailHost.exe");
        if (!File.Exists(helperPath))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--check");
        startInfo.ArgumentList.Add(pluginPath);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception)
        {
            return null;
        }

        if (process is null)
        {
            return null;
        }

        using (process)
        using (var timeout = new CancellationTokenSource(Timeout))
        {
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A plug-in that hangs while loading does not work.
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }

                return false;
            }

            return process.ExitCode == 0;
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(_results, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The results are only a shortcut; without the file the plug-ins are checked again next time.
        }
    }

    private static Dictionary<string, bool> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }
}
