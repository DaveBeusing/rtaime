// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control.Contracts;
using rtaime.Core;
using Xunit;

namespace rtaime.Tests.Contracts;

public sealed class ShowTemplateContractTests
{
	[Fact]
	public void Portable_roundtrip_uses_fresh_cue_and_action_identities()
	{
		var list = List(new ShowControlAction(ShowControlActionId.New(), ShowControlActionKind.WaitFrames, waitFrames: 3));
		var document = ShowTemplateSerializer.Create("Opening", new("[]", ShowControlCanonicalSerializer.Serialize(list), []));
		var restored = ShowTemplateSerializer.Deserialize(ShowTemplateSerializer.Serialize(document));
		var instance = ShowTemplateSerializer.InstantiateCueList(restored, "New opening", (_, value) => value);
		Assert.NotEqual(list.CueListId, instance.CueListId);
		Assert.NotEqual(list.Cues[0].CueId, instance.Cues[0].CueId);
		Assert.NotEqual(list.Cues[0].Actions[0].ActionId, instance.Cues[0].Actions[0].ActionId);
		Assert.Equal(3u, instance.Cues[0].Actions[0].WaitFrames);
	}

	[Fact]
	public void Corruption_newer_format_unknown_fields_and_duplicate_properties_fail_closed()
	{
		var document = ShowTemplateSerializer.Create("Opening", new("[]",
			ShowControlCanonicalSerializer.Serialize(List(new(ShowControlActionId.New(), ShowControlActionKind.Cut))), []));
		var json = ShowTemplateSerializer.Serialize(document);
		Assert.Throws<InvalidDataException>(() => ShowTemplateSerializer.Deserialize(json.Replace("Opening", "Changed")));
		Assert.Throws<NotSupportedException>(() => ShowTemplateSerializer.Deserialize(json.Replace(".v1", ".v2")));
		Assert.Throws<System.Text.Json.JsonException>(() => ShowTemplateSerializer.Deserialize(json.Insert(1, "\"secret\":\"private\",")));
		Assert.Throws<InvalidDataException>(() => ShowTemplateSerializer.Deserialize(json.Insert(1, "\"format\":\"duplicate\",")));
		Assert.Throws<InvalidDataException>(() => ShowTemplateSerializer.Deserialize(new string(' ', ShowTemplateSerializer.MaximumBytes + 1)));
	}

	[Fact]
	public void Recording_destinations_are_rejected_instead_of_exporting_private_paths()
	{
		var action = new ShowControlAction(ShowControlActionId.New(), ShowControlActionKind.StartRecording,
			recordingDestinationDirectory: "C:\\private", recordingFileName: "show.mp4");
		var document = ShowTemplateSerializer.Create("Private", new("[]", ShowControlCanonicalSerializer.Serialize(List(action)), []));
		Assert.Throws<InvalidDataException>(() => ShowTemplateSerializer.Serialize(document));
	}

	[Fact]
	public void Bindings_preserve_action_parameters_and_replace_declared_source_references()
	{
		var original = Identity.New().ToString();
		var target = Identity.New().ToString();
		var list = List(new(ShowControlActionId.New(), ShowControlActionKind.SetAudioInputState,
			sourceId: original, audioGain: 0.5, audioMuted: true));
		var document = ShowTemplateSerializer.Create("Audio", new("[]", ShowControlCanonicalSerializer.Serialize(list),
			[new("source", original, "Camera", "")]));
		var instance = ShowTemplateSerializer.InstantiateCueList(document, "Bound audio",
			(kind, value) => kind == "source" && value == original ? target : throw new InvalidOperationException());
		Assert.Equal(target, instance.Cues[0].Actions[0].SourceId);
		Assert.Equal(0.5, instance.Cues[0].Actions[0].AudioGain);
		Assert.True(instance.Cues[0].Actions[0].AudioMuted);
	}

	private static ShowControlCueList List(ShowControlAction action) => new(
		ShowControlContractVersion.Current, ShowControlCueListId.New(), "Opening",
		[new(ShowControlCueId.New(), "Cue", [action])]);
}
