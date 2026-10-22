using System.IO;
using Sonator.Services.Effects;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class EffectPresetTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sonator-presets-" + Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_folder, "effect-presets.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void EveryEffect_HasFactoryPresets_ThatFitItsKnobs()
    {
        foreach (var effect in EffectCatalog.All)
        {
            var presets = FactoryPresets.For(effect.Kind);
            Assert.True(presets.Count >= 4, $"{effect.Name} has only {presets.Count} presets");
            Assert.Equal(presets.Count, presets.Select(preset => preset.Name).Distinct().Count());
            foreach (var preset in presets)
            {
                Assert.Equal(effect.Parameters.Count, preset.Values.Count);
                for (var i = 0; i < effect.Parameters.Count; i++)
                {
                    Assert.InRange(preset.Values[i], effect.Parameters[i].Minimum, effect.Parameters[i].Maximum);
                }
            }
        }
    }

    [Fact]
    public void ChoosingAPreset_SetsTheKnobs_AndTurningOneMarksItModified()
    {
        var effect = new TrackEffectViewModel(EffectCatalog.Get(EffectKind.Verb));
        effect.Presets = FactoryPresets.For(EffectKind.Verb);

        effect.SelectedPreset = effect.Presets.First(preset => preset.Name == "Large Hall");

        Assert.Equal(new[] { 85.0, 35, 100, 25, 30 }, effect.Values);
        Assert.Equal("Large Hall", effect.PresetName);
        Assert.False(effect.IsPresetModified);

        effect.Parameters[4].Value = 50;
        Assert.True(effect.IsPresetModified);
    }

    [Fact]
    public void UserPresets_AreSavedByName_ReplacedAndKeptAcrossRestarts()
    {
        var verb = EffectCatalog.Get(EffectKind.Verb);
        var store = new EffectPresetStore(StorePath);
        store.Save(verb, "My Room", new[] { 10.0, 20, 30, 40, 50 });
        store.Save(verb, "my room", new[] { 11.0, 21, 31, 41, 51 }); // the same name replaces it

        var reopened = new EffectPresetStore(StorePath);
        var mine = Assert.Single(reopened.UserPresets(verb));
        Assert.Equal("my room", mine.Name);
        Assert.Equal(new[] { 11.0, 21, 31, 41, 51 }, mine.Values);
        Assert.False(mine.IsFactory);
        Assert.Equal("★ my room", mine.DisplayName);

        // The list offers Sonator's presets first, then the user's.
        var all = reopened.PresetsFor(verb);
        Assert.True(all.Take(FactoryPresets.For(EffectKind.Verb).Count).All(preset => preset.IsFactory));
        Assert.Equal("my room", all.Last().Name);

        Assert.True(reopened.Delete(verb, "My Room"));
        Assert.Empty(new EffectPresetStore(StorePath).UserPresets(verb));
    }

    [Fact]
    public void FactoryNames_CannotBeTaken()
    {
        Assert.True(EffectPresetStore.IsFactoryName(EffectCatalog.Get(EffectKind.Delay), " slapback "));
        Assert.False(EffectPresetStore.IsFactoryName(EffectCatalog.Get(EffectKind.Delay), "My Slap"));
    }

    [Fact]
    public void ThePresetInUse_IsSavedWithTheProject()
    {
        var track = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];
        var effect = new TrackEffectViewModel(EffectCatalog.Get(EffectKind.Delay));
        effect.Presets = FactoryPresets.For(EffectKind.Delay);
        effect.SelectedPreset = effect.Presets[0];
        effect.Parameters[1].Value = 20;
        track.Effects.Add(effect);

        var restored = ProjectDocumentMapper.ApplyTrackDocument(ProjectDocumentMapper.BuildTrackDocument(track)).Effects[0];

        Assert.Equal("Slapback", restored.PresetName);
        Assert.True(restored.IsPresetModified);
        Assert.Equal(20, restored.Parameters[1].Value);
    }
}
