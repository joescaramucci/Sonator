using System.IO;
using Sonator.Models;

namespace Sonator.Services;

/// <summary>
/// Keeps a project's audio clips with the project, so that copying or moving the project's file together with
/// its audio folder takes the whole song along. A project saved as "song.sonator.json" keeps its clips in the
/// folder "song Audio" beside it, and its file names them relative to itself ("song Audio\clip.wav"); clips
/// anywhere else are copied in when the project is saved. Projects saved before this name their clips by full
/// path, and still open as they were.
/// </summary>
public static class ProjectAudioFiles
{
    // The folder beside the project file that holds its clips.
    public static string FolderFor(string projectPath)
    {
        var name = Path.GetFileName(projectPath);
        foreach (var extension in new[] { ".sonator.json", ".json" })
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^extension.Length];
                break;
            }
        }

        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? string.Empty, name + " Audio");
    }

    // The document as it is written to the project's file: every clip copied into the project's audio folder,
    // unless it is there already, and named relative to the project. moved tells, for each clip that was
    // elsewhere, where its copy now is.
    public static ProjectDocument Pack(ProjectDocument document, string projectPath, out IReadOnlyDictionary<string, string> moved)
    {
        var folder = FolderFor(projectPath);
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? string.Empty;
        var copies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        moved = copies;

        string? PackPath(string? clip)
        {
            if (string.IsNullOrEmpty(clip) || !Path.IsPathRooted(clip) || !File.Exists(clip))
            {
                return clip; // nothing to take along: kept as it is
            }

            var full = Path.GetFullPath(clip);
            if (!IsInside(full, folder))
            {
                if (!copies.TryGetValue(full, out var copy))
                {
                    Directory.CreateDirectory(folder);
                    copy = Path.Combine(folder, Path.GetFileName(full));
                    if (File.Exists(copy) && new FileInfo(copy).Length != new FileInfo(full).Length)
                    {
                        copy = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(full)}-{Guid.NewGuid():N}{Path.GetExtension(full)}");
                    }

                    if (!File.Exists(copy))
                    {
                        File.Copy(full, copy);
                    }

                    copies[full] = copy;
                }

                full = copy;
            }

            return Path.GetRelativePath(projectDirectory, full);
        }

        return MapClips(document, PackPath);
    }

    // The document as it is used once read: the clips named relative to the project made full paths again.
    public static ProjectDocument Unpack(ProjectDocument document, string projectPath)
    {
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? string.Empty;
        return MapClips(document, clip =>
            string.IsNullOrEmpty(clip) || Path.IsPathRooted(clip) ? clip : Path.GetFullPath(Path.Combine(projectDirectory, clip)));
    }

    // Every clip a document plays, by full path.
    public static IEnumerable<string> ClipsOf(ProjectDocument document) =>
        Tracks(document).SelectMany(track => new[] { track.AudioClipPath, track.FrozenAudio }).OfType<string>().Where(Path.IsPathRooted);

    // The files of the project's audio folder that none of the documents given plays any more, as clips taken
    // out of the song or replaced by an edit, which no undo can bring back.
    public static IReadOnlyList<string> Unused(string projectPath, IEnumerable<ProjectDocument> stillWanted)
    {
        var folder = FolderFor(projectPath);
        if (!Directory.Exists(folder))
        {
            return Array.Empty<string>();
        }

        var wanted = stillWanted.SelectMany(ClipsOf).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateFiles(folder).Where(file => !wanted.Contains(Path.GetFullPath(file))).ToList();
    }

    private static bool IsInside(string file, string folder) =>
        string.Equals(Path.GetDirectoryName(file)?.TrimEnd('\\', '/'), Path.GetFullPath(folder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<TrackDocument> Tracks(ProjectDocument document) =>
        document.Patterns.SelectMany(pattern => pattern.Tracks).Concat(document.Buses ?? Enumerable.Empty<TrackDocument>());

    private static ProjectDocument MapClips(ProjectDocument document, Func<string?, string?> map) => document with
    {
        Patterns = document.Patterns
            .Select(pattern => pattern with { Tracks = pattern.Tracks.Select(track => track with { AudioClipPath = map(track.AudioClipPath), FrozenAudio = map(track.FrozenAudio) }).ToList() })
            .ToList(),
        Buses = document.Buses?.Select(track => track with { AudioClipPath = map(track.AudioClipPath), FrozenAudio = map(track.FrozenAudio) }).ToList(),
    };
}
