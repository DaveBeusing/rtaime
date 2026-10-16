// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace rtaime.Operator;

public sealed record OperatorWorkspaceTemplate(string Name, string Workspace, OperatorWorkspaceLayoutSettings Layout)
{
    public const int MaximumTemplates = 16;
    public bool IsValid => !string.IsNullOrWhiteSpace(Name) && Name.Length <= 64 && !Name.Any(char.IsControl) &&
        OperatorWorkspaceNames.All.Contains(Workspace) && Layout is not null;
}
public sealed record OperatorWorkspaceTemplateDocument(string Format, int Version, int LayoutVersion, OperatorWorkspaceTemplate Template);

// Portable presentation only. No production identifiers, device credentials or media state are serialized.
public static class OperatorWorkspaceTemplateFiles
{
    public const int MaximumBytes = 1024 * 1024;
    public const string Format = "rtaime.operator-workspace-template";
    public static async Task<OperatorWorkspaceTemplate> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > MaximumBytes) throw new InvalidDataException("Layout file exceeds 1 MiB.");
        var bytes = new byte[MaximumBytes + 1]; var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (read == 0) break; count += read;
        }
        if (count > MaximumBytes) throw new InvalidDataException("Layout file exceeds 1 MiB.");
        using var json = JsonDocument.Parse(bytes.AsMemory(0, count));
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a layout document.");
        OperatorWorkspaceTemplate template;
        if (json.RootElement.TryGetProperty("Format", out _))
        {
            var document = JsonSerializer.Deserialize<OperatorWorkspaceTemplateDocument>(bytes.AsSpan(0, count));
            if (document is null || document.Format != Format || document.Version != 1 ||
                document.LayoutVersion is < 2 or > OperatorLayoutSettings.CurrentVersion || document.Template?.IsValid != true)
                throw new InvalidDataException("Unsupported or invalid workspace template.");
            template = Migrate(document.Template, document.LayoutVersion);
        }
        else
        {
            // Explicit import of an existing layout file uses its established migration policy.
            if (!json.RootElement.TryGetProperty("Version", out var version) || !version.TryGetInt32(out var schema) ||
                schema is < 2 or > OperatorLayoutSettings.CurrentVersion ||
                !json.RootElement.TryGetProperty("Workspaces", out var workspaces) || workspaces.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("A legacy layout requires an explicit supported version and workspace object.");
            var legacy = JsonSerializer.Deserialize<OperatorLayoutSettings>(bytes.AsSpan(0, count));
            if (legacy is null || legacy.Version is < 2 or > OperatorLayoutSettings.CurrentVersion ||
                !json.RootElement.TryGetProperty("Workspaces", out _))
                throw new InvalidDataException("Unsupported legacy layout.");
            var migrated = legacy.Normalize();
            template = new("Imported " + migrated.SelectedWorkspace, migrated.SelectedWorkspace, migrated.Workspaces[migrated.SelectedWorkspace]);
        }
        return template with { Layout = template.Layout.Normalize() };
    }
    private static OperatorWorkspaceTemplate Migrate(OperatorWorkspaceTemplate template, int version)
    {
        var envelope = new OperatorLayoutSettings { Version = version, SelectedWorkspace = template.Workspace };
        envelope.Workspaces[template.Workspace] = template.Layout;
        return template with { Layout = envelope.Normalize().Workspaces[template.Workspace] };
    }
    public static async Task ExportAsync(string path, OperatorWorkspaceTemplate template, CancellationToken cancellationToken = default)
    {
        if (!template.IsValid) throw new InvalidDataException("Invalid workspace template.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new OperatorWorkspaceTemplateDocument(Format, 1,
            OperatorLayoutSettings.CurrentVersion, template with { Layout = template.Layout.Normalize() }),
            new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Layout file exceeds 1 MiB.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }
}
public sealed partial class OperatorShellViewModel
{
    private string _templateName = "", _templateStatus = "Templates contain layout preferences only.";
    private OperatorWorkspaceTemplate? _selectedTemplate;
    private Func<string?>? _templateImportPicker, _templateExportPicker;
    private Action? _capturePanels;
    private readonly CancellationTokenSource _templateFileStop = new();
    private Task _templateImportTask = Task.CompletedTask, _templateExportTask = Task.CompletedTask;
    private bool _templateFilesStopping;
    public ObservableCollection<OperatorWorkspaceTemplate> WorkspaceTemplates { get; } = [];
    public string TemplateName { get => _templateName; set { if (Set(ref _templateName, (value ?? "")[..Math.Min(value?.Length ?? 0, 64)])) NotifyTemplates(); } }
    public string TemplateStatus { get => _templateStatus; private set => Set(ref _templateStatus, value); }
    public OperatorWorkspaceTemplate? SelectedTemplate { get => _selectedTemplate; set { if (Set(ref _selectedTemplate, value)) NotifyTemplates(); } }
    public ICommand SaveTemplateCommand { get; private set; } = null!;
    public ICommand ApplyTemplateCommand { get; private set; } = null!;
    public ICommand DeleteTemplateCommand { get; private set; } = null!;
    public ICommand ImportTemplateCommand { get; private set; } = null!;
    public ICommand ExportTemplateCommand { get; private set; } = null!;
    internal void ConfigureTemplateFiles(Func<string?> importPicker, Func<string?> exportPicker, Action capturePanels)
    {
        _templateImportPicker = importPicker; _templateExportPicker = exportPicker; _capturePanels = capturePanels; NotifyTemplates();
    }
    private void InitializeWorkspaceTemplates(IEnumerable<OperatorWorkspaceTemplate> templates)
    {
        foreach (var template in templates) WorkspaceTemplates.Add(template);
        SaveTemplateCommand = new AsyncRelayCommand(async () =>
        {
            _capturePanels?.Invoke();
            var template = new OperatorWorkspaceTemplate(TemplateName.Trim(), _presentation?.Workspace ?? SelectedWorkspace, _presentation?.Layout ?? CurrentWorkspaceLayout());
            if (AddTemplate(template)) await PersistTemplatesAsync("Saved " + template.Name);
        }, () => NewTemplateNameValid());
        ApplyTemplateCommand = new OperatorShellCommand(() =>
        {
            if (SelectedTemplate is not { } selected || !WorkspaceTemplates.Contains(selected)) return;
            RestorePresentation(); SelectWorkspace(selected.Workspace); ApplyWorkspaceLayout(selected.Layout);
            Save(); TemplateStatus = "Applied " + selected.Name + " · production unchanged.";
        }, () => SelectedTemplate is not null && WorkspaceTemplates.Contains(SelectedTemplate));
        DeleteTemplateCommand = new AsyncRelayCommand(async () =>
        {
            if (SelectedTemplate is not { } selected) return;
            WorkspaceTemplates.Remove(selected); SelectedTemplate = null;
            await PersistTemplatesAsync("Removed local template.");
        }, () => SelectedTemplate is not null && WorkspaceTemplates.Contains(SelectedTemplate));
        ImportTemplateCommand = new AsyncRelayCommand(async () =>
        {
            if (_templateImportPicker?.Invoke() is { } path && !_templateFilesStopping)
            { _templateImportTask = ImportTemplateAsync(path); await _templateImportTask; }
        }, () => !_templateFilesStopping && _templateImportPicker is not null);
        ExportTemplateCommand = new AsyncRelayCommand(async () =>
        {
            if (SelectedTemplate is { } selected && _templateExportPicker?.Invoke() is { } path && !_templateFilesStopping)
            { _templateExportTask = ExportTemplateAsync(path, selected); await _templateExportTask; }
        }, () => !_templateFilesStopping && SelectedTemplate is not null && WorkspaceTemplates.Contains(SelectedTemplate) && _templateExportPicker is not null);
    }
    private async Task ExportTemplateAsync(string path, OperatorWorkspaceTemplate selected)
    {
        try
        {
            await Task.Run(() => OperatorWorkspaceTemplateFiles.ExportAsync(path, selected, _templateFileStop.Token), _templateFileStop.Token);
            TemplateStatus = "Exported " + selected.Name;
        }
        catch (OperationCanceledException) when (_templateFilesStopping) { TemplateStatus = "Export cancelled."; }
        catch (Exception ex) when (TemplateFileFailure(ex)) { TemplateStatus = "Export failed: " + ex.Message; }
    }
    internal async Task ImportTemplateAsync(string path)
    {
        if (_templateFilesStopping) { TemplateStatus = "Import cancelled."; return; }
        try
        {
            var imported = await Task.Run(() => OperatorWorkspaceTemplateFiles.ImportAsync(path, _templateFileStop.Token), _templateFileStop.Token);
            _templateFileStop.Token.ThrowIfCancellationRequested();
            if (AddTemplate(imported)) await PersistTemplatesAsync("Imported " + imported.Name + " · select Apply when ready.");
        }
        catch (OperationCanceledException) when (_templateFilesStopping) { TemplateStatus = "Import cancelled."; }
        catch (Exception ex) when (TemplateFileFailure(ex)) { TemplateStatus = "Import failed: " + ex.Message; }
    }
    internal async Task StopTemplateFilesAsync()
    {
        if (!_templateFilesStopping)
        {
            _templateFilesStopping = true; _templateFileStop.Cancel(); NotifyTemplates();
            await Task.WhenAll(_templateImportTask, _templateExportTask);
            _templateFileStop.Dispose();
        }
    }

