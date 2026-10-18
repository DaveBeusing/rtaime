// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace rtaime.Operator;

public sealed record OperatorGuideStep(string Id, string Title, string Detail);
public sealed record OperatorGuideProgress(int Version, bool Enabled, bool Minimized, int Step, int ReviewedMask)
{
	public static OperatorGuideProgress Empty => new(1, true, false, 0, 0);
	[JsonIgnore]
	public bool IsValid => Version == 1 && Step >= 0 && Step < OperatorOnboardingViewModel.Steps.Count &&
		ReviewedMask >= 0 && ReviewedMask < (1 << OperatorOnboardingViewModel.Steps.Count);
}

// Presentation preferences only; no production identifiers, media paths or command authority.
public sealed class OperatorOnboardingStore(string path)
{
	public const int MaximumBytes = 4096;
	private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
	public async Task<OperatorGuideProgress> ReadAsync(CancellationToken token)
	{
		if (!File.Exists(path)) return OperatorGuideProgress.Empty;
		await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
		var bytes = new byte[MaximumBytes + 1];
		var count = 0;
		while (count < bytes.Length)
		{
			var read = await stream.ReadAsync(bytes.AsMemory(count), token);
			if (read == 0) break;
			count += read;
		}
		if (count > MaximumBytes) throw new InvalidDataException("Guide preferences exceed 4 KiB.");
		using var document = JsonDocument.Parse(bytes.AsMemory(0, count));
		var properties = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
		if (properties.Length != 5 || properties.Distinct(StringComparer.Ordinal).Count() != 5)
			throw new InvalidDataException("Guide preferences require five unique fields.");
		var value = JsonSerializer.Deserialize<OperatorGuideProgress>(bytes.AsSpan(0, count), Options);
		return value?.IsValid == true ? value : throw new InvalidDataException("Unsupported guide preferences.");
	}
	public async Task WriteAsync(OperatorGuideProgress value)
	{
		if (!value.IsValid) throw new InvalidDataException("Invalid guide preferences.");
		var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			var directory = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
			await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(value));
			File.Move(temporary, path, overwrite: true);
		}
		finally
		{
			try { if (File.Exists(temporary)) File.Delete(temporary); }
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
		}
	}
}

