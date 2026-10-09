// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class NetworkOutputConfigurationIntegrationTests
{
	[Fact]
	public void Legacy_SRT_descriptor_without_protocol_remains_compatible()
	{
		const string json = """
		[
		  {
		    "roleId": "program",
		    "targetId": "program-srt",
		    "endpoint": "srt://127.0.0.1:9000/live",
		    "mode": "caller",
		    "videoBitRate": 12000000,
		    "audioBitRate": 192000,
		    "latencyMilliseconds": 120
		  }
		]
		""";

		var target = Assert.Single(Load(json));

		Assert.Equal("program", target.RoleId);
		Assert.Equal(NetworkOutputProtocolFamily.Srt, target.Configuration.Protocol);
		Assert.NotNull(target.Configuration.SrtSettings);
		Assert.Null(target.Configuration.NdiSettings);
		Assert.Equal("srt://127.0.0.1:9000/live", target.Configuration.SafeTargetIdentity);
	}

	[Fact]
	public void Typed_NDI_descriptor_requires_only_common_fields_and_source_name()
	{
		const string json = """
		[
		  {
		    "roleId": "program",
		    "targetId": "program-ndi",
		    "protocol": "ndi",
		    "sourceName": "rtaime Program",
		    "queueCapacity": 4,
		    "reconnectMaximumAttempts": 2
		  }
		]
		""";

		var target = Assert.Single(Load(json));

		Assert.Equal(NetworkOutputProtocolFamily.Ndi, target.Configuration.Protocol);
		Assert.Equal("rtaime Program", target.Configuration.NdiSettings?.SourceName);
		Assert.Equal("ndi://rtaime Program", target.Configuration.SafeTargetIdentity);
		Assert.Null(target.Configuration.SrtSettings);
		Assert.Equal(4, target.Configuration.QueueCapacity);
	}

	[Fact]
	public void NDI_descriptor_rejects_mixed_SRT_fields()
	{
		const string json = """
		[
		  {
		    "roleId": "program",
		    "targetId": "program-ndi",
		    "protocol": "ndi",
		    "sourceName": "rtaime Program",
		    "endpoint": "srt://127.0.0.1:9000/live"
		  }
		]
		""";

		var exception = Assert.Throws<ArgumentException>(() => Load(json));
		Assert.Contains("must not define SRT", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void SRT_descriptor_rejects_NDI_source_name()
	{
		const string json = """
		[
		  {
		    "roleId": "program",
		    "targetId": "program-srt",
		    "protocol": "srt",
		    "endpoint": "srt://127.0.0.1:9000/live",
		    "sourceName": "should-not-exist"
		  }
		]
		""";

		var exception = Assert.Throws<ArgumentException>(() => Load(json));
		Assert.Contains("must not define NDI sourceName", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Program_and_Aux_can_use_different_network_output_providers()
	{
		const string json = """
		[
		  {
		    "roleId": "program",
		    "targetId": "program-ndi",
		    "protocol": "ndi",
		    "sourceName": "rtaime Program"
		  },
		  {
		    "roleId": "aux",
		    "targetId": "aux-srt",
		    "protocol": "srt",
		    "endpoint": "srt://127.0.0.1:9001/aux"
		  }
		]
		""";

		var targets = Load(json);

		Assert.Equal(2, targets.Count);
		Assert.Equal(NetworkOutputProtocolFamily.Ndi, targets.Single(target => target.RoleId == "program").Configuration.Protocol);
		Assert.Equal(NetworkOutputProtocolFamily.Srt, targets.Single(target => target.RoleId == "aux").Configuration.Protocol);
	}

	[Fact]
	public void Duplicate_output_role_is_rejected_across_protocols()
	{
		const string json = """
		[
		  {
		    "roleId": "program",
		    "targetId": "program-ndi",
		    "protocol": "ndi",
		    "sourceName": "rtaime Program"
		  },
		  {
		    "roleId": "program",
		    "targetId": "program-srt",
		    "protocol": "srt",
		    "endpoint": "srt://127.0.0.1:9001/live"
		  }
		]
		""";

		var exception = Assert.Throws<ArgumentException>(() => Load(json));
		Assert.Contains("currently supports one configured network target", exception.Message, StringComparison.Ordinal);
	}

	private static IReadOnlyList<RuntimeNetworkOutputTarget> Load(string json) =>
		RuntimeNetworkOutputConfigurationLoader.Load(
			new[] { $"--network-outputs={json}" },
			_ => null,
			VideoFormat.Hd1080p50Rgba8);
}
