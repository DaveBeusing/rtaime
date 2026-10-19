// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using rtaime.Client;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Operator;
using rtaime.Recording;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Operator;

public sealed class OutputCanvasRealHostTests
{
    [Fact]
    public async Task Editor_saves_two_mapped_surfaces_and_runtime_sink_changes_only_at_confirmed_boundary()
    {
        var suffix=Guid.NewGuid().ToString("N");
        var root=Path.Combine(Path.GetTempPath(),"rtaime-canvas-editor-"+suffix);
        var options=RuntimeHostProcessOptions.Default;
        using var stop=new CancellationTokenSource();
        await using var runtime=new V1RuntimeHostService(options.SourceAId,options.SourceBId,
            options.Format,new NullRecordingWriter());
        await using var runtimeIpc=new RuntimeHostIpcServer("rtaime.canvas.editor.runtime."+suffix,()=>runtime);
        await runtimeIpc.StartAsync();
        var control=new ControlHostProcess(ControlHostProcessOptions.Default with
        {
            ListenEndpoint="rtaime.canvas.editor.control."+suffix,
            RuntimeEndpoint=runtimeIpc.Endpoint,
            DurabilityRoot=root,RuntimeRetryInterval=TimeSpan.FromMilliseconds(25)
        });
        var controlRun=control.RunAsync(stop.Token);
        try
        {
            await Until(()=>control.Lifecycle.State==ControlHostProcessState.Ready &&
                control.Control?.HasAuthoritativeState==true);
            using(var baseline=runtime.ProcessNextBoundary())
            {
                AssertColor(baseline.ProgramPixels.Span,options.Format,200,540,32,72,196);
                AssertColor(baseline.ProgramPixels.Span,options.Format,1500,540,32,72,196);
            }

            var client=new OperatorControlClient(new NamedPipeOperatorControlTransport(
                "rtaime.canvas.editor.control."+suffix));
            await using var owner=new OperatorViewModel(client,runtimeReadiness:new FunctionalReadiness());
            owner.ApplyConfirmedSnapshot(await client.SynchronizeAsync());
            using var editor=new OutputCanvasEditorViewModel(owner);
            await editor.ReloadAsync();
            var programCanvas=Assert.Single(editor.Canvases.Where(c=>c.Name=="program"));
            editor.SelectedCanvas=programCanvas;
            Assert.Equal(programCanvas.CanvasId,editor.SelectedCanvas?.CanvasId);
            Assert.Equal("0",editor.SavedRevision);
            Assert.Equal("UNVERIFIED",editor.ActiveRevision);

            Assert.Equal("program",editor.SelectedTarget?.Target.RoleId);
            editor.BindProgramCommand.Execute(null);
            editor.AddSurfaceCommand.Execute(null);
            Assert.True(editor.IsDirty);
            Assert.Equal(2,editor.Surfaces.Count);
            var addedId=editor.VisibleSurfaces.OrderByDescending(s=>s.ZOrder).First().SurfaceId;
            editor.SelectSurface(addedId);
            var secondary=Assert.Single(editor.Inputs.Where(i=>
                i.Input.SourceId==options.SourceBId.ToString()));
            editor.SelectedInput=secondary;
            foreach(var (key,value) in new (string,string)[]
            {
                ("tlx","960"),("tly","0"),("trx","1920"),("try","0"),
                ("brx","1920"),("bry","1080"),("blx","960"),("bly","1080"),
                ("el","0.12"),("et","0"),("er","0"),("eb","0")
            }) editor.Fields.Single(field=>field.Key==key).Value=value;
            editor.ApplyNumericCommand.Execute(null);
            Assert.True(editor.IsDirty);
            await editor.ValidateAsync();
            Assert.Empty(editor.Issues);
            await editor.SaveAsync();
            Assert.False(editor.IsDirty);
            Assert.Equal("1",editor.SavedRevision);
            Assert.Equal("UNVERIFIED",editor.ActiveRevision);
            Assert.Null(runtime.OutputMappingSnapshot.ActiveRenderRevision);

            using(var beforeApply=runtime.ProcessNextBoundary())
                AssertColor(beforeApply.ProgramPixels.Span,options.Format,1500,540,32,72,196);
            await editor.ValidateAsync();
            Assert.True(editor.ApplyCommand.CanExecute(null),editor.Status);
            await editor.ApplyAsync();
            Assert.Equal("PENDING_BOUNDARY",editor.ActivationState);
            Assert.Equal("UNVERIFIED",editor.ActiveRevision);
            using(var mapped=runtime.ProcessNextBoundary())
            {
                AssertColor(mapped.ProgramPixels.Span,options.Format,200,540,32,72,196);
                AssertColor(mapped.ProgramPixels.Span,options.Format,1500,540,196,72,32);
                Assert.Equal(0,mapped.ActiveGpuSurfacesAfterBoundary);
            }
            await editor.ObserveBoundaryAsync();
            Assert.Equal("ACTIVE",editor.ActivationState);
            Assert.Equal("1",editor.ActiveRevision);
            Assert.Equal(1UL,runtime.OutputMappingSnapshot.ActiveRenderRevision);

            await editor.DisableAsync();
            Assert.Equal("PENDING_BOUNDARY",editor.ActivationState);
            using(var disabled=runtime.ProcessNextBoundary())
                AssertColor(disabled.ProgramPixels.Span,options.Format,1500,540,32,72,196);
            await editor.ObserveBoundaryAsync();
            Assert.Equal("UNMAPPED",editor.ActivationState);
            Assert.Null(runtime.OutputMappingSnapshot.ActiveRenderRevision);
        }
        finally
        {
            stop.Cancel();
            Assert.Equal(ControlHostExitCode.Success,
                await controlRun.WaitAsync(TimeSpan.FromSeconds(10)));
            await runtimeIpc.DisposeAsync();
            try{Directory.Delete(root,true);}catch(IOException){}
        }
    }

