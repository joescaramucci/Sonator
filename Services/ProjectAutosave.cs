using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Sonator.Models;

namespace Sonator.Services;

// What an autosave holds besides the project: the file the project came from, if any, when it was taken, and
// the session of Sonator that took it.
public sealed record AutosaveInfo(string? ProjectPath, DateTime SavedAt, string Session);

/// <summary>
/// The copy of the open project taken every few minutes while it has unsaved changes, so that work is not lost
/// when Sonator or Windows stops without the project being saved. It is kept apart from the project's own file,
/// which only the player's own saves write, in %AppData%\Sonator\Autosave, under the name of the session that
/// took it, so that two Sonators open at once keep theirs apart. A save, or closing Sonator properly, clears the
/// session's copy; one left by a session that is no longer running means it ended without either, and its work
/// can be recovered.
/// </summary>
public sealed class ProjectAutosave
{
    // The choices offered for how often to autosave, in minutes; 0 is off.
    public static IReadOnlyList<int> IntervalChoices { get; } = new[] { 0, 1, 3, 5, 10, 15 };

    public const int DefaultMinutes = 5;

    private const string Prefix = "autosave-";
    private const string InfoExtension = ".info.json";
    private const string ProjectExtension = ".sonator.json";

    private readonly string _folder;
    private readonly string _session;
    private readonly Func<string, bool> _isRunning;
    private readonly ProjectFileService _files = new();

    public ProjectAutosave(string? folder = null, string? session = null, Func<string, bool>? isRunning = null)
    {
        _folder = folder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sonator", "Autosave");
        _session = session ?? ThisSession();
        _isRunning = isRunning ?? IsRunning;
    }

    private string ProjectFile(string session) => Path.Combine(_folder, Prefix + session + ProjectExtension);

    private string InfoFile(string session) => Path.Combine(_folder, Prefix + session + InfoExtension);

    // Writes the project, then the note of where it came from: the note marks the autosave as complete, so a
    // copy cut short by a crash is never offered.
    public void Write(ProjectDocument document, string? projectPath)
    {
        Directory.CreateDirectory(_folder);
        var partial = ProjectFile(_session) + ".partial";
        _files.Save(partial, document);
        File.Move(partial, ProjectFile(_session), overwrite: true);
        File.WriteAllText(InfoFile(_session), JsonSerializer.Serialize(new AutosaveInfo(projectPath, DateTime.Now, _session)));
    }

    // The latest autosave left by a session that is no longer running, when there is one.
    public AutosaveInfo? Find()
    {
        try
        {
            if (!Directory.Exists(_folder))
            {
                return null;
            }

            return Directory.EnumerateFiles(_folder, Prefix + "*" + InfoExtension)
                .Select(ReadInfo)
                .Where(info => info is not null && info.Session != _session && !_isRunning(info.Session)
                               && File.Exists(ProjectFile(info.Session)))
                .OrderByDescending(info => info!.SavedAt)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static AutosaveInfo? ReadInfo(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<AutosaveInfo>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public ProjectDocument Load(AutosaveInfo info) => _files.Load(ProjectFile(info.Session));

    // Takes a recovered autosave over as this session's own, so it is kept until the project is saved, and
    // cleared then.
    public void Adopt(AutosaveInfo info)
    {
        Directory.CreateDirectory(_folder);
        File.Move(ProjectFile(info.Session), ProjectFile(_session), overwrite: true);
        File.WriteAllText(InfoFile(_session), JsonSerializer.Serialize(info with { Session = _session }));
        Delete(InfoFile(info.Session));
    }

    // Throws away the autosave left by another session.
    public void Discard(AutosaveInfo info)
    {
        Delete(InfoFile(info.Session));
        Delete(ProjectFile(info.Session));
    }

    // Throws away this session's autosave.
    public void Clear()
    {
        Delete(InfoFile(_session));
        Delete(ProjectFile(_session));
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left over, it is offered again at the next start; nothing is lost.
        }
    }

    // The interval kept in the settings, or the default when none was chosen or it is not one of the choices.
    public static int MinutesFrom(AppSettings settings) =>
        settings.AutosaveMinutes is { } minutes && IntervalChoices.Contains(minutes) ? minutes : DefaultMinutes;

    // A session is this process, told apart from a later one given the same number by when it started.
    private static string ThisSession()
    {
        using var process = Process.GetCurrentProcess();
        return $"{process.Id}-{process.StartTime.ToUniversalTime().Ticks}";
    }

    private static bool IsRunning(string session)
    {
        var parts = session.Split('-');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var id) || !long.TryParse(parts[1], out var started))
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(id);
            return process.StartTime.ToUniversalTime().Ticks == started;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true; // there, but not ours to look at: leave its autosave alone
        }
    }
}
