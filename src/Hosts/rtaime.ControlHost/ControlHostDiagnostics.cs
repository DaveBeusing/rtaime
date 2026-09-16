// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Persistence;

namespace rtaime.ControlHost;

public static class ControlHostDiagnostics
{
	private const int RetainedSupportEvents = 64;

	public static SupportSnapshot Capture(ControlHostProcess process, DateTimeOffset? capturedAtUtc = null)
	{
		ArgumentNullException.ThrowIfNull(process);
		var lifecycle = process.Lifecycle;
		var build = ProductBuildInfo.FromAssembly(typeof(ControlHostProcess).Assembly);
		var builder = new SupportSnapshotBuilder(
			build,
			"ControlHost",
			Environment.ProcessId,
			lifecycle.State.ToString(),
			lifecycle.Health.ToString())
			.Status("lifecycle.detail", lifecycle.Detail)
			.Status("recovery.state", process.Recovery.State.ToString())
			.Status("recovery.detail", process.Recovery.Detail)
			.Status("runtime.connected", (process.RuntimeTransport?.IsConnected ?? false).ToString())
			.Status("ipc.running", (process.IpcServer?.Running ?? false).ToString());

		if (process.IpcServer is { } ipc)
		{
			builder.Identity("hostInstanceId", ipc.HostInstanceId)
				.Configuration("controlEndpoint", ipc.Endpoint)
				.Counter("ipc.stateVersion", ToCounter(ipc.StateVersion));
		}

		if (process.RuntimeTransport is { } runtime)
		{
			builder.Identity("runtimeHostInstanceId", runtime.HostInstanceId ?? "unbound")
				.Counter("runtime.providerCount", runtime.ProviderDescriptors.Count);
		}

		if (process.Control is { } control)
		{
			builder.Status("authority.present", control.HasAuthoritativeState.ToString())
				.Status("authority.pendingExecution", control.HasPendingExecution.ToString());
			if (control.HasAuthoritativeState)
			{
				var state = control.State;
				builder.Identity("productionId", state.ProductionId.ToString())
					.Status("authority.revision", state.Revision.ToString())
					.Status("routing.previewSourceId", state.Routing.PreviewSourceId.ToString())
					.Status("routing.programSourceId", state.Routing.ProgramSourceId.ToString());
			}
		}

		if (process.Journal is { } journal)
		{
			var statistics = journal.Statistics;
			var health = journal.Health;
			builder.Status("journal.health", health.State.ToString())
				.Counter("journal.accepted", ToCounter(statistics.Accepted))
				.Counter("journal.persisted", ToCounter(statistics.Persisted))
				.Counter("journal.dropped", ToCounter(statistics.Dropped))
				.Counter("journal.failed", ToCounter(statistics.Failed))
				.Counter("journal.pending", statistics.Pending)
				.Counter("journal.retained", statistics.Retained)
				.Counter("journal.capacity", statistics.Capacity)
				.Events(ToDiagnosticEvents(journal.Entries.TakeLast(RetainedSupportEvents)));
		}

		return builder.Build(capturedAtUtc);
	}

	public static string CaptureJson(ControlHostProcess process, DateTimeOffset? capturedAtUtc = null) =>
		SupportSnapshotSerializer.Serialize(Capture(process, capturedAtUtc));

	private static IReadOnlyList<DiagnosticEvent> ToDiagnosticEvents(IEnumerable<ProductionJournalEntry> entries) =>
		entries.Select(entry =>
		{
			var journalEvent = entry.Event;
			var dimensions = new Dictionary<string, string>
			{
				["authoritativeRevision"] = journalEvent.AuthoritativeRevision.ToString()
			};
			if (journalEvent.CausationId is { } causationId)
				dimensions["causationId"] = causationId.ToString();
			Failure? failure = journalEvent.Failure is { } sourceFailure
				? new Failure(sourceFailure.Code, DiagnosticRedactor.RedactText(sourceFailure.Message))
				: null;
			return new DiagnosticEvent(
				entry.Ordinal,
				journalEvent.Timestamp.Value,
				failure is null ? DiagnosticSeverity.Information : DiagnosticSeverity.Error,
				journalEvent.Category,
				journalEvent.Code,
				DiagnosticRedactor.RedactText(journalEvent.Detail),
				failure,
				DiagnosticRedactor.Sanitize(dimensions));
		}).ToArray();

	private static long ToCounter(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;
}
