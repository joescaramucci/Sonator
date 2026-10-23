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
    private readonly string _kindsPath;
    private readonly Func<string, Task<bool?>> _runCheck;
    private readonly Func<string, Task<PluginKind?>> _readKind;
    private readonly object _sync = new();
    private Dictionary<string, bool> _results;
    private readonly Dictionary<string, PluginKind> _kinds;

    public Vst3PluginCheckService(string? cachePath = null, Func<string, Task<bool?>>? runCheck = null, Func<string, Task<PluginKind?>>? readKind = null)
    {
        _cachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sonator", "plugin-check.json");
        _kindsPath = Path.Combine(Path.GetDirectoryName(_cachePath) ?? string.Empty, "plugin-kinds.json");
        _runCheck = runCheck ?? RunHelperAsync;
        _readKind = readKind ?? ReadKindWithHelperAsync;
        _results = Load<bool>(_cachePath);
        _kinds = Load<PluginKind>(_kindsPath);
    }

    // Whether the plug-in is an instrument or an effect, when already known for it as it is on disk now.
    public PluginKind? KnownKind(string pluginPath)
    {
        lock (_sync)
        {
            return _kinds.TryGetValue(Identity(pluginPath), out var kind) ? kind : null;
        }
    }

    // Whether the plug-in is an instrument or an effect, finding out (in the helper process) when not known.
    // Null when it could not be found out.
    public async Task<PluginKind?> KindAsync(string pluginPath)
    {
        if (KnownKind(pluginPath) is { } known)
        {
            return known;
        }

        await CheckSlots.WaitAsync().ConfigureAwait(false);
        PluginKind? kind;
        try
        {
            kind = await _readKind(pluginPath).ConfigureAwait(false);
        }
        finally
        {
            CheckSlots.Release();
        }

        if (kind is { } result)
        {
            lock (_sync)
            {
                _kinds[Identity(pluginPath)] = result;
                Save(_kindsPath, _kinds);
            }
        }

        return kind;
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
                Save(_cachePath, _results);
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
            _kinds.Clear();
            Save(_cachePath, _results);
            Save(_kindsPath, _kinds);
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

    private static async Task<bool?> RunHelperAsync(string pluginPath) =>
        await RunHelperAsync("--check", pluginPath).ConfigureAwait(false) is { } exitCode ? exitCode == 0 : null;

    // The helper answers 10 plus the kind; a plug-in that fails or hangs there has no kind to tell.
    private static async Task<PluginKind?> ReadKindWithHelperAsync(string pluginPath) =>
        await RunHelperAsync("--kind", pluginPath).ConfigureAwait(false) is >= 10 and <= 12 and var exitCode
            ? (PluginKind)(exitCode - 10)
            : null;

    // The helper's exit code, -1 when it hangs and is stopped, or null when it cannot run at all.
    private static async Task<int?> RunHelperAsync(string mode, string pluginPath)
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
        startInfo.ArgumentList.Add(mode);
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

                return -1;
            }

            return process.ExitCode;
        }
    }

    private static void Save<T>(string path, Dictionary<string, T> values)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(values, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The results are only a shortcut; without the file the plug-ins are checked again next time.
        }
    }

    private static Dictionary<string, T> Load<T>(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, T>>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }
}
