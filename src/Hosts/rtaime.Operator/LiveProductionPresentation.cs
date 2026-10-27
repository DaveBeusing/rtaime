// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Operator;

// Presentation-only classification of an existing governed Preview-to-Program
// operation. A command response or last displayed monitor frame is never
// promoted to confirmed output authority by this projection.
internal static class LiveProductionPresentation
{
    internal static string PreviewEvidence(string? selectedSourceName,
        bool connected, bool stale, bool pending, string transitionStatus)
    {
        if (!connected || stale)
            return "PREVIEW UNVERIFIED · synchronize Control before routing another source.";
        if (pending)
            return "PREVIEW REQUEST PENDING · await Control confirmation, do not repeat.";
        if (transitionStatus.EndsWith("REJECTED", StringComparison.OrdinalIgnoreCase) &&
            transitionStatus.Contains("PREVIEW", StringComparison.OrdinalIgnoreCase))
            return "PREVIEW REJECTED · confirmed routing is unchanged; inspect the failure.";
        if (transitionStatus.EndsWith("FAILED", StringComparison.OrdinalIgnoreCase) &&
            transitionStatus.Contains("PREVIEW", StringComparison.OrdinalIgnoreCase))
            return "PREVIEW OUTCOME UNCONFIRMED · synchronize before another request.";
        if (string.IsNullOrWhiteSpace(selectedSourceName))
            return "SELECT SOURCE · selection never changes Preview or Program by itself.";
        if (string.Equals(transitionStatus, "PREVIEW CONFIRMED", StringComparison.OrdinalIgnoreCase))
            return $"PREVIEW CONTROL CONFIRMED · selected {selectedSourceName}; matching monitor frames remain separate evidence.";
        return $"SELECTED {selectedSourceName} → SET PVW REQUEST · Program is never changed by local selection.";
    }

    internal static string TakeEvidence(
        bool connected, bool stale, bool pending, bool hasControlSnapshot,
        bool productionBlocked, string runtimeStatus, string transitionStatus,
        bool previewIdentityKnown, uint durationFrames)
    {
        if (!connected || stale)
            return "UNVERIFIED · Control connection is stale; synchronize before another Take.";
        if (pending)
            return "PENDING · production request or cue action is in flight; do not repeat it.";
        if (IsTakeOutcome(transitionStatus, "REJECTED"))
            return "REJECTED BY CONTROL · keep the last confirmed Program; review the reason before requesting again.";
        if (IsTakeOutcome(transitionStatus, "FAILED") ||
            IsTakeOutcome(transitionStatus, "UNCONFIRMED"))
            return "UNCONFIRMED TAKE · synchronize authoritative Program routing before retrying.";
        if (!hasControlSnapshot)
            return "UNVERIFIED · no synchronized Control production snapshot.";
        if (productionBlocked || !string.Equals(runtimeStatus, "READY", StringComparison.OrdinalIgnoreCase))
            return "BLOCKED · Runtime or production readiness does not permit a Take.";
        if (!previewIdentityKnown)
            return "UNVERIFIED · no confirmed Preview source identity.";
        if (durationFrames < 2)
            return "CUT ELIGIBLE · AUTO needs at least two frames; Runtime still revalidates the command.";
        return IsTakeOutcome(transitionStatus, "CONFIRMED")
            ? "CONTROL COMMIT CONFIRMED · Program identity is authoritative; actual monitor pixels and external delivery remain separate evidence."
            : "REQUEST ELIGIBLE · CUT/AUTO target confirmed Preview; Runtime revalidates, output delivery remains unverified.";
    }

    private static bool IsTakeOutcome(string? value, string suffix) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.StartsWith("CUT ", StringComparison.OrdinalIgnoreCase) ||
         value.StartsWith("DISSOLVE ", StringComparison.OrdinalIgnoreCase)) &&
        value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
}
