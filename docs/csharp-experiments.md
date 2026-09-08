# Writing C# experiments

This is the authoring contract for humans and AI assistants. Read this alongside
the [actual SDK](../src/TasStudio.Sdk/Experiments.cs) and the compilable
[PulseExperiment](../examples/csharp/Basic.Experiments/PulseExperiment.cs).
Do not infer APIs from old screenshots or historical design documents.

## What an experiment is

An experiment is a .NET 10 class library containing one selected public class that
implements `IExperiment<TResult>`. Each trial constructs a fresh instance in a
separate worker process. It can choose boot parameters, observe/advance its own
emulator, edit its own inputs, and return one typed result.

The batch coordinator builds/publishes your library once, snapshots its dependencies
and the saved source project, discovers the result schema, and schedules trial
indices with bounded parallelism. Workers never write SQLite directly. The
coordinator commits their returned results serially.

There is no embedded script language or editor. Use ordinary C# files, normal class
libraries, ProjectReferences, and your editor's C# language support. Trusted C# has
normal filesystem/network access; worker isolation protects Studio's emulator and
timeline, not the host computer from arbitrary experiment code.

## Create a workspace

1. Run `just build` in the Studio repository.
2. Save your input recording with **Ctrl+S** in Studio. This is a `.tasproj`, not
   **Export Replay**, a DTM file, or a video.
3. Install the local [VS Code extension](../extensions/tas-studio/README.md).
4. Set `tasStudio.workerPath` to the absolute path of the published
   `artifacts/prod/TasStudio.Worker.exe`.
5. Run **TAS Studio: New C# Experiment Workspace** and select the project and a new
   workspace folder. Alternatively:

```powershell
& ./artifacts/prod/TasStudio.Worker.exe new-csharp 'D:/TAS/My run/movie.tasproj' 'D:/TAS/Scripts'
```

The generated workspace contains `Experiments.csproj`, `Experiment.cs`,
`experiment.tascsharp.json`, and VS Code settings. Its SDK references point at the
build that generated it. Move/update those references if you relocate the build.
Do not target a worker in intermediate `bin/Release` output: it lacks the complete
native runtime and system resources.

## Configuration reference

```json
{
  "Name": "UTC search",
  "SourceProject": "D:/TAS/My run/movie.tasproj",
  "Project": "Experiments.csproj",
  "AssemblyName": "Experiments",
  "TypeName": "Experiment",
  "Count": 25000,
  "Parallelism": 4,
  "Headless": true,
  "TimeoutSeconds": 300,
  "Start": "Boot",
  "StateId": null,
  "StartUtcSeconds": null,
  "PrerollSeconds": 0,
  "PrerollGroups": 0,
  "Parameters": { "PulseGroups": 60, "MaximumSeconds": 300 }
}
```

| Field | Meaning |
| --- | --- |
| `Name` | Human-readable batch name |
| `SourceProject` | Saved `.tasproj`; absolute or relative to this config file |
| `Project` | C# `.csproj`; absolute or relative to this config file |
| `AssemblyName` | Output DLL's simple name, without `.dll` |
| `TypeName` | Full public class name, including namespace when present |
| `Count` | Positive `int`; indices are `0..Count-1`. No artificial 10,000-trial cap |
| `Parallelism` | 1–4 worker processes; default 2 |
| `Headless` | Default true. False opens silent, read-only preview windows for active workers |
| `TimeoutSeconds` | 1–86400, default 300; wall-clock limit for each worker process |
| `Start` | `Boot` or `SaveState`; default `Boot` |
| `StateId` | Required for SaveState; `project-start` for the embedded baseline, or a marker ID from the saved source project's `States` or `AutomaticCheckpoints` |
| `StartUtcSeconds` | Optional default boot UTC; Initialize can override it |
| `PrerollSeconds` | Replay recorded input until at least this much emulated time has passed; default 0 |
| `PrerollGroups` | Replay this many recorded frame groups; default 0 |
| `Parameters` | JSON object deserialized into your own parameter type |

