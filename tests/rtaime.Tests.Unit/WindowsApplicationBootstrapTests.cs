// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;
using rtaime.AppHost;

namespace rtaime.Tests.Unit;

public sealed class WindowsApplicationBootstrapTests : IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		"rtaime-windows-bootstrap-tests",
		Guid.NewGuid().ToString("N"));

	public WindowsApplicationBootstrapTests()
	{
		Directory.CreateDirectory(_root);
	}

	[Theory]
	[InlineData(false, "--profile=Interactive")]
	[InlineData(false, "--profile=Showcase")]
	[InlineData(true, "--profile=HeadlessEngine")]
	[InlineData(true, "--profile=Interactive", "--show-console")]
	[InlineData(false, "--windows-service", "--profile=HeadlessEngine", "--show-console")]
	public void Console_request_policy_preserves_interactive_and_operational_modes(
		bool expected,
		params string[] args)
	{
		Assert.Equal(expected, WindowsConsoleBootstrap.ShouldRequestConsole(args));
	}

	[Fact]
	public void Early_startup_failure_is_persisted_below_explicit_work_root()
	{
		var args = new[]
		{
			"--profile=InvalidProfile",
			$"--work-root={_root}"
		};
		var expectedPath = Path.Combine(_root, ApplicationStartupDiagnostics.FileName);

		var actualPath = ApplicationStartupDiagnostics.TryPersistFailure(
			args,
			new InvalidOperationException("bootstrap qualification failure"));

		Assert.Equal(expectedPath, actualPath);
		Assert.True(File.Exists(expectedPath));

		using var document = JsonDocument.Parse(File.ReadAllText(expectedPath).Trim());
		Assert.Equal("1.0", document.RootElement.GetProperty("schemaVersion").GetString());
		Assert.Equal("FAILED", document.RootElement.GetProperty("state").GetString());
		Assert.Equal(
			"bootstrap qualification failure",
			document.RootElement.GetProperty("message").GetString());
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}
}
