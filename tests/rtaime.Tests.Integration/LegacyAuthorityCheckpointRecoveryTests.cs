// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Persistence;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class LegacyAuthorityCheckpointRecoveryTests
{
	private static readonly Identity CheckpointId = Identity.Parse("73000000-0000-0000-0000-000000000001");
	private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", name);

	[Fact]
	public async Task Legacy_checkpoint_reconciles_real_Runtime_and_preserves_authority_across_restarts_and_next_commit()
	{
		if (!OperatingSystem.IsWindows()) return;
		var root = Path.Combine(Path.GetTempPath(), "rtaime-legacy-recovery-" + Guid.NewGuid().ToString("N"));
		var runtimeEndpoint = "rtaime.test.legacy-runtime." + Guid.NewGuid().ToString("N");
		var options = ControlHostProcessOptions.Default with
		{
			ListenEndpoint = "rtaime.test.legacy-control." + Guid.NewGuid().ToString("N"),
			RuntimeEndpoint = runtimeEndpoint,
			DurabilityRoot = root,
			ConnectTimeout = TimeSpan.FromMilliseconds(150),
			RequestTimeout = TimeSpan.FromSeconds(3),
			RuntimeRetryInterval = TimeSpan.FromMilliseconds(25)
		};
		var payload = await File.ReadAllBytesAsync(Fixture("LegacyAuthorityV1.json"));
		using var document = JsonDocument.Parse(payload);
		var expected = document.RootElement;
		try
		{
			await SeedAsync(options, payload);
			for (var round = 0; round < 3; round++)
			{
				using var runtimeStop = new CancellationTokenSource();
				using var controlStop = new CancellationTokenSource();
				var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
				var runtimeRun = runtime.RunAsync(runtimeStop.Token);
				var control = new ControlHostProcess(options);
				var controlRun = control.RunAsync(controlStop.Token);
				try
				{
					await WaitForReadyAsync(control, controlRun);
					Assert.Equal(ControlContractVersion.Current, control.Control!.State.Version);
					Assert.Equal(new Revision(round == 2 ? 9UL : 8UL), control.Control.State.Revision);
					Assert.Equal(expected.GetProperty("ProductionId").GetString(), control.Control.State.ProductionId.ToString());
					Assert.Equal(expected.GetProperty("PreviewSourceId").GetString(), control.Control.State.Routing.PreviewSourceId.ToString());
					Assert.Equal(expected.GetProperty("ProgramSourceId").GetString(), control.Control.State.Routing.ProgramSourceId.ToString());
					Assert.Null(control.Control.State.ActiveSceneId);
					Assert.Equal(expected.GetProperty("OutputRoles").GetArrayLength(), control.Control.State.OutputRoles.Count);
					foreach (var saved in expected.GetProperty("OutputRoles").EnumerateArray())
					{
						var role = Assert.Single(control.Control.State.OutputRoles, role => role.RoleId.ToString() == saved.GetProperty("RoleId").GetString());
						var expectedSource = round == 2 && role.RoleId == OutputRoleIds.Aux
							? expected.GetProperty("ProgramSourceId").GetString() : saved.GetProperty("SourceId").GetString();
						Assert.Equal(expectedSource, role.SourceId.ToString());
						Assert.Equal(saved.GetProperty("Kind").GetInt32(), (int)role.Kind);
						Assert.Equal(saved.GetProperty("ProviderSelector").GetString(), role.ProviderSelector);
						Assert.Equal(saved.GetProperty("TargetId").GetString(), role.TargetId);
						Assert.Equal(saved.GetProperty("FormatPolicy").GetString(), role.FormatPolicy);
						Assert.Equal(saved.GetProperty("TimingPolicy").GetString(), role.TimingPolicy);
						Assert.Equal(saved.GetProperty("Enabled").GetBoolean(), role.Enabled);
						Assert.Equal("program", role.AudioBusId);
					}
					var savedLayer = expected.GetProperty("CompositingState").GetProperty("Layers")[0];
					var layer = Assert.Single(control.Control.State.CompositingState!.Layers);
					Assert.Equal(savedLayer.GetProperty("LayerId").GetString(), layer.LayerId);
					Assert.Equal(savedLayer.GetProperty("ContentIdentity").GetString(), layer.ContentIdentity);
					Assert.Equal(savedLayer.GetProperty("PositionX").GetDouble(), layer.PositionX);
					Assert.Equal(savedLayer.GetProperty("PositionY").GetDouble(), layer.PositionY);
					Assert.Equal(savedLayer.GetProperty("Scale").GetDouble(), layer.Scale);
					Assert.Equal(savedLayer.GetProperty("Opacity").GetByte(), layer.Opacity);
					Assert.Equal(savedLayer.GetProperty("Visible").GetBoolean(), layer.Visible);
					Assert.Equal(savedLayer.GetProperty("Order").GetInt32(), layer.Order);
					Assert.Equal(savedLayer.GetProperty("RotationDegrees").GetDouble(), layer.RotationDegrees);
					Assert.Equal(savedLayer.GetProperty("AnchorX").GetDouble(), layer.AnchorX);
					Assert.Equal(savedLayer.GetProperty("AnchorY").GetDouble(), layer.AnchorY);
					Assert.Equal(savedLayer.GetProperty("CropLeft").GetDouble(), layer.CropLeft);
					Assert.Equal(savedLayer.GetProperty("CropTop").GetDouble(), layer.CropTop);
					Assert.Equal(savedLayer.GetProperty("CropRight").GetDouble(), layer.CropRight);
					Assert.Equal(savedLayer.GetProperty("CropBottom").GetDouble(), layer.CropBottom);
					Assert.Empty(layer.ProcessingStack);
					var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(
						options.ListenEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));
					var snapshot = await client.SynchronizeAsync();
					Assert.Equal(control.Control.State.Revision, snapshot.Production.Revision);
					Assert.Equal("LIVE FROM RTAIME", snapshot.ProductionCgText.Text);
					Assert.Equal(0.05002605523710266,
						Assert.Single(snapshot.CompositingLayers, layer => layer.LayerId == "production-cg").PositionX);
					await AssertOriginalAsync(options, payload, round == 2 ? 2 : 1);
					if (round == 1)
					{
						var mutation = await client.RouteOutputRoleAsync("aux", expected.GetProperty("ProgramSourceId").GetString()!);
						Assert.True(mutation.Accepted, mutation.Failure?.ToString());
						Assert.Equal(new Revision(9), control.Control.State.Revision);
						await control.CheckpointWriter!.FlushAsync();
						await using var store = new SqliteManagementStore(Database(options));
						var latest = await store.ReadLatestAsync(options.ProductionId.Value);
						Assert.Equal(new Revision(9), latest!.AuthoritativeRevision);
						using var currentPayload = JsonDocument.Parse(latest.Payload);
						Assert.Equal("1.1", currentPayload.RootElement.GetProperty("Version").GetString());
						await AssertOriginalAsync(options, payload, 2);
					}
				}
				finally
				{
					controlStop.Cancel();
					Assert.Equal(ControlHostExitCode.Success, await controlRun.WaitAsync(TimeSpan.FromSeconds(12)));
					runtimeStop.Cancel();
					Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun.WaitAsync(TimeSpan.FromSeconds(12)));
				}
			}
		}
		finally
		{
			if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
		}
	}

	[Theory]
	[InlineData("1.2")]
	[InlineData("2.0")]
	public async Task Unsupported_checkpoint_contract_fails_closed_and_preserves_the_original_payload(string version)
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-legacy-invalid-" + Guid.NewGuid().ToString("N"));
		var options = ControlHostProcessOptions.Default with
		{
			ListenEndpoint = "rtaime.test.legacy-invalid." + Guid.NewGuid().ToString("N"),
			RuntimeEndpoint = "rtaime.test.legacy-invalid-runtime." + Guid.NewGuid().ToString("N"),
			DurabilityRoot = root
		};
		var node = JsonNode.Parse(await File.ReadAllTextAsync(Fixture("LegacyAuthorityV1.json")))!;
		node["Version"] = version;
		var payload = Encoding.UTF8.GetBytes(node.ToJsonString());
		try
		{
			await SeedAsync(options, payload);
			var control = new ControlHostProcess(options);
			Assert.Equal(ControlHostExitCode.StartupFailure, await control.RunAsync(CancellationToken.None));
			Assert.Equal(ControlHostProcessState.Failed, control.Lifecycle.State);
			Assert.False(control.Control?.HasAuthoritativeState ?? false);
			await AssertOriginalAsync(options, payload, 1);
		}
		finally
		{
			if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
		}
	}

	private static string Database(ControlHostProcessOptions options) =>
		Path.Combine(options.DurabilityRoot, $"{options.ProductionId}-{options.ListenEndpoint}", "management.db");

	private static async Task SeedAsync(ControlHostProcessOptions options, byte[] payload)
	{
		await using var store = new SqliteManagementStore(Database(options));
		var project = await store.PutDocumentAsync("show.project", options.ProductionId.ToString(),
			await File.ReadAllTextAsync(Fixture("LegacyShowProjectV1.json")), 0);
		Assert.True(project.Written);
		await store.WriteAsync(new ProductionCheckpoint(CheckpointId, options.ProductionId.Value, new Revision(8),
			new UtcTimestamp(DateTimeOffset.Parse("2026-09-26T19:51:13.1453784+00:00")),
			"rtaime.control.authority.v1", payload));
	}

	private static async Task AssertOriginalAsync(ControlHostProcessOptions options, byte[] payload, int expectedCount)
	{
		await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
		{
			DataSource = Database(options), Mode = SqliteOpenMode.ReadOnly, Pooling = false
		}.ToString());
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT payload FROM production_checkpoints WHERE checkpoint_id = @id";
		command.Parameters.AddWithValue("@id", CheckpointId.ToString());
		Assert.Equal(payload, Assert.IsType<byte[]>(await command.ExecuteScalarAsync()));
		command.CommandText = "SELECT COUNT(*) FROM production_checkpoints";
		Assert.Equal((long)expectedCount, (long)(await command.ExecuteScalarAsync())!);
		await using var store = new SqliteManagementStore(Database(options));
		Assert.True((await store.VerifyIntegrityAsync()).Healthy);
	}

	private static async Task WaitForReadyAsync(ControlHostProcess process, Task<ControlHostExitCode> run)
	{
		var deadline = DateTime.UtcNow.AddSeconds(15);
		while (process.Lifecycle.State != ControlHostProcessState.Ready)
		{
			if (run.IsCompleted) throw new InvalidOperationException($"ControlHost exited before readiness: {await run}");
			if (DateTime.UtcNow >= deadline)
				throw new TimeoutException($"Legacy recovery did not qualify: {process.Lifecycle.Detail}; {process.Recovery.Detail}");
			await Task.Delay(20);
		}
		Assert.Equal(ControlHostHealthState.Healthy, process.Lifecycle.Health);
		Assert.Equal(ControlHostRecoveryState.Recovered, process.Recovery.State);
	}
}
