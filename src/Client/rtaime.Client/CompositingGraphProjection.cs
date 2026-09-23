// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Client;

public enum CompositingGraphNodeKind
{
	Source,
	Layer,
	Routing,
	Transform,
	Composite,
	Output,
	Recorder
}

public enum CompositingGraphHealth
{
	Normal,
	Degraded,
	Error
}

public enum CompositingGraphPortDirection
{
	Input,
	Output
}

public sealed record CompositingGraphPortProjection(
	string Id,
	string Label,
	CompositingGraphPortDirection Direction);

public sealed record CompositingGraphNodeProjection(
	string Id,
	CompositingGraphNodeKind Kind,
	string Title,
	string Detail,
	string Status,
	CompositingGraphHealth Health,
	IReadOnlyList<CompositingGraphPortProjection> Ports,
	bool CanRewire = false);

public sealed record CompositingGraphConnectionProjection(
	string Id,
	string FromNodeId,
	string FromPortId,
	string ToNodeId,
	string ToPortId,
	bool IsActive = true);

public sealed record CompositingGraphSourceProjection(
	string Id,
	string Name,
	string Type,
	string Format,
	string Health,
	bool IsPreview,
	bool IsProgram);

public sealed record CompositingGraphProjectionInput(
	IReadOnlyList<CompositingGraphSourceProjection> Sources,
	string PreviewSourceId,
	string ProgramSourceId,
	string RuntimeStatus,
	string RuntimeHealth,
	string GpuProviderHealth,
	string CommitStatus,
	string TransitionStatus,
	string CurrentFormat,
	string VisualLayerStatus,
	string GraphicsAssetName,
	string GraphicsDimensions,
	string GraphicsState,
	bool GraphicsVisible,
	double GraphicsPositionX,
	double GraphicsPositionY,
	double GraphicsScale,
	string RecordingStatus,
	string? RecordingError,
	IReadOnlyList<OperatorCompositingLayerDescriptor>? CompositingLayers = null);

public sealed record CompositingGraphProjection(
	IReadOnlyList<CompositingGraphNodeProjection> Nodes,
	IReadOnlyList<CompositingGraphConnectionProjection> Connections);

public static class CompositingGraphProjector
{
	private static readonly CompositingGraphPortProjection SourceVideoOut = new(
		"video",
		"Video",
		CompositingGraphPortDirection.Output);

