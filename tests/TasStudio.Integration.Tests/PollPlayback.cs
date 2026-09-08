using System.Security.Cryptography;
using System.Text.Json;
using TasStudio.Core;
using TasStudio.Dolphin;
using TasStudio.Emulation;

internal static class PollPlayback
{
    public static async Task Run(string rom, string output, bool restore)
    {
        var backend = new DolphinBackend(); using var service = new ExecutionService(backend);
        VideoFrame? preview = null; service.VideoReady += frame => preview = frame;
        string Pixels() => preview == null ? throw new Exception("No preview") : $"{preview.Width}x{preview.Height}:" + Convert.ToHexString(SHA256.HashData(preview.Rgba));
        var options = new BackendOptions(Path.Combine(AppContext.BaseDirectory, "native", "dolphin_libretro.dll"),
            Path.Combine(AppContext.BaseDirectory, "system"), Path.Combine(output, restore ? "restore-profile" : "profile"))
            { Configuration = new EmulationConfiguration() };
        var project = Path.Combine(output, "polls.tasproj");
        async Task<string> Ram() => Convert.ToHexString(SHA256.HashData(await service.ReadMemoryAsync(0x80000000, 24 * 1024 * 1024)));
        if (!restore)
        {
            await service.CreateProjectAsync(project, rom, options);
            for (var i = 0; i < 120; i++) await service.AdvanceFrameAsync(ControllerState.Neutral);
            var boundaries = service.PollBoundaries.ToArray();
            if (boundaries[^1] <= 120) throw new Exception("No multi-poll frames recorded.");
            var original = await Ram();
            await service.SaveProjectAsync(project);
            await service.LoadProjectAsync(project, options);
            if (await Ram() != original || !service.PollBoundaries.SequenceEqual(boundaries)) throw new Exception("Same-process poll playback diverged.");
            await service.SaveNamedStateAsync("End");
            var input = ControllerState.Neutral with { Buttons = PadButtons.A, StickX = 200 };
            await service.SetInputAsync(20, input);
            var edited = (await service.GetPollFrameAsync(20))!;
            if (edited.Frame.Polls.Length < 2 || edited.Frame.Polls.Any(p => p.Input != input) || service.StateMarkers.Count != 0)
                throw new Exception("Frame edit did not rewrite all polls and invalidate dependent states.");
            await service.SeekAsync(120); await service.SaveProjectAsync(project);
            File.WriteAllText(Path.Combine(output, "ram.txt"), await Ram());
            File.WriteAllText(Path.Combine(output, "pixels.txt"), Pixels());
            File.WriteAllText(Path.Combine(output, "polls.json"), JsonSerializer.Serialize(service.PollBoundaries));
            Console.WriteLine($"Saved {service.Position} groups, {service.PollBoundaries[^1]} polls, VI {service.VideoFieldCount}.");
        }
        else
        {
            await service.LoadProjectAsync(project, options);
            if (await Ram() != File.ReadAllText(Path.Combine(output, "ram.txt"))) throw new Exception("Fresh-process poll playback diverged.");
            if (Pixels() != File.ReadAllText(Path.Combine(output, "pixels.txt"))) throw new Exception("Fresh-process preview diverged.");
            if (!service.PollBoundaries.SequenceEqual(JsonSerializer.Deserialize<ulong[]>(File.ReadAllText(Path.Combine(output, "polls.json")))!))
                throw new Exception("Fresh-process poll positions diverged.");
            // A valid container with deliberately wrong poll timing must be rejected by the core.
            var content = FolderProject.Load(project); var records = content.Archive.Metadata.PollFrames.ToArray();
            var record = records.First(r => r.Frame.Polls.Length > 0); var polls = record.Frame.Polls.ToArray();
            polls[0] = polls[0] with { TickOffset = polls[0].TickOffset + 1 };
            records[Array.IndexOf(records, record)] = record with { Frame = record.Frame with { Polls = polls } };
            var bad = Path.Combine(output, "bad-timing.tasproj");
            FolderProject.Save(bad, content.Archive.Metadata with { PollFrames = records }, content.Archive.InitialState, [], [], []);
            var rejected = false;
            try { await service.LoadProjectAsync(bad, options); }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
            { rejected = true; Console.WriteLine("Rejected altered poll timing: " + error.Message); }
            if (!rejected || !service.IsPreviewCurrent || await Ram() != File.ReadAllText(Path.Combine(output, "ram.txt")))
                throw new Exception("Desync detection or session rollback failed.");
            // Verify that playback supplies each poll's own bytes, not just the group's held input.
            records = content.Archive.Metadata.PollFrames.ToArray();
            record = records.First(r => r.Index > 0 && r.Frame.Polls.Length >= 4);
            polls = record.Frame.Polls.Select((p, i) => p with { Input = p.Input with { CStickX = (byte)(128 + i % 2) } }).ToArray();
            records[Array.IndexOf(records, record)] = record with { Frame = record.Frame with { Polls = polls } };
            var varied = Path.Combine(output, "varied-polls.tasproj");
            FolderProject.Save(varied, content.Archive.Metadata with { PollFrames = records, Position = (ulong)record.Index + 1 }, content.Archive.InitialState, [], [], []);
            await service.LoadProjectAsync(varied, options);
            if (backend.LastInputPollFrame == null || !backend.LastInputPollFrame.Polls.SequenceEqual(polls))
                throw new Exception("Playback did not deliver distinct per-poll inputs.");
            Console.WriteLine("Verified distinct controller bytes within one frame group.");
        }
        Console.WriteLine("PASS: per-poll persistence, grouped editing, and validated playback.");
    }
}
