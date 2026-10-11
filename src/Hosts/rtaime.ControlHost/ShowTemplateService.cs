// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using rtaime.Control.Contracts;
using rtaime.Core;

namespace rtaime.ControlHost;

public sealed class ShowTemplateService
{
	private readonly Func<ControlHostService?> _controlAccessor;
	private readonly ShowProjectPersistenceStore _projects;
	private readonly ShowControlCoordinator _showControl;
	private readonly Func<ShowTemplateDependency, string, CancellationToken, ValueTask<string?>> _availability;

	public ShowTemplateService(Func<ControlHostService?> controlAccessor, ShowProjectPersistenceStore projects,
		ShowControlCoordinator showControl,
		Func<ShowTemplateDependency, string, CancellationToken, ValueTask<string?>> availability)
	{
		_controlAccessor = controlAccessor;
		_projects = projects;
		_showControl = showControl;
		_availability = availability;
	}

	public async ValueTask<string> CreateAsync(string name, string? cueListId, CancellationToken cancellationToken)
	{
		var control = RequireControl();
		var project = await _projects.LoadAsync(control.Specification, cancellationToken).ConfigureAwait(false);
		ShowControlCueList list;
		if (cueListId is not null)
		{
			var workspace = await _showControl.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
			list = workspace.CueLists.FirstOrDefault(candidate => candidate.CueListId.ToString() == cueListId)
				?? throw new InvalidDataException("Save the selected cue list before creating its template.");
		}
		else
		{
			var actions = new List<ShowControlAction>();
			if (control.State.ActiveSceneId is { } active)
				actions.Add(new(ShowControlActionId.New(), ShowControlActionKind.ActivateScene, sceneId: active.ToString()));
			else
			{
				actions.Add(new(ShowControlActionId.New(), ShowControlActionKind.SetPreview, sourceId: control.State.Routing.ProgramSourceId.ToString()));
				actions.Add(new(ShowControlActionId.New(), ShowControlActionKind.Cut));
			}
			actions.Add(new(ShowControlActionId.New(), ShowControlActionKind.SetPreview, sourceId: control.State.Routing.PreviewSourceId.ToString()));
			var routing = project.AudioRouting ?? DurableAudioRoutingState.FollowVideo;
			actions.Add(new(ShowControlActionId.New(), ShowControlActionKind.SetAudioRouting,
				sourceId: routing.BreakawaySourceId?.ToString(), audioRoutingMode: routing.Mode));
			foreach (var source in project.AudioProduction?.Sources ?? [])
				actions.Add(new(ShowControlActionId.New(), ShowControlActionKind.SetAudioInputState,
					sourceId: source.SourceId.ToString(), audioGain: source.Gain, audioMuted: source.Muted));
			foreach (var output in control.State.OutputRoles.Where(role => role.RoleId != OutputRoleIds.Program))
				actions.Add(new(ShowControlActionId.New(), ShowControlActionKind.RouteOutputRole,
					sourceId: output.SourceId.ToString(), outputRoleId: output.RoleId.ToString()));
			list = new(ShowControlContractVersion.Current, ShowControlCueListId.New(), name,
				[new(ShowControlCueId.New(), "Recall setup", actions)]);
		}
		var dependencies = new Dictionary<(string, string), ShowTemplateDependency>();
		void Add(string kind, string? id, string compatibility = "")
		{
			if (id is null) return;
			var label = kind == "source"
				? control.Specification.Sources.FirstOrDefault(source => source.SourceId.ToString() == id)?.Name ?? id
				: id;
			if (kind == "source")
				compatibility = control.Specification.Sources.FirstOrDefault(source => source.SourceId.ToString() == id)?.ProviderBinding?.CapabilityKind ?? "";
			var dependency = new ShowTemplateDependency(kind, id, label, compatibility);
			if (dependencies.TryGetValue((kind, id), out var prior) && prior.Compatibility != compatibility)
				throw new InvalidDataException("Scenes require conflicting content for the same layer. Prepare compatible graphics before export.");
			dependencies[(kind, id)] = dependency;
		}
		var nodes = JsonNode.Parse(ShowProjectPersistenceStore.SerializeTemplateScenes(project.Scenes))!.AsArray();
		foreach (var node in nodes)
		{
			Add("source", node!["previewSourceId"]!.GetValue<string>());
			Add("source", node["programSourceId"]!.GetValue<string>());
			if (node["compositingState"]?["layers"] is JsonArray layers)
				foreach (var layer in layers)
				{
					var digest = Digest(layer!["contentIdentity"]!.GetValue<string>());
					layer["contentIdentity"] = digest;
					Add("layer", layer["layerId"]!.GetValue<string>(), digest);
				}
		}
		foreach (var action in list.Cues.SelectMany(cue => cue.Actions))
		{
			Add("source", action.SourceId);
			Add("media", action.MediaAssetId);
			Add("mediaCue", action.MediaCuePointId);
			Add("output", action.OutputRoleId,
				control.State.OutputRoles.FirstOrDefault(role => role.RoleId.ToString() == action.OutputRoleId)?.Kind.ToString() ?? "");
			if (action.LayerId is { } layerId && !dependencies.ContainsKey(("layer", layerId)))
				Add("layer", layerId);
		}
		var content = new ShowTemplateContent(nodes.ToJsonString(ShowTemplateSerializer.Options),
			ShowControlCanonicalSerializer.Serialize(list), dependencies.Values.OrderBy(d => d.Kind, StringComparer.Ordinal)
				.ThenBy(d => d.ReferenceId, StringComparer.Ordinal).ToArray());
		return ShowTemplateSerializer.Serialize(ShowTemplateSerializer.Create(name, content));
	}