	public static CompositingGraphProjection Project(CompositingGraphProjectionInput input)
	{
		ArgumentNullException.ThrowIfNull(input);
		ArgumentNullException.ThrowIfNull(input.Sources);

		var nodes = new List<CompositingGraphNodeProjection>(input.Sources.Count + 7);
		var connections = new List<CompositingGraphConnectionProjection>(input.Sources.Count + 7);

		foreach (var source in input.Sources
			.Where(source => source is not null)
			.OrderBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(source => source.Id, StringComparer.Ordinal))
		{
			var tally = source.IsProgram && source.IsPreview
				? "PGM + PVW"
				: source.IsProgram
					? "PGM"
					: source.IsPreview
						? "PVW"
						: source.Health;
			nodes.Add(new CompositingGraphNodeProjection(
				$"source:{source.Id}",
				CompositingGraphNodeKind.Source,
				source.Name,
				$"{source.Type} · {source.Format}",
				tally,
				EvaluateHealth(source.Health),
				[SourceVideoOut]));

			connections.Add(new CompositingGraphConnectionProjection(
				$"source:{source.Id}->routing",
				$"source:{source.Id}",
				"video",
				"routing",
				"sources",
				source.IsProgram || source.IsPreview));
		}

		nodes.Add(new CompositingGraphNodeProjection(
			"routing",
			CompositingGraphNodeKind.Routing,
			"Preview / Program Routing",
			$"PVW {ValueOrDash(input.PreviewSourceId)} · PGM {ValueOrDash(input.ProgramSourceId)}",
			ValueOrDash(input.TransitionStatus),
			EvaluateHealth(input.RuntimeStatus, input.RuntimeHealth),
			[
				new("sources", "Sources", CompositingGraphPortDirection.Input),
				new("preview", "Preview", CompositingGraphPortDirection.Output),
				new("program", "Program", CompositingGraphPortDirection.Output)
			]));

		nodes.Add(new CompositingGraphNodeProjection(
			"preview-output",
			CompositingGraphNodeKind.Output,
			"Preview Monitor",
			$"Source {ValueOrDash(input.PreviewSourceId)}",
			ValueOrDash(input.RuntimeStatus),
			EvaluateHealth(input.RuntimeStatus),
			[new("video", "Video", CompositingGraphPortDirection.Input)]));
		connections.Add(new CompositingGraphConnectionProjection(
			"routing->preview",
			"routing",
			"preview",
			"preview-output",
			"video"));

		var compositingLayers = input.CompositingLayers ?? Array.Empty<OperatorCompositingLayerDescriptor>();
		if (compositingLayers.Count == 0)
		{
			var graphicsTitle = IsMeaningful(input.GraphicsAssetName)
				? input.GraphicsAssetName.Trim()
				: "Graphics Layer";
			nodes.Add(new CompositingGraphNodeProjection(
				"graphics",
				CompositingGraphNodeKind.Source,
				graphicsTitle,
				IsMeaningful(input.GraphicsDimensions) ? input.GraphicsDimensions.Trim() : "RGBA overlay",
				ValueOrDash(input.GraphicsState),
				EvaluateHealth(input.GraphicsState),
				[new("rgba", "RGBA", CompositingGraphPortDirection.Output)]));

			nodes.Add(new CompositingGraphNodeProjection(
				"graphics-transform",
				CompositingGraphNodeKind.Transform,
				"Graphics Transform",
				$"X {Finite(input.GraphicsPositionX):0.##}% · Y {Finite(input.GraphicsPositionY):0.##}% · {Finite(input.GraphicsScale, 1):0.##}x",
				input.GraphicsVisible ? "VISIBLE" : "READY",
				EvaluateHealth(input.GraphicsState),
				[
					new("input", "Layer", CompositingGraphPortDirection.Input),
					new("output", "Transformed", CompositingGraphPortDirection.Output)
				]));
			connections.Add(new CompositingGraphConnectionProjection(
				"graphics->transform",
				"graphics",
				"rgba",
				"graphics-transform",
				"input",
				input.GraphicsVisible));
		}
		else
		{
			foreach (var layer in compositingLayers
				.OrderBy(layer => layer.Order)
				.ThenBy(layer => layer.LayerId, StringComparer.Ordinal))
			{
				var layerNodeId = $"layer:{layer.LayerId}";
				var opacity = layer.Opacity / 255.0 * 100.0;
				nodes.Add(new CompositingGraphNodeProjection(
					layerNodeId,
					CompositingGraphNodeKind.Layer,
					layer.ContentIdentity,
					$"ORDER {layer.Order} · {opacity:0}% · X {layer.PositionX * 100:0.#}% · Y {layer.PositionY * 100:0.#}% · {layer.Scale:0.##}x",
					layer.Visible ? "CONFIRMED VISIBLE" : "CONFIRMED HIDDEN",
					CompositingGraphHealth.Normal,
					[new("rgba", "RGBA", CompositingGraphPortDirection.Output)],
					CanRewire: true));
				connections.Add(new CompositingGraphConnectionProjection(
					$"{layerNodeId}->composite",
					layerNodeId,
					"rgba",
					"composite",
					"layer",
					layer.Visible));
			}
		}

		nodes.Add(new CompositingGraphNodeProjection(
			"composite",
			CompositingGraphNodeKind.Composite,
			"GPU Composite",
			$"{ValueOrDash(input.CurrentFormat)} · GPU {ValueOrDash(input.GpuProviderHealth)}",
			ValueOrDash(input.VisualLayerStatus),
			EvaluateHealth(input.RuntimeHealth, input.GpuProviderHealth, input.VisualLayerStatus),
			[
				new("background", "Background", CompositingGraphPortDirection.Input),
				new("layer", "Layer", CompositingGraphPortDirection.Input),
				new("program", "Program", CompositingGraphPortDirection.Output)
			]));
		connections.Add(new CompositingGraphConnectionProjection(
			"routing->composite",
			"routing",
			"program",
			"composite",
			"background"));
		if (compositingLayers.Count == 0)
		{
			connections.Add(new CompositingGraphConnectionProjection(
				"transform->composite",
				"graphics-transform",
				"output",
				"composite",
				"layer",
				input.GraphicsVisible));
		}

		nodes.Add(new CompositingGraphNodeProjection(
			"program-output",
			CompositingGraphNodeKind.Output,
			"Program Output",
			$"Source {ValueOrDash(input.ProgramSourceId)} · {ValueOrDash(input.CurrentFormat)}",
			ValueOrDash(input.CommitStatus),
			EvaluateHealth(input.RuntimeStatus, input.RuntimeHealth, input.CommitStatus),
			[
				new("program", "Program", CompositingGraphPortDirection.Input),
				new("record", "Record", CompositingGraphPortDirection.Output)
			]));
		connections.Add(new CompositingGraphConnectionProjection(
			"composite->program",
			"composite",
			"program",
			"program-output",
			"program"));

		nodes.Add(new CompositingGraphNodeProjection(
			"recorder",
			CompositingGraphNodeKind.Recorder,
			"Recorder",
			string.IsNullOrWhiteSpace(input.RecordingError) ? "Program recording" : input.RecordingError.Trim(),
			ValueOrDash(input.RecordingStatus),
			EvaluateHealth(input.RecordingStatus, input.RecordingError),
			[new("program", "Program", CompositingGraphPortDirection.Input)]));
		connections.Add(new CompositingGraphConnectionProjection(
			"program->recorder",
			"program-output",
			"record",
			"recorder",
			"program",
			string.Equals(input.RecordingStatus, "RECORDING", StringComparison.OrdinalIgnoreCase)));

		return new CompositingGraphProjection(nodes, connections);
	}

	private static CompositingGraphHealth EvaluateHealth(params string?[] values)
	{
		foreach (var value in values)
		{
			if (!IsMeaningful(value))
				continue;
			if (ContainsAny(value!, "ERROR", "FAIL", "OFFLINE", "REJECTED"))
				return CompositingGraphHealth.Error;
		}

		foreach (var value in values)
		{
			if (!IsMeaningful(value))
				continue;
			if (ContainsAny(
				value!,
				"DEGRADED",
				"UNAVAILABLE",
				"UNVERIFIED",
				"UNKNOWN",
				"STALE",
				"DISCONNECTED",
				"BLOCKED",
				"STARTING"))
			{
				return CompositingGraphHealth.Degraded;
			}
		}

		return CompositingGraphHealth.Normal;
	}

	private static bool ContainsAny(string value, params string[] tokens) =>
		tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));

	private static string ValueOrDash(string? value) =>
		IsMeaningful(value) ? value!.Trim() : "—";

	private static bool IsMeaningful(string? value) =>
		!string.IsNullOrWhiteSpace(value) &&
		!string.Equals(value.Trim(), "—", StringComparison.Ordinal);

	private static double Finite(double value, double fallback = 0) =>
		double.IsFinite(value) ? value : fallback;
}
