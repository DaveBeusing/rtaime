// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Provider.Contracts;
using rtaime.Provider.Ndi;
using rtaime.Provider.Srt;

namespace rtaime.RuntimeHost;

internal interface IRuntimeNetworkOutputProviderRegistry
{
	INetworkOutputSession CreateSession(RuntimeNetworkOutputTarget target);
	IReadOnlyList<ProviderDescriptor> ProviderDescriptorsFor(IReadOnlyList<RuntimeNetworkOutputTarget> targets);
}

internal sealed class RuntimeNetworkOutputProviderRegistry : IRuntimeNetworkOutputProviderRegistry
{
	private readonly SrtNetworkOutputProvider _srt;
	private readonly NdiNetworkOutputProvider _ndi;

	public RuntimeNetworkOutputProviderRegistry(
		SrtNetworkOutputProvider? srt = null,
		NdiNetworkOutputProvider? ndi = null)
	{
		_srt = srt ?? new SrtNetworkOutputProvider();
		_ndi = ndi ?? new NdiNetworkOutputProvider();
	}

	public INetworkOutputSession CreateSession(RuntimeNetworkOutputTarget target)
	{
		ArgumentNullException.ThrowIfNull(target);
		return target.Configuration.Protocol switch
		{
			NetworkOutputProtocolFamily.Srt => _srt.CreateSession(
				target.Configuration,
				Environment.GetEnvironmentVariable("RTAIME_SRT_LIBRARY_PATH")),
			NetworkOutputProtocolFamily.Ndi => _ndi.CreateSession(target.Configuration),
			_ => throw new NotSupportedException($"Unsupported network-output protocol '{target.Configuration.Protocol}'.")
		};
	}

	public IReadOnlyList<ProviderDescriptor> ProviderDescriptorsFor(IReadOnlyList<RuntimeNetworkOutputTarget> targets)
	{
		ArgumentNullException.ThrowIfNull(targets);
		var protocols = targets
			.Select(target => target.Configuration.Protocol)
			.Distinct()
			.ToArray();

		var descriptors = new List<ProviderDescriptor>(protocols.Length);
		foreach (var protocol in protocols)
		{
			descriptors.Add(protocol switch
			{
				NetworkOutputProtocolFamily.Srt => _srt.Descriptor,
				NetworkOutputProtocolFamily.Ndi => _ndi.Descriptor,
				_ => throw new NotSupportedException($"Unsupported network-output protocol '{protocol}'.")
			});
		}

		return Array.AsReadOnly(descriptors.ToArray());
	}
}
