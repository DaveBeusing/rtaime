// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Input;
using rtaime.Client;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

/// <summary>
/// Loads the single bundled AP-56 funding-demo package through existing Operator/Client control seams.
/// This is showcase bootstrap orchestration, not a general Production Package authority or persistence subsystem.
/// </summary>
public sealed class DemoProductionPackageController : INotifyPropertyChanged
{
	private const string SupportedSchema = "rtaime.demo.production-package/1";
	private readonly OperatorControlClient _client;
	private readonly OperatorViewModel _operator;
	private readonly MediaDeckViewModel _mediaDeck;
	private string _state = "IDLE";
	private string _detail = "Demo package has not been opened.";
	private string _summary = "Input A/B · Product Clip · Lower Third · Cue Points · Audio · AI";
	private bool _isBusy;
	private DateTimeOffset? _openedAtUtc;

	public DemoProductionPackageController(
		OperatorControlClient client,
		OperatorViewModel operatorViewModel,
		MediaDeckViewModel mediaDeck)
	{
		_client = client ?? throw new ArgumentNullException(nameof(client));
		_operator = operatorViewModel ?? throw new ArgumentNullException(nameof(operatorViewModel));
		_mediaDeck = mediaDeck ?? throw new ArgumentNullException(nameof(mediaDeck));
		OpenCommand = new AsyncRelayCommand(OpenAsync, () => !IsBusy);
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public ICommand OpenCommand { get; }
	public string State { get => _state; private set => Set(ref _state, value); }
	public string Detail { get => _detail; private set => Set(ref _detail, value); }
	public string Summary { get => _summary; private set => Set(ref _summary, value); }
	public bool IsBusy
	{
		get => _isBusy;
		private set
		{
			if (!Set(ref _isBusy, value))
				return;
			(OpenCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		}
	}
	public string OpenedAt => _openedAtUtc is { } opened
		? opened.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
		: "—";

	private async Task OpenAsync()
	{
		if (IsBusy)
			return;

		IsBusy = true;
		State = "LOADING";
		Detail = "Synchronizing authoritative production state.";
		try
		{
			var package = LoadPackage();
			var snapshot = await _client.SynchronizeAsync();
			_operator.ApplyConfirmedSnapshot(snapshot);

			var programSource = RequireSource(snapshot, package.Sources.Program);
			var productSource = RequireSource(snapshot, package.Sources.ProductClip);
			if (string.Equals(programSource.Id, productSource.Id, StringComparison.Ordinal))
				throw new InvalidDataException("Demo package Program and Product Clip sources must be distinct.");

			await RestoreProgramSourceAsync(programSource, snapshot);

			Detail = "Materializing integrity-checked bundled demo assets.";
			var productPath = MaterializeBundledAsset(package.ProductClip.BundleFiles, package.ProductClip.MaterializedFile, package.ProductClip.Sha256);
			var lowerThirdPath = MaterializeBundledAsset(package.Graphics.BundleFiles, package.Graphics.MaterializedFile, package.Graphics.Sha256);

			Detail = "Preparing Product Clip, IN/OUT, cue points and playback policy.";
			var cues = package.ProductClip.CuePoints
				.Select(cue => (cue.Name, cue.Frame))
				.ToArray();
			await _mediaDeck.PrepareDemoPackageAsync(
				productPath,
				new MediaSourceId(Identity.Parse(productSource.Id)),
				package.ProductClip.InFrame,
				cues,
				package.ProductClip.AutoPlayOnProgram,
				ParseEndBehavior(package.ProductClip.EndBehavior));

			Detail = "Preparing Program audio and pre-rendered lower third.";
			await _client.SetAudioInputStateAsync(
				productSource.Id,
				package.Audio.Gain,
				package.Audio.Muted);

			var graphicsAsset = GraphicsOverlayAssetLoader.LoadPng(lowerThirdPath);
			await _client.LoadGraphicsOverlayAsync(graphicsAsset);
			await _client.SetGraphicsOverlayAsync(
				package.Graphics.InitialVisible,
				package.Graphics.PositionX,
				package.Graphics.PositionY,
				package.Graphics.Scale);

			Detail = "Enabling governed AI showcase.";
			await _client.SetAIShowcaseEnabledAsync(package.AIShowcase.Enabled);

			_operator.TransitionFrames = package.Transition.DissolveFrames;
			var current = await _client.SynchronizeAsync();
			if (!string.Equals(current.Production.Routing.PreviewSourceId.ToString(), productSource.Id, StringComparison.Ordinal))
			{
				var preview = await _client.SelectPreviewAsync(productSource.Id);
				if (!preview.Accepted)
					throw new InvalidOperationException(preview.Failure?.Message ?? "Product Clip could not be selected for Preview.");
			}

			var confirmed = await _client.SynchronizeAsync();
			ValidateReadyState(package, confirmed, programSource, productSource);
			_operator.ApplyConfirmedSnapshot(confirmed);

			_openedAtUtc = DateTimeOffset.UtcNow;
			OnPropertyChanged(nameof(OpenedAt));
			State = "READY";
			Summary = $"PGM {programSource.Name} · PVW Product Clip/{productSource.Name} · AUTO {package.Transition.DissolveFrames}F · {cues.Length} cues · Lower Third READY · Audio 1.0x · AI ON";
			Detail = "Demo Production is prepared and TAKE-ready. Lower Third is loaded but initially hidden so the AI highlight remains visible.";
		}
		catch (OperationCanceledException)
		{
			State = "FAILED";
			Detail = "Demo Production synchronization was canceled because the engine endpoint became unavailable or the operation was stopped.";
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException or FormatException or UnauthorizedAccessException or CryptographicException or TimeoutException)
		{
			State = "FAILED";
			Detail = exception.Message;
		}
		finally
		{
			IsBusy = false;
		}
	}

	private async Task RestoreProgramSourceAsync(OperatorSourceDescriptor programSource, OperatorStatusSnapshot snapshot)
	{
		if (string.Equals(snapshot.Production.Routing.ProgramSourceId.ToString(), programSource.Id, StringComparison.Ordinal))
			return;

		if (!string.Equals(snapshot.Production.Routing.PreviewSourceId.ToString(), programSource.Id, StringComparison.Ordinal))
		{
			var preview = await _client.SelectPreviewAsync(programSource.Id);
			if (!preview.Accepted)
				throw new InvalidOperationException(preview.Failure?.Message ?? "Demo Program source could not be restored to Preview.");
		}

		var cut = await _client.CutAsync(programSource.Id);
		if (!cut.Accepted)
			throw new InvalidOperationException(cut.Failure?.Message ?? "Demo Program source could not be restored to Program.");
	}

	private static OperatorSourceDescriptor RequireSource(OperatorStatusSnapshot snapshot, string name)
	{
		var matches = snapshot.Sources
			.Where(source => string.Equals(source.Name, name, StringComparison.Ordinal))
			.ToArray();
		return matches.Length switch
		{
			1 => matches[0],
			0 => throw new InvalidDataException($"Demo package requires production source '{name}'."),
			_ => throw new InvalidDataException($"Demo package source name '{name}' is ambiguous.")
		};
	}

	private static DemoProductionPackageDefinition LoadPackage()
	{
		var path = Path.Combine(AppContext.BaseDirectory, "DemoAssets", "demo-production.package.json");
		if (!File.Exists(path))
			throw new FileNotFoundException("Bundled Demo Production manifest is missing.", path);
		var package = JsonSerializer.Deserialize<DemoProductionPackageDefinition>(
			File.ReadAllText(path),
			new JsonSerializerOptions(JsonSerializerDefaults.Web))
			?? throw new InvalidDataException("Demo Production manifest is empty.");

		ValidatePackage(package);
		return package;
	}

	private static void ValidatePackage(DemoProductionPackageDefinition package)
	{
		if (!string.Equals(package.Schema, SupportedSchema, StringComparison.Ordinal))
			throw new InvalidDataException($"Unsupported Demo Production schema '{package.Schema}'.");
		if (!string.Equals(package.Version, "1.0", StringComparison.Ordinal))
			throw new InvalidDataException($"Unsupported Demo Production package version '{package.Version}'.");
		if (string.IsNullOrWhiteSpace(package.Sources.Program) || string.IsNullOrWhiteSpace(package.Sources.ProductClip))
			throw new InvalidDataException("Demo Production source names are required.");
		if (package.ProductClip.InFrame < 0)
			throw new InvalidDataException("Demo Product Clip IN frame must not be negative.");
		if (!string.Equals(package.ProductClip.OutFrame, "last", StringComparison.Ordinal))
			throw new InvalidDataException("AP-56 supports only a deterministic last-frame OUT marker.");
		if (package.ProductClip.CuePoints.Count == 0 ||
			package.ProductClip.CuePoints.Any(cue => string.IsNullOrWhiteSpace(cue.Name) || cue.Frame < 0))
			throw new InvalidDataException("Demo Product Clip requires valid cue points.");
		if (package.ProductClip.CuePoints.Select(cue => cue.Name).Distinct(StringComparer.Ordinal).Count() != package.ProductClip.CuePoints.Count)
			throw new InvalidDataException("Demo Product Clip cue names must be unique.");
		_ = ParseEndBehavior(package.ProductClip.EndBehavior);
		ValidateAsset(package.ProductClip.BundleFiles, package.ProductClip.MaterializedFile, package.ProductClip.Sha256, ".mp4");
		ValidateAsset(package.Graphics.BundleFiles, package.Graphics.MaterializedFile, package.Graphics.Sha256, ".png");
		if (!double.IsFinite(package.Graphics.PositionX) || package.Graphics.PositionX is < 0 or > 1 ||
			!double.IsFinite(package.Graphics.PositionY) || package.Graphics.PositionY is < 0 or > 1 ||
			!double.IsFinite(package.Graphics.Scale) || package.Graphics.Scale is < 0.05 or > 4)
			throw new InvalidDataException("Demo graphics placement is outside V1 bounds.");
		if (package.Graphics.InitialVisible)
			throw new InvalidDataException("AP-56 lower third must start hidden so the AP-55 AI highlight is not falsely presented as simultaneously visible.");
		if (package.Transition.DissolveFrames < 2)
			throw new InvalidDataException("Demo DISSOLVE duration must be at least two frames.");
		if (!double.IsFinite(package.Audio.Gain) || package.Audio.Gain is < 0 or > 4)
			throw new InvalidDataException("Demo audio gain must be in the inclusive range 0..4.");
		if (!string.Equals(package.Audio.Source, package.Sources.ProductClip, StringComparison.Ordinal))
			throw new InvalidDataException("Demo audio source must match the Product Clip source.");
		if (package.Audio.Muted)
			throw new InvalidDataException("Demo Product Clip audio must start unmuted.");
		if (!string.Equals(package.Graphics.Kind, "PreRenderedLowerThirdWithRtaimeLogo", StringComparison.Ordinal))
			throw new InvalidDataException("AP-56 graphics asset must remain the pre-rendered lower-third/logo package asset.");
		if (!package.AIShowcase.Enabled ||
			!string.Equals(package.AIShowcase.Feature, "Person Segmentation Highlight", StringComparison.Ordinal))
			throw new InvalidDataException("AP-56 requires the AP-55 Person Segmentation Highlight showcase.");
	}

	private static void ValidateAsset(
		IReadOnlyList<string> bundleFiles,
		string materializedFile,
		string sha256,
		string expectedExtension)
	{
		ArgumentNullException.ThrowIfNull(bundleFiles);
		if (bundleFiles.Count == 0)
			throw new InvalidDataException("Demo bundle requires at least one asset chunk.");
		foreach (var bundleFile in bundleFiles)
		{
			if (string.IsNullOrWhiteSpace(bundleFile) ||
				!string.Equals(Path.GetFileName(bundleFile), bundleFile, StringComparison.Ordinal))
				throw new InvalidDataException("Demo bundle asset chunk name must be a simple file name.");
		}
		if (bundleFiles.Distinct(StringComparer.Ordinal).Count() != bundleFiles.Count)
			throw new InvalidDataException("Demo bundle asset chunk names must be unique.");
		if (string.IsNullOrWhiteSpace(materializedFile) ||
			!string.Equals(Path.GetFileName(materializedFile), materializedFile, StringComparison.Ordinal))
			throw new InvalidDataException("Demo materialized asset name must be a simple file name.");
		if (!string.Equals(Path.GetExtension(materializedFile), expectedExtension, StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException($"Demo materialized asset must use '{expectedExtension}'.");
		if (sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))
			throw new InvalidDataException("Demo bundle asset SHA-256 must contain exactly 64 hexadecimal characters.");
	}

	private static string MaterializeBundledAsset(
		IReadOnlyList<string> bundleFiles,
		string materializedFile,
		string expectedSha256)
	{
		ArgumentNullException.ThrowIfNull(bundleFiles);
		var encoded = string.Concat(bundleFiles.Select(ReadBundleChunk));
		byte[] bytes;
		try { bytes = Convert.FromBase64String(encoded); }
		catch (FormatException exception) { throw new InvalidDataException($"Bundled Demo Production asset '{materializedFile}' is not valid base64.", exception); }

		var actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
		if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
			throw new CryptographicException($"Bundled Demo Production asset '{materializedFile}' failed SHA-256 verification.");

		var root = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"rtaime",
			"demo",
			"v1");
		Directory.CreateDirectory(root);
		var destination = Path.Combine(root, materializedFile);
		if (File.Exists(destination))
		{
			var currentHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(destination)));
			if (string.Equals(currentHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
				return destination;
		}

		var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			File.WriteAllBytes(temporary, bytes);
			File.Move(temporary, destination, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporary))
				File.Delete(temporary);
		}
		return destination;
	}