    private static void AssertColor(ReadOnlySpan<byte> rgba,VideoFormat format,
        int x,int y,byte r,byte g,byte b)
    {
        var start=checked((y*(int)format.Width+x)*4);
        Assert.Equal(r,rgba[start]);
        Assert.Equal(g,rgba[start+1]);
        Assert.Equal(b,rgba[start+2]);
        Assert.Equal((byte)255,rgba[start+3]);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline=DateTime.UtcNow.AddSeconds(10);
        while(!condition())
        {
            if(DateTime.UtcNow>=deadline)throw new TimeoutException("Control/Runtime host did not become ready.");
            await Task.Delay(20);
        }
    }

    private sealed class FunctionalReadiness : IRuntimeReadinessService
    {
        // Manual-boundary functional fixture: only the IPC and emitted sample path
        // are qualified here, not production scheduler or physical output readiness.
        public RuntimeReadinessSnapshot Current { get; } = new(RuntimeReadinessState.Ready,
            DateTimeOffset.UnixEpoch,DateTimeOffset.UnixEpoch,[],true,
            RuntimePerformanceVerificationSnapshot.Unverified);
        public event EventHandler<RuntimeReadinessChangedEventArgs>? Changed { add{} remove{} }
        public void Observe(RuntimeReadinessObservation observation){}
        public void InvalidatePerformance(string detail){}
    }

    private sealed class NullRecordingWriter : IProgramRecordingWriter
    {
        public ValueTask OpenAsync(RecordingStartRequest request,CancellationToken token)=>ValueTask.CompletedTask;
        public ValueTask WriteAsync(RecordingProgramSample sample,CancellationToken token)=>ValueTask.CompletedTask;
        public ValueTask FinalizeAsync(CancellationToken token)=>ValueTask.CompletedTask;
        public ValueTask AbortAsync(CancellationToken token)=>ValueTask.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
