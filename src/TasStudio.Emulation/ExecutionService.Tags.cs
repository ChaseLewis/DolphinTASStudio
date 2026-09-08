namespace TasStudio.Emulation;

public sealed record TimelineTag(string Id, ulong Position, string Name);

public sealed partial class ExecutionService
{
    private readonly List<TimelineTag> _tags = [];
    private TimelineTag[] _publishedTags = [];
    public IReadOnlyList<TimelineTag> Tags => Array.AsReadOnly(Volatile.Read(ref _publishedTags));
    private static string TagName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 40 || name.Any(char.IsControl)) throw new ArgumentException("Tag names must contain 1–40 characters on one line.", nameof(name));
        return name;
    }
    public Task<string> AddTagAsync(ulong position, string name) => Enqueue(() =>
    {
        RequireProject();
        if (position > (ulong)_inputs.Count) throw new ArgumentOutOfRangeException(nameof(position));
        if (_tags.Count >= 10000) throw new InvalidOperationException("Project tag limit reached.");
        var tag = new TimelineTag(Guid.NewGuid().ToString("N"), position, TagName(name));
        _tags.Add(tag); Interlocked.Increment(ref _revision); Notify("Tag added"); return tag.Id;
    });
    public Task RenameTagAsync(string id, string name) => Enqueue(() =>
    {
        RequireProject(); var index = _tags.FindIndex(tag => tag.Id == id);
        if (index < 0) throw new InvalidOperationException("Tag was removed.");
        _tags[index] = _tags[index] with { Name = TagName(name) };
        Interlocked.Increment(ref _revision); Notify("Tag renamed");
    });
    public Task RemoveTagAsync(string id) => Enqueue(() =>
    {
        RequireProject();
        if (_tags.RemoveAll(tag => tag.Id == id) == 0) return;
        Interlocked.Increment(ref _revision); Notify("Tag removed");
    });
}
