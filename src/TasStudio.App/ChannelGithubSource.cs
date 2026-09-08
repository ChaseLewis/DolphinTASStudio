using System.Text.Json;
using Velopack.Sources;

namespace TasStudio.App;

internal sealed class ChannelGithubSource : GithubSource
{
    private readonly string _feed;
    private readonly Func<int, Task<string>> _readPage;

    public ChannelGithubSource(ReleaseChannel channel, Func<int, Task<string>>? readPage = null)
        : base(AppUpdates.Repository, null, channel != ReleaseChannel.Stable)
    {
        _feed = $"releases.{channel.Feed()}.json";
        _readPage = readPage ?? (page => Downloader.DownloadString(
            $"https://api.github.com/repos/ChaseLewis/DolphinTASStudio/releases?per_page=100&page={page}",
            GetRequestHeaders("application/vnd.github+json")));
    }

    protected override Task<GithubRelease[]> GetReleases(bool includePrereleases) => ReadReleases(includePrereleases);

    internal async Task<GithubRelease[]> ReadReleases(bool includePrereleases)
    {
        // Velopack's default source only looks at ten releases. Frequent alpha releases must
        // not hide an older stable/beta release. Inspect every page, then let SemVer choose.
        var matches = new List<GithubRelease>();
        for (var page = 1; page <= 50; page++)
        {
            var releases = JsonSerializer.Deserialize<GithubRelease[]>(await _readPage(page).ConfigureAwait(false))
                ?? throw new InvalidDataException("GitHub returned an empty release response.");
            matches.AddRange(releases.Where(r => (includePrereleases || !r.Prerelease) &&
                r.Assets.Any(a => a.Name == _feed)));
            if (releases.Length < 100) return matches.ToArray();
        }
        throw new IOException("The release history is too large to check in one request. Update Studio from GitHub Releases.");
    }
}
