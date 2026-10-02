// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Recording;

public interface IProgramRecordingWriterProvider
{
	RecordingWriterProviderId ProviderId { get; }
	string DisplayName { get; }
	IReadOnlyList<RecordingProfileDescriptor> Profiles { get; }
	IProgramRecordingPayloadWriter CreateWriter(RecordingProfileId profileId);
}

public interface IProfileConfigurableProgramRecordingWriter : IConfigurableProgramRecordingWriter
{
	string ConfigureTarget(
		RecordingProfileId? profileId,
		string destinationDirectory,
		string fileName);
}

public interface IProgramRecordingProfileStateProvider : IProgramRecordingProfileCatalogProvider
{
	RecordingProfileDescriptor? ActiveProfile { get; }
	RecordingWriterProviderId? ActiveProviderId { get; }
}

public sealed record RecordingWriterSelection(
	RecordingProfileDescriptor Profile,
	IProgramRecordingWriterProvider Provider);

public sealed class RecordingWriterProviderRegistry : IProgramRecordingProfileCatalogProvider
{
	private readonly IReadOnlyDictionary<RecordingProfileId, IProgramRecordingWriterProvider> _providerByProfile;

	public RecordingWriterProviderRegistry(
		IEnumerable<IProgramRecordingWriterProvider> providers,
		RecordingProfileId defaultProfileId)
	{
		ArgumentNullException.ThrowIfNull(providers);
		var materialized = providers.ToArray();
		if (materialized.Length == 0)
			throw new ArgumentException("At least one recording writer provider is required.", nameof(providers));

		var duplicateProviders = materialized
			.GroupBy(provider => provider.ProviderId)
			.Where(group => group.Count() > 1)
			.Select(group => group.Key.ToString())
			.ToArray();
		if (duplicateProviders.Length != 0)
			throw new ArgumentException($"Duplicate recording writer provider identities: {string.Join(", ", duplicateProviders)}.", nameof(providers));

		var profiles = new List<RecordingProfileDescriptor>();
		var providerByProfile = new Dictionary<RecordingProfileId, IProgramRecordingWriterProvider>();
		foreach (var provider in materialized)
		{
			if (string.IsNullOrWhiteSpace(provider.DisplayName))
				throw new ArgumentException($"Recording provider '{provider.ProviderId}' requires a display name.", nameof(providers));
			if (provider.Profiles is null || provider.Profiles.Count == 0)
				throw new ArgumentException($"Recording provider '{provider.ProviderId}' must advertise at least one profile.", nameof(providers));

			foreach (var profile in provider.Profiles)
			{
				if (profile.ProviderId != provider.ProviderId)
					throw new ArgumentException($"Recording profile '{profile.ProfileId}' provider identity does not match provider '{provider.ProviderId}'.", nameof(providers));
				if (!providerByProfile.TryAdd(profile.ProfileId, provider))
					throw new ArgumentException($"Duplicate recording profile identity '{profile.ProfileId}' across providers.", nameof(providers));
				profiles.Add(profile);
			}
		}

		ProfileCatalog = new RecordingProfileCatalog(profiles, defaultProfileId);
		_providerByProfile = providerByProfile;
	}

	public RecordingProfileCatalog ProfileCatalog { get; }

	public RecordingWriterSelection Resolve(RecordingProfileId? requestedProfileId = null)
	{
		var profileId = requestedProfileId ?? ProfileCatalog.DefaultProfileId;
		if (!ProfileCatalog.TryGet(profileId, out var profile) || !_providerByProfile.TryGetValue(profileId, out var provider))
			throw new RecordingOutputUnavailableException($"Recording profile '{profileId}' is not registered.");
		if (!profile.Available)
			throw new RecordingOutputUnavailableException(profile.UnavailableReason ?? $"Recording profile '{profileId}' is unavailable.");
		return new RecordingWriterSelection(profile, provider);
	}

	public IProgramRecordingPayloadWriter CreateWriter(RecordingProfileId? requestedProfileId = null)
	{
		var selection = Resolve(requestedProfileId);
		return selection.Provider.CreateWriter(selection.Profile.ProfileId)
			?? throw new InvalidOperationException($"Recording provider '{selection.Provider.ProviderId}' returned no writer.");
	}
}

public sealed class WindowsMediaFoundationRecordingWriterProvider : IProgramRecordingWriterProvider
{
	private readonly string _rootDirectory;
	private readonly long? _maximumPayloadBytes;
	private readonly RecordingProfileDescriptor[] _profiles;

	public WindowsMediaFoundationRecordingWriterProvider(
		string rootDirectory,
		long? maximumPayloadBytes = null)
	{
		if (string.IsNullOrWhiteSpace(rootDirectory))
			throw new ArgumentException("Recording root directory is required.", nameof(rootDirectory));
		if (maximumPayloadBytes is <= 0)
			throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));

		_rootDirectory = Path.GetFullPath(rootDirectory);
		_maximumPayloadBytes = maximumPayloadBytes;
		_profiles = [ProfessionalRecordingFormats.CreateMp4H264AacDescriptor()];
	}

	public RecordingWriterProviderId ProviderId => ProfessionalRecordingFormats.WindowsMediaFoundationProviderId;
	public string DisplayName => "Windows Media Foundation";
	public IReadOnlyList<RecordingProfileDescriptor> Profiles => _profiles;

	public IProgramRecordingPayloadWriter CreateWriter(RecordingProfileId profileId)
	{
		var profile = _profiles.SingleOrDefault(candidate => candidate.ProfileId == profileId)
			?? throw new RecordingOutputUnavailableException($"Windows Media Foundation does not provide recording profile '{profileId}'.");
		if (!profile.Available)
			throw new RecordingOutputUnavailableException(profile.UnavailableReason ?? "Windows Media Foundation recording is unavailable.");
		return new WindowsMediaFoundationMp4RecordingWriter(_rootDirectory, _maximumPayloadBytes);
	}
}

