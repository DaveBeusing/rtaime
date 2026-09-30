// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Xml.Linq;

namespace rtaime.Tests.Architecture;

public sealed class ExternalControlArchitectureTests
{
	[Fact]
	public void External_control_schema_exposes_only_ControlHost_semantics()
	{
		var root = FindRepositoryRoot();
		var schema = File.ReadAllText(Path.Combine(root, "schemas", "external-control", "v1", "external_control.proto"));
		var server = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.ControlHost", "ExternalControlServer.cs"));
		var client = File.ReadAllText(Path.Combine(root, "src", "Client", "rtaime.Client", "GrpcOperatorControlTransport.cs"));

		Assert.Contains("service ExternalControl", schema, StringComparison.Ordinal);
		Assert.DoesNotContain("RuntimeHost", schema, StringComparison.Ordinal);
		Assert.DoesNotContain("AIHost", schema, StringComparison.Ordinal);
		Assert.DoesNotContain("\"runtime.", server, StringComparison.Ordinal);
		Assert.DoesNotContain("\"ai.", server, StringComparison.Ordinal);
		Assert.DoesNotContain("RuntimeHost", client, StringComparison.Ordinal);
		Assert.DoesNotContain("AIHost", client, StringComparison.Ordinal);
	}

	[Fact]
	public void ControlHost_external_transport_does_not_reference_other_host_projects()
	{
		var root = FindRepositoryRoot();
		var project = XDocument.Load(Path.Combine(root, "src", "Hosts", "rtaime.ControlHost", "rtaime.ControlHost.csproj"));
		var references = project.Descendants("ProjectReference")
			.Select(reference => (string?)reference.Attribute("Include"))
			.Where(reference => !string.IsNullOrWhiteSpace(reference))
			.ToArray();

		Assert.DoesNotContain(references, reference => reference!.Contains("rtaime.RuntimeHost", StringComparison.Ordinal));
		Assert.DoesNotContain(references, reference => reference!.Contains("rtaime.AIHost", StringComparison.Ordinal));
		Assert.DoesNotContain(references, reference => reference!.Contains("rtaime.Client", StringComparison.Ordinal));
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