public sealed class OperatorOnboardingViewModel : INotifyPropertyChanged, IDisposable
{
	public static IReadOnlyList<OperatorGuideStep> Steps { get; } = Array.AsReadOnly<OperatorGuideStep>(
	[
		new("import", "1 · Import test media",
			"Start in Media Library with explicitly designated TEST MEDIA and an off-air production. An empty library needs an import through its existing controls. Missing or offline media needs Refresh / Relink there. Inspect the selected asset before loading it. This guide never imports, loads or writes a saved production."),
		new("preflight", "2 · Review show checks",
			"Open Show checks. Review assets, cues, audio, output roles and saved state. UNKNOWN, UNVERIFIED or stale evidence is not a pass. Refresh checks only reevaluates received evidence; refresh missing catalogue evidence in Media Library. A denied request stays denied; inspect its reason and your role. The guide cannot grant readiness or permissions."),
		new("preview", "3 · Prepare Preview",
			"Use TEST MEDIA. Explicitly load and select Preview with the existing guarded controls, then inspect the green Preview canvas on the left. Verify source identity, framing and audio. A retained image marked STALE does not prove current playback. Control loss blocks affected mutations; monitoring loss only limits observation."),
		new("take", "4 · Review before Program",
			"The red Program canvas is on the right. TAKE / CUT / DISSOLVE is a deliberate live operation: verify the intended source and existing readiness gates first. Use only test media in an off-air exercise. Reading or marking this step reviewed never sends TAKE, GO, Seek or any command. If a reply is lost, synchronize and inspect action history and the authoritative revision before deciding on a new explicit action."),
		new("cues", "5 · Prepare cues and rundown",
			"In Scenes / Show Control, inspect cue references, dependencies and the selected rundown. ARM / GO / CANCEL remain explicit existing controls. An asset marker is not a show-control cue. After disconnect or restart inspect restored authoritative state; never replay an uncertain GO. Durable sequence authoring is available through its Control contract; a complete multitrack Operator editor is not supplied by this guide."),
		new("outputs", "6 · Review outputs",
			"In Outputs, inspect Program and each AUX role separately. Review recording target evidence and actual sent / written samples where published. Healthy monitoring does not certify a physical output, SSD, codec or hardware. Starting an output or recording requires its own explicit existing action. Open Health for incidents and export a support bundle through the existing bounded exporter."),
		new("layout", "7 · Restore the workspace",
			"Use Workspace templates to preview and explicitly apply a presentation layout, or restore the existing safe layout through its normal controls. Layout recovery changes presentation only and never starts outputs. Output canvas configuration has a durable authoring contract; Runtime mapping activation and a graphical mapping editor must be independently implemented and qualified. You can minimize or turn off this guide and reopen it from Help.")
	]);
	private readonly OperatorOnboardingStore _store;
	private OperatorGuideProgress _progress = OperatorGuideProgress.Empty;
	private OperatorGuideProgress? _pending;
	private Task _save = Task.CompletedTask;
	private readonly object _saveGate = new();
	private bool _saving, _loaded, _disposed;
	public OperatorOnboardingViewModel(OperatorOnboardingStore store)
	{
		_store = store;
		PreviousCommand = new NotificationCommand(() => Change(_progress with { Step = _progress.Step - 1 }), () => CanEdit && _progress.Step > 0);
		NextCommand = new NotificationCommand(() => Change(_progress with { Step = _progress.Step + 1 }), () => CanEdit && _progress.Step < Steps.Count - 1);
		ReviewCommand = new NotificationCommand(() => Change(_progress with { ReviewedMask = _progress.ReviewedMask | (1 << _progress.Step) }), () => CanEdit && !IsReviewed);
		MinimizeCommand = new NotificationCommand(() => Change(_progress with { Minimized = true }), () => CanEdit);
		DisableCommand = new NotificationCommand(() => Change(_progress with { Enabled = false }), () => CanEdit);
		ReopenCommand = new NotificationCommand(() => Change(_progress with { Enabled = true, Minimized = false }), () => CanEdit);
	}
	public event PropertyChangedEventHandler? PropertyChanged;
	public ICommand PreviousCommand { get; }
	public ICommand NextCommand { get; }
	public ICommand ReviewCommand { get; }
	public ICommand MinimizeCommand { get; }
	public ICommand DisableCommand { get; }
	public ICommand ReopenCommand { get; }
	private bool CanEdit => _loaded && !_disposed;
	public bool IsExpanded => _progress.Enabled && !_progress.Minimized;
	public bool IsReviewed => (_progress.ReviewedMask & (1 << _progress.Step)) != 0;
	public OperatorGuideStep Current => Steps[_progress.Step];
	public string Progress => $"{System.Numerics.BitOperations.PopCount((uint)_progress.ReviewedMask)} / {Steps.Count} topics reviewed locally · not production readiness";
	public string Status { get; private set; } = "Loading local guide preferences…";
	public async Task LoadAsync(CancellationToken token = default)
	{
		if (_loaded || _disposed) return;
		try
		{
			token.ThrowIfCancellationRequested();
			var progress = await _store.ReadAsync(token);
			if (_disposed) return;
			_progress = progress;
			Status = "Guidance only. Examples require TEST MEDIA; no production action runs.";
		}
		catch (OperationCanceledException) { throw; }
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
		{
			Status = "Guide preferences unavailable or unsupported. Original file retained; explicit guide edits replace only local guide preferences.";
		}
		_loaded = true; Notify();
	}
	private void Change(OperatorGuideProgress progress)
	{
		if (!CanEdit) return;
		_progress = progress; Status = "Saving local guide progress…";
		lock (_saveGate)
		{
			_pending = progress;
			if (!_saving) { _saving = true; _save = SaveAsync(); }
		}
		Notify();
	}
	private async Task SaveAsync()
	{
		await Task.Yield();
		while (true)
		{
			OperatorGuideProgress progress;
			lock (_saveGate)
			{
				if (_pending is null) { _saving = false; return; }
				progress = _pending; _pending = null;
			}
			try
			{
				await _store.WriteAsync(progress);
				if (!_disposed && ReferenceEquals(_progress, progress)) Status = "Guide progress saved locally. Production state unchanged.";
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				if (!_disposed) Status = "Session only: guide preferences could not be saved. Production state unchanged.";
			}
			if (!_disposed) Notify();
		}
	}
	public Task FlushAsync() { lock (_saveGate) return _save; }
	public void Dispose() { _disposed = true; Notify(); }
	private void Notify()
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
		foreach (var command in new[] { PreviousCommand, NextCommand, ReviewCommand, MinimizeCommand, DisableCommand, ReopenCommand })
			(command as NotificationCommand)?.RaiseCanExecuteChanged();
	}
}
