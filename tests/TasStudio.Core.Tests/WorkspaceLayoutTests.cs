using Dock.Model.Controls;
using Dock.Model.Core;
using TasStudio.App;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class WorkspaceLayoutTests
{
    private static WorkspaceFactory Factory() => new(WorkspaceLayout.PanelIds.ToDictionary(id => id,
        id => new StudioPanel { Id = id, Title = id, CanPin = false, CanDockAsDocument = false }));

    [Fact]
    public void DefaultKeepsInputBesideBothGameAndTimeline()
    {
        var factory = Factory();
        var root = factory.CreateLayout();
        var saved = WorkspaceFactory.Capture(root);
        var split = Assert.Single(saved.Main.Children!);
        Assert.Equal("horizontal", split.Kind);
        Assert.Equal("vertical", split.Children![0].Kind);
        Assert.Equal("input", Assert.Single(split.Children[1].Children!).Panel);
        Assert.Equal(new[] { "game", "timeline", "input" }, WorkspaceFactory.Walk(root).OfType<StudioPanel>().Select(p => p.Id));
    }

    [Fact]
    public void SplitProportionsActiveTabsAndFloatingPanelsRoundTripUsingSamePanelInstances()
    {
        var factory = Factory();
        var saved = new WorkspaceLayout(1,
            new("root", Children: [new("horizontal", Children:
            [new("tabs", Proportion: .6, Active: 1, Children: [new("panel", "game"), new("panel", "watcher")]),
             new("tabs", Proportion: .4, Children: [new("panel", "timeline")])])]),
            [new(new("root", Children: [new("tabs", Children: [new("panel", "input")])]), 70, 80, 380, 540)]);
        var restored = factory.Restore(WorkspaceLayout.Parse(saved.ToJson()));
        Assert.Equal(saved.ToJson(), WorkspaceFactory.Capture(restored).ToJson());
        Assert.All(WorkspaceFactory.Walk(restored).OfType<StudioPanel>(), panel => Assert.Same(factory.Panels[panel.Id], panel));
        var tabs = WorkspaceFactory.Walk(restored).OfType<IToolDock>().First();
        Assert.Same(factory.Panels["watcher"], tabs.ActiveDockable);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("game")]
    public void RejectsUnknownOrDuplicatePanels(string id)
    {
        var saved = new WorkspaceLayout(1, new("root", Children:
            [new("tabs", Children: [new("panel", "game"), new("panel", id)])]), []);
        Assert.Throws<InvalidDataException>(() => WorkspaceLayout.Parse(saved.ToJson()));
    }

    [Fact]
    public void ClosedPanelsStayAbsentAfterRestoreButRemainAvailableToReopen()
    {
        var factory = Factory();
        var saved = new WorkspaceLayout(1, new("root", Children: [new("tabs", Children: [new("panel", "timeline")])]), []);
        var restored = factory.Restore(saved);
        Assert.Single(WorkspaceFactory.Walk(restored).OfType<StudioPanel>());
        Assert.Equal(4, factory.Panels.Count);
    }

    [Fact]
    public void RejectsUnsupportedVersionAndMalformedContainer()
    {
        Assert.Throws<InvalidDataException>(() => WorkspaceLayout.Parse(new WorkspaceLayout(2, new("root", Children: []), []).ToJson()));
        Assert.Throws<InvalidDataException>(() => WorkspaceLayout.Parse(new WorkspaceLayout(1, new("root"), []).ToJson()));
        Assert.Throws<InvalidDataException>(() => WorkspaceLayout.Parse(new WorkspaceLayout(1, new("root", Children: [new("tabs", Children: [new("vertical", Children: [])])]), []).ToJson()));
    }
}
