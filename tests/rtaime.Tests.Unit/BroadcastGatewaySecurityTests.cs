// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;
using rtaime.IntegrationHost;

namespace rtaime.Tests.Unit;

public sealed class BroadcastGatewaySecurityTests
{
	[Theory]
	[InlineData("0.0.0.0")]
	[InlineData("192.0.2.10")]
	[InlineData("::")]
	[InlineData("not-an-address")]
	public void Osc_udp_rejects_nonloopback_listener_addresses(string address)
	{
		Assert.Throws<ArgumentException>(() => new OscAdapterOptions { ListenAddress = address }.Validate());
		Assert.Throws<ArgumentException>(() => new OscIntegrationAdapter(
			new IntegrationAdapterOptions { Id = "osc", Kind = IntegrationAdapterKind.Osc, Osc = new OscAdapterOptions { ListenAddress = address } },
			Array.Empty<IntegrationFeedbackMappingOptions>()));
	}

	[Theory]
	[InlineData("0.0.0.0")]
	[InlineData("192.0.2.10")]
	[InlineData("::")]
	[InlineData("not-an-address")]
	public void Plaintext_companion_rejects_nonloopback_listener_addresses(string address)
	{
		var options = new CompanionAdapterOptions
		{
			BindAddress = address,
			BearerTokenEnvironmentVariable = "RTAIME_TEST_COMPANION_TOKEN"
		};
		Assert.Throws<ArgumentException>(options.Validate);
		Assert.Throws<ArgumentException>(() => new CompanionIntegrationAdapter(
			new IntegrationAdapterOptions { Id = "companion", Kind = IntegrationAdapterKind.Companion, Companion = options },
			Array.Empty<IntegrationMappingOptions>(),
			Array.Empty<IntegrationFeedbackMappingOptions>()));
	}

	[Fact]
	public void Local_gateway_listeners_remain_valid_with_secure_defaults()
	{
		new OscAdapterOptions().Validate();
		new CompanionAdapterOptions { BearerTokenEnvironmentVariable = "RTAIME_TEST_COMPANION_TOKEN" }.Validate();
		Assert.False(new IntegrationGatewayOptions().Enabled);
	}

	[Theory]
	[InlineData("https://example.com:55101/")]
	[InlineData("https://192.0.2.10:55101/")]
	public void Insecure_upstream_certificate_bypass_requires_loopback(string endpoint)
	{
		var options = TestGateway() with
		{
			Upstream = new IntegrationUpstreamOptions { Endpoint = endpoint, TrustMode = IntegrationTrustMode.TestOnlyInsecure }
		};
		Assert.Throws<ArgumentException>(options.Validate);
	}

	[Fact]
	public void Insecure_upstream_certificate_bypass_requires_both_test_and_dry_run()
	{
		var options = TestGateway();
		Assert.Throws<ArgumentException>(() => (options with { TestMode = false }).Validate());
		Assert.Throws<ArgumentException>(() => (options with { DryRun = false }).Validate());
		options.Validate();
	}

	[Theory]
	[InlineData("http://127.0.0.1:55101/")]
	[InlineData("https://user:secret@127.0.0.1:55101/")]
	[InlineData("https://127.0.0.1:55101/control")]
	[InlineData("https://127.0.0.1:55101/?token=value")]
	[InlineData("https://127.0.0.1:55101/#fragment")]
	public void Gateway_upstream_rejects_insecure_or_credential_bearing_urls(string endpoint) =>
		Assert.Throws<ArgumentException>(() => new IntegrationUpstreamOptions { Endpoint = endpoint }.Validate());

	[Theory]
	[InlineData("https://user:secret@127.0.0.1:55101/")]
	[InlineData("https://127.0.0.1:55101/control")]
	[InlineData("https://127.0.0.1:55101/?token=value")]
	[InlineData("https://127.0.0.1:55101/#fragment")]
	public void Client_transport_rejects_credential_bearing_or_path_urls(string endpoint) =>
		Assert.Throws<ArgumentException>(() => new GrpcOperatorControlOptions { Endpoint = new Uri(endpoint) }.Validate());

	[Fact]
	public void Client_test_only_tls_bypass_is_loopback_only()
	{
		Assert.Throws<ArgumentException>(() => new GrpcOperatorControlOptions
		{
			Endpoint = new Uri("https://192.0.2.10:55101"),
			TrustMode = ExternalControlTrustMode.TestOnlyInsecure
		}.Validate());
		new GrpcOperatorControlOptions
		{
			Endpoint = new Uri("https://127.0.0.1:55101"),
			TrustMode = ExternalControlTrustMode.TestOnlyInsecure
		}.Validate();
	}

	private static IntegrationGatewayOptions TestGateway() => new()
	{
		Enabled = true,
		TestMode = true,
		DryRun = true,
		Upstream = new IntegrationUpstreamOptions { Endpoint = "https://127.0.0.1:55101", TrustMode = IntegrationTrustMode.TestOnlyInsecure },
		Adapters = [new IntegrationAdapterOptions { Id = "osc", Kind = IntegrationAdapterKind.Osc, Osc = new OscAdapterOptions() }]
	};
}
