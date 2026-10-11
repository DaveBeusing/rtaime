// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using rtaime.Core;

namespace rtaime.Control.Contracts;

// Templates contain authored definitions only. Execution and workspace presentation are never portable.
public sealed record ShowTemplateDependency(string Kind, string ReferenceId, string Name, string Compatibility);
public sealed record ShowTemplateBinding(string Kind, string ReferenceId, string TargetId);
public sealed record ShowTemplateContent(string ScenesJson, string CueListJson, ShowTemplateDependency[] Dependencies);
public sealed record ShowTemplateDocument(string Format, string TemplateId, string Name, string ControlVersion,
	ShowTemplateContent Content, string ChecksumSha256);
public sealed record ShowTemplateRequest(string TemplateJson, string InstanceName, ShowTemplateBinding[] Bindings,
	ulong? ExpectedProjectVersion = null, ulong? ExpectedWorkspaceVersion = null);
public sealed record ShowTemplatePreflight(bool CanCommit, ulong ProjectVersion, ulong WorkspaceVersion,
	string[] Changes, string[] Conflicts);

public static class ShowTemplateSerializer
{
	public const string Format = "rtaime.show-template.v1";
	public const int MaximumBytes = 256 * 1024;
	public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
	{
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		MaxDepth = 32
	};

	public static ShowTemplateDocument Create(string name, ShowTemplateContent content)
	{
		var document = new ShowTemplateDocument(Format, Identity.New().ToString(), name.Trim(),
			ControlContractVersion.Current.ToString(), content, "");
		return document with { ChecksumSha256 = Checksum(document) };
	}

	public static string Serialize(ShowTemplateDocument document)
	{
		var json = JsonSerializer.Serialize(document, Options);
		_ = Deserialize(json);
		return json;
	}

	public static ShowTemplateDocument Deserialize(string json)
	{
		if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaximumBytes)
			throw new InvalidDataException("Template is empty or exceeds the 256 KiB portable definition limit.");
		using (var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 }))
			RejectDuplicateProperties(parsed.RootElement);
		var document = JsonSerializer.Deserialize<ShowTemplateDocument>(json, Options)
			?? throw new InvalidDataException("Template document is required.");
		if (document.Format != Format)
			throw new NotSupportedException($"Unsupported template format '{document.Format}'. No automatic migration is available.");
		ControlContractVersion.EnsureSupported(CompatibilityVersion.Parse(document.ControlVersion));
		if (Identity.Parse(document.TemplateId).IsEmpty || string.IsNullOrWhiteSpace(document.Name) || document.Name.Length > 100)
			throw new InvalidDataException("Template requires an identity and a name of 1-100 characters.");
		if (document.Content is null || document.Content.Dependencies is null ||
			document.Content.Dependencies.Length > 512 || string.IsNullOrWhiteSpace(document.Content.ScenesJson))
			throw new InvalidDataException("Template content or dependency declarations are invalid.");
		var cueList = ShowControlCanonicalSerializer.Deserialize(document.Content.CueListJson);
		if (ShowControlCanonicalSerializer.Serialize(cueList) != document.Content.CueListJson)
			throw new InvalidDataException("Template cue definitions must use the supported canonical format.");
		if (cueList.Cues.SelectMany(cue => cue.Actions).Any(action => action.Kind == ShowControlActionKind.StartRecording))
			throw new InvalidDataException("Portable templates cannot contain recording destinations. Author recording locally after import.");
		if (document.Content.Dependencies.Any(d => d is null ||
			d.Kind is not ("source" or "output" or "media" or "mediaCue" or "layer") ||
			string.IsNullOrWhiteSpace(d.ReferenceId) || d.ReferenceId.Length > 512 ||
			string.IsNullOrWhiteSpace(d.Name) || d.Name.Length > 200 || d.Compatibility is null || d.Compatibility.Length > 512) ||
			document.Content.Dependencies.Select(d => (d.Kind, d.ReferenceId)).Distinct().Count() != document.Content.Dependencies.Length)
			throw new InvalidDataException("Template dependencies must have unique, supported bounded references.");
		if (!string.Equals(document.ChecksumSha256, Checksum(document), StringComparison.Ordinal))
			throw new InvalidDataException("Template checksum does not match its authored content.");
		return document;
	}

	public static ShowControlCueList InstantiateCueList(ShowTemplateDocument document, string name,
		Func<string, string, string> resolve)
	{
		var source = ShowControlCanonicalSerializer.Deserialize(document.Content.CueListJson);
		return new ShowControlCueList(ShowControlContractVersion.Current, ShowControlCueListId.New(), name,
			source.Cues.Select(cue => new ShowControlCue(ShowControlCueId.New(), cue.Name,
				cue.Actions.Select(a => new ShowControlAction(ShowControlActionId.New(), a.Kind,
					a.SceneId is null ? null : resolve("scene", a.SceneId),
					a.SourceId is null ? null : resolve("source", a.SourceId), a.DurationFrames,
					a.MediaAssetId is null ? null : resolve("media", a.MediaAssetId),
					a.MediaCuePointId is null ? null : resolve("mediaCue", a.MediaCuePointId),
					a.LayerId is null ? null : resolve("layer", a.LayerId), a.Visible,
					waitFrames: a.WaitFrames, audioRoutingMode: a.AudioRoutingMode,
					outputRoleId: a.OutputRoleId is null ? null : resolve("output", a.OutputRoleId),
					audioGain: a.AudioGain, audioMuted: a.AudioMuted)).ToArray())).ToArray());
	}

	private static string Checksum(ShowTemplateDocument document) =>
		Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
			document with { ChecksumSha256 = "" }, Options))));

	private static void RejectDuplicateProperties(JsonElement element)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			var names = new HashSet<string>(StringComparer.Ordinal);
			foreach (var property in element.EnumerateObject())
			{
				if (!names.Add(property.Name)) throw new InvalidDataException("Template contains duplicate JSON properties.");
				RejectDuplicateProperties(property.Value);
			}
		}
		else if (element.ValueKind == JsonValueKind.Array)
			foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
	}
}
