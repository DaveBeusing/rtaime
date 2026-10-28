// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Tests.Architecture;

public sealed class OGrafIsolationArchitectureTests
{
    [Fact]
    public void OGraf_inspection_does_not_execute_scripts_or_mutate_live_graphics()
    {
        var root = RepositorySnapshot.Load().Root;
        var client = File.ReadAllText(Path.Combine(root, "src", "Client",
            "rtaime.Client", "OGrafPackageInspector.cs"));
        var model = File.ReadAllText(Path.Combine(root, "src", "Client",
            "rtaime.Client", "OGrafManifestProfile.cs"));
        var inspector = File.ReadAllText(Path.Combine(root, "src", "Hosts",
            "rtaime.Operator", "OGrafOperatorInspection.cs"));
        var view = File.ReadAllText(Path.Combine(root, "src", "Hosts",
            "rtaime.Operator", "MainWindow.xaml"));

        foreach (var source in new[] { client, inspector })
        {
            foreach (var forbidden in new[]
            {
                "Process.Start(", "ProcessStartInfo", "WebView2", "WebBrowser",
                "HttpClient", "WebSocket", "HttpListener", "ExecuteScript",
                "LoadGraphicsOverlayAsync(", "ApplyProductionCgTextAsync(",
                "SetGraphicsOverlayAsync(", "SelectPreviewAsync(", "CutAsync(",
                "DissolveAsync(", "CommitExecution", "SetProgram"
            })
                Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }

        Assert.Contains("public bool CanTakeToProgram => false", model, StringComparison.Ordinal);
        Assert.Contains("public bool PreviewFrameConfirmed => false", model, StringComparison.Ordinal);
        Assert.Contains("INSPECT MANIFEST", view, StringComparison.Ordinal);
        Assert.Contains("STATIC PACKAGE THUMBNAIL ONLY", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding OGrafCanTakeToProgram}", view, StringComparison.Ordinal);
    }
}
