// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

public sealed partial class OperatorViewModel
{
	private MediaReferenceClockObservation? _referenceClock;

	// Read-only Runtime provider evidence, never an Operator-side PTP poll.
	public MediaReferenceClockObservation? ClockReference
	{
		get => _referenceClock;
		private set
		{
			if (!Set(ref _referenceClock, value)) return;
			foreach (var property in new[] {
				nameof(ClockLockState), nameof(ClockEvidence), nameof(ClockTimeQuality), nameof(ClockProvider),
				nameof(ClockDomain), nameof(ClockEpoch), nameof(ClockGrandmaster),
				nameof(ClockOffset), nameof(ClockDrift), nameof(ClockHoldover),
				nameof(ClockLastSync), nameof(ClockDetail)
			}) OnPropertyChanged(property);
		}
	}

	private bool ClockCurrent => IsConnected && !IsStale &&
		_referenceClock is { } reference && reference.IsCurrent(DateTimeOffset.UtcNow);

	public string ClockLockState => !ClockCurrent ? "UNVERIFIED" :
		_referenceClock!.LockState.ToString().ToUpperInvariant();
	public string ClockTimeQuality => !ClockCurrent ? "UNVERIFIED" :
		_referenceClock!.TimeQuality.ToString().ToUpperInvariant();
	public string ClockEvidence => !ClockCurrent ? "UNVERIFIED" :
		_referenceClock!.Evidence switch {
			MediaReferenceEvidence.HardwareQualified => "HARDWARE-QUALIFIED SOURCE",
			MediaReferenceEvidence.Simulated => "SIMULATED ONLY",
			_ => "UNVERIFIED"
		};
	public string ClockProvider => ClockCurrent ? _referenceClock!.ProviderId : "UNVERIFIED";
	public string ClockDomain => ClockCurrent ? _referenceClock!.ClockDomainId : "UNVERIFIED";
	public string ClockEpoch => ClockCurrent ? _referenceClock!.EpochId.ToString("D") : "UNVERIFIED";
	public string ClockGrandmaster => ClockCurrent ? _referenceClock!.GrandmasterId ?? "NO PTP GRANDMASTER" : "UNVERIFIED";
	public string ClockOffset => ClockCurrent && _referenceClock!.OffsetNanoseconds is long value
		? $"{value} ns" : "UNVERIFIED";
	public string ClockDrift => ClockCurrent && _referenceClock!.DriftPartsPerBillion is double value
		? $"{value:0.###} ppb" : "UNVERIFIED";
	public string ClockHoldover => ClockCurrent && _referenceClock!.LockState == MediaReferenceLockState.Holdover
		? $"{_referenceClock.HoldoverElapsed.TotalSeconds:0} s / {_referenceClock.MaximumHoldover.TotalSeconds:0} s"
		: "—";
	public string ClockLastSync => ClockCurrent && _referenceClock!.LastSyncAtUtc is { } sync
		? sync.ToString("O") : "UNVERIFIED";
	public string ClockDetail => !ClockCurrent
		? "No current, provider-backed media clock evidence. PTP/Genlock remains unverified."
		: _referenceClock!.Detail;

	private void ApplyReferenceClock(OperatorStatusSnapshot snapshot) =>
		ClockReference = snapshot.ClockReference;
}
