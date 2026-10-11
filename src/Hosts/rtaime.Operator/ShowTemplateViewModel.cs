// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.Win32;
using rtaime.Client;
using rtaime.Control.Contracts;

namespace rtaime.Operator;

public sealed record ShowTemplateTarget(string Id, string Name);

public sealed class ShowTemplateBindingViewModel : INotifyPropertyChanged
{
	private string _targetId;
	public ShowTemplateBindingViewModel(ShowTemplateDependency dependency, IReadOnlyList<ShowTemplateTarget> choices)
	{
		Dependency = dependency;
		Choices = choices;
		_targetId = dependency.ReferenceId;
	}
	public event PropertyChangedEventHandler? PropertyChanged;
	public ShowTemplateDependency Dependency { get; }
	public IReadOnlyList<ShowTemplateTarget> Choices { get; }
	public string Label => Dependency.Kind.ToUpperInvariant() + " · " + Dependency.Name;
	public string TargetId
	{
		get => _targetId;
		set { if (_targetId == value) return; _targetId = value; PropertyChanged?.Invoke(this, new(nameof(TargetId))); }
	}
	public ShowTemplateBinding ToContract() => new(Dependency.Kind, Dependency.ReferenceId, TargetId);
}

public sealed class ShowTemplateViewModel : INotifyPropertyChanged
{
	private readonly OperatorControlClient _client;
	private readonly Func<string?> _selectedCueListId;
	private readonly Action<ShowControlWorkspaceSnapshot> _confirmed;
	private string? _draftJson;
	private ShowTemplatePreflight? _preview;
	private string _name = "Show setup";
	private string _instanceName = "Imported show";
	private string _status = "NO DRAFT";
	private string _detail = "Create from the current show or import a portable template. Preview all dependencies before saving.";
	private bool _busy;
	private ulong _draftRevision;
	private ulong _previewRevision;
	public bool IsIdle => !_busy;

	public ShowTemplateViewModel(OperatorControlClient client, Func<string?> selectedCueListId,
		Action<ShowControlWorkspaceSnapshot> confirmed)
	{
		_client = client;
		_selectedCueListId = selectedCueListId;
		_confirmed = confirmed;
		CreateCurrentCommand = new AsyncRelayCommand(() => CreateAsync(null), () => !_busy);
		CreateSelectedCommand = new AsyncRelayCommand(() => CreateAsync(_selectedCueListId()),
			() => !_busy && _selectedCueListId() is not null);
		ImportCommand = new AsyncRelayCommand(ImportAsync, () => !_busy);
		ExportCommand = new AsyncRelayCommand(ExportAsync, () => !_busy && _draftJson is not null);
		PreviewCommand = new AsyncRelayCommand(PreviewAsync, () => !_busy && _draftJson is not null);
		CommitCommand = new AsyncRelayCommand(CommitAsync, () => !_busy && _preview?.CanCommit == true);
	}

	public event PropertyChangedEventHandler? PropertyChanged;
	public ObservableCollection<ShowTemplateBindingViewModel> Bindings { get; } = [];
	public ICommand CreateCurrentCommand { get; }
	public ICommand CreateSelectedCommand { get; }
	public ICommand ImportCommand { get; }
	public ICommand ExportCommand { get; }
	public ICommand PreviewCommand { get; }
	public ICommand CommitCommand { get; }
	public string TemplateName { get => _name; set { _name = value; Changed(); } }
	public string InstanceName { get => _instanceName; set { if (_instanceName == value) return; _instanceName = value; InvalidatePreview(); Changed(); } }
	public string Status { get => _status; private set { _status = value; Changed(); } }
	public string Detail { get => _detail; private set { _detail = value; Changed(); } }
	public string? DraftJson => _draftJson;

	public void LoadDraft(string json, IReadOnlyList<ShowTemplateTarget>? mediaChoices = null)
	{
		var document = ShowTemplateSerializer.Deserialize(json);
		_draftJson = json;
		TemplateName = document.Name;
		foreach (var binding in Bindings) binding.PropertyChanged -= BindingChanged;
		Bindings.Clear();
		var snapshot = _client.Snapshot;
		foreach (var dependency in document.Content.Dependencies)
		{
			IReadOnlyList<ShowTemplateTarget> choices = dependency.Kind switch
			{
				"source" => snapshot?.Sources.Select(source => new ShowTemplateTarget(source.Id, source.Name)).ToArray() ?? [],
				"output" => snapshot?.OutputRoles.Select(role => new ShowTemplateTarget(role.RoleId, role.RoleKind)).ToArray() ?? [],
				"media" => mediaChoices ?? [],
				"mediaCue" => snapshot?.MediaDeck.Markers?.CuePoints.Select(cue => new ShowTemplateTarget(cue.Id.ToString(), cue.Name)).ToArray() ?? [],
				"layer" => snapshot?.Production.CompositingState?.Layers.Select(layer => new ShowTemplateTarget(layer.LayerId, layer.LayerId)).ToArray() ?? [],
				_ => []
			};
			var binding = new ShowTemplateBindingViewModel(dependency, choices);
			binding.PropertyChanged += BindingChanged;
			Bindings.Add(binding);
		}
		InvalidatePreview();
		Status = "DRAFT";
		Detail = $"{document.Name} · {Bindings.Count} dependencies. Choose local targets, then preview. Imported definitions are not committed.";
		Changed(nameof(DraftJson));
		RaiseCommandState();
	}

