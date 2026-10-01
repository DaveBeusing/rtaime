// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control.Contracts;
using rtaime.Core;

namespace rtaime.ControlHost;

internal static class GovernedProductionActionValidator
{
	public static void ValidateReferenceShape(ControlHostService control, ShowControlAction action)
	{
		ArgumentNullException.ThrowIfNull(control);
		ArgumentNullException.ThrowIfNull(action);

		switch (action.Kind)
		{
			case ShowControlActionKind.ActivateScene:
			{
				var sceneId = new SceneId(Identity.Parse(action.SceneId!));
				if (!control.Specification.Scenes.Any(scene => scene.SceneId == sceneId))
					throw new InvalidDataException($"Governed action references unknown Scene '{sceneId}'.");
				break;
			}
			case ShowControlActionKind.SetPreview:
			{
				ValidateSource(control, action.SourceId!, "preview");
				break;
			}
			case ShowControlActionKind.JumpMediaCue:
			case ShowControlActionKind.MediaPlay:
			case ShowControlActionKind.MediaPause:
			case ShowControlActionKind.MediaStop:
				_ = Identity.Parse(action.MediaAssetId!);
				if (action.Kind == ShowControlActionKind.JumpMediaCue)
					_ = Identity.Parse(action.MediaCuePointId!);
				break;
			case ShowControlActionKind.MediaOpen:
				_ = Identity.Parse(action.MediaAssetId!);
				ValidateSource(control, action.SourceId!, "media-open");
				break;
			case ShowControlActionKind.SetAudioRouting:
				if (action.SourceId is { } audioSource)
					ValidateSource(control, audioSource, "audio-routing");
				break;
			case ShowControlActionKind.RouteOutputRole:
			{
				var roleId = new OutputRoleId(action.OutputRoleId!);
				if (roleId == OutputRoleIds.Program)
					throw new InvalidDataException("Program output routing must use governed CUT/DISSOLVE/Scene semantics and cannot be assigned by a generic output-role action.");
				if (!control.State.OutputRoles.Any(role => role.RoleId == roleId))
					throw new InvalidDataException($"Governed action references unknown output role '{roleId}'.");
				ValidateSource(control, action.SourceId!, "output-role");
				break;
			}
		}
	}

	private static void ValidateSource(ControlHostService control, string source, string context)
	{
		var sourceId = new ProductionSourceId(Identity.Parse(source));
		if (!control.Specification.Sources.Any(candidate => candidate.SourceId == sourceId))
			throw new InvalidDataException($"Governed {context} action references unknown production source '{sourceId}'.");
	}
}
