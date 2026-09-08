using System.Security.Cryptography;
using TasStudio.App;
using TasStudio.Core;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class UpdateTests
{
    [Fact]
    public async Task ReleaseDiscoveryFindsStableBehindMoreThanOnePageOfAlphas()
    {
        var pagesRead = new List<int>();
        var alpha = new GithubRelease { Prerelease = true, Assets = [new GithubReleaseAsset { Name = "releases.win-x64-alpha.json" }] };
        var stable = new GithubRelease { Name = "stable", Assets = [new GithubReleaseAsset { Name = "releases.win-x64-stable.json" }] };
        var source = new ChannelGithubSource(ReleaseChannel.Stable, page =>
        {
            pagesRead.Add(page);
            return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(page == 1 ? Enumerable.Repeat(alpha, 100).ToArray() : [stable]));
        });
        var releases = await source.ReadReleases(false);
        Assert.Equal("stable", Assert.Single(releases).Name);
        Assert.Equal([1, 2], pagesRead);
    }

    [Theory]
    [InlineData("0.2.0", "Stable", true)]
    [InlineData("0.2.0-beta.1", "Stable", false)]
    [InlineData("0.2.0-alpha.2", "Beta", false)]
    [InlineData("0.2.0", "Alpha", false)]
    [InlineData("0.2.0-beta.1", "Beta", true)]
    [InlineData("0.2.0-alpha.2", "Alpha", true)]
    public void ChannelsNeverCrossImplicitly(string version, string channelName, bool expected)
    {
        var channel = Enum.Parse<ReleaseChannel>(channelName);
        Assert.Equal(expected, channel.Matches(version));
        Assert.Equal(channel, ReleaseChannels.FromFeed(channel.Feed()));
        Assert.Null(ReleaseChannels.FromFeed("win"));
    }

    [Fact]
    public async Task VerifiedDownloadSurvivesOfflineRestartAndChannelChangeDiscardsIt()
    {
        using var files = new TestWorkspace();
        var folder = files.FilePath("updates"); Directory.CreateDirectory(folder);
        var manager = new FakeManager(folder);
        var preferences = Path.Combine(folder, "preferences.json");
        using (var service = Create(manager, preferences))
        {
            await service.CheckAsync();
            Assert.Equal("0.2.0-beta.2", service.PendingVersion);
            Assert.Contains("ready", service.Status);
            Assert.Contains("\"Version\": \"0.2.0-beta.2\"", File.ReadAllText(preferences));
        }
        using var reloaded = Create(manager, preferences);
        Assert.Equal("0.2.0-beta.2", reloaded.PendingVersion);
        manager.FailCheck = true;
        await reloaded.CheckAsync();
        Assert.Equal("0.2.0-beta.2", reloaded.PendingVersion);
        reloaded.Configure(ReleaseChannel.Stable, false);
        Assert.Null(reloaded.PendingVersion);
        using var changed = Create(manager, preferences);
        Assert.Equal(ReleaseChannel.Stable, changed.Channel);
        Assert.False(changed.Automatic);
        Assert.Null(changed.PendingVersion);
    }

    [Theory]
    [InlineData("0.2.0-alpha.2", "DolphinTASStudio", "update.nupkg")]
    [InlineData("0.1.0-beta.1", "DolphinTASStudio", "update.nupkg")]
    [InlineData("0.2.0-beta.2", "AnotherApp", "update.nupkg")]
    [InlineData("0.2.0-beta.2", "DolphinTASStudio", "../update.nupkg")]
    public async Task UnexpectedOrOlderFeedAssetsAreRejectedBeforeDownload(string version, string packageId, string filename)
    {
        using var files = new TestWorkspace();
        var manager = new FakeManager(files.FilePath("updates"));
        manager.Asset = manager.Asset with { Version = SemanticVersion.Parse(version), PackageId = packageId, FileName = filename };
        using var service = Create(manager, files.FilePath("preferences.json"));
        await service.CheckAsync();
        Assert.Null(service.PendingVersion);
        Assert.Equal(0, manager.Downloads);
        Assert.Contains("failed", service.Status);
    }

    [Fact]
    public async Task DamagedDownloadCannotBeQueuedOrApplied()
    {
        using var files = new TestWorkspace();
        var manager = new FakeManager(files.FilePath("updates")) { DamageDownload = true };
        using var service = Create(manager, files.FilePath("preferences.json"));
        await service.CheckAsync();
        Assert.Null(service.PendingVersion);
        manager.DamageDownload = false;
        await service.CheckAsync();
        Assert.NotNull(service.PendingVersion);
        File.WriteAllText(Path.Combine(manager.Folder, manager.Asset.FileName), "damaged");
        using var activity = new RuntimeActivity(files.FilePath("activity"), () => false);
        Assert.False(service.ApplyAfterShutdown(activity, restart: true));
        Assert.Contains("checksum", service.Status);
    }

    [Fact]
    public async Task AChannelChangeInAnotherWindowPreventsApplyingAStaleSelection()
    {
        using var files = new TestWorkspace();
        var manager = new FakeManager(files.FilePath("updates"));
        var preferences = files.FilePath("preferences.json");
        var managerRequests = 0;
        using var first = new AppUpdates(_ => { managerRequests++; return manager; }, preferences,
            manager.Folder, "0.2.0-beta.1", ReleaseChannel.Beta);
        await first.CheckAsync();
        using (var second = Create(manager, preferences)) second.Configure(ReleaseChannel.Stable, true);
        using var activity = new RuntimeActivity(files.FilePath("activity"), () => false);
        Assert.False(first.ApplyAfterShutdown(activity, restart: false));
        Assert.Equal(1, managerRequests); // Only the original check reached Velopack.
        Assert.Contains("another Studio window", first.Status);
    }

    [Fact]
    public async Task DevelopmentBuildDoesNotContactUpdateSource()
    {
        using var files = new TestWorkspace();
        var manager = new FakeManager(files.FilePath("updates"));
        using var service = Create(manager, files.FilePath("preferences.json"), enabled: false);
        await service.CheckAsync();
        Assert.Equal(0, manager.Checks);
    }

    [Fact]
    public void InvalidPreferencesFallBackToInstalledChannel()
    {
        using var files = new TestWorkspace();
        var preferences = files.FilePath("preferences.json");
        File.WriteAllText(preferences, "{incomplete");
        using var service = Create(new FakeManager(files.FilePath("updates")), preferences);
        Assert.Equal(ReleaseChannel.Beta, service.Channel);
        Assert.True(service.Automatic);
    }

    [Fact]
    public void RuntimeLeaseDefersReplacementUntilEveryWorkerHasExited()
    {
        using var files = new TestWorkspace();
        var path = files.FilePath("activity");
        var updaterRunning = false;
        using var studio = new RuntimeActivity(path, () => updaterRunning);
        using (var worker = new RuntimeActivity(path, () => updaterRunning))
        {
            Assert.False(studio.CanApply());
            Assert.False(studio.TryStartUpdate(() => updaterRunning = true));
            Assert.False(updaterRunning);
        }
        Assert.True(studio.CanApply());
        Assert.True(studio.TryStartUpdate(() => updaterRunning = true));
        Assert.Throws<IOException>(() => new RuntimeActivity(path, () => updaterRunning));
        // Velopack's post-update restart happens after files have been replaced.
        using var restarted = new RuntimeActivity(path, () => updaterRunning, launchedByUpdater: true);
    }

    private static AppUpdates Create(FakeManager manager, string preferences, bool enabled = true) =>
        new(_ => manager, preferences, manager.Folder, "0.2.0-beta.1", ReleaseChannel.Beta, enabled);

    private sealed class FakeManager : UpdateManager
    {
        private static readonly byte[] Content = "a complete update payload"u8.ToArray();
        public string Folder { get; }
        public VelopackAsset Asset { get; set; } = new()
        {
            PackageId = AppUpdates.PackageId, Version = SemanticVersion.Parse("0.2.0-beta.2"),
            FileName = "update.nupkg", Type = VelopackAssetType.Full,
            Size = Content.Length, SHA256 = Convert.ToHexString(SHA256.HashData(Content))
        };
        public bool FailCheck, DamageDownload;
        public int Checks, Downloads;
        public FakeManager(string folder) : base(new SimpleFileSource(new DirectoryInfo(folder)),
            locator: new TestVelopackLocator(AppUpdates.PackageId, "0.2.0-beta.1", folder))
        { Folder = folder; Directory.CreateDirectory(folder); }
        public override Task<UpdateInfo?> CheckForUpdatesAsync()
        {
            Checks++;
            if (FailCheck) throw new IOException("Offline");
            return Task.FromResult<UpdateInfo?>(new UpdateInfo(Asset, false));
        }
        public override Task DownloadUpdatesAsync(UpdateInfo updates, Action<int>? progress = null, CancellationToken cancelToken = default)
        {
            Downloads++;
            File.WriteAllBytes(Path.Combine(Folder, Asset.FileName), DamageDownload ? [0] : Content);
            return Task.CompletedTask;
        }
    }
}
