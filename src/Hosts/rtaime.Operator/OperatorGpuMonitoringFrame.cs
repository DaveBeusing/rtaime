// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;

namespace rtaime.Operator;

public sealed record OperatorGpuMonitoringFrame
{
	public OperatorGpuMonitoringFrame(MonitoringFrameDescriptor descriptor)
	{
		Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
		Resource = descriptor.SharedResource ??
			throw new ArgumentException("GPU monitoring frames require a shared resource.", nameof(descriptor));
		if (!Resource.Interop.IsPresentable)
			throw new ArgumentException("GPU monitoring frames require presentable Windows graphics interop.", nameof(descriptor));
	}

	public MonitoringFrameDescriptor Descriptor { get; }
	public MonitoringSharedResourceDescriptor Resource { get; }
	public ulong SequenceNumber => Descriptor.Timing.SequenceNumber;

	public bool IsNewerThan(OperatorGpuMonitoringFrame? other) =>
		other is null ||
		Resource.ProviderInstanceId != other.Resource.ProviderInstanceId ||
		Resource.Lifetime.Generation.Value > other.Resource.Lifetime.Generation.Value ||
		(Resource.Lifetime.Generation == other.Resource.Lifetime.Generation && SequenceNumber > other.SequenceNumber);
}
