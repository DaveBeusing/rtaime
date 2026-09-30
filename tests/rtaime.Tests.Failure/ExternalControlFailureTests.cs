// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.ControlHost;

namespace rtaime.Tests.Failure;

public sealed class ExternalControlFailureTests
{
	[Fact]
	public void Enabled_external_control_without_exactly_one_server_certificate_source_fails_closed()
	{
		var options = ValidOptions() with
		{
			CertificatePath = null,
			CertificateThumbprint = null
		};

		Assert.Throws<ArgumentException>(options.Validate);
	}

	[Fact]
	public void Mutual_tls_without_a_certificate_bound_identity_fails_closed()
	{
		var options = ValidOptions() with
		{
			RequireMutualTls = true,
			Identities =
			[
				new ExternalControlIdentityOptions(
					"operator",
					ExternalControlRole.Operator,
					TokenEnvironmentVariable: "RTAIME_TEST_EXTERNAL_TOKEN")
			]
		};

		Assert.Throws<ArgumentException>(options.Validate);
	}

	[Fact]
	public void External_resource_limits_reject_unbounded_configuration()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => (ValidOptions() with { MaxConcurrentConnections = 0 }).Validate());
		Assert.Throws<ArgumentOutOfRangeException>(() => (ValidOptions() with { MaxConcurrentOperationsPerClient = 0 }).Validate());
		Assert.Throws<ArgumentOutOfRangeException>(() => (ValidOptions() with { MaxRequestBytes = 512 }).Validate());
		Assert.Throws<ArgumentOutOfRangeException>(() => (ValidOptions() with { RequestTimeout = TimeSpan.FromMinutes(6) }).Validate());
	}

	[Fact]
	public void Required_external_control_cannot_be_disabled()
	{
		var options = new ExternalControlServerOptions { Enabled = false, Required = true };

		Assert.Throws<ArgumentException>(options.Validate);
	}

	private static ExternalControlServerOptions ValidOptions() => new()
	{
		Enabled = true,
		Required = true,
		CertificatePath = "test-only-server.pfx",
		Identities =
		[
			new ExternalControlIdentityOptions(
				"operator",
				ExternalControlRole.Operator,
				TokenEnvironmentVariable: "RTAIME_TEST_EXTERNAL_TOKEN")
		]
	};
}
