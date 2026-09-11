using System.Diagnostics;
using System.Text;

namespace TasStudio.Emulation;

public enum DolphinMovieExportMode { Quick, Full }
public enum DolphinMovieExportStage { Preparing, CheckingCache, Baking, Restoring, MemoryCards, Writing }
public sealed record DolphinMovieExportProgress(DolphinMovieExportStage Stage, int CompletedGroups, int TotalGroups);

public sealed partial class ExecutionService
{
    public Task<DolphinMovieExportResult> ExportDolphinMovieAsync(string path,
        IProgress<DolphinMovieExportProgress>? progress = null,
        DolphinMovieExportMode mode = DolphinMovieExportMode.Full) => Enqueue(() =>
    {
        RequireProject(); Pause();
        if (_projectStart?.Kind != ProjectStartKind.PowerOn)
            throw new InvalidOperationException("Dolphin movie export requires a power-on project.");
        if (_events.Count != 0)
            throw new InvalidOperationException("This experimental export does not support memory writes or reset events.");
        if (_inputs.Count == 0) throw new InvalidOperationException("Record some inputs before exporting a Dolphin movie.");
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));

        var completed = 0;
        void Report(DolphinMovieExportStage stage) => progress?.Report(new(stage, completed, _inputs.Count));
        Report(DolphinMovieExportStage.Preparing);
        InputPollFrame[] frames = [];
        if (mode == DolphinMovieExportMode.Quick)
        {
            Report(DolphinMovieExportStage.CheckingCache);
            // Fail before touching the emulator or destination; Quick never silently bakes.
            frames = RequireDolphinMoviePolls(mode);
        }
        var snapshot = _backend.Capture();
        var executedHistory = _executedHistory;
        DolphinMovieOrigin origin;
        DolphinMovieSettings movieSettings;
        try
        {
            // Read the original boot boundary, including the disc header copied to MEM1 by Dolphin.
            _backend.Restore(_initial!);
            _executedHistory = _history!.At(0);
            var ticks = _backend.EmulatedTicks ?? throw new NotSupportedException("The backend cannot report exact movie timing.");
            var header = _backend.ReadMemory(0x80000000, 32);
            if (header.Length != 32 || !header.AsSpan(28, 4).SequenceEqual(new byte[] { 0xC2, 0x33, 0x9F, 0x3D }))
                throw new NotSupportedException("Dolphin movie export currently supports GameCube disc games only.");
            origin = new(Encoding.ASCII.GetString(header, 0, 6), ticks, _backend.VideoFieldCount);
            movieSettings = DolphinMovieSettings.Create(
                _options!.Configuration ?? EmulationConfiguration.FromLegacy(_options.InternalResolution, _options.DspHle),
                origin.GameId, header[7], Path.Combine(_options.SystemDirectory, "dolphin-emu", "Sys", "GameSettings"),
                Path.Combine(_options.SaveDirectory, "User", "GameSettings"));

            if (mode == DolphinMovieExportMode.Full)
            {
                // Bake every group against the original baseline. Do not skip groups through checkpoints.
                _suppressMedia = true;
                var interruptVersion = Volatile.Read(ref _seekInterruptVersion);
                var progressTimer = Stopwatch.StartNew();
                Report(DolphinMovieExportStage.Baking);
                for (var index = 0; index < _inputs.Count; index++)
                {
                    if (interruptVersion != Volatile.Read(ref _seekInterruptVersion))
                        throw new OperationCanceledException("Dolphin movie export interrupted.");
                    ExecuteInputGroup(index, _inputs[index]);
                    _executedHistory = _history.At((ulong)index + 1);
                    completed = index + 1;
                    // Keep UI updates bounded even when the emulator runs far faster than real time.
                    if (completed == _inputs.Count || progressTimer.ElapsedMilliseconds >= 100)
                    {
                        Report(DolphinMovieExportStage.Baking);
                        progressTimer.Restart();
                    }
                }
                frames = RequireDolphinMoviePolls(mode);
            }
        }
        finally
        {
            Report(DolphinMovieExportStage.Restoring);
            _suppressMedia = false;
            _executedHistory = null;
            _backend.Restore(snapshot);
            _executedHistory = executedHistory;
            Publish();
        }

        GameCubeMovieCard[] cards = [];
        InputPollFrame? bootInputs = null;
        if (_backend is IGameCubeMovieBootBackend bootBackend)
        {
            Report(DolphinMovieExportStage.MemoryCards);
            try
            {
                var boot = bootBackend.ExportMovieBoot(_initial!, GamePath!, _options!);
                cards = boot.Cards;
                bootInputs = boot.Inputs;
            }
            catch
            {
                _loaded = _backend.IsLoaded;
                _executedHistory = null;
                Publish();
                throw;
            }
        }
        Report(DolphinMovieExportStage.Writing);
        var result = DolphinMovieExport.Save(path, origin, frames,
            _options!.Configuration ?? EmulationConfiguration.FromLegacy(_options.InternalResolution, _options.DspHle),
            GamePath!, _gameHash, _backend.Identity, cards, movieSettings, bootInputs, mode);
        Notify("Dolphin movie exported — see the accompanying playback instructions");
        return result;
    });

    private InputPollFrame[] RequireDolphinMoviePolls(DolphinMovieExportMode mode)
    {
        var frames = new InputPollFrame[_inputs.Count];
        for (var index = 0; index < frames.Length; index++)
        {
            if (!_pollFrames.TryGetValue(index, out var record) || record.Index != index ||
                record.Frame.Input != _inputs[index] || record.PrefixHash != _history!.At((ulong)index))
                throw new InvalidDataException(mode == DolphinMovieExportMode.Quick
                    ? $"Quick export requires current cached polls for every input group. Group {index + 1:N0} has missing or stale polls. Use Export to Dolphin → Full."
                    : $"Input group {index + 1:N0} does not have a current controller poll recording.");
            record.Frame.Validate();
            frames[index] = record.Frame;
        }
        return frames;
    }
}
