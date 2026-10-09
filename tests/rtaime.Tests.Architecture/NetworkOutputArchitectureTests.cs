// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Xml.Linq;

namespace rtaime.Tests.Architecture;

public sealed class NetworkOutputArchitectureTests
{
	[Fact]
	public void Srt_provider_does_not_reference_Control_or_host_projects()
	{
		var root = FindRepositoryRoot();
		var project = XDocument.Load(Path.Combine(root, "src", "Providers", "rtaime.Provider.Srt", "rtaime.Provider.Srt.csproj"));
		var references = project.Descendants("ProjectReference")
			.Select(reference => (string?)reference.Attribute("Include"))
			.Where(reference => !string.IsNullOrWhiteSpace(reference))
			.ToArray();

		Assert.DoesNotContain(references, reference => reference!.Contains("rtaime.Control", StringComparison.Ordinal));
		Assert.DoesNotContain(references, reference => reference!.Contains("rtaime.RuntimeHost", StringComparison.Ordinal));
		Assert.DoesNotContain(references, reference => reference!.Contains("rtaime.ControlHost", StringComparison.Ordinal));
		Assert.DoesNotContain(references, reference => reference!.Contains("rtaime.Operator", StringComparison.Ordinal));
	}

	[Fact]
	public void Ndi_provider_does_not_reference_Control_or_host_projects()
	{
		var root = FindRepositoryRoot();
		var project = XDocument.Load(Path.Combine(root, "src", "Providers", "rtaime.Provider.Ndi", "rtaime.Provider.Ndi.csproj"));
		var references = project.Descendants("ProjectReference")
			.Select(reference => (string?)reference.Attribute("Include"))
			.Where(reference => !string.IsNullOrWhiteSpace(reference))
			.ToArray();

		Assert.DoesNotContain(references, reference => reference!.Contains("rtaime.Control", StringComparison.Ordinal));
		Assert.DoesNotContain(references, reference => reference!.Contains("rtaime.RuntimeHost", StringComparison.Ordinal));
		Assert.DoesNotContain(references, reference => reference!.Contains("rtaime.ControlHost", StringComparison.Ordinal));
		Assert.DoesNotContain(references, reference => reference!.Contains("rtaime.Operator", StringComparison.Ordinal));
	}

	[Fact]
	public void Stable_network_output_contracts_do_not_leak_NDI_runtime_types()
	{
		var root = FindRepositoryRoot();
		var providerContract = File.ReadAllText(Path.Combine(root, "src", "Contracts", "rtaime.Provider.Contracts", "NetworkOutput.cs"));

		Assert.DoesNotContain("Processing.NDI", providerContract, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("NDIlib_", providerContract, StringComparison.Ordinal);
		Assert.DoesNotContain("NativeLibrary", providerContract, StringComparison.Ordinal);
		Assert.DoesNotContain("NdiVideoFrame", providerContract, StringComparison.Ordinal);
	}

	[Fact]
	public void Runtime_bridge_uses_provider_registry_instead_of_hard_coded_sessions()
	{
		var root = FindRepositoryRoot();
		var bridge = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.RuntimeHost", "RuntimeNetworkOutputBridge.cs"));
		var registry = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.RuntimeHost", "RuntimeNetworkOutputProviderRegistry.cs"));

		Assert.Contains("IRuntimeNetworkOutputProviderRegistry", bridge, StringComparison.Ordinal);
		Assert.Contains("INetworkOutputSession", bridge, StringComparison.Ordinal);
		Assert.DoesNotContain("SrtNetworkOutputSession", bridge, StringComparison.Ordinal);
		Assert.DoesNotContain("NdiNetworkOutputSession", bridge, StringComparison.Ordinal);
		Assert.DoesNotContain("rtaime.Provider.Srt", bridge, StringComparison.Ordinal);
		Assert.DoesNotContain("rtaime.Provider.Ndi", bridge, StringComparison.Ordinal);
		Assert.Contains("NetworkOutputProtocolFamily.Srt", registry, StringComparison.Ordinal);
		Assert.Contains("NetworkOutputProtocolFamily.Ndi", registry, StringComparison.Ordinal);
	}

	[Fact]
	public void Network_media_handoff_stays_out_of_management_ipc_contracts()
	{
		var root = FindRepositoryRoot();
		var providerContract = File.ReadAllText(Path.Combine(root, "src", "Contracts", "rtaime.Provider.Contracts", "NetworkOutput.cs"));
		var runtimeContract = File.ReadAllText(Path.Combine(root, "src", "Contracts", "rtaime.Runtime.Contracts", "Contracts.cs"));
		var bridge = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.RuntimeHost", "RuntimeNetworkOutputBridge.cs"));

		Assert.Contains("INetworkOutputPayloadLease", providerContract, StringComparison.Ordinal);
		Assert.Contains("GpuReadbackLease", bridge, StringComparison.Ordinal);
		Assert.DoesNotContain("NetworkOutputProgramSample", runtimeContract, StringComparison.Ordinal);
		Assert.DoesNotContain("INetworkOutputPayloadLease", runtimeContract, StringComparison.Ordinal);
	}

	[Fact]
	public void Network_output_remains_Runtime_owned_and_provider_neutral_at_Control_boundary()
	{
		var root = FindRepositoryRoot();
		var controlHost = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.ControlHost", "ControlHostIpcServer.cs"));
		var runtimeService = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.RuntimeHost", "V1RuntimeHostService.cs"));

		Assert.DoesNotContain("NativeSrtTransport", controlHost, StringComparison.Ordinal);
		Assert.DoesNotContain("WindowsMediaFoundationSrtEncoder", controlHost, StringComparison.Ordinal);
		Assert.Contains("RuntimeNetworkOutputBridge", runtimeService, StringComparison.Ordinal);
	}

	private static string FindRepositoryRoot()
	{
		var current = new DirectoryInfo(AppContext.BaseDirectory);
		while (current is not null)
		{
			if (File.Exists(Path.Combine(current.FullName, "rtaime.slnx")))
				return current.FullName;
			current = current.Parent;
		}
		throw new DirectoryNotFoundException("Repository root could not be located.");
	}
}
