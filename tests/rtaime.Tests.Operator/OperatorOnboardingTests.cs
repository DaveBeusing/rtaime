// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using System.Text.Json;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class OperatorOnboardingTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "rtaime-guide-" + Guid.NewGuid().ToString("N"));
	private string PathName => Path.Combine(_root, "guide.json");
	[Fact]
	public async Task Cold_start_review_minimize_disable_and_reopen_persist_only_versioned_local_progress()
	{
		var store = new OperatorOnboardingStore(PathName);
		using (var model = new OperatorOnboardingViewModel(store))
		{
			Assert.False(model.ReviewCommand.CanExecute(null));
			await model.LoadAsync();
			Assert.False(File.Exists(PathName));
			Assert.True(model.IsExpanded);
			Assert.Equal("import", model.Current.Id);
			for (var index = 0; index < OperatorOnboardingViewModel.Steps.Count; index++)
			{
				model.ReviewCommand.Execute(null);
				model.NextCommand.Execute(null);
			}
			Assert.Equal("7 / 7 topics reviewed locally · not production readiness", model.Progress);
			model.MinimizeCommand.Execute(null); Assert.False(model.IsExpanded);
			model.ReopenCommand.Execute(null); Assert.True(model.IsExpanded);
			model.DisableCommand.Execute(null); Assert.False(model.IsExpanded);
			await model.FlushAsync();
		}
		using var restored = new OperatorOnboardingViewModel(store);
		await restored.LoadAsync();
		Assert.False(restored.IsExpanded);
		Assert.Equal("layout", restored.Current.Id);
		Assert.True(restored.IsReviewed);
		restored.ReopenCommand.Execute(null); await restored.FlushAsync();
		Assert.True(restored.IsExpanded);
		using var json = JsonDocument.Parse(await File.ReadAllTextAsync(PathName));
		Assert.Equal(new[] { "Version", "Enabled", "Minimized", "Step", "ReviewedMask" },
			json.RootElement.EnumerateObject().Select(property => property.Name));
		Assert.Equal(1, json.RootElement.GetProperty("Version").GetInt32());
		restored.Dispose(); Assert.False(restored.ReviewCommand.CanExecute(null));
	}
	[Theory]
	[InlineData("{")]
	[InlineData("{\"Version\":2,\"Enabled\":true,\"Minimized\":false,\"Step\":0,\"ReviewedMask\":0}")]
	[InlineData("{\"Version\":1,\"Enabled\":true,\"Minimized\":false,\"Step\":99,\"ReviewedMask\":0}")]
	[InlineData("{\"Version\":1,\"Enabled\":true,\"Minimized\":false,\"Step\":0,\"ReviewedMask\":-1}")]
	[InlineData("{\"Version\":1,\"Enabled\":true,\"Minimized\":false,\"Step\":0,\"Step\":1,\"ReviewedMask\":0}")]
	public async Task Invalid_or_future_preferences_are_retained_until_an_explicit_local_edit(string content)
	{
		Directory.CreateDirectory(_root); await File.WriteAllTextAsync(PathName, content);
		using var model = new OperatorOnboardingViewModel(new(PathName));
		await model.LoadAsync();
		Assert.Equal(content, await File.ReadAllTextAsync(PathName));
		Assert.Equal("import", model.Current.Id);
		Assert.Contains("Original file retained", model.Status);
		model.MinimizeCommand.Execute(null); await model.FlushAsync();
		Assert.True((await new OperatorOnboardingStore(PathName).ReadAsync(default)).Minimized);
	}
	[Fact]
	public async Task Oversized_cancelled_and_unwritable_preferences_do_not_affect_production()
	{
		Directory.CreateDirectory(_root);
		await File.WriteAllTextAsync(PathName, new string(' ', OperatorOnboardingStore.MaximumBytes + 1));
		using var oversized = new OperatorOnboardingViewModel(new(PathName));
		await oversized.LoadAsync(); Assert.Contains("Original file retained", oversized.Status);
		using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
		using var model = new OperatorOnboardingViewModel(new(PathName));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.LoadAsync(cancelled.Token));
		Assert.False(model.ReopenCommand.CanExecute(null));
		using var unwritable = new OperatorOnboardingViewModel(new(_root));
		await unwritable.LoadAsync();
		unwritable.ReviewCommand.Execute(null); await unwritable.FlushAsync();
		Assert.True(unwritable.IsReviewed); Assert.Contains("Session only", unwritable.Status);
		Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
	}
	public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
