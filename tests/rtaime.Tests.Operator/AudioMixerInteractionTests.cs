// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using System.Windows.Input;
using System.Xml.Linq;
using rtaime.Operator.Controls;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class AudioMixerInteractionTests
{
	[Fact]
	public void Mixer_gain_controls_offer_named_fine_keyboard_steps_and_evidence_aware_meters()
	{
		var document = Surface();
		var faders = document.Descendants().Where(element => element.Name.LocalName == "RtaimeSlider").ToArray();
		Assert.Equal(2, faders.Length);
		Assert.All(faders, fader =>
		{
			Assert.Equal("0", fader.Attribute("Minimum")?.Value);
			Assert.Equal("4", fader.Attribute("Maximum")?.Value);
			Assert.Equal("0.01", fader.Attribute("SmallChange")?.Value);
			Assert.Equal("0.1", fader.Attribute("LargeChange")?.Value);
			Assert.False(string.IsNullOrWhiteSpace(fader.Attribute("AutomationProperties.Name")?.Value));
			Assert.NotEqual("False", fader.Attribute("Focusable")?.Value);
			Assert.Contains("Mode=TwoWay", fader.Attribute("Value")!.Value, StringComparison.Ordinal);
		});
		var meters = document.Descendants().Where(element => element.Name.LocalName == "RtaimeMetricBar").ToArray();
		Assert.Equal(4, meters.Length);
		Assert.All(meters, meter => Assert.Equal("{Binding HasMeterEvidence}", meter.Attribute("HasValue")?.Value));
		foreach (var command in new[] { "ApplyCommand", "DiscardDraftCommand" })
			Assert.All(document.Descendants().Where(element => element.Attribute("Command")?.Value == "{Binding " + command + "}"), action =>
			{
				Assert.False(string.IsNullOrWhiteSpace(action.Attribute("AutomationProperties.Name")?.Value));
				Assert.DoesNotContain(action.Ancestors(), ancestor => ancestor.Attribute("Style")?.Value == "{StaticResource AudioProcessingPanel}");
			});
		Assert.Contains(document.Descendants(), element => element.Attribute("AutomationProperties.LiveSetting")?.Value == "Polite");
	}

	[Fact]
	public void Custom_fader_keyboard_commands_adjust_without_unbounded_values()
	{
		Exception? failure = null;
		var thread = new Thread(() =>
		{
			try
			{
				var slider = new RtaimeSlider { Minimum = 0, Maximum = 4, Value = 1, SmallChange = 0.01, LargeChange = 0.1 };
				SliderCommand(System.Windows.Controls.Slider.IncreaseSmall, slider);
				Assert.Equal(1.01, slider.Value, 6);
				SliderCommand(System.Windows.Controls.Slider.DecreaseLarge, slider);
				Assert.Equal(0.91, slider.Value, 6);
				slider.Value = 4;
				SliderCommand(System.Windows.Controls.Slider.IncreaseSmall, slider);
				Assert.Equal(4, slider.Value);
			}
			catch (Exception exception) { failure = exception; }
		});
		thread.IsBackground = true;
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
		Assert.Null(failure);
	}

	private static void SliderCommand(RoutedCommand command, RtaimeSlider slider) => command.Execute(null, slider);

	private static XDocument Surface()
	{
		DirectoryInfo? root = new(AppContext.BaseDirectory);
		while (root is not null && !File.Exists(Path.Combine(root.FullName, "rtaime.slnx")))
			root = root.Parent;
		Assert.NotNull(root);
		return XDocument.Load(Path.Combine(root.FullName, "src", "Hosts", "rtaime.Operator", "AudioMixerControl.xaml"));
	}
}