	private static string ReadBundleChunk(string bundleFile)
	{
		var bundlePath = Path.Combine(AppContext.BaseDirectory, "DemoAssets", bundleFile);
		if (!File.Exists(bundlePath))
			throw new FileNotFoundException($"Bundled Demo Production asset chunk '{bundleFile}' is missing.", bundlePath);
		return string.Concat(
			File.ReadLines(bundlePath)
				.Select(line => line.Trim())
				.Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal)));
	}

	private static MediaDeckEndBehavior ParseEndBehavior(string value) =>
		Enum.TryParse<MediaDeckEndBehavior>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
			? parsed
			: throw new InvalidDataException($"Unsupported Demo Product Clip end behavior '{value}'.");

	private void ValidateReadyState(
		DemoProductionPackageDefinition package,
		OperatorStatusSnapshot snapshot,
		OperatorSourceDescriptor programSource,
		OperatorSourceDescriptor productSource)
	{
		if (!string.Equals(snapshot.Production.Routing.ProgramSourceId.ToString(), programSource.Id, StringComparison.Ordinal))
			throw new InvalidDataException("Demo package validation failed: Program is not Input A.");
		if (!string.Equals(snapshot.Production.Routing.PreviewSourceId.ToString(), productSource.Id, StringComparison.Ordinal))
			throw new InvalidDataException("Demo package validation failed: Product Clip is not on Preview.");
		if (!snapshot.GraphicsOverlay.AssetLoaded || snapshot.GraphicsOverlay.Visible != package.Graphics.InitialVisible)
			throw new InvalidDataException("Demo package validation failed: lower-third graphics state was not confirmed.");
		if (snapshot.AIShowcase.Enabled != package.AIShowcase.Enabled)
			throw new InvalidDataException("Demo package validation failed: AI showcase enable state was not confirmed.");

		var deck = _mediaDeck.ConfirmedSnapshot;
		if (!deck.IsLoaded || deck.SourceId?.ToString() != productSource.Id)
			throw new InvalidDataException("Demo package validation failed: Product Clip was not confirmed on Input B.");
		if (deck.Markers?.InPointFrame != package.ProductClip.InFrame ||
			deck.Markers.OutPointFrame != deck.Transport!.Position.TotalFrames - 1 ||
			deck.Markers.CuePoints.Count != package.ProductClip.CuePoints.Count)
			throw new InvalidDataException("Demo package validation failed: Product Clip markers do not match the package.");
		if (deck.Transport.AutoPlayOnProgram != package.ProductClip.AutoPlayOnProgram ||
			deck.Transport.EndBehavior != ParseEndBehavior(package.ProductClip.EndBehavior))
			throw new InvalidDataException("Demo package validation failed: Product Clip playback policy was not confirmed.");
	}

	private void OnPropertyChanged([CallerMemberName] string? name = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

	private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
			return false;
		field = value;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
		return true;
	}

	private sealed record DemoProductionPackageDefinition(
		string Schema,
		string Name,
		string Version,
		DemoSources Sources,
		DemoProductClip ProductClip,
		DemoGraphics Graphics,
		DemoTransition Transition,
		DemoAudio Audio,
		DemoAIShowcase AIShowcase);

	private sealed record DemoSources(string Program, string ProductClip);
	private sealed record DemoProductClip(
		IReadOnlyList<string> BundleFiles,
		string MaterializedFile,
		string Sha256,
		bool AutoPlayOnProgram,
		string EndBehavior,
		long InFrame,
		string OutFrame,
		IReadOnlyList<DemoCuePoint> CuePoints);
	private sealed record DemoCuePoint(string Name, long Frame);
	private sealed record DemoGraphics(
		IReadOnlyList<string> BundleFiles,
		string MaterializedFile,
		string Sha256,
		string Kind,
		bool InitialVisible,
		double PositionX,
		double PositionY,
		double Scale);
	private sealed record DemoTransition(uint DissolveFrames);
	private sealed record DemoAudio(string Source, double Gain, bool Muted);
	private sealed record DemoAIShowcase(bool Enabled, string Feature);
}
