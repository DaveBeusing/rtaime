// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.ControlHost;

namespace rtaime.Tests.Failure;

public sealed class BroadcastControlSecurityTests
{
	[Fact]
	public void Duplicate_certificate_thumbprints_cannot_map_to_different_privilege_roles()
	{
		var thumbprint = new string('A', 40);
		var options = Valid() with
		{
			Identities =
			[
				new ExternalControlIdentityOptions("observer", ExternalControlRole.Observer, CertificateThumbprint: thumbprint),
				new ExternalControlIdentityOptions("administrator", ExternalControlRole.Administrator, CertificateThumbprint: thumbprint.ToLowerInvariant())
			]
		};
		Assert.Throws<ArgumentException>(options.Validate);
	}

	[Fact]
	public void Shared_token_secret_cannot_select_another_principal_role()
	{
		var options = Valid() with
		{
			Identities =
			[
				new ExternalControlIdentityOptions("observer", ExternalControlRole.Observer, TokenEnvironmentVariable: "RTAIME_SHARED_TOKEN"),
				new ExternalControlIdentityOptions("administrator", ExternalControlRole.Administrator, TokenEnvironmentVariable: "RTAIME_SHARED_TOKEN")
			]
		};
		Assert.Throws<ArgumentException>(options.Validate);
	}

	[Theory]
	[InlineData("line\nfeed")]
	[InlineData("name with spaces")]
	[InlineData("key/value")]
	public void Control_identity_rejects_unsafe_client_identifiers(string clientId)
	{
		Assert.Throws<ArgumentException>(() => (Valid() with
		{
			Identities = [new ExternalControlIdentityOptions(clientId, ExternalControlRole.Operator, TokenEnvironmentVariable: "RTAIME_CONTROL_TOKEN")]
		}).Validate());
	}

	[Theory]
	[InlineData("SECRET=plaintext")]
	[InlineData("bad token")]
	[InlineData("RTAIME_TOKEN\nNEXT")]
	public void Secret_references_cannot_be_literal_or_control_character_values(string reference)
	{
		Assert.Throws<ArgumentException>(() => (Valid() with
		{
			Identities = [new ExternalControlIdentityOptions("operator", ExternalControlRole.Operator, TokenEnvironmentVariable: reference)]
		}).Validate());
		Assert.Throws<ArgumentException>(() => (Valid() with
		{
			CertificatePasswordEnvironmentVariable = reference
		}).Validate());
	}

	[Theory]
	[InlineData("1234")]
	[InlineData("Z000000000000000000000000000000000000000")]
	public void Invalid_certificate_identity_fingerprints_fail_closed(string thumbprint)
	{
		Assert.Throws<ArgumentException>(() => (Valid() with
		{
			Identities = [new ExternalControlIdentityOptions("operator", ExternalControlRole.Operator, CertificateThumbprint: thumbprint)]
		}).Validate());
	}

	[Fact]
	public void Distinct_certificates_and_secret_references_remain_supported()
	{
		(Valid() with
		{
			Identities =
			[
				new ExternalControlIdentityOptions("operator", ExternalControlRole.Operator, CertificateThumbprint: new string('A', 40)),
				new ExternalControlIdentityOptions("observer", ExternalControlRole.Observer, TokenEnvironmentVariable: "RTAIME_OBSERVER_TOKEN")
			]
		}).Validate();
	}

	private static ExternalControlServerOptions Valid() => new()
	{
		Enabled = true,
		Required = true,
		CertificatePath = "test-only.pfx",
		Identities = [new ExternalControlIdentityOptions("operator", ExternalControlRole.Operator, TokenEnvironmentVariable: "RTAIME_CONTROL_TOKEN")]
	};
}
