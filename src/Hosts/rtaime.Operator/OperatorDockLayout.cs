// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
namespace rtaime.Operator;

public sealed record OperatorDockGroup(string Id, string[] Panels, string? ActivePanel);
public sealed record OperatorFloatingPanel(string Panel, string ReturnGroup, OperatorPanelPlacement Placement);
public sealed record OperatorPanelPlacement(string Monitor, double Left, double Top, double Width, double Height,
    double WorkLeft, double WorkTop, double Dpi)
{
    public static OperatorPanelPlacement Default { get; } = new("", 80, 80, 800, 600, 0, 0, 96);
    public OperatorPanelPlacement Normalize() => this with
    {
        Monitor = (Monitor ?? "")[..Math.Min(128, (Monitor ?? "").Length)],
        Left = Number(Left, 80, -1000000, 1000000), Top = Number(Top, 80, -1000000, 1000000),
        Width = Number(Width, 800, 1, 16000), Height = Number(Height, 600, 1, 16000),
        WorkLeft = Number(WorkLeft, 0, -1000000, 1000000), WorkTop = Number(WorkTop, 0, -1000000, 1000000),
        Dpi = Number(Dpi, 96, 48, 768)
    };
    private static double Number(double value, double fallback, double min, double max) => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
public sealed record OperatorMonitorArea(string Id, double Left, double Top, double Width, double Height, double Dpi, bool Primary);
public sealed record OperatorPanelBounds(double Left, double Top, double Width, double Height);

public sealed record OperatorDockLayout
{
    public const int CurrentVersion = 1;
    public static IReadOnlyList<string> PanelIds { get; } = Array.AsReadOnly(new[] { "TOOLS", "MONITORS", "INSPECTOR", "TIMELINE" });
    public static IReadOnlyList<string> GroupIds { get; } = Array.AsReadOnly(new[] { "LEFT", "CENTER", "RIGHT", "BOTTOM" });
    public int Version { get; init; } = CurrentVersion;
    public bool Enabled { get; init; }
    public OperatorDockGroup[] Groups { get; init; } = Defaults();
    public OperatorFloatingPanel[] Floating { get; init; } = [];
    public double LeftRatio { get; init; } = .22;
    public double RightRatio { get; init; } = .22;
    public double BottomRatio { get; init; } = .30;
    public static OperatorDockLayout Default => new();
    private static OperatorDockGroup[] Defaults() => GroupIds.Select((g, i) => new OperatorDockGroup(g, [PanelIds[i]], PanelIds[i])).ToArray();

    public OperatorDockLayout Normalize()
    {
        if (Version != CurrentVersion) return Default;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var groups = GroupIds.Select(id =>
        {
            var candidate = (Groups ?? []).Take(4).FirstOrDefault(g => g is not null && g.Id == id);
            var panels = (candidate?.Panels ?? []).Take(4).Where(p => PanelIds.Contains(p) && seen.Add(p)).ToArray();
            return new OperatorDockGroup(id, panels, panels.Contains(candidate?.ActivePanel) ? candidate!.ActivePanel : panels.FirstOrDefault());
        }).ToArray();
        var floating = (Floating ?? []).Take(4)
            .Where(p => p is not null && PanelIds.Contains(p.Panel) && seen.Add(p.Panel))
            .Select(p => p with { ReturnGroup = GroupIds.Contains(p.ReturnGroup) ? p.ReturnGroup : "CENTER",
                Placement = (p.Placement ?? OperatorPanelPlacement.Default).Normalize() }).ToArray();
        for (var i = 0; i < PanelIds.Count; i++)
        {
            if (!seen.Add(PanelIds[i])) continue;
            groups[i] = groups[i] with { Panels = [.. groups[i].Panels, PanelIds[i]], ActivePanel = groups[i].ActivePanel ?? PanelIds[i] };
        }
        return this with { Groups = groups, Floating = floating,
            LeftRatio = Ratio(LeftRatio, .22, .10, .35), RightRatio = Ratio(RightRatio, .22, .10, .35),
            BottomRatio = Ratio(BottomRatio, .30, .15, .60) };
    }

    public OperatorDockLayout Dock(string panel, string group)
    {
        if (!PanelIds.Contains(panel) || !GroupIds.Contains(group)) return Normalize();
        var current = Normalize();
        var groups = current.Groups.Select(g =>
        {
            var panels = g.Panels.Where(p => p != panel).ToArray();
            if (g.Id == group) panels = [.. panels, panel];
            return g with { Panels = panels, ActivePanel = g.Id == group ? panel : panels.Contains(g.ActivePanel) ? g.ActivePanel : panels.FirstOrDefault() };
        }).ToArray();
        return (current with { Enabled = true, Groups = groups, Floating = current.Floating.Where(p => p.Panel != panel).ToArray() }).Normalize();
    }

    public OperatorDockLayout Float(string panel, OperatorPanelPlacement placement)
    {
        if (!PanelIds.Contains(panel)) return Normalize();
        var current = Normalize();
        var returnGroup = current.Groups.FirstOrDefault(g => g.Panels.Contains(panel))?.Id
            ?? current.Floating.FirstOrDefault(p => p.Panel == panel)?.ReturnGroup ?? "CENTER";
        var groups = current.Groups.Select(g => g with { Panels = g.Panels.Where(p => p != panel).ToArray(),
            ActivePanel = g.ActivePanel == panel ? g.Panels.FirstOrDefault(p => p != panel) : g.ActivePanel }).ToArray();
        return (current with { Enabled = true, Groups = groups,
            Floating = [.. current.Floating.Where(p => p.Panel != panel), new OperatorFloatingPanel(panel, returnGroup, placement)] }).Normalize();
    }

    public OperatorDockLayout SelectTab(string group, string panel) => (Normalize() with
    {
        Groups = Normalize().Groups.Select(g => g.Id == group && g.Panels.Contains(panel) ? g with { ActivePanel = panel } : g).ToArray()
    });
    private static double Ratio(double value, double fallback, double min, double max) => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

public static class OperatorPanelRestore
{
    public static OperatorPanelBounds Restore(OperatorPanelPlacement? placement, IReadOnlyList<OperatorMonitorArea> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        var valid = monitors.Where(m => Finite(m.Left, m.Top, m.Width, m.Height, m.Dpi) && m.Width > 0 && m.Height > 0 && m.Dpi is >= 48 and <= 768).Take(32).ToArray();
        if (valid.Length == 0) return new(80, 80, 800, 600);
        var p = placement ?? OperatorPanelPlacement.Default;
        var monitor = valid.FirstOrDefault(m => m.Id == p.Monitor) ?? valid.FirstOrDefault(m => m.Primary) ?? valid[0];
        var oldDpi = double.IsFinite(p.Dpi) && p.Dpi is >= 48 and <= 768 ? p.Dpi : 96;
        var scale = monitor.Dpi / oldDpi;
        var width = Math.Clamp(double.IsFinite(p.Width) && p.Width > 0 ? p.Width * scale : 800 * monitor.Dpi / 96, Math.Min(320 * monitor.Dpi / 96, monitor.Width), monitor.Width);
        var height = Math.Clamp(double.IsFinite(p.Height) && p.Height > 0 ? p.Height * scale : 600 * monitor.Dpi / 96, Math.Min(220 * monitor.Dpi / 96, monitor.Height), monitor.Height);
        var left = Finite(p.Left, p.WorkLeft) ? monitor.Left + (p.Left - p.WorkLeft) * scale : monitor.Left;
        var top = Finite(p.Top, p.WorkTop) ? monitor.Top + (p.Top - p.WorkTop) * scale : monitor.Top;
        return new(Math.Clamp(left, monitor.Left, monitor.Left + monitor.Width - width),
            Math.Clamp(top, monitor.Top, monitor.Top + monitor.Height - height), width, height);
    }
    private static bool Finite(params double[] values) => values.All(double.IsFinite);
}
