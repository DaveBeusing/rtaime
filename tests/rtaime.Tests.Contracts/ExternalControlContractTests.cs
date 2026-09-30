// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Tests.Contracts;

public sealed class ExternalControlContractTests
{
	[Fact]
	public void External_api_v1_preserves_version_identity_and_concurrency_metadata()
	{
		var root = FindRepositoryRoot();
		var schema = File.ReadAllText(Path.Combine(root, "schemas", "external-control", "v1", "external_control.proto"));
		var server = File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.ControlHost", "ExternalControlServer.cs"));
		var client = File.ReadAllText(Path.Combine(root, "src", "Client", "rtaime.Client", "GrpcOperatorControlTransport.cs"));

		Assert.Contains("syntax = \"proto3\";", schema, StringComparison.Ordinal);
		Assert.Contains("package rtaime.external_control.v1;", schema, StringComparison.Ordinal);
		Assert.Contains("string api_version", schema, StringComparison.Ordinal);
		Assert.Contains("string request_id", schema, StringComparison.Ordinal);
		Assert.Contains("string correlation_id", schema, StringComparison.Ordinal);
		Assert.Contains("string client_name", schema, StringComparison.Ordinal);
		Assert.Contains("string client_version", schema, StringComparison.Ordinal);
		Assert.Contains("string host_instance_id", schema, StringComparison.Ordinal);
		Assert.Contains("uint64 state_version", schema, StringComparison.Ordinal);
		Assert.Contains("uint64 based_on_state_version", schema, StringComparison.Ordinal);
		Assert.Contains("string command_id", schema, StringComparison.Ordinal);
		Assert.Contains("uint64 expected_revision", schema, StringComparison.Ordinal);
		Assert.Contains("ApiVersion = \"1.0\"", server, StringComparison.Ordinal);
		Assert.Contains("ApiVersion = \"1.0\"", client, StringComparison.Ordinal);
	}

	[Fact]
	public void External_api_v1_contains_no_direct_Runtime_or_raw_media_transport_contract()
	{
		var root = FindRepositoryRoot();
		var schema = File.ReadAllText(Path.Combine(root, "schemas", "external-control", "v1", "external_control.proto"));

		Assert.DoesNotContain("RuntimeHost", schema, StringComparison.Ordinal);
		Assert.DoesNotContain("AIHost", schema, StringComparison.Ordinal);
		Assert.DoesNotContain("video_frame", schema, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("audio_samples", schema, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("gpu_surface", schema, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("device_pointer", schema, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void External_configuration_schema_keeps_secure_defaults_and_bounded_roles()
	{
		var root = FindRepositoryRoot();
		var schema = File.ReadAllText(Path.Combine(root, "schemas", "external-control", "v1", "external_control_config.schema.json"));

		Assert.Contains("\"enabled\": { \"type\": \"boolean\", \"default\": false }", schema, StringComparison.Ordinal);
		Assert.Contains("\"bindAddress\": { \"type\": \"string\", \"default\": \"127.0.0.1\"", schema, StringComparison.Ordinal);
		Assert.Contains("\"Observer\"", schema, StringComparison.Ordinal);
		Assert.Contains("\"Operator\"", schema, StringComparison.Ordinal);
		Assert.Contains("\"Administrator\"", schema, StringComparison.Ordinal);
		Assert.Contains("\"maxConcurrentConnections\"", schema, StringComparison.Ordinal);
		Assert.Contains("\"maxConcurrentOperationsPerClient\"", schema, StringComparison.Ordinal);
		Assert.Contains("\"requestsPerSecond\"", schema, StringComparison.Ordinal);
		Assert.Contains("\"requestTimeoutMilliseconds\"", schema, StringComparison.Ordinal);
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
