using System.IO;

namespace Sonator.Services;

// What a song file says about itself, as players and phones show it (ID3 tags in an MP3, INFO and ID3 in a WAV):
// its title, artist, album, genre, year, track number, composer, a comment and the cover's picture.
public sealed record SongTags(
    string? Title = null,
    string? Artist = null,
    string? Album = null,
    string? Genre = null,
    int? Year = null,
    int? Track = null,
    string? Composer = null,
    string? Comment = null,
    byte[]? Cover = null,
    string? CoverMimeType = null)
{
    public static SongTags Empty { get; } = new();

    public bool IsEmpty => this == Empty || (string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Artist)
        && string.IsNullOrWhiteSpace(Album) && string.IsNullOrWhiteSpace(Genre) && Year is null && Track is null
        && string.IsNullOrWhiteSpace(Composer) && string.IsNullOrWhiteSpace(Comment) && Cover is null);

    // Genres players know, offered in the list; any other can be typed.
    public static IReadOnlyList<string> CommonGenres { get; } = new[]
    {
        "Pop", "Rock", "Dance", "Electronic", "Hip-Hop", "R&B", "Soul", "Funk", "Jazz", "Blues", "Country", "Folk",
        "Reggae", "Latin", "Classical", "Soundtrack", "Ambient", "Metal", "Punk", "Indie", "Singer-Songwriter",
        "Gospel", "World", "Children's", "Italian Pop",
    };

    // The tags a song file has; empty ones when it has none or cannot be read.
    public static SongTags Read(string path)
    {
        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        var picture = tag.Pictures.FirstOrDefault();
        return new SongTags(
            NullIfEmpty(tag.Title),
            NullIfEmpty(tag.FirstPerformer),
            NullIfEmpty(tag.Album),
            NullIfEmpty(tag.FirstGenre),
            tag.Year > 0 ? (int)tag.Year : null,
            tag.Track > 0 ? (int)tag.Track : null,
            NullIfEmpty(tag.FirstComposer),
            NullIfEmpty(tag.Comment),
            picture?.Data.Data,
            picture?.MimeType);
    }

    // Writes the tags into a song file, in place of those it had.
    public void Write(string path)
    {
        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        tag.Title = NullIfEmpty(Title);
        tag.Performers = Words(Artist);
        tag.AlbumArtists = Words(Artist);
        tag.Album = NullIfEmpty(Album);
        tag.Genres = Words(Genre);
        tag.Year = (uint)Math.Max(0, Year ?? 0);
        tag.Track = (uint)Math.Max(0, Track ?? 0);
        tag.Composers = Words(Composer);
        tag.Comment = NullIfEmpty(Comment);
        tag.Pictures = Cover is { Length: > 0 }
            ? new TagLib.IPicture[]
            {
                new TagLib.Picture(new TagLib.ByteVector(Cover))
                {
                    Type = TagLib.PictureType.FrontCover,
                    MimeType = CoverMimeType ?? MimeTypeOf(Cover),
                    Description = "Cover",
                },
            }
            : Array.Empty<TagLib.IPicture>();
        file.Save();
    }

    // The picture's type, from its first bytes: PNG or JPEG.
    public static string MimeTypeOf(byte[] picture) =>
        picture.Length > 4 && picture[0] == 0x89 && picture[1] == 0x50 ? "image/png" : "image/jpeg";

    private static string[] Words(string? text) =>
        string.IsNullOrWhiteSpace(text) ? Array.Empty<string>() : new[] { text.Trim() };

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // True for the files whose tags can be read and written here.
    public static bool CanTag(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mp3" or ".wav" or ".flac" or ".m4a" or ".ogg";
}
