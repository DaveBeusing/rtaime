// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.ControlHost;

/// <summary>
/// ControlHost authority boundary for replay operations. RuntimeHost owns capture/materialization;
/// ControlHost owns admission into the persistent Media Library and never accepts raw replay media.
/// </summary>
public sealed class ReplayControlService
{
	private readonly IControlRuntimeTransportSeam _runtime;
	private readonly MediaAssetCatalogService _catalog;
	private readonly SemaphoreSlim _gate = new(1, 1);
	private ReplayClipAssetResult? _lastAdoptedClip;

	public ReplayControlService(
		IControlRuntimeTransportSeam runtime,
		MediaAssetCatalogService catalog)
	{
		_runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
		_catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
	}

	public ReplayClipAssetResult? LastAdoptedClip => _lastAdoptedClip;

	public async ValueTask<ReplayControlSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
	{
		var runtime = await _runtime.GetReplaySnapshotAsync(cancellationToken).ConfigureAwait(false);
		return ToControl(runtime);
	}

	public async ValueTask<ReplayControlSnapshot> MarkInAsync(
		TimeSpan? lookback = null,
		CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			_lastAdoptedClip = null;
			var runtime = await _runtime.MarkReplayInAsync(lookback, cancellationToken).ConfigureAwait(false);
			return ToControl(runtime);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<ReplayControlSnapshot> MarkOutAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			_lastAdoptedClip = null;
			var runtime = await _runtime.MarkReplayOutAsync(cancellationToken).ConfigureAwait(false);
			return ToControl(runtime);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<ReplayControlSnapshot> SetRangeAsync(
		TimeSpan @in,
		TimeSpan @out,
		CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			_lastAdoptedClip = null;
			var runtime = await _runtime.SetReplayRangeAsync(@in, @out, cancellationToken).ConfigureAwait(false);
			return ToControl(runtime);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<ReplayClipAssetResult> CreateClipAsync(
		string name,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(name))
			throw new ArgumentException("Replay clip name is required.", nameof(name));

		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			_lastAdoptedClip = null;
			var materialized = await _runtime.CreateReplayClipAsync(name.Trim(), cancellationToken).ConfigureAwait(false);
			if (!materialized.Succeeded || string.IsNullOrWhiteSpace(materialized.FinalPath))
			{
				return new ReplayClipAssetResult(
					ReplayControlContractVersion.Current,
					false,
					materialized.ClipId,
					null,
					materialized.FinalPath,
					materialized.SourceIn,
					materialized.SourceOut,
					materialized.Sha256,
					materialized.Failure ?? new Failure(
						"replay.clip.materialization_failed",
						"RuntimeHost did not materialize a valid replay clip."));
			}

			var import = await _catalog
				.ImportAsync([materialized.FinalPath], cancellationToken)
				.ConfigureAwait(false);
			var item = import.Items.SingleOrDefault();
			if (item is null || item.AssetId is null || item.Failure is not null)
			{
				var failure = item?.Failure ?? new Failure(
					"replay.clip.catalog_adoption_failed",
					"Materialized replay clip was not adopted into the Media Library.");
				return new ReplayClipAssetResult(
					ReplayControlContractVersion.Current,
					false,
					materialized.ClipId,
					item?.AssetId?.ToString(),
					materialized.FinalPath,
					materialized.SourceIn,
					materialized.SourceOut,
					materialized.Sha256,
					failure);
			}

			var asset = import.Snapshot.Assets.FirstOrDefault(candidate => candidate.AssetId == item.AssetId.Value);
			if (asset is null || asset.Availability != MediaAssetAvailability.Online)
			{
				return new ReplayClipAssetResult(
					ReplayControlContractVersion.Current,
					false,
					materialized.ClipId,
					item.AssetId,
					materialized.FinalPath,
					materialized.SourceIn,
					materialized.SourceOut,
					materialized.Sha256,
					new Failure(
						"replay.clip.catalog_verification_failed",
						"Replay clip import completed without a confirmed online Media Library asset."));
			}

			_lastAdoptedClip = new ReplayClipAssetResult(
				ReplayControlContractVersion.Current,
				true,
				materialized.ClipId,
				asset.AssetId.ToString(),
				asset.SourceLocation,
				materialized.SourceIn,
				materialized.SourceOut,
				materialized.Sha256,
				null);
			return _lastAdoptedClip;
		}
		finally
		{
			_gate.Release();
		}
	}

	private ReplayControlSnapshot ToControl(RuntimeReplaySnapshot runtime)
	{
		var captureState = Enum.IsDefined(typeof(ReplayControlCaptureState), runtime.CaptureState)
			? (ReplayControlCaptureState)runtime.CaptureState
			: throw new InvalidDataException("Runtime replay capture state is invalid.");
		var runtimeClipState = Enum.IsDefined(typeof(ReplayControlClipState), runtime.ClipState)
			? (ReplayControlClipState)runtime.ClipState
			: throw new InvalidDataException("Runtime replay clip state is invalid.");
		var clipState = runtimeClipState == ReplayControlClipState.Ready && _lastAdoptedClip?.Succeeded != true
			? ReplayControlClipState.Finalizing
			: runtimeClipState;

		return new ReplayControlSnapshot(
			ReplayControlContractVersion.Current,
			captureState,
			clipState,
			runtime.Retention,
			runtime.RetainedDuration,
			runtime.MaximumStorageBytes,
			runtime.RetainedBytes,
			runtime.SegmentDuration,
			runtime.Segments.Count,
			runtime.MarkIn,
			runtime.MarkOut,
			runtime.AcceptedSamples,
			runtime.DroppedSamples,
			runtime.FinalizedSegments,
			runtime.EvictedSegments,
			runtime.Discontinuities,
			runtime.Failure);
	}
}
