// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using rtaime.Client;
using rtaime.Operator;
using rtaime.Operator.Controls;
using Xunit;

namespace rtaime.Tests.Operator;

/// <summary>Geometry goldens and review images; deliberately no driver-dependent pixel equality.</summary>
public sealed class OperatorCombinedVisualQualification
{
    private static readonly string[] Regions =
        ["TopBar", "WorkspaceNavigation", "CenterWorkspace", "StatusBar"];

    [Fact]
    public void Geometry_golden_rejects_clipping_empty_nonfinite_and_displaced_regions()
    {
        var viewport = new Size(960, 540);
        Assert.True(Fits(new Rect(0, 0, 960, 60), viewport));
        Assert.True(Fits(new Rect(-0.25, 0, 960, 60), viewport));
        Assert.False(Fits(new Rect(0, 0, 0, 60), viewport));
        Assert.False(Fits(new Rect(0, 0, 961, 60), viewport));
        Assert.False(Fits(new Rect(0, 539, 960, 30), viewport));
        Assert.False(Fits(Rect.Empty, viewport));
        Assert.False(Fits(new Rect(0, 0, double.PositiveInfinity, 30), viewport));
        Assert.False(Near(new Rect(0, 0, 960, 60), new Rect(0, 2, 960, 60)));
    }

    internal static async Task VerifyWindowAsync(MainWindow window, Dispatcher dispatcher)
    {
        var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        var owner = Assert.IsType<OperatorViewModel>(window.DataContext);
        var original = (RootWidth: root.Width, RootHeight: root.Height, Transform: root.LayoutTransform,
            Width: window.Width, Height: window.Height, MinWidth: window.MinWidth, MinHeight: window.MinHeight,
            Workspace: window.Shell.SelectedWorkspace);
        var program = owner.ProgramSourceId;
        var preview = owner.PreviewSourceId;
        var evidenceRoot = Environment.GetEnvironmentVariable("RTAIME_OPERATOR_VISUAL_EVIDENCE");
        var observations = new List<object>();
        if (!string.IsNullOrWhiteSpace(evidenceRoot)) Directory.CreateDirectory(evidenceRoot);
        var watch = Stopwatch.StartNew();
        try
        {
            Assert.False(owner.CutCommand.CanExecute(null));
            window.MinWidth = 960; window.MinHeight = 500;
            foreach (var row in OperatorVisualQualificationTests.QualificationMatrix())
            {
                var name = (string)row[0];
                var width = (double)row[1]; var height = (double)row[2];
                var dpi = (int)row[3]; var scale = dpi / 100.0;
                // Inject layout scaling, not WM_DPICHANGED or a physical monitor transition.
                root.Width = width; root.Height = height;
                root.LayoutTransform = new ScaleTransform(scale, scale);
                window.Width = width * scale; window.Height = height * scale;
                foreach (var workspace in OperatorWorkspaceNames.All)
                {
                    window.Shell.SelectWorkspace(workspace);
                    await dispatcher.InvokeAsync(() =>
                    {
                        window.Shell.UpdateViewportSize(width, height);
                        window.UpdateLayout();
                    }, DispatcherPriority.Loaded);
                    Assert.Equal(width, root.ActualWidth, 1);
                    Assert.Equal(height, root.ActualHeight, 1);
                    var first = Capture(window, root);
                    await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
                    var second = Capture(window, root);
                    foreach (var region in Regions)
                    {
                        Assert.True(Fits(first[region], new Size(width, height)),
                            name + "/" + workspace + "/" + region + ": clipped or empty " + first[region]);
                        Assert.True(Near(first[region], second[region]),
                            name + "/" + workspace + "/" + region + ": unstable settled layout.");
                    }
                    // Stable anchors independent of font, rasterizer and shell scale calculations.
                    Assert.InRange(Math.Abs(first["TopBar"].Y), 0, 0.5);
                    Assert.InRange(Math.Abs(first["TopBar"].Width - width), 0, 0.5);
                    Assert.InRange(Math.Abs(first["TopBar"].Height - 60), 0, 0.5);
                    Assert.InRange(Math.Abs(first["StatusBar"].Bottom - height), 0, 0.5);
                    Assert.InRange(Math.Abs(first["StatusBar"].Height - 30), 0, 0.5);
                    Assert.True(first["WorkspaceNavigation"].Right <= first["CenterWorkspace"].Left + 0.5);
                    Assert.True(first["CenterWorkspace"].Top >= first["TopBar"].Bottom - 0.5);
                    Assert.True(first["CenterWorkspace"].Bottom <= first["StatusBar"].Top + 0.5);
                    Assert.Equal(program, owner.ProgramSourceId);
                    Assert.Equal(preview, owner.PreviewSourceId);
                    Assert.False(owner.CutCommand.CanExecute(null));
                    var overlay = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("StartupOverlay"));
                    observations.Add(new
                    {
                        name, workspace, width, height, injectedScale = scale,
                        actualDpi = VisualTreeHelper.GetDpi(window).PixelsPerInchX,
                        startupOverlayVisible = overlay.IsVisible,
                        regions = first.ToDictionary(pair => pair.Key, pair => new
                        { x = pair.Value.X, y = pair.Value.Y, width = pair.Value.Width, height = pair.Value.Height })
                    });
                    if (workspace == "LIVE" && !string.IsNullOrWhiteSpace(evidenceRoot))
                        SaveImage(root, width, height, scale, Path.Combine(evidenceRoot, name + ".png"));
                }
            }

            // Warm up all workspaces above before measuring a stable viewport. No forced GC:
            // these are process observations, not an exclusive heap or physical latency claim.
            root.LayoutTransform = Transform.Identity; root.Width = 1600; root.Height = 900;
            window.Width = 1600; window.Height = 900;
            window.Shell.SelectWorkspace("LIVE");
            window.Shell.UpdateViewportSize(1600, 900);
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            var metrics = await MeasureSettledAsync(window, root, dispatcher);
            Assert.Equal(program, owner.ProgramSourceId);
            Assert.Equal(preview, owner.PreviewSourceId);
            Console.WriteLine("COMBINED VISUAL PASS: 40 workspace/viewport cases; injected 100/125/150/200% layout; geometry goldens; Program unchanged.");
            Console.WriteLine("COMBINED UI METRICS: " + JsonSerializer.Serialize(metrics));
            if (!string.IsNullOrWhiteSpace(evidenceRoot))
                File.WriteAllText(Path.Combine(evidenceRoot, "combined-ui.json"), JsonSerializer.Serialize(new
                {
                    sourceCommit = Environment.GetEnvironmentVariable("RTAIME_SOURCE_COMMIT"),
                    sdk = Environment.GetEnvironmentVariable("RTAIME_BASELINE_SDK"),
                    scope = "Windows WPF layout scale injection; screenshots exclude hardware D3DImage content; physical mixed-DPI UNVERIFIED",
                    cases = observations, metrics, elapsedMilliseconds = watch.Elapsed.TotalMilliseconds
                }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            root.Width = original.RootWidth; root.Height = original.RootHeight; root.LayoutTransform = original.Transform;
            window.Width = original.Width; window.Height = original.Height;
            window.MinWidth = original.MinWidth; window.MinHeight = original.MinHeight;
            window.Shell.SelectWorkspace(original.Workspace);
            window.Shell.UpdateViewportSize(window.Width, window.Height);
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
        }
    }

    private static async Task<object> MeasureSettledAsync(MainWindow window, FrameworkElement root, Dispatcher dispatcher)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var handles = process.HandleCount; var memory = process.PrivateMemorySize64;
        var managed = GC.GetTotalMemory(false);
        var surfaces = Descendants(root).OfType<OperatorDrawingSurface>().ToArray();
        Assert.NotEmpty(surfaces);
        var creates = surfaces.Sum(surface => surface.TargetCreateCount);
        var draws = 0; var layouts = 0;
        EventHandler layout = (_, _) => layouts++;
        EventHandler<OperatorSurfaceDrawEventArgs> draw = (_, _) => draws++;
        root.LayoutUpdated += layout;
        foreach (var surface in surfaces) surface.Draw += draw;
        var latency = new List<double>();
        try
        {
            for (var iteration = 0; iteration < 24; iteration++)
            {
                // Repeating the same viewport must not create native targets on each update.
                window.Shell.UpdateViewportSize(1600, 900);
                window.InvalidateVisual();
                var posted = Stopwatch.GetTimestamp();
                await dispatcher.InvokeAsync(() => latency.Add(Stopwatch.GetElapsedTime(posted).TotalMilliseconds),
                    DispatcherPriority.Input).Task.WaitAsync(TimeSpan.FromSeconds(5));
                await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            }
        }
        finally
        {
            root.LayoutUpdated -= layout;
            foreach (var surface in surfaces) surface.Draw -= draw;
        }
        process.Refresh();
        var handleGrowth = process.HandleCount - handles;
        var privateGrowth = process.PrivateMemorySize64 - memory;
        var managedGrowth = GC.GetTotalMemory(false) - managed;
        var targetGrowth = surfaces.Sum(surface => surface.TargetCreateCount) - creates;
        var sorted = latency.Order().ToArray();
        var p95 = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
        Assert.InRange(handleGrowth, int.MinValue, 64);
        Assert.InRange(privateGrowth, long.MinValue, 128L * 1024 * 1024);
        Assert.InRange(managedGrowth, long.MinValue, 32L * 1024 * 1024);
        Assert.InRange(targetGrowth, 0, surfaces.Length);
        Assert.InRange(layouts, 0, 24 * 8);
        Assert.InRange(draws, 0, 24 * Math.Max(1, surfaces.Length) * 8);
        Assert.InRange(sorted[^1], 0, 5000);
        return new { samples = sorted.Length, dispatcherP95Milliseconds = p95,
            dispatcherMaxMilliseconds = sorted[^1], layouts, draws, surfaceCount = surfaces.Length,
            targetGrowth, handleGrowth, privateGrowthBytes = privateGrowth, managedGrowthBytes = managedGrowth };
    }

