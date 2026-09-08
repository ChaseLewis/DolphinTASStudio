using System.Text.Json;
using System.Xml.Linq;
using TasStudio.Emulation;

internal static class CSharpWorkspace
{
    public static void Create(string sourceProject, string destination)
    {
        sourceProject = Path.GetFullPath(sourceProject); destination = Path.GetFullPath(destination);
        if (!File.Exists(sourceProject)) throw new FileNotFoundException("Select a saved Studio project.", sourceProject);
        if (Directory.Exists(destination)) throw new IOException("Choose a new workspace folder. Existing workspaces are preserved.");
        Directory.CreateDirectory(destination);
        var sdkDirectory = AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(sdkDirectory, "TasStudio.Sdk.dll"))) throw new FileNotFoundException("Studio SDK is missing. Rebuild Studio.");
        var project = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"), new XElement("Nullable", "enable"),
                new XElement("ImplicitUsings", "enable"), new XElement("EnableDynamicLoading", "true")),
            new XElement("ItemGroup", new[] { "TasStudio.Sdk", "TasStudio.Core" }.Select(name =>
                new XElement("Reference", new XAttribute("Include", name), new XElement("HintPath", Path.Combine(sdkDirectory, name + ".dll"))))));
        File.WriteAllText(Path.Combine(destination, "Experiments.csproj"), project.ToString());
        using var templateStream = typeof(CSharpWorkspace).Assembly.GetManifestResourceStream("TasStudio.Worker.ExperimentTemplate.cs")
            ?? throw new InvalidOperationException("The bundled experiment template is missing.");
        using var templateReader = new StreamReader(templateStream);
        var template = templateReader.ReadToEnd().Replace("namespace Basic.Experiments;", "")
            .Replace("PulseExperiment", "Experiment");
        File.WriteAllText(Path.Combine(destination, "Experiment.cs"), template);
        ExperimentFiles.Write(Path.Combine(destination, "experiment.tascsharp.json"), new CSharpExperimentFile("Experiment", sourceProject,
            "Experiments.csproj", "Experiments", "Experiment", Parameters: JsonSerializer.SerializeToElement(new { })));
        ExperimentFiles.Write(Path.Combine(destination, ".vscode", "settings.json"), new Dictionary<string, object>
        { ["tasStudio.workerPath"] = Path.Combine(sdkDirectory, "TasStudio.Worker.exe") });
        ExperimentFiles.Write(Path.Combine(destination, ".vscode", "extensions.json"), new { recommendations = new[] { "ms-dotnettools.csharp", "tas-studio.experiments" } });
        File.WriteAllText(Path.Combine(destination, ".gitignore"), "bin/\nobj/\n.runs/\n");
        File.WriteAllText(Path.Combine(destination, "README.md"), """
            # TAS experiments

            Edit Experiment.cs and experiment.tascsharp.json in VS Code. Use the TAS
            Experiments sidebar to run/cancel/resume batches. Read typed results in results.sqlite.
            SourceProject refers to a saved .tasproj; save changes in Studio before running.
            Initialize receives only trial metadata; RunAsync receives emulator capabilities.
            The example replays the frozen movie, then alternates A/neutral for 60 groups.
            It varies power-on UTC by trial index; a save-state start retains its saved clock.
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
        Console.WriteLine("Created workspace: " + destination);
    }
}
