// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using System.Text;
using System.Threading;
using System.Runtime.ExceptionServices;
using System.Xml.Linq;
using rtaime.Client;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class TimedTextReviewTests
{
	private static readonly MediaAssetId Asset = new(Identity.Parse("89000000-0000-0000-0000-000000000001"));
	private static readonly MediaSourceId Source = new(Identity.Parse("89000000-0000-0000-0000-000000000002"));

	[Fact]
	public void Caption_preview_requires_confirmed_source_and_never_admits_program_mutations()
	{
		using var review = new TimedTextReviewViewModel();
		Assert.False(review.ProgramOverlayAdmitted);
		Assert.False(review.Import(Encoding.UTF8.GetBytes(Xml), TimedTextProfile.EbuTtDistribution101, review.ContextEpoch));
		Assert.Contains("UNVERIFIED", review.Status);
		Assert.Null(review.ImportedDocument);
	}

	[Fact]
	public void Actual_timeline_caption_preview_is_source_fenced_and_uses_confirmed_not_drag_frame()
	{
		Exception? fault = null;
		var thread = new Thread(() =>
		{
			try { VerifyRealModel(); }
			catch (Exception exception) { fault = exception; }
		});
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Timed Text local review STA did not terminate.");
		if (fault is not null) ExceptionDispatchInfo.Capture(fault).Throw();
	}

	private static void VerifyRealModel()
	{
		var controller = new MediaTimelineController();
		var model = new MediaTimelineViewModel(controller);
		using var review = new TimedTextReviewViewModel();
		try
		{
			var first = Snapshot(50);
			controller.ApplyConfirmedSnapshot(first.Transport!);
			model.ApplyMediaDeckSnapshot(first);
			review.Attach(model);
			var epoch = review.ContextEpoch;
			Assert.True(review.Import(Encoding.UTF8.GetBytes(Xml), TimedTextProfile.EbuTtDistribution101, epoch));
			Assert.Contains("LOCAL CAPTION PREVIEW", review.Status);
			Assert.Equal("Live subtitle", review.PreviewText);
			Assert.False(review.ProgramOverlayAdmitted);
			Assert.DoesNotContain("Program-confirmed", review.Detail, StringComparison.Ordinal);

			review.IsLocalPreviewEnabled = false;
			Assert.Equal("", review.PreviewText);
			review.IsLocalPreviewEnabled = true;
			model.EditCursorMode = true;
			model.SetEditCursor(76);
			Assert.Equal(50, model.ConfirmedFrame);
			Assert.Equal("Live subtitle", review.PreviewText);

			var advanced = Snapshot(99);
			controller.ApplyConfirmedSnapshot(advanced.Transport!);
			model.ApplyMediaDeckSnapshot(advanced);
			Assert.Empty(review.PreviewText);

			var changed = Snapshot(60, new MediaSourceId(Identity.Parse("89000000-0000-0000-0000-000000000003")));
			controller.ApplyConfirmedSnapshot(changed.Transport!);
			model.ApplyMediaDeckSnapshot(changed);
			Assert.Null(review.ImportedDocument);
			Assert.False(review.Import(Encoding.UTF8.GetBytes(Xml), TimedTextProfile.EbuTtDistribution101, epoch));
			Assert.Empty(review.PreviewText);
		}
		finally { model.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
	}

	[Fact]
	public void Timeline_contains_custom_timed_text_import_and_no_program_output_binding()
	{
		var file = Path.Combine(Root(), "src", "Hosts", "rtaime.Operator", "MediaTimelineControl.xaml");
		var xaml = XDocument.Load(file);
		Assert.Contains(xaml.Descendants(), element =>
			element.Name.LocalName == "RtaimeButton" &&
			element.Attributes().Any(attribute => attribute.Name.LocalName == "Content" &&
				attribute.Value == "IMPORT EBU-TT-D"));
		Assert.Contains(xaml.Descendants(), element =>
			element.Name.LocalName == "RtaimeButton" &&
			element.Attributes().Any(attribute => attribute.Name.LocalName == "Content" &&
				attribute.Value == "IMPORT EBU-TT LIVE"));
		Assert.Contains(xaml.Descendants(), element =>
			element.Name.LocalName == "TextBlock" &&
			element.Attributes().Any(attribute => attribute.Value.Contains("TimedTextReview.PreviewText", StringComparison.Ordinal)));
		var code = File.ReadAllText(Path.Combine(Root(), "src", "Hosts", "rtaime.Operator", "MediaTimelineControl.xaml.cs"));
		Assert.Contains("FileOptions.Asynchronous", code, StringComparison.Ordinal);
		Assert.Contains("TimedTextReview.Import(bytes, profile, epoch)", code, StringComparison.Ordinal);
		Assert.DoesNotContain("ApplyProductionCgText", code, StringComparison.Ordinal);
		Assert.DoesNotContain("CutProgramAsync", code, StringComparison.Ordinal);
	}

	private static MediaDeckSnapshot Snapshot(long frame, MediaSourceId? sourceId = null)
	{
		var source = sourceId ?? Source;
		var duration = TimeSpan.FromSeconds(2);
		var position = TimeSpan.FromMilliseconds(frame * 20);
		var transport = new MediaTransportSnapshot(MediaContractVersion.Current, Asset, source,
			MediaTransportState.Paused,
			new MediaTransportPosition(frame, 100, position, duration, duration - position, FrameRate.Fps50),
			null, isOnProgram: false);
		var probe = new LocalMediaProbe(MediaContractVersion.Current, Asset, source, "caption-reference.mp4",
			MediaContainerFormat.Mp4, MediaVideoCodec.H264, MediaAudioCodec.Aac,
			VideoFormat.Hd1080p50Rgba8, AudioFormat.Stereo48kFloat32, duration);
		var markers = new MediaMarkerSnapshot(MediaContractVersion.Current, Asset, 100);
		return new MediaDeckSnapshot(MediaContractVersion.Current, MediaDeckState.Paused,
			source, probe, transport, markers);
	}

	private static string Root()
	{
		for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
			if (File.Exists(Path.Combine(directory.FullName, "rtaime.slnx"))) return directory.FullName;
		throw new DirectoryNotFoundException("Repository root is not mounted.");
	}

	private const string Xml = """
		<tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttp="http://www.w3.org/ns/ttml#parameter"
		    xmlns:tts="http://www.w3.org/ns/ttml#styling" xmlns:ttm="http://www.w3.org/ns/ttml#metadata"
		    xmlns:ebuttm="urn:ebu:tt:metadata" xml:lang="en-GB" ttp:timeBase="media">
		  <head><metadata><ebuttm:conformsToStandard>urn:ebu:tt:distribution:2018-04</ebuttm:conformsToStandard></metadata>
		    <styling><style xml:id="base" tts:fontFamily="Segoe UI" tts:fontSize="32px"
		      tts:color="#FFFFFFFF" tts:backgroundColor="#00000000" tts:textAlign="center"/></styling>
		    <layout><region xml:id="bottom" tts:origin="5% 75%" tts:extent="90% 20%"/></layout>
		  </head>
		  <body><div><p xml:id="cue1" region="bottom" style="base"
		    begin="1s" end="1.5s">Live subtitle</p></div></body>
		</tt>
		""";
}
