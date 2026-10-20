using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class UndoHistoryTests
{
    [Fact]
    public void NewHistory_HasNothingToUndoOrRedo()
    {
        var history = new UndoHistory<string>();

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Undo_ReturnsStatesInReverseOrderOfChange()
    {
        var history = new UndoHistory<string>();
        history.RecordChange("v1");
        history.RecordChange("v2");

        Assert.Equal("v2", history.Undo("v3"));
        Assert.Equal("v1", history.Undo("v2"));
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Redo_ReturnsStatesUndoneEarlier()
    {
        var history = new UndoHistory<string>();
        history.RecordChange("v1");
        var restored = history.Undo("v2");

        Assert.Equal("v1", restored);
        Assert.Equal("v2", history.Redo(restored));
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void RecordChange_AfterUndo_DiscardsRedoHistory()
    {
        var history = new UndoHistory<string>();
        history.RecordChange("v1");
        history.Undo("v2");

        history.RecordChange("v1b");

        Assert.False(history.CanRedo);
        Assert.True(history.CanUndo);
    }

    [Fact]
    public void Clear_RemovesUndoAndRedoHistory()
    {
        var history = new UndoHistory<string>();
        history.RecordChange("v1");
        history.Undo("v2");

        history.Clear();

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Undo_WhenEmpty_Throws()
    {
        var history = new UndoHistory<string>();

        Assert.Throws<InvalidOperationException>(() => history.Undo("current"));
    }

    [Fact]
    public void Redo_WhenEmpty_Throws()
    {
        var history = new UndoHistory<string>();

        Assert.Throws<InvalidOperationException>(() => history.Redo("current"));
    }
}
