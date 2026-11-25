using System.Text.Json;
using Microsoft.Data.Sqlite;
using TasStudio.Core;
using TasStudio.Emulation;
using TasStudio.Sdk;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ExperimentLivePlayTests
{
    public sealed record Row(double Score);
    private sealed class Script(Func<ExperimentRunContext, Task<Row>> run) : IExperiment<Row>
    {
        public Task<Row> RunAsync(ExperimentRunContext context, CancellationToken token) => run(context);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaysAreReadableBeforeTrialEndsAndSurviveFailureCancellationAndRetry(bool cancelled)
    {
        using var files = new TestWorkspace();
        using var source = new ExecutionService(new FakeBackend { RecordPolls = true });
        await source.LoadGameAsync(files.GamePath, files.Options); await source.NewProjectAsync();
        await source.AdvanceFrameAsync(ControllerState.Neutral);
        await source.SaveNamedStateAsync("Start");
        var state = Assert.Single(source.StateMarkers).Id;
        var path = files.FilePath("source/project.tasproj"); await source.SaveProjectAsync(path);
        var batch = files.FilePath("batch"); var top = new ExperimentTopPlays(HigherIsBetter: false, Keep: 1);
        ExperimentFiles.Write(Path.Combine(batch,"experiment.json"),new ExperimentDefinition(1,"Live",ExperimentStart.SaveState,state,null,1,30,
            [new("Trial",JsonSerializer.SerializeToElement(new{}))]) { TopPlays=top });
        await using var writer = new SerializedExperimentWriter(new SqliteExperimentWriter(typeof(Row),topPlays:top));
        await writer.InitializeAsync(new(batch,"Live",1),default);
        var job = new ExperimentJob("Trial",path,files.Options.CorePath,files.Options.SystemDirectory,files.FilePath("attempt-1"),
            ExperimentStart.SaveState,state,null,JsonSerializer.SerializeToElement(new{}),30,0,1,KeepSuccessfulArtifact:false)
            { TopPlays=top,BatchDirectory=batch };
        using var first = new ExecutionService(new FakeBackend { RecordPolls = true });
        var result = await ExperimentWorker.RunAsync(job,first,default,new Script(async c =>
        {
            await c.Emulator.AdvanceAsync(ControllerState.Neutral with { Buttons=PadButtons.A },2);
            await c.SubmitPlayAsync(10,"First");
            Assert.Equal(10,Assert.Single(ExperimentPlayResults.Read(batch)).Score);
            var captured=ExperimentPlayData.Decode(ExperimentPlayResults.Load(batch,0,0));
            Assert.Equal(2,captured.Inputs.Length); Assert.Equal(2,captured.PollFrames.Length);
            Assert.All(captured.Inputs,x=>Assert.Equal(PadButtons.A,x.Buttons));
            await c.Emulator.LoadStateAsync(state,true);
            await c.Emulator.AdvanceAsync(ControllerState.Neutral with { Buttons=PadButtons.B });
            await c.SubmitPlayAsync(20,"Second");
            Assert.Equal(2,Count(batch,"submitted_plays"));
            Assert.Equal(10,Assert.Single(ExperimentPlayResults.Read(batch)).Score);
            if(cancelled)throw new OperationCanceledException();
            throw new InvalidOperationException("Failure after durable submissions");
        }));
        Assert.Equal(cancelled?"cancelled":"failed",result.Status);
        await writer.WriteAsync(result);
        Assert.Equal(2,Count(batch,"submitted_plays"));
        Assert.Equal(10,Assert.Single(ExperimentPlayResults.Read(batch)).Score);
        using var retry = new ExecutionService(new FakeBackend { RecordPolls = true });
        result=await ExperimentWorker.RunAsync(job with{OutputDirectory=files.FilePath("attempt-2")},retry,default,new Script(async c=>
        {
            await c.Emulator.AdvanceAsync(ControllerState.Neutral);
            await c.SubmitPlayAsync(5,"Retry");return new(5);
        }));
        Assert.Equal("completed",result.Status);await writer.WriteAsync(result);
        Assert.Equal(3,Count(batch,"submitted_plays"));
        var best=Assert.Single(ExperimentPlayResults.Read(batch));Assert.Equal(5,best.Score);Assert.Equal(2,best.SubmissionIndex);
    }

    [Fact]
    public async Task EightConcurrentWorkersCommitAllSubmissionsAndShareOneTopK()
    {
        using var files=new TestWorkspace();var batch=files.DirectoryPath;
        var top=new ExperimentTopPlays(HigherIsBetter:false,Keep:4);
        await using var writer=new SqliteExperimentWriter(typeof(Row),topPlays:top);
        await writer.InitializeAsync(new(batch,"Concurrent",8),default);
        await Task.WhenAll(Enumerable.Range(0,8).Select(trial=>Task.Run(()=>
        {
            var job=new ExperimentJob("Trial "+trial,"unused","unused","unused","unused",ExperimentStart.Boot,null,null,
                JsonSerializer.SerializeToElement(new{}),30,trial,8){TopPlays=top,BatchDirectory=batch};
            using var live=new ExperimentLivePlays(job,DateTimeOffset.UtcNow);
            for(int n=0;n<5;n++)live.Save(new(n,"Play",trial*5+n,0,1,
                new ExperimentPlayData("baseline",[ControllerState.Neutral],[],[]).Encode()));
        })));
        Assert.Equal(40,Count(batch,"submitted_plays"));Assert.Equal(4,Count(batch,"plays"));
        using var db=Open(batch);using var command=db.CreateCommand();command.CommandText="SELECT SUM(score) FROM plays";
        Assert.Equal(6d,Convert.ToDouble(command.ExecuteScalar()));
    }

    private static SqliteConnection Open(string batch)
    {
        var db=new SqliteConnection($"Data Source={Path.Combine(batch,"results.sqlite")};Pooling=False");db.Open();return db;
    }
    private static long Count(string batch,string table)
    {
        using var db=Open(batch);using var command=db.CreateCommand();command.CommandText=$"SELECT COUNT(*) FROM {table}";
        return (long)command.ExecuteScalar()!;
    }
}
