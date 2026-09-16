// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class RuntimeMediaIoModeTests
{
	[Fact]
	public void Default_RuntimeHost_media_io_mode_remains_virtual()
	{
		var options = RuntimeHostProcessOptions.Default;

		Assert.Equal(RuntimeMediaIoMode.Virtual, options.MediaIoMode);
		Assert.False(options.RequireExternalReference);
		options.Validate();
	}

	[Fact]
	public void Configuration_loading_selects_native_mode_explicitly_and_command_line_wins()
	{
		var environment = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["RTAIME_RUNTIME_MEDIA_IO"] = "virtual",
			["RTAIME_RUNTIME_REQUIRE_EXTERNAL_REFERENCE"] = "false"
		};

		var options = RuntimeHostProcessOptions.Load(
			new[] { "--media-io=native", "--require-external-reference=true" },
			name => environment.TryGetValue(name, out var value) ? value : null);

		Assert.Equal(RuntimeMediaIoMode.Native, options.MediaIoMode);
		Assert.True(options.RequireExternalReference);
		options.Validate();
	}

	[Fact]
	public void External_reference_requirement_fails_closed_for_virtual_mode()
	{
		var invalid = RuntimeHostProcessOptions.Default with { RequireExternalReference = true };

		var exception = Assert.Throws<ArgumentException>(invalid.Validate);
		Assert.Contains("native Media I/O", exception.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Unknown_media_io_mode_is_rejected_during_configuration_load()
	{
		var exception = Assert.Throws<ArgumentException>(() => RuntimeHostProcessOptions.Load(new[] { "--media-io=automatic" }));

		Assert.Contains("virtual", exception.Message, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("native", exception.Message, StringComparison.OrdinalIgnoreCase);
	}
}
