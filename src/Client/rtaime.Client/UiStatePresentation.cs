// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Client;

public enum UiStateKind
{
	Loading,
	Ready,
	Empty,
	Offline,
	Unavailable,
	Error,
	Recovering
}

public enum StartupDependencyClass
{
	Critical,
	RequiredForProduction,
	Optional
}

public sealed record UiStatePresentation(
	UiStateKind State,
	string Title,
	string Detail,
	bool IsBlocking,
	bool IsProductionReady)
{
	public bool IsReady => State == UiStateKind.Ready;
	public bool IsVisible => State != UiStateKind.Ready;
}

public static class UiStatePresentationFactory
{
	public static UiStatePresentation FromRuntime(
		RuntimeReadinessSnapshot snapshot,
		bool hasContent,
		string emptyTitle,
		string emptyDetail)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		if (snapshot.State == RuntimeReadinessState.Failed)
			return Create(UiStateKind.Error, "Runtime unavailable", PrimaryDetail(snapshot, "Runtime recovery failed."), true, false);

		if (snapshot.State == RuntimeReadinessState.Recovering)
			return Create(UiStateKind.Recovering, "Recovering runtime", PrimaryDetail(snapshot, "Rebuilding runtime state…"), true, false);

		if (snapshot.State == RuntimeReadinessState.Initializing)
			return Create(UiStateKind.Loading, "Initializing runtime", PrimaryDetail(snapshot, "Waiting for authoritative runtime state…"), true, false);

		if (snapshot.State == RuntimeReadinessState.NotReady)
		{
			var offline = snapshot.Reasons.Any(reason =>
				reason.IsBlocking &&
				(reason.Code.StartsWith("control.", StringComparison.Ordinal) ||
				 reason.Code.StartsWith("runtime.", StringComparison.Ordinal)));

			return Create(
				offline ? UiStateKind.Offline : UiStateKind.Unavailable,
				offline ? "Runtime offline" : "Production unavailable",
				PrimaryDetail(snapshot, "Required production services are not ready."),
				true,
				false);
		}

		if (!hasContent)
			return Create(UiStateKind.Empty, emptyTitle, emptyDetail, false, snapshot.IsProductionReady);

		return Create(UiStateKind.Ready, string.Empty, string.Empty, false, snapshot.IsProductionReady);
	}

	public static StartupDependencyClass ClassifyDependency(string component, bool requireAI = false)
	{
		if (string.IsNullOrWhiteSpace(component))
			throw new ArgumentException("Component is required.", nameof(component));

		return component.Trim().ToUpperInvariant() switch
		{
			"OPERATOR" or "CONFIGURATION" => StartupDependencyClass.Critical,
			"CONTROL" or "RUNTIME" or "MEDIA" or "PROVIDER" or "GPU" or "GPU / PROVIDER" => StartupDependencyClass.RequiredForProduction,
			"AI" when requireAI => StartupDependencyClass.RequiredForProduction,
			_ => StartupDependencyClass.Optional
		};
	}

	private static UiStatePresentation Create(
		UiStateKind state,
		string title,
		string detail,
		bool isBlocking,
		bool isProductionReady) =>
		new(state, title, detail, isBlocking, isProductionReady);

	private static string PrimaryDetail(RuntimeReadinessSnapshot snapshot, string fallback) =>
		snapshot.Reasons.FirstOrDefault(reason => reason.IsBlocking)?.Detail
			?? snapshot.Reasons.FirstOrDefault()?.Detail
			?? fallback;
}