	public async ValueTask<ShowTemplatePreflight> PreflightAsync(ShowTemplateRequest request, CancellationToken cancellationToken)
	{
		var control = RequireControl();
		var project = await _projects.LoadAsync(control.Specification, cancellationToken).ConfigureAwait(false);
		var workspace = await _showControl.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
		var version = await _showControl.GetStorageVersionAsync(cancellationToken).ConfigureAwait(false);
		var conflicts = new List<string>();
		(ShowTemplateDocument Document, IReadOnlyList<ProductionSceneSpecification> Scenes, ShowControlCueList CueList) prepared;
		try { prepared = Prepare(request, control); }
		catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or
			NotSupportedException or FormatException or JsonException)
		{
			return new(false, project.StorageVersion, version, [], [exception.Message]);
		}
		if (request.ExpectedProjectVersion is { } expected && expected != project.StorageVersion ||
			request.ExpectedWorkspaceVersion is { } expectedWorkspace && expectedWorkspace != version)
			conflicts.Add("STALE: Show project or cue workspace changed. Preview again.");
		if (workspace.Execution.State is ShowControlExecutionState.Armed or ShowControlExecutionState.Executing or
			ShowControlExecutionState.Waiting or ShowControlExecutionState.RecoveryRequired)
			conflicts.Add("ACTIVE: Disarm or acknowledge Show Control before import.");
		if (workspace.CueLists.Count >= 32 || workspace.CueLists.Any(list =>
			string.Equals(list.Name, request.InstanceName.Trim(), StringComparison.OrdinalIgnoreCase)))
			conflicts.Add("NAME: Choose a new show name; existing shows are never overwritten. The limit is 32 cue lists.");
		if (project.Scenes.Count + prepared.Scenes.Count > 128 || prepared.Scenes.Any(scene =>
			project.Scenes.Any(existing => string.Equals(existing.Name, scene.Name, StringComparison.OrdinalIgnoreCase))))
			conflicts.Add("SCENES: Duplicate scene name or the 128-scene limit would be exceeded.");
		foreach (var dependency in prepared.Document.Content.Dependencies)
		{
			var target = Resolve(request, dependency.Kind, dependency.ReferenceId);
			var failure = await _availability(dependency, target, cancellationToken).ConfigureAwait(false);
			if (failure is not null) conflicts.Add($"{dependency.Kind.ToUpperInvariant()} · {dependency.Name}: {failure}");
		}
		return new(conflicts.Count == 0, project.StorageVersion, version,
			prepared.Scenes.Select(scene => $"ADD SCENE · {scene.Name} · Preview {scene.Routing.PreviewSourceId} / Program {scene.Routing.ProgramSourceId}")
				.Append($"ADD CUE LIST · {prepared.CueList.Name} · {prepared.CueList.Cues.Count} cues")
				.Append("Save authored definitions only. Program, selected cue list and execution remain unchanged.").ToArray(), conflicts.ToArray());
	}

	public async ValueTask<ShowControlWorkspaceSnapshot> InstantiateAsync(ShowTemplateRequest request, CancellationToken cancellationToken)
	{
		if (request.ExpectedProjectVersion is null || request.ExpectedWorkspaceVersion is null)
			throw new InvalidOperationException("Preview the template before committing.");
		var preflight = await PreflightAsync(request, cancellationToken).ConfigureAwait(false);
		if (!preflight.CanCommit) throw new InvalidOperationException(string.Join("\n", preflight.Conflicts));
		var prepared = Prepare(request, RequireControl());
		return await _showControl.ImportTemplateAsync(prepared.CueList, prepared.Scenes,
			preflight.ProjectVersion, preflight.WorkspaceVersion, cancellationToken).ConfigureAwait(false);
	}

	private static (ShowTemplateDocument Document, IReadOnlyList<ProductionSceneSpecification> Scenes, ShowControlCueList CueList)
		Prepare(ShowTemplateRequest request, ControlHostService control)
	{
		if (string.IsNullOrWhiteSpace(request.InstanceName) || request.InstanceName.Trim().Length > 100)
			throw new InvalidDataException("Choose an instance name of 1-100 characters.");
		var document = ShowTemplateSerializer.Deserialize(request.TemplateJson);
		if (request.Bindings is null || request.Bindings.Length != document.Content.Dependencies.Length ||
			request.Bindings.Any(binding => binding is null || string.IsNullOrWhiteSpace(binding.TargetId) || binding.TargetId.Length > 512) ||
			request.Bindings.Select(binding => (binding.Kind, binding.ReferenceId)).Distinct().Count() != request.Bindings.Length ||
			request.Bindings.Any(binding => !document.Content.Dependencies.Any(d => d.Kind == binding.Kind && d.ReferenceId == binding.ReferenceId)))
			throw new InvalidDataException("Provide exactly one explicit target for every declared dependency.");
		var original = ShowProjectPersistenceStore.DeserializeTemplateScenes(document.Content.ScenesJson);
		var sceneIds = original.ToDictionary(scene => scene.SceneId.ToString(), _ => SceneId.New());
		string Map(string kind, string id) => kind == "scene"
			? sceneIds.TryGetValue(id, out var sceneId) ? sceneId.ToString() : throw new InvalidDataException("Cue references an undeclared template scene.")
			: Resolve(request, kind, id);
		var nodes = JsonNode.Parse(document.Content.ScenesJson)!.AsArray();
		foreach (var node in nodes)
		{
			node!["sceneId"] = Map("scene", node["sceneId"]!.GetValue<string>());
			node["name"] = request.InstanceName.Trim() + " / " + node["name"]!.GetValue<string>();
			node["previewSourceId"] = Map("source", node["previewSourceId"]!.GetValue<string>());
			node["programSourceId"] = Map("source", node["programSourceId"]!.GetValue<string>());
			if (node["compositingState"]?["layers"] is JsonArray layers)
				foreach (var layer in layers)
				{
					var id = layer!["layerId"]!.GetValue<string>();
					var target = Map("layer", id);
					if (target != id) throw new InvalidDataException("Compositing layer kinds cannot be rebound to a different layer.");
					var current = control.State.CompositingState?.Layers.FirstOrDefault(candidate => candidate.LayerId == target);
					if (current is null || Digest(current.ContentIdentity) != layer["contentIdentity"]!.GetValue<string>())
						throw new InvalidDataException($"Layer '{id}' requires matching locally prepared graphics. Graphics payloads are not packaged.");
					layer["contentIdentity"] = current.ContentIdentity;
				}
		}
		var scenes = ShowProjectPersistenceStore.DeserializeTemplateScenes(nodes.ToJsonString(ShowTemplateSerializer.Options));
		var knownSources = control.Specification.Sources.Select(source => source.SourceId).ToHashSet();
		if (scenes.Any(scene => !knownSources.Contains(scene.Routing.PreviewSourceId) || !knownSources.Contains(scene.Routing.ProgramSourceId)))
			throw new InvalidDataException("SOURCE: Bind every scene to an adopted local production source.");
		var cueList = ShowTemplateSerializer.InstantiateCueList(document, request.InstanceName.Trim(), Map);
		foreach (var action in cueList.Cues.SelectMany(cue => cue.Actions))
			GovernedProductionActionValidator.ValidateReferenceShape(control, action, scenes);
		return (document, scenes, cueList);
	}

	private static string Resolve(ShowTemplateRequest request, string kind, string id) =>
		request.Bindings.FirstOrDefault(binding => binding.Kind == kind && binding.ReferenceId == id)?.TargetId
			?? throw new InvalidDataException($"Undeclared or unbound dependency '{kind}/{id}'.");
	public static string Digest(string value) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
	private ControlHostService RequireControl() => _controlAccessor() is { HasAuthoritativeState: true } control
		? control : throw new InvalidOperationException("ControlHost authority is unavailable.");
}