Choose seconds **or** groups for preroll, not both. Preroll fails if it runs past the
recorded movie. A seconds limit stops at a group boundary, so it can overshoot slightly.
Use `"Start": "SaveState", "StateId": "project-start"` to run an experiment from
the embedded baseline of a project made from a manually captured `.tasstate`.
The workspace generator selects this automatically for captured/save-state projects.
`Boot` explicitly restarts from power-on and does not restore that fixture's progress.
See [Play mode and fixture capture](../README.md#playing-by-hand-and-making-test-fixtures).

Other `StateId` values are Studio marker IDs, not filenames or numbered Dolphin slots. Use a
compatible Studio state; arbitrary standalone Dolphin state formats are not accepted.

Parameter deserialization uses `System.Text.Json` defaults. Match property names and
types; use numeric enum values unless you explicitly configure your own converter.
The config's `Start` field is a separate runner enum and accepts the strings above.

## Lifecycle and contexts

```csharp
public sealed record Parameters(int VariantsPerUtc = 30);
public sealed record SearchResult(long Utc, int Variant, double Seconds);

public sealed class Experiment : IExperiment<SearchResult>
{
    private int variant;

    public ExperimentBootOptions Initialize(ExperimentInitializationContext context)
    {
        var options = context.GetParameters<Parameters>();
        if (options.VariantsPerUtc < 1) throw new ArgumentException("VariantsPerUtc must be positive.");
        variant = context.Index % options.VariantsPerUtc;
        return context.StartingPoint == ExperimentStartingPoint.PowerOn
            ? new(checked(context.DefaultStartUtcSeconds + context.Index / options.VariantsPerUtc))
            : new();
    }

    public async Task<SearchResult> RunAsync(ExperimentRunContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Use context.Emulator here; it does not exist in Initialize.
        var position = await context.Emulator.GetPositionAsync();
        return new(await context.Emulator.GetStartUtcAsync(), variant, position.Seconds);
    }
}
```

Use `using TasStudio.Sdk;` and, when editing inputs, `using TasStudio.Core;`.

**Before boot:** `Initialize(ExperimentInitializationContext)` runs once per trial,
on the same instance later used for `RunAsync`. Its context exposes only `Index`,
`Count`, `Parameters`, `StartingPoint`, `DefaultStartUtcSeconds`, and
`CancellationToken`. Return `ExperimentBootOptions` with optional UTC seconds.
Null means preserve the configured default. Valid UTC is Unix seconds from 0 through
`uint.MaxValue`. Save-state trials must return no UTC override: their saved clock is
part of their state. Create memory/game wrappers in RunAsync, after boot.

**After boot/restoration/preroll:** `RunAsync(ExperimentRunContext, CancellationToken)`
gets `Emulator`, immutable `Movie`, `Initialization`, `Index`, `Count`, `Parameters`,
`GetParameters<T>()`, and `Log(object?)`. Return one non-null `TResult` on completion.

Result schema discovery happens once per batch, before launching workers, without
constructing an experiment or calling Initialize. There is no per-trial schema
registration and no custom SQL writer to put in Initialize.

Index is stable across parallel scheduling and resume. It is not a worker number.
Division/remainder can decode a parameter grid as in the example. Increasing Count
does not alter an existing batch: a new config run creates a new frozen batch.

## Time, input groups, and the movie

`context.Movie` is an immutable `IReadOnlyList<ControllerState>` containing the
original active input intent, one item per **frame group**, frozen before preroll or
worker edits. ControllerState is a value type. Copying/modifying a value cannot edit
the movie. The original project also contains canonical poll recordings, which the
emulator uses internally; `Movie` is not that raw poll stream.

`GetPositionAsync()` returns:

| Property | Unit/meaning |
| --- | --- |
| `Group` | Zero-based execution boundary, before that group's input |
| `VideoField` | Actual emulator video-field counter |
| `Seconds` | Emulated elapsed time relative to the project baseline |
| `InputCount` | Current worker timeline's group count, including authored unadvanced input |
| `IsCurrent` | Whether the preview matches its input/event history |

One advance runs until the next presented frame. The number of video fields/polls
can vary. Do not assume 60 advances per second, or convert a group index to time
with a fixed division. Measure differences in `Seconds`. Waiting with `Task.Delay`
does not advance the game.

The source project is copied when the batch starts. Unsaved changes in Studio are
not included. Later edits to Studio, the source project, or current script files do
not update a running/resumed batch. The selected worker's mutable timeline is a
separate object from both the source session and `context.Movie`.

## Playback versus script takeover

This distinction is essential:

| API | Existing recorded input | At the unrecorded end |
| --- | --- | --- |
| `PlayAsync()` | Execute one recorded group | Return false; append nothing |
| `AdvanceAsync(input, count)` | Preserve and execute existing input | Fill missing groups using input (neutral if omitted) |
| `SetCurrentInputAsync(input)` | Replace the current group's input without advancing | Append authored input without advancing |
| `SetInputAsync(group, input)` | Edit the specified existing group | May append exactly at InputCount; cannot leave gaps |

`AdvanceAsync(A)` **does not override recorded movie input**. To drive the next group
explicitly, set it first:

```csharp
var input = ControllerState.Neutral with { Buttons = PadButtons.A };
await context.Emulator.SetCurrentInputAsync(input);
await context.Emulator.AdvanceAsync();
```

Use `ControllerState.Neutral` rather than `default(ControllerState)`: centered sticks
are 128, not zero. Buttons use the `PadButtons` flags enum. StickX/Y, CStickX/Y and
TriggerL/R use raw bytes 0–255; neutral triggers are zero.

To play the original recording then take over, see the complete
[PulseExperiment](../examples/csharp/Basic.Experiments/PulseExperiment.cs).
To take over **early**, stop calling PlayAsync once your game-specific condition is
observed, latch that decision, then explicitly author each executed worker group.
Do not switch back to movie playback merely because the game leaves that state.

For example, with the provided Skies reader in a bounded loop:

```csharp
bool takenOver = false;
bool pressNext = true;
// Each iteration: check cancellation and your emulated-time limit first.
var state = await skies.ReadBattleStateAsync();
takenOver |= state == BattleState.ActionSelect;
if (!takenOver)
{
    if (!await context.Emulator.PlayAsync())
        await context.Emulator.AdvanceAsync(ControllerState.Neutral);
}
else
{
    bool selecting = state is BattleState.ActionSelect or BattleState.FinishActionSelect;
    var input = ControllerState.Neutral with
    {
        Buttons = selecting && pressNext ? PadButtons.A : PadButtons.None
    };
    await context.Emulator.SetCurrentInputAsync(input);
    await context.Emulator.AdvanceAsync();
    pressNext = !selecting || !pressNext;
}
```

The booleans live **outside** the loop. This alternates A/neutral continuously through
both selection states, releases controls elsewhere, and restarts with A on re-entry.
Battle-state detection and addresses are game/revision-specific; do not invent them
for another game. Observations happen at group boundaries, not every guest instruction.

## Emulator API reference

All operations marshal to the worker emulator's owner thread. Await them in sequence;
do not share one emulator between concurrent Task.Run/WhenAll strategies. Parallelize
independent trials through the coordinator instead.

| Method | Behavior |
| --- | --- |
| `GetPositionAsync()` | Get the timing/validity fields above |
| `GetStartUtcAsync()` | Get the worker's configured start UTC |
| `GetInputAsync(int group)` | Read worker input; exactly at InputCount returns neutral |
| `SetInputAsync(int group, ControllerState input)` | Author one group; earlier edits make the preview stale |
| `SetCurrentInputAsync(ControllerState input)` | Author current boundary atomically without advancing |
| `AdvanceAsync(ControllerState? input = null, int count = 1)` | Advance 1–100000 groups, preserving existing recording |
| `PlayAsync()` | Play one recorded group; false at end |
| `SeekAsync(ulong group)` | Restore a valid baseline/state and replay to the worker group |
| `SaveStateAsync(string name)` | Save a worker-owned named state; return its ID |
| `LoadStateAsync(string id)` | Restore a valid worker state/marker |
| `DeleteStateAsync(string id)` | Delete a worker marker and owned state file; unknown/deletion failures throw |
| `GetStatesAsync()` | Snapshot of IDs, names, groups and validity |
| `AddTakeAsync(string name, int startGroup, IEnumerable<ControllerState> inputs)` | Add a worker candidate take without applying it; 1–100000 inputs |
| `ReadMemoryAsync(uint address, int count)` | Read 1–1048576 guest bytes |
| `WriteMemoryAsync(uint address, byte[] bytes)` | Write guest bytes and record a worker execution event |

Earlier input edits invalidate dependent states and the current preview. Advance
does not secretly rewind to repair that preview; call Seek explicitly. Editing the
input at the current boundary is valid because it has not executed yet.

State load does not rewind your C# variables or restore an independent version of
the future worker inputs. Keep your algorithm's own state consistent with restores.
A state whose preceding history changed cannot be used as a valid baseline.

### Bounded save-state ownership

States are disk-backed, with temporary capture/restore buffers in memory. There is
no automatic guarantee that an unlimited SaveStateAsync loop has bounded disk use.
Hold only the IDs you need and delete replaced states explicitly:

```csharp
string? previous = null;
try
{
    // Inside your bounded search loop, after advancing:
    var current = await context.Emulator.SaveStateAsync("rolling baseline");
    if (previous != null) await context.Emulator.DeleteStateAsync(previous);
    previous = current;
}
finally
{
    if (previous != null && !token.IsCancellationRequested)
        await context.Emulator.DeleteStateAsync(previous);
}
```

During cancellation, SDK calls can throw cancellation exceptions; normal coordinator
cleanup removes committed cancelled/successful worker directories after exit. Failed
or timed-out trials retain their artifacts for diagnosis, including remaining states.
No worker state operation changes the source project's files.

## Reusable game libraries and memory access

Put game addresses, layouts, enum decoding, and pointer traversal in a class library.
The experiment should read concepts such as battle state or inventory, not repeat
literal memory addresses throughout its control flow. Reference that project normally:

```xml
<ItemGroup>
  <ProjectReference Include="../GameUtilities/GameUtilities.csproj" />
</ItemGroup>
```

Construct a wrapper with `context.Emulator` inside RunAsync. Return typed snapshots
and use `Unknown` enum values for unrecognized game states where that is meaningful.
Treat unreadable enemy lists as unknown, not as zero living enemies. Return expected
negative search outcomes as data; reserve exceptions for errors that need diagnostics.

The generic SDK's [MemoryExtensions](../src/TasStudio.Sdk/MemoryExtensions.cs) provide
big-endian ReadU8/S8/U16/S16/U32/S32/F32/F64Async and WriteU8/U16/U32Async. More
specialized pointer/packed reads currently live in the example Skies library's
[GameCubeMemoryReader](../examples/csharp/Skies/GameCubeMemoryReader.cs), not in
IExperimentEmulator itself:

```csharp
var skies = new Skies.SkiesGame(context.Emulator);
var battle = await skies.ReadBattleStateAsync();
var inventory = await skies.ReadInventoryAsync();
var enemies = await skies.ReadCurrentEncounterAsync();
```

The [Skies guide](../examples/csharp/Skies/README.md) documents supported readers and
revision-specific assumptions. `ResolveAddressAsync(address, offsets)` dereferences
one big-endian pointer per offset, then adds that offset. Empty offsets read the
direct address. `ReadPackedAsync<T>`/`ReadPackedArrayAsync<T>` require unmanaged types
and copy raw bytes: **they do not convert guest endianness automatically**. Packed
structs must expose decoded fields/properties explicitly. Use the InventorySlot
implementation as a reference. Validate layout/size and bound array lengths.

## Typed SQLite output

Declare your columns by returning a record/struct/class from `IExperiment<TResult>`:

```csharp
public enum SearchOutcome { Found, NotFoundWithinLimit }
public readonly record struct Details(int[] EnemyHp, uint Seed);
public sealed record SearchResult(
    SearchOutcome Outcome,
    long StartUtcSeconds,
    double EncounterSeconds,
    int DropCount,
    bool FirstDrop,
    Details Details);
```

No experiment-side database connection is needed. `trial_index` is injected as the
primary key; do not declare a result member with that name. Public properties and
fields become columns. `[JsonPropertyName("drop_count")]` controls the column name.
Members marked JsonIgnore are omitted according to the schema's supported rules.

| C# value | SQLite storage |
| --- | --- |
| Boolean, signed/unsigned integer through Int64 | INTEGER (bool 0/1) |
| Enum | Underlying numeric value |
| Float/double | REAL |
| String/char/GUID/date/time | TEXT |
| UInt64 and decimal | TEXT, preserving their full range/precision |
| Nested structs, objects, arrays, dictionaries | JSONB BLOB |
| Nullable member | Nullable column; otherwise NOT NULL |

For enums backed by UInt64, storage follows UInt64. Avoid depending on lexicographic
TEXT ordering for wide numeric fields. Nested JSONB requires a SQLite client with
JSONB support; current Microsoft.Data.Sqlite uses SQLite that supports it.

The runner transaction saves both the `trials` receipt/status and the successful
typed `results` row. Failed/cancelled/timed-out trials have no typed success row.
Returning `NotFoundWithinLimit` normally is still a completed trial with useful data;
throwing instead marks it failed and retains diagnostics. Choose this deliberately.

```sql
SELECT t.trial_index, r.DropCount, r.EncounterSeconds
FROM trials AS t JOIN results AS r USING (trial_index)
ORDER BY r.DropCount DESC, r.EncounterSeconds ASC;

SELECT trial_index, json_extract(Details, '$.Seed') AS seed
FROM results;

SELECT status, COUNT(*) FROM trials GROUP BY status;
```

Your result type determines the names in these queries. A client can query completed
results while a batch runs; the coordinator uses WAL and serial writes. Keep that
database read-only from experiment code. Do not bypass the coordinator's writer.

`context.Log(...)` adds structured diagnostic JSON lines while the worker runs.
Logs are bounded to 100000 entries and 65536 characters per serialized entry. They
are not the durable success result: return the information you want to query.

## Cancellation, retention, and resume

- Successful and cancelled trial folders are removed **after a successful SQLite
  commit and worker exit**. Successful typed trials do not export result timelines.
- Failed/timed-out trials keep diagnostics, profiles and saved result projects when
  available. A database write failure also preserves evidence for recovery.
- The batch keeps its source/compiled snapshots and SQLite database. Full result
  receipts in SQLite make committed trials independent of result.json files.
- Cancellation stops scheduling; only in-flight trials finish/cancel. Unstarted
  indices get no placeholder files/rows and remain pending.
- Resume reuses the exact saved definition and assembly. It skips finished trials
  independently of completion order. Interrupted/unstarted trials begin again from
  the configured boot/state start. Finished failures/timeouts are not automatically retried.
- Source edits do not update a resumed batch. Use a new batch to test changed code,
  parameters or input. New batches fingerprint the core/runtime; resume rejects a
  mismatch. Older batches without fingerprints report that limitation.
- One coordinator lock prevents concurrent run/resume/cleanup on the same batch.
- `clean-csharp <batch>` removes committed success artifacts and legacy synthetic
  cancellation placeholders in a stopped batch. It keeps actual interrupted work
  and failures. Do not manually delete shared snapshots needed for resume.

For an emulated-time bound, measure Seconds inside your script. TimeoutSeconds is
the separate wall-clock worker limit, including boot/initialization/preroll. Check
cancellation inside CPU-only loops as well as around emulator operations. An
unresponsive worker can be terminated after the cancellation grace period.

An AddTakeAsync result stays in the worker. With successful artifact cleanup enabled
it is not a retained main-project take or replay. There is currently no dedicated
successful-artifact retention flag or single-index rerun command in the public config.
Do not promise an AI-generated tool can open every successful run's movie afterward.

## AI authoring checklist

Before writing code, establish:

1. Which saved SourceProject, game revision and starting point to use.
2. Whether to vary UTC, another parameter, or a flattened grid; map it from Index.
3. How much recorded input to play and exactly when script takeover becomes permanent.
4. Which verified game wrapper/memory fields identify progress, success and failure.
5. The emulated-time limit and separate wall-clock timeout.
6. The typed result columns, units, enums, and genuinely optional values.

Generate a normal class library, a typed experiment, a config and small readable
helper methods. Use only SDK members documented here or present in the source. Do
not invent ROM addresses, a `context.Results` service, a `context.WorkerId`, direct
poll-index Movie entries, writable Initialize memory, or a settings API absent from
ExperimentBootOptions. Place reusable game reads in a separate wrapper/library.

Validate with a small Count first. Compile the experiment. Test state transitions
and exact input sequences with a fake emulator that preserves recorded input during
AdvanceAsync; otherwise a takeover bug can pass a misleading test. For game-dependent
behavior, use `Headless: false` on a small batch and inspect results. Compilation and
fake tests alone do not prove a game-specific memory map or encounter strategy.

Example request to an AI:

> Read docs/csharp-experiments.md and src/TasStudio.Sdk/Experiments.cs first. Create
> an IExperiment<MyResult> class library using the existing game utility wrapper.
> Start from the saved project, choose UTC from the trial index in Initialize,
> replay until the specified game state, then explicitly set each worker input.
> Bound the search by emulated time, honor cancellation, and return typed outcome,
> elapsed seconds and observations. Preserve context.Movie and the source project.
> Supply the .csproj, experiment.tascsharp.json, reusable helpers and a small test
> verifying the input sequence. Ask for missing game addresses rather than guessing.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Missing `native/dolphin_libretro.dll` | Use the worker in a complete published folder, not intermediate bin output |
| Inputs still follow the movie | SetCurrentInputAsync before AdvanceAsync when taking over |
| Preview predates edits | Seek after editing earlier worker input |
| State no longer valid | Preceding input/events/settings changed; use a matching baseline |
| Cannot change UTC | SaveState starts retain their clock; choose Boot to vary UTC |
| No results row | Inspect trials.status/error; exceptions and cancellation are not typed successes |
| No successful trial folder | Expected retention policy; query SQLite |
| Resume still uses old code | Expected frozen batch; create a new batch for changes |
| No new trials after resume | All indices may already be finished, including recorded failures |
| Screen stays hidden | Headless defaults true; false affects new batches, not a saved definition |
| Trace export missing | Normal core compiles tracing out; see the separate native trace build |
