using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sonator.Models;

namespace Sonator.Services;

public sealed class ProjectFileService
{
    private readonly JsonSerializerOptions _options;

    public ProjectFileService()
    {
        _options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        _options.Converters.Add(new JsonStringEnumConverter());
    }

    public void Save(string path, ProjectDocument document)
    {
        var json = JsonSerializer.Serialize(document, _options);
        File.WriteAllText(path, json);
    }

    public ProjectDocument Load(string path)
    {
        var json = File.ReadAllText(path);
        var document = JsonSerializer.Deserialize<ProjectDocument>(json, _options);
        if (document is null)
        {
            throw new InvalidOperationException("Project file is empty or invalid.");
        }

        return document;
    }
}
