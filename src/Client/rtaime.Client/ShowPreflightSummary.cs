// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;
using rtaime.Media.Contracts;

namespace rtaime.Client;

public enum ShowPreflightState { Pass, Warning, Fail, Unknown, NotApplicable }
public sealed record ShowPreflightCheck(string Id, string Label, ShowPreflightState State, string Detail, string Workspace);

// Advisory projection only: this never grants production authority or starts a show.
public static class ShowPreflightSummary
{
	public static IReadOnlyList<ShowPreflightCheck> Build(OperatorStatusSnapshot? snapshot,
		bool fresh, MediaAssetCatalogSnapshot? catalogue, DateTimeOffset? catalogueObservedAtUtc,
		IReadOnlyList<SubsystemHealthSnapshot> health, DateTimeOffset now,
        IReadOnlyList<MediaAssetPreflightObservation>? preparation = null)
	{
		var checks = new List<ShowPreflightCheck>();
		void Add(string id, string label, ShowPreflightState state, string detail, string workspace) =>
			checks.Add(new(id, label, state, detail, workspace));
		if (!fresh || snapshot is null)
		{
			foreach (var (id, label, workspace) in new[] {
				("assets", "Show assets", "MEDIA"), ("asset-playback", "Asset playback", "MEDIA"), ("cues", "Cue references", "EDIT"),
				("outputs", "Output roles", "OUTPUTS"), ("program-output", "Program output", "OUTPUTS"),
                ("aux-output", "AUX outputs", "OUTPUTS"), ("recording", "Recording target", "OUTPUTS"), ("audio", "Program audio", "LIVE"),
				("ai", "AI readiness", "SETTINGS"), ("durable", "Saved show", "HEALTH") })
				Add(id, label, ShowPreflightState.Unknown, "Fresh authoritative state is unavailable. Synchronize before production.", workspace);
			return checks;
		}
		var selected = snapshot.ShowControl.CueLists.FirstOrDefault(list => list.CueListId == snapshot.ShowControl.SelectedCueListId);
		var actions = selected?.Cues.SelectMany(cue => cue.Actions).ToArray() ?? [];
		var assetIds = actions.Where(action => action.MediaAssetId is not null).Select(action => action.MediaAssetId!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		var catalogueFresh = catalogue is not null && catalogueObservedAtUtc is { } observed && now >= observed && now - observed <= TimeSpan.FromSeconds(10);
		var assets = catalogue?.Assets.ToDictionary(asset => asset.AssetId.ToString(), StringComparer.OrdinalIgnoreCase);
		Add("assets", "Show assets",
			selected is null ? ShowPreflightState.Unknown : assetIds.Length == 0 ? ShowPreflightState.NotApplicable :
			!catalogueFresh ? ShowPreflightState.Unknown :
			assetIds.Any(id => !assets!.TryGetValue(id, out var asset) || asset.Availability != MediaAssetAvailability.Online) ? ShowPreflightState.Fail : ShowPreflightState.Pass,
			selected is null ? "No authoritative cue list is selected." : assetIds.Length == 0 ? "Selected show has no media asset references." :
			!catalogueFresh ? "Refresh availability in Media Library; evidence is missing or older than 10 seconds." :
			$"{assetIds.Length} distinct media references checked against catalogue revision {catalogue!.Revision}. Availability is observed, not guaranteed at execution.", "MEDIA");

        var fileEvidence = assetIds.Select(id => preparation?.LastOrDefault(value => string.Equals(value.AssetId, id, StringComparison.OrdinalIgnoreCase))).ToArray();
        var playbackState = selected is null ? ShowPreflightState.Unknown : assetIds.Length == 0 ? ShowPreflightState.NotApplicable :
            !catalogueFresh || preparation?.Select(value => value.RuntimeEpoch).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1 ||
            preparation?.Select(value => value.TargetFormat).Distinct(StringComparer.Ordinal).Count() > 1 || fileEvidence.Any(value => value is null || value.Running || value.Cancelled ||
                now < value.ObservedAtUtc || now - value.ObservedAtUtc > TimeSpan.FromSeconds(10) ||
                !assets!.TryGetValue(value.AssetId, out var asset) ||
                asset.SourceLocation != value.SourceLocation || asset.FingerprintSha256 != value.FingerprintSha256) ? ShowPreflightState.Unknown :
            fileEvidence.Any(value => value!.State == MediaAssetReadinessState.Unavailable) ? ShowPreflightState.Fail :
            fileEvidence.All(value => value!.RepresentativeVerificationPassed) ? ShowPreflightState.Warning : ShowPreflightState.Unknown;
        Add("asset-playback", "Asset playback", playbackState,
            playbackState == ShowPreflightState.NotApplicable ? "Selected show has no media asset references." :
            playbackState == ShowPreflightState.Warning ? "Representative checks passed conditionally. Decoders were released; whole-file, sustained hardware and future deck capacity remain UNVERIFIED." :
            playbackState == ShowPreflightState.Fail ? "A selected asset failed representative verification. Open Media Library for the observed reason and retry deliberately." :
            "Playback evidence is missing, invalidated or expired. Use Prepare selected assets in Media Library. Availability alone is not decoder/preroll readiness.", "MEDIA");

		var sourceIds = snapshot.Sources.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var sceneIds = snapshot.Scenes.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var layerIds = snapshot.CompositingLayers.Select(item => item.LayerId).ToHashSet(StringComparer.Ordinal);
		var outputIds = snapshot.OutputRoles.Select(item => item.RoleId).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var missingReference = actions.Any(action =>
			action.SourceId is { } source && !sourceIds.Contains(source) ||
			action.SceneId is { } scene && !sceneIds.Contains(scene) ||
			action.LayerId is { } layer && !layerIds.Contains(layer) ||
			action.OutputRoleId is { } role && !outputIds.Contains(role));
		var markerUnknown = false;
		foreach (var action in actions.Where(item => item.MediaCuePointId is not null))
		{
			var markers = snapshot.MediaDeck.Markers;
			if (markers is null || !string.Equals(markers.AssetId.ToString(), action.MediaAssetId, StringComparison.OrdinalIgnoreCase)) markerUnknown = true;
			else if (!markers.CuePoints.Any(cue => string.Equals(cue.Id.ToString(), action.MediaCuePointId, StringComparison.OrdinalIgnoreCase))) missingReference = true;
		}
		Add("cues", "Cue references", selected is null ? ShowPreflightState.Unknown :
			missingReference ? ShowPreflightState.Fail : markerUnknown ? ShowPreflightState.Unknown : ShowPreflightState.Pass,
			selected is null ? "No authoritative cue list is selected." : missingReference ? "At least one source, scene, layer, output or loaded media cue reference is missing." :
			markerUnknown ? "Cue markers for an unloaded asset are unavailable. ControlHost validates them at execution." :
			$"{selected.Cues.Count} cues / {actions.Length} actions inspected. Runtime conditions are validated by ControlHost at ARM/GO.", "EDIT");

		var outputs = snapshot.OutputRoles;
		Add("outputs", "Output roles", outputs.Count == 0 ? ShowPreflightState.Unknown :
			outputs.Any(role => role.HealthState == "FAIL") ? ShowPreflightState.Fail :
			outputs.Any(role => role.HealthState != "PASS" || !role.AuthoritativeActive) ? ShowPreflightState.Unknown : ShowPreflightState.Pass,
			outputs.Count == 0 ? "No authoritative output role evidence is published." :
			$"{outputs.Count} roles observed. An inactive or unqualified role is not proof of available physical output.", "OUTPUTS");
		foreach (var (id, label, kind) in new[] { ("program-output", "Program output", "PROGRAM"), ("aux-output", "AUX outputs", "AUX") })
		{
			var roles = outputs.Where(role => role.RoleKind == kind).ToArray();
			Add(id, label, roles.Length == 0 ? ShowPreflightState.Unknown :
				roles.Any(role => role.HealthState == "FAIL" || role.Error is not null) ? ShowPreflightState.Fail :
				roles.All(role => role.HealthState == "PASS" && role.AuthoritativeActive) ? ShowPreflightState.Pass : ShowPreflightState.Unknown,
				roles.Length == 0 ? "No current role evidence is published; availability is UNVERIFIED." :
				$"{roles.Length} {kind} roles checked against the authoritative snapshot. Physical transport qualification remains separate.", "OUTPUTS");
		}
		var recording = snapshot.Recording;
		var recordingFailed = recording.Failure is not null || recording.WriterFailures > 0 || recording.State is "FAILED" or "ERROR";
		var recordingActive = recording.State == "RECORDING";
		Add("recording", "Recording target", recordingFailed ? ShowPreflightState.Fail :
			recordingActive && recording.Written > 0 && recording.Destination is not null && recording.ActiveProfileId is not null &&
			recording.ActiveProviderId is not null ? ShowPreflightState.Pass : ShowPreflightState.Unknown,
			recordingFailed ? "Runtime reports a recording failure. Review the recording workflow; Program remains independent." :
			recordingActive && recording.Written > 0 && recording.Destination is not null && recording.ActiveProfileId is not null && recording.ActiveProviderId is not null ?
			"Runtime confirms samples written to its active target/profile/provider. Future capacity is not guaranteed." :
			"Target writability and capacity are UNVERIFIED. An idle profile or a locally entered path is not a target probe; review existing Recording controls.", "OUTPUTS");
		var audio = health.FirstOrDefault(item => item.Id == "audio");
		var audioFresh = audio is not null && audio.LastObservedAt != default && now >= audio.LastObservedAt && now - audio.LastObservedAt <= TimeSpan.FromSeconds(10);
		Add("audio", "Program audio", !audioFresh ? ShowPreflightState.Unknown : audio!.State switch {
			SubsystemHealthState.Healthy => ShowPreflightState.Pass, SubsystemHealthState.Failed => ShowPreflightState.Fail,
			SubsystemHealthState.Warning or SubsystemHealthState.Degraded => ShowPreflightState.Warning, _ => ShowPreflightState.Unknown },
			audioFresh ? audio!.Detail : "Current Health Center audio evidence is unavailable.", "LIVE");
		var ai = snapshot.AIShowcase;
		Add("ai", "AI readiness", ai.Failure is not null ? ShowPreflightState.Fail :
			ai.Status is "UNAVAILABLE" or "UNVERIFIED" or "UNKNOWN" ? ShowPreflightState.Unknown : !ai.Enabled ? ShowPreflightState.NotApplicable :
			ai.UpdatedAtUtc is not { } updated || now < updated || now - updated > TimeSpan.FromSeconds(10) ? ShowPreflightState.Unknown :
			ai.EffectVisible && ai.SourceSequence.HasValue && ai.AppliedSequence.HasValue && ai.SourceSequence == ai.AppliedSequence &&
            !string.IsNullOrWhiteSpace(ai.Provider) && ai.Provider is not ("UNVERIFIED" or "UNAVAILABLE" or "UNKNOWN") ? ShowPreflightState.Pass : ShowPreflightState.Unknown,
			ai.Failure is not null ? "The effect reports a failure; inspect governed AI diagnostics." : ai.Status is "UNAVAILABLE" or "UNVERIFIED" or "UNKNOWN" ? "Current AI evidence is unavailable." : !ai.Enabled ? "AI is disabled; no effect is required for clean Program." :
			"Enabled effect requires current synchronized application evidence. Production provider qualification is separate.", "SETTINGS");
		Add("durable", "Saved show", snapshot.ShowProject.State is "SAVED" or "RESTORED" ? ShowPreflightState.Pass :
			snapshot.ShowProject.State == "RECOVERY_REQUIRED" ? ShowPreflightState.Warning : ShowPreflightState.Unknown,
			$"Durable project: {snapshot.ShowProject.State}. Review saved state in Health; this check does not save or restore anything.", "HEALTH");
		return checks;
	}
}