    private static Dictionary<string, Rect> Capture(MainWindow window, Visual root) =>
        Regions.ToDictionary(name => name, name =>
        {
            var region = name == "StatusBar"
                ? Descendants(root).OfType<RuntimePerformanceStatusBarControl>().Single()
                : Assert.IsAssignableFrom<FrameworkElement>(window.FindName(name));
            Assert.True(region.IsLoaded && region.IsVisible, name + ": expected visible region.");
            return region.TransformToAncestor(root).TransformBounds(new Rect(region.RenderSize));
        });

    private static bool Fits(Rect bounds, Size viewport) => !bounds.IsEmpty &&
        double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
        double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) &&
        bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= -0.5 && bounds.Top >= -0.5 &&
        bounds.Right <= viewport.Width + 0.5 && bounds.Bottom <= viewport.Height + 0.5;

    private static bool Near(Rect a, Rect b) => Math.Abs(a.X - b.X) <= 0.5 &&
        Math.Abs(a.Y - b.Y) <= 0.5 && Math.Abs(a.Width - b.Width) <= 0.5 && Math.Abs(a.Height - b.Height) <= 0.5;

    private static void SaveImage(FrameworkElement root, double width, double height, double scale, string path)
    {
        // VisualBrush avoids applying the root LayoutTransform twice.
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.Fill }, null, new Rect(0, 0, width, height));
        var image = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
