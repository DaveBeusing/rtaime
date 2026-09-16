// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.Text.Json;
using rtaime.Persistence;

namespace rtaime.Tests.Integration;

public sealed class StateMaintenanceCliIntegrationTests
{
	[Fact]
	public async Task ControlHost_state_maintenance_inspects_and_backs_up_real_management_database()
	{
		var root = TempDirectory();
		try
		{
			var database = Path.Combine(root, "management.db");
			await using (var store = new SqliteManagementStore(database))
			{
				var result = await store.PutDocumentAsync("configuration", "control", "{\"mode\":\"cli\"}", 0);
				Assert.True(result.Written);
			}

			var host = ControlHostAssemblyPath();
			var inspection = Path.Combine(root, "inspection.json");
			var inspect = await RunAsync(host,
				"state-maintenance",
				"inspect",
				$"--database={database}",
				$"--output={inspection}");
			Assert.Equal(0, inspect.ExitCode);
			Assert.Contains("outcome=pass", inspect.StandardOutput, StringComparison.OrdinalIgnoreCase);
			using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(inspection)))
			{
				var schema = json.RootElement.GetProperty("schema");
				Assert.Equal(1, schema.GetArrayLength());
				Assert.Equal("management", schema[0].GetProperty("component").GetString());
				Assert.Equal(1, schema[0].GetProperty("version").GetInt32());
			}

			var backup = Path.Combine(root, "backup", "management.db");
			var snapshot = Path.Combine(root, "backup", "snapshot.json");
			var backupResult = await RunAsync(host,
				"state-maintenance",
				"backup",
				$"--database={database}",
				$"--backup={backup}",
				$"--output={snapshot}");
			Assert.Equal(0, backupResult.ExitCode);
			Assert.True(File.Exists(backup));
			Assert.True(File.Exists(snapshot));
			Assert.True((await new SqliteManagementStore(backup).VerifyIntegrityAsync()).Healthy);
		}
		finally
		{
			DeleteDirectory(root);
		}
	}

	[Fact]
	public async Task ControlHost_state_maintenance_restore_without_acknowledgement_fails_closed()
	{
		var root = TempDirectory();
		try
		{
			var database = Path.Combine(root, "management.db");
			await using (var store = new SqliteManagementStore(database))
				await store.InitializeAsync();

			var host = ControlHostAssemblyPath();
			var backup = Path.Combine(root, "backup", "management.db");
			var snapshot = Path.Combine(root, "backup", "snapshot.json");
			var backupResult = await RunAsync(host,
				"state-maintenance",
				"backup",
				$"--database={database}",
				$"--backup={backup}",
				$"--output={snapshot}");
			Assert.Equal(0, backupResult.ExitCode);

			var restore = await RunAsync(host,
				"state-maintenance",
				"restore",
				$"--snapshot={snapshot}",
				$"--database={database}");
			Assert.Equal(64, restore.ExitCode);
			Assert.Contains("acknowledge-exclusive-access", restore.StandardError, StringComparison.OrdinalIgnoreCase);
		}
		finally
		{
			DeleteDirectory(root);
		}
	}

	private static string ControlHostAssemblyPath()
	{
		var path = Path.Combine(AppContext.BaseDirectory, "rtaime.ControlHost.dll");
		if (!File.Exists(path))
			throw new FileNotFoundException("Integration-test output does not contain rtaime.ControlHost.dll.", path);
		return path;
	}

	private static async Task<ProcessResult> RunAsync(string assembly, params string[] arguments)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = "dotnet",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		startInfo.ArgumentList.Add(assembly);
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ControlHost maintenance process.");
		var standardOutput = process.StandardOutput.ReadToEndAsync();
		var standardError = process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
	}

	private static string TempDirectory()
	{
		var path = Path.Combine(Path.GetTempPath(), "rtaime-state-cli-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(path);
		return path;
	}

	private static void DeleteDirectory(string path)
	{
		try { Directory.Delete(path, recursive: true); }
		catch { }
	}

	private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
