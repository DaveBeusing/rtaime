// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;

namespace rtaime.Tests.Contracts;

public sealed class ReferencePlatformQualificationContractTests
{
	private static readonly string[] QualificationStatuses =
	{
		"PASS",
		"FAIL",
		"NOT_APPLICABLE",
		"UNVERIFIED"
	};

	[Fact]
	public void Reference_platform_profile_has_stable_v1_contract()
	{
		var repositoryRoot = FindRepositoryRoot();
		using var document = LoadJson(repositoryRoot, "qualification/reference-platform/reference-platform.json");
		var root = document.RootElement;

		Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());
		Assert.Equal("rtaime-v1-reference-platform", root.GetProperty("profile").GetString());

		var platform = root.GetProperty("platform");
		Assert.Equal("Windows", platform.GetProperty("operatingSystemFamily").GetString());
		Assert.Equal("x64", platform.GetProperty("architecture").GetString());
		Assert.Equal("10.0.401", platform.GetProperty("dotnetSdk").GetString());
		Assert.Equal(
			new[] { "1080p50", "1080p59.94" },
			platform.GetProperty("developmentFormats").EnumerateArray().Select(value => value.GetString()).ToArray());
		Assert.Equal(
			new[] { "rtaime.ControlHost", "rtaime.RuntimeHost", "rtaime.AIHost", "rtaime.Operator" },
			platform.GetProperty("requiredHosts").EnumerateArray().Select(value => value.GetString()).ToArray());

		var scenarios = root.GetProperty("softwareScenarios").EnumerateArray().ToArray();
		Assert.Equal(10, scenarios.Length);
		Assert.Equal(
			Enumerable.Range(1, 10).Select(value => $"Q{value:D2}").ToArray(),
			scenarios.Select(value => value.GetProperty("id").GetString()).ToArray());
		Assert.All(scenarios, value => Assert.True(value.GetProperty("mandatory").GetBoolean()));

		var hardware = root.GetProperty("hardwareRequirements").EnumerateArray().ToArray();
		Assert.Equal(5, hardware.Length);
		Assert.Equal(
			new[] { "REFERENCE_GPU", "PROFESSIONAL_MEDIA_IO", "GENLOCK", "PHYSICAL_END_TO_END_LATENCY", "LONG_SOAK" },
			hardware.Select(value => value.GetProperty("requirement").GetString()).ToArray());
		Assert.All(hardware, value => Assert.True(value.GetProperty("mandatory").GetBoolean()));
	}

	[Fact]
	public void Qualification_schemas_publish_stable_v1_identity_and_status_model()
	{
		var repositoryRoot = FindRepositoryRoot();
		using var profileSchema = LoadJson(repositoryRoot, "schemas/qualification/v1/reference-platform.schema.json");
		using var resultSchema = LoadJson(repositoryRoot, "schemas/qualification/v1/reference-platform-result.schema.json");
		using var environmentSchema = LoadJson(repositoryRoot, "schemas/qualification/v1/reference-platform-environment.schema.json");

		AssertSchemaIdentity(
			profileSchema.RootElement,
			"https://rtaime.local/schemas/qualification/v1/reference-platform.schema.json");
		AssertSchemaIdentity(
			resultSchema.RootElement,
			"https://rtaime.local/schemas/qualification/v1/reference-platform-result.schema.json");
		AssertSchemaIdentity(
			environmentSchema.RootElement,
			"https://rtaime.local/schemas/qualification/v1/reference-platform-environment.schema.json");

		var statuses = resultSchema.RootElement
			.GetProperty("$defs")
			.GetProperty("qualificationStatus")
			.GetProperty("enum")
			.EnumerateArray()
			.Select(value => value.GetString())
			.ToArray();
		Assert.Equal(QualificationStatuses, statuses);

		var resultPlatformStatuses = resultSchema.RootElement
			.GetProperty("properties")
			.GetProperty("platformStatus")
			.GetProperty("enum")
			.EnumerateArray()
			.Select(value => value.GetString())
			.ToArray();
		Assert.Equal(new[] { "PASS", "FAIL" }, resultPlatformStatuses);

		var environmentPlatformStatuses = environmentSchema.RootElement
			.GetProperty("properties")
			.GetProperty("platformStatus")
			.GetProperty("enum")
			.EnumerateArray()
			.Select(value => value.GetString())
			.ToArray();
		Assert.Equal(new[] { "PASS", "FAIL" }, environmentPlatformStatuses);
	}

	[Fact]
	public void Qualification_result_schema_requires_fail_closed_evidence_fields()
	{
		var repositoryRoot = FindRepositoryRoot();
		using var document = LoadJson(repositoryRoot, "schemas/qualification/v1/reference-platform-result.schema.json");
		var required = document.RootElement
			.GetProperty("required")
			.EnumerateArray()
			.Select(value => value.GetString())
			.ToArray();

		foreach (var property in new[]
		{
			"schemaVersion",
			"profile",
			"capturedAtUtc",
			"sourceCommit",
			"status",
			"softwareStatus",
			"hardwareStatus",
			"platformStatus",
			"counts",
			"environmentPath",
			"scenarios",
			"hardwareRequirements"
		})
		{
			Assert.Contains(property, required);
		}

		var counts = document.RootElement
			.GetProperty("properties")
			.GetProperty("counts")
			.GetProperty("required")
			.EnumerateArray()
			.Select(value => value.GetString())
			.ToArray();
		Assert.Equal(QualificationStatuses, counts);
	}

	private static JsonDocument LoadJson(string repositoryRoot, string relativePath) =>
		JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar))));

	private static void AssertSchemaIdentity(JsonElement schema, string expectedId)
	{
		Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema.GetProperty("$schema").GetString());
		Assert.Equal(expectedId, schema.GetProperty("$id").GetString());
		Assert.Contains("Dave Beusing", schema.GetProperty("$comment").GetString(), StringComparison.Ordinal);
		Assert.Contains("david.beusing@gmail.com", schema.GetProperty("$comment").GetString(), StringComparison.Ordinal);
	}

	private static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "rtaime.slnx")))
				return directory.FullName;
			directory = directory.Parent;
		}

		throw new DirectoryNotFoundException("Unable to locate the rtaime repository root.");
	}
}