public sealed class ProfileSelectingProgramRecordingWriter :
	IProgramRecordingPayloadWriter,
	IProfileConfigurableProgramRecordingWriter,
	IProgramRecordingProfileStateProvider
{
	private readonly object _gate = new();
	private readonly RecordingWriterProviderRegistry _registry;
	private IProgramRecordingPayloadWriter? _pendingWriter;
	private RecordingProfileDescriptor? _pendingProfile;
	private IProgramRecordingPayloadWriter? _activeWriter;
	private RecordingProfileDescriptor? _activeProfile;
	private bool _sessionOpen;

	public ProfileSelectingProgramRecordingWriter(RecordingWriterProviderRegistry registry) =>
		_registry = registry ?? throw new ArgumentNullException(nameof(registry));

	public RecordingProfileCatalog ProfileCatalog => _registry.ProfileCatalog;

	public RecordingProfileDescriptor? ActiveProfile
	{
		get
		{
			lock (_gate)
				return _activeProfile;
		}
	}

	public RecordingWriterProviderId? ActiveProviderId => ActiveProfile?.ProviderId;

	public string? FinalPath
	{
		get
		{
			lock (_gate)
			{
				return (_activeWriter as IConfigurableProgramRecordingWriter)?.FinalPath ??
					(_pendingWriter as IConfigurableProgramRecordingWriter)?.FinalPath;
			}
		}
	}

	public string ConfigureTarget(string destinationDirectory, string fileName) =>
		ConfigureTarget(null, destinationDirectory, fileName);

	public string ConfigureTarget(
		RecordingProfileId? profileId,
		string destinationDirectory,
		string fileName)
	{
		var selection = _registry.Resolve(profileId);
		var writer = selection.Provider.CreateWriter(selection.Profile.ProfileId);
		if (writer is not IConfigurableProgramRecordingWriter configurable)
			throw new RecordingOutputUnavailableException($"Recording profile '{selection.Profile.ProfileId}' does not support configurable file targets.");

		var normalized = configurable.ConfigureTarget(destinationDirectory, fileName);
		lock (_gate)
		{
			if (_sessionOpen)
				throw new InvalidOperationException("Recording target cannot change while a recording session is active.");
			_pendingWriter = writer;
			_pendingProfile = selection.Profile;
		}
		return normalized;
	}

	public async ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		cancellationToken.ThrowIfCancellationRequested();

		var selection = _registry.Resolve(request.ProfileId);
		IProgramRecordingPayloadWriter writer;
		lock (_gate)
		{
			if (_sessionOpen)
				throw new InvalidOperationException("Recording writer selection already has an active session.");

			if (_pendingWriter is not null && _pendingProfile?.ProfileId == selection.Profile.ProfileId)
				writer = _pendingWriter;
			else
				writer = selection.Provider.CreateWriter(selection.Profile.ProfileId);

			_pendingWriter = null;
			_pendingProfile = null;
			_activeWriter = writer;
			_activeProfile = selection.Profile;
			_sessionOpen = true;
		}

		await writer.OpenAsync(
			request.ProfileId == selection.Profile.ProfileId
				? request
				: new RecordingStartRequest(request.Version, request.SessionId, request.Output, selection.Profile.ProfileId),
			cancellationToken).ConfigureAwait(false);
	}

	public void StagePayload(
		ulong sequenceNumber,
		IProgramRecordingPayloadLease videoPayload,
		ReadOnlyMemory<byte> audioPayload) =>
		ActivePayloadWriter().StagePayload(sequenceNumber, videoPayload, audioPayload);

	public void DiscardPayload(ulong sequenceNumber) =>
		ActivePayloadWriter().DiscardPayload(sequenceNumber);

	public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken) =>
		ActivePayloadWriter().WriteAsync(sample, cancellationToken);

	public async ValueTask FinalizeAsync(CancellationToken cancellationToken)
	{
		var writer = ActivePayloadWriter();
		try
		{
			await writer.FinalizeAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			lock (_gate)
				_sessionOpen = false;
		}
	}

	public async ValueTask AbortAsync(CancellationToken cancellationToken)
	{
		IProgramRecordingPayloadWriter? writer;
		lock (_gate)
			writer = _activeWriter;

		if (writer is not null)
			await writer.AbortAsync(cancellationToken).ConfigureAwait(false);

		lock (_gate)
			_sessionOpen = false;
	}

	private IProgramRecordingPayloadWriter ActivePayloadWriter()
	{
		lock (_gate)
		{
			return _activeWriter ??
				throw new InvalidOperationException("No recording writer is selected for the active session.");
		}
	}
}