    private bool NewTemplateNameValid() => WorkspaceTemplates.Count < OperatorWorkspaceTemplate.MaximumTemplates &&
        new OperatorWorkspaceTemplate(TemplateName.Trim(), SelectedWorkspace, CurrentWorkspaceLayout()).IsValid &&
        !WorkspaceTemplates.Any(t => string.Equals(t.Name, TemplateName.Trim(), StringComparison.OrdinalIgnoreCase));
    private bool AddTemplate(OperatorWorkspaceTemplate template)
    {
        if (!template.IsValid || WorkspaceTemplates.Count >= OperatorWorkspaceTemplate.MaximumTemplates ||
            WorkspaceTemplates.Any(t => string.Equals(t.Name, template.Name, StringComparison.OrdinalIgnoreCase)))
        { TemplateStatus = "Use a unique name; at most 16 templates are retained."; return false; }
        WorkspaceTemplates.Add(template with { Layout = template.Layout.Normalize() });
        SelectedTemplate = WorkspaceTemplates[^1]; NotifyTemplates(); return true;
    }
    private async Task PersistTemplatesAsync(string success)
    {
        await SaveAsync();
        TemplateStatus = _store.LastSaveError is { } error ? "Preferences were not saved: " + error : success;
        NotifyTemplates();
    }
    private static bool TemplateFileFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
        ArgumentException or InvalidOperationException or NotSupportedException;
    private void NotifyTemplates()
    {
        foreach (var command in new[] { SaveTemplateCommand, DeleteTemplateCommand, ImportTemplateCommand, ExportTemplateCommand })
            (command as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ApplyTemplateCommand as OperatorShellCommand)?.RaiseCanExecuteChanged();
    }
}
