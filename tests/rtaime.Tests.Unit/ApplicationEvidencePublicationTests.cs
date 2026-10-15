// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Text.Json;
using rtaime.AppHost;

namespace rtaime.Tests.Unit;

public sealed class ApplicationEvidencePublicationTests
{
	[Fact]
	public void Creates_then_atomically_replaces_evidence_without_leaving_temporary_files()
	{
		var root = CreateRoot();
		var path = Path.Combine(root, "lifecycle.json");
		var platform = new SystemApplicationHostPlatform();
		try
		{
			platform.WriteAllTextAtomic(path, "{\"revision\":1}");
			platform.WriteAllTextAtomic(path, "{\"revision\":2}");
			using var document = JsonDocument.Parse(platform.ReadAllText(path));
			Assert.Equal(2, document.RootElement.GetProperty("revision").GetInt32());
			Assert.Single(Directory.GetFiles(root));
		}
		finally { Directory.Delete(root, true); }
	}

	[Fact]
	public void Cooperative_reader_keeps_old_snapshot_while_writer_publishes_complete_new_snapshots()
	{
		var root = CreateRoot();
		var path = Path.Combine(root, "lifecycle.json");
		var platform = new SystemApplicationHostPlatform();
		try
		{
			platform.WriteAllTextAtomic(path, "{\"revision\":0}");
			using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
			using (var reader = new StreamReader(stream))
			{
				for (var revision = 1; revision <= 32; revision++)
				{
					platform.WriteAllTextAtomic(path, JsonSerializer.Serialize(new { revision }));
					using var current = JsonDocument.Parse(platform.ReadAllText(path));
					Assert.Equal(revision, current.RootElement.GetProperty("revision").GetInt32());
				}
				using var original = JsonDocument.Parse(reader.ReadToEnd());
				Assert.Equal(0, original.RootElement.GetProperty("revision").GetInt32());
			}
			Assert.Single(Directory.GetFiles(root));
		}
		finally { Directory.Delete(root, true); }
	}

	[Fact]
	public void Persistent_noncooperative_Windows_reader_fails_with_target_context_and_retains_previous_evidence()
	{
		if (!OperatingSystem.IsWindows()) return; // Windows sharing contract exercised by Required Gates.
		var root = CreateRoot();
		var path = Path.Combine(root, "lifecycle.json");
		var platform = new SystemApplicationHostPlatform();
		try
		{
			platform.WriteAllTextAtomic(path, "{\"revision\":7}");
			using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				var failure = Assert.Throws<ApplicationEvidencePublicationException>(() =>
					platform.WriteAllTextAtomic(path, "{\"revision\":8}"));
				Assert.Equal(path, failure.TargetPath);
				Assert.Contains(path, failure.Message);
				Assert.NotNull(failure.InnerException);
				Assert.Single(Directory.GetFiles(root));
			}
			Assert.Equal("{\"revision\":7}", platform.ReadAllText(path));
		}
		finally { Directory.Delete(root, true); }
	}

	private static string CreateRoot()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-evidence-publication-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return root;
	}
}
