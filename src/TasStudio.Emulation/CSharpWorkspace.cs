using System.Text.Json;
using System.Xml.Linq;

namespace TasStudio.Emulation;

public static class CSharpWorkspace
{
    public static void Create(string sourceProject, string destination, string? stateId = null, string? name = null, IEnumerable<string>? libraries = null)
    {
        sourceProject = Path.GetFullPath(sourceProject); destination = Path.GetFullPath(destination);
        if (!File.Exists(sourceProject)) throw new FileNotFoundException("Select a saved Studio project.", sourceProject);
        var source = FolderProject.Load(sourceProject);
        if (stateId != null) ProjectExperiments.RequireState(source, stateId);
        var capturedStart = source.Archive.Metadata.Start?.Kind is ProjectStartKind.SaveState or ProjectStartKind.CapturedState;
        if (Directory.Exists(destination)) throw new IOException("Choose a new workspace folder. Existing workspaces are preserved.");
        var references = ExperimentLibraryReferences.Create(libraries ?? [], destination);
        var sdkDirectory = AppContext.BaseDirectory;
        foreach (var assembly in new[] { "TasStudio.Sdk", "TasStudio.Core" })
            if (!File.Exists(Path.Combine(sdkDirectory, assembly + ".dll"))) throw new FileNotFoundException("Studio SDK is missing. Rebuild Studio.");
        using var templateStream = typeof(CSharpWorkspace).Assembly.GetManifestResourceStream(stateId == null
            ? "TasStudio.ExperimentTemplate.cs" : "TasStudio.StateExperimentTemplate.cs")
            ?? throw new InvalidOperationException("The bundled experiment template is missing.");
        using var templateReader = new StreamReader(templateStream);
        var template = templateReader.ReadToEnd().Replace("namespace Basic.Experiments;", "")
            .Replace("PulseExperiment", "Experiment");
        Directory.CreateDirectory(destination);
        var project = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"), new XElement("Nullable", "enable"),
                new XElement("ImplicitUsings", "enable"), new XElement("EnableDynamicLoading", "true")),
            new XElement("ItemGroup", new[] { "TasStudio.Sdk", "TasStudio.Core" }.Select(name =>
                new XElement("Reference", new XAttribute("Include", name), new XElement("HintPath", Path.Combine(sdkDirectory, name + ".dll"))))));
        if (references.Length > 0) project.Add(new XElement("ItemGroup", references));
        File.WriteAllText(Path.Combine(destination, "Experiments.csproj"), project.ToString());
        File.WriteAllText(Path.Combine(destination, "Experiment.cs"), template);
        ExperimentFiles.Write(Path.Combine(destination, ProjectExperiments.ConfigFileName), new CSharpExperimentFile(name ?? "Experiment", Path.GetRelativePath(destination, sourceProject),
            "Experiments.csproj", "Experiments", "Experiment", Count: stateId == null ? 4 : 1,
            Start: stateId != null || capturedStart ? ExperimentStart.SaveState : ExperimentStart.Boot,
            StateId: stateId ?? (capturedStart ? ExperimentStates.ProjectStart : null), Parameters: JsonSerializer.SerializeToElement(new { })));
        ExperimentFiles.Write(Path.Combine(destination, ".vscode", "settings.json"), new Dictionary<string, object>
        { ["tasStudio.workerPath"] = Path.Combine(sdkDirectory, "TasStudio.Worker.exe") });
        ExperimentFiles.Write(Path.Combine(destination, ".vscode", "extensions.json"), new { recommendations = new[] { "ms-dotnettools.csharp", "tas-studio.experiments" } });
        File.WriteAllText(Path.Combine(destination, ".gitignore"), "bin/\nobj/\n.runs/\n");
        File.WriteAllText(Path.Combine(destination, "README.md"), (stateId == null ? "" : """
            This experiment is attached to a saved state. RunAsync begins at that exact
            state; the starter explicitly authors one neutral group there. Replace it
            with your input/search logic. Count defaults to one validation trial.
            Reattach from another state's Experiment menu to reuse this code there.
            Reattaching resets preroll and UTC overrides so execution starts at that state.

            """) + """
            # TAS experiments

            Edit Experiment.cs and experiment.tascsharp.json in your code editor.
            Studio's state menu offers Open Folder and Open in Editor when available.
            VS Code's TAS Experiments sidebar can run/cancel/resume batches.
            Read typed results in results.sqlite. Default libraries selected in Studio
            are included in new workspaces; edit Experiments.csproj to change references.
            SourceProject refers to a saved .tasproj; save changes in Studio before running.
            Initialize receives only trial metadata; RunAsync receives emulator capabilities.
            The generic power-on example replays the movie, then alternates A/neutral.
            The state-specific starter takes over immediately and retains the saved clock.
            For captured project baselines, the generated config uses Start: SaveState
            and StateId: project-start. Boot explicitly restarts the game from power-on.
            Existing inputs are preserved by AdvanceAsync. SetCurrentInputAsync explicitly
            authors the current group when your algorithm takes over.
            Successful and cancelled trial folders are cleaned after their results commit;
            failed trials retain logs/state for debugging. The source project stays unchanged.
            Add shared utilities as normal C# class libraries and ProjectReferences.
            IExperiment<Result> declares the SQLite schema once per batch; return your typed values.
            results.sqlite contains trials (status/error) and results (successful typed values),
            joined by the runner-owned trial_index. Nested values are stored as JSONB.
            The SDK references point at the Studio build that created this workspace.
            """);
    }
}