	public async Task PreviewAsync() => await RunAsync(async () =>
	{
		var request = Request();
		var revision = _draftRevision;
		var preview = await _client.PreflightShowTemplateAsync(request);
		if (revision != _draftRevision) { InvalidatePreview(); return; }
		_preview = preview;
		_previewRevision = revision;
		Status = preview.CanCommit ? "PREVIEW READY" : "CONFLICT";
		Detail = string.Join(Environment.NewLine, preview.Changes.Concat(preview.Conflicts));
	});

	public async Task CommitAsync() => await RunAsync(async () =>
	{
		var preview = _preview ?? throw new InvalidOperationException("Preview the draft first.");
		if (_previewRevision != _draftRevision || !preview.CanCommit) throw new InvalidOperationException("Resolve all preflight conflicts first.");
		var snapshot = await _client.InstantiateShowTemplateAsync(Request() with {
			ExpectedProjectVersion = preview.ProjectVersion, ExpectedWorkspaceVersion = preview.WorkspaceVersion });
		_preview = null;
		_confirmed(snapshot);
		Status = "COMMITTED";
		Detail = "New scenes and cue list saved by ControlHost. Select the cue list explicitly; ARM and GO remain separate operator actions.";
	});

	private async Task CreateAsync(string? cueListId) => await RunAsync(async () =>
		await LoadWithTargetsAsync(await _client.CreateShowTemplateAsync(TemplateName, cueListId)));

	private async Task LoadWithTargetsAsync(string json)
	{
		var document = ShowTemplateSerializer.Deserialize(json);
		IReadOnlyList<ShowTemplateTarget>? choices = null;
		if (document.Content.Dependencies.Any(d => d.Kind == "media"))
			choices = (await _client.GetMediaAssetCatalogAsync()).Assets
				.Select(asset => new ShowTemplateTarget(asset.AssetId.ToString(), asset.DisplayName)).ToArray();
		LoadDraft(json, choices);
	}

	private async Task ImportAsync() => await RunAsync(async () =>
	{
		var dialog = new OpenFileDialog { Filter = "Show templates (*.json)|*.json", CheckFileExists = true };
		if (dialog.ShowDialog() != true) return;
		if (new FileInfo(dialog.FileName).Length > ShowTemplateSerializer.MaximumBytes)
			throw new InvalidDataException("Template exceeds the 256 KiB portable limit.");
		await LoadWithTargetsAsync(await File.ReadAllTextAsync(dialog.FileName));
	});

	private async Task ExportAsync() => await RunAsync(async () =>
	{
		var json = _draftJson ?? throw new InvalidOperationException("Create or import a template first.");
		var dialog = new SaveFileDialog { Filter = "Show templates (*.json)|*.json", FileName = "show.rtaime-template.json", OverwritePrompt = true };
		if (dialog.ShowDialog() != true) return;
		// Portable exports never replace an existing file, even after a dialog race.
		await using var stream = new FileStream(dialog.FileName, FileMode.CreateNew, FileAccess.Write, FileShare.None);
		await using var writer = new StreamWriter(stream);
		await writer.WriteAsync(json);
		Detail = "Portable template exported. Media, graphics payloads, credentials and workspace layouts are not packaged.";
	});

	private ShowTemplateRequest Request() => new(_draftJson ?? throw new InvalidOperationException("Import a template first."),
		InstanceName, Bindings.Select(binding => binding.ToContract()).ToArray());
	private void BindingChanged(object? sender, PropertyChangedEventArgs args) => InvalidatePreview();
	private void InvalidatePreview()
	{
		_draftRevision++;
		_preview = null;
		if (_draftJson is not null) { Status = "DRAFT"; Detail = "Draft changed. Preview again before committing."; }
		RaiseCommandState();
	}
	private async Task RunAsync(Func<Task> action)
	{
		if (_busy) return;
		_busy = true;
		Changed(nameof(IsIdle));
		RaiseCommandState();
		try { await action(); }
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or
			NotSupportedException or System.Text.Json.JsonException or FormatException or TimeoutException)
		{
			_preview = null;
			Status = "REJECTED / REFRESH REQUIRED";
			Detail = exception.Message;
		}
		finally { _busy = false; Changed(nameof(IsIdle)); RaiseCommandState(); }
	}
	public void RaiseCommandState()
	{
		foreach (var command in new[] { CreateCurrentCommand, CreateSelectedCommand, ImportCommand,
			ExportCommand, PreviewCommand, CommitCommand }.OfType<AsyncRelayCommand>()) command.RaiseCanExecuteChanged();
	}
	private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
