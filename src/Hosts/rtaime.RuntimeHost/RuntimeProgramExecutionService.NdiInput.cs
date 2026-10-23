// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.RuntimeHost;

internal sealed record RuntimeNdiInputBinding(MediaSourceId SourceId, PreparedExecutionBinding Binding);

internal sealed record RuntimeNdiBindingSnapshot(ulong Generation, IReadOnlyList<RuntimeNdiInputBinding> Bindings);

public sealed partial class RuntimeProgramExecutionService
{
	private readonly Dictionary<MediaSourceId, RuntimeNdiInputBinding> _committedNdiInputs = [];

	private bool IsNdiInputBinding(PreparedExecutionBinding binding) =>
		binding.MediaSourceId is not null && binding.ExternalSourceId is not null &&
		binding.Resource.ProviderId == _ndiProvider.Descriptor.ProviderId &&
		string.Equals(binding.Resource.Kind, MediaSourceCapabilityKinds.Input, StringComparison.Ordinal);

	private Failure? ValidateNdiSourceCapacityUnsafe(IReadOnlyList<PreparedExecutionBinding> bindings)
	{
		var newSources = bindings.Where(IsNdiInputBinding)
			.Select(binding => binding.MediaSourceId!.Value).Distinct()
			.Count(sourceId => !_backgrounds.ContainsKey(sourceId));
		return _audioStreams.Count + newSources > AudioProductionLimits.MaximumSources
			? new Failure("runtime.input.source_capacity_exceeded",
				"Committed NDI inputs exceed the bounded Runtime source registry.")
			: null;
	}

	// Called only by a successful commit, under capture -> state locks. The exact owner
	// object survives unrelated commits but is replaced on removal/re-adoption or rebinding.
	private RuntimeNdiBindingSnapshot CommitNdiBindingsUnsafe(
		IReadOnlyList<PreparedExecutionBinding> bindings, ulong generation)
	{
		var required = bindings
			.Where(IsNdiInputBinding)
			.GroupBy(binding => binding.MediaSourceId!.Value)
			.ToDictionary(group => group.Key, group => group.First());

		foreach (var previous in _committedNdiInputs.ToArray())
		{
			if (required.TryGetValue(previous.Key, out var binding) && previous.Value.Binding == binding)
				continue;
			_committedNdiInputs.Remove(previous.Key);
			if (_inputSignals.ContainsKey(previous.Key))
				_inputSignals[previous.Key] = RuntimeInputSignalState.Lost;
			if (_externalAudioQueues.TryGetValue(previous.Key, out var queue))
				queue.Clear();
			if (_audioMeters.ContainsKey(previous.Key))
				_audioMeters[previous.Key] = new AudioMeterObservation(
					new AudioStereoMeter(0, 0), Available: false, External: true);
		}

		foreach (var requiredBinding in required)
		{
			if (!_committedNdiInputs.ContainsKey(requiredBinding.Key))
			{
				// Program may execute immediately after commit, even while native discovery
				// or retirement is delayed. Admit its managed frame/audio registry atomically.
				RegisterExternalSource(requiredBinding.Key);
				_backgrounds[requiredBinding.Key].CopyPixelsFrom(_blackBackground.Pixels.Span);
				_inputSignals[requiredBinding.Key] = RuntimeInputSignalState.Recovering;
				_externalAudioQueues[requiredBinding.Key].Clear();
				_audioMeters[requiredBinding.Key] = new AudioMeterObservation(
					new AudioStereoMeter(0, 0), Available: false, External: true);
				_committedNdiInputs.Add(requiredBinding.Key,
					new RuntimeNdiInputBinding(requiredBinding.Key, requiredBinding.Value));
			}
		}

		return new RuntimeNdiBindingSnapshot(generation, _committedNdiInputs.Values.ToArray());
	}

	internal bool IsNdiInputCurrent(RuntimeNdiInputBinding owner)
	{
		lock (_gate)
			return IsNdiInputCurrentUnsafe(owner);
	}

	private bool IsNdiInputCurrentUnsafe(RuntimeNdiInputBinding owner) =>
		!_disposed && _committedNdiInputs.TryGetValue(owner.SourceId, out var current) &&
		ReferenceEquals(current, owner);

	internal bool TryRegisterNdiSource(RuntimeNdiInputBinding owner)
	{
		lock (_boundaryCaptureGate)
		lock (_gate)
		{
			if (!IsNdiInputCurrentUnsafe(owner))
				return false;
			RegisterExternalSource(owner.SourceId);
			_inputSignals[owner.SourceId] = RuntimeInputSignalState.Recovering;
			_externalAudioQueues[owner.SourceId].Clear();
			_audioMeters[owner.SourceId] = new AudioMeterObservation(
				new AudioStereoMeter(0, 0), Available: false, External: true);
			return true;
		}
	}

	internal bool TrySetNdiInputContent(RuntimeNdiInputBinding owner, ReadOnlySpan<byte> rgbaPixels)
	{
		lock (_boundaryCaptureGate)
		{
			lock (_gate)
				if (!IsNdiInputCurrentUnsafe(owner))
					return false;
			// A commit also owns the capture lock, so it cannot revoke this owner midway
			// through the existing bounded frame-copy seam.
			SetExternalInputContent(owner.SourceId, rgbaPixels, RuntimeInputSignalState.Valid);
			return true;
		}
	}

	internal bool TrySetNdiAudioInput(RuntimeNdiInputBinding owner, ReadOnlySpan<byte> float32Payload)
	{
		lock (_boundaryCaptureGate)
		lock (_gate)
		{
			if (!IsNdiInputCurrentUnsafe(owner))
				return false;
			SetExternalAudioInput(owner.SourceId, float32Payload, available: true);
			return true;
		}
	}

	internal bool TrySetNdiInputHealth(RuntimeNdiInputBinding owner, RuntimeInputSignalState state)
	{
		lock (_boundaryCaptureGate)
		lock (_gate)
		{
			if (!IsNdiInputCurrentUnsafe(owner))
				return false;
			SetInputSignalState(owner.SourceId, state);
			if (state == RuntimeInputSignalState.Lost)
				ClearExternalAudioMeter(owner.SourceId);
			return true;
		}
	}
}
