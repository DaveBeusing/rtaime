// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Client;

public enum OperatorUiStateKind
{
	Ready,
	Loading,
	Empty,
	Offline,
	Unavailable,
	Error,
	Recovering
}

public sealed record OperatorUiStateSnapshot(
	OperatorUiStateKind State,
	string Title,
	string Detail)
{
	public bool IsReady => State == OperatorUiStateKind.Ready;
}

public static class OperatorUiStateMachine
{
	public static OperatorUiStateKind Resolve(
		bool hasContent,
		bool isLoading = false,
		bool isOffline = false,
		bool isUnavailable = false,
		bool hasError = false,
		bool isRecovering = false)
	{
		if (hasError)
			return OperatorUiStateKind.Error;
		if (isRecovering)
			return OperatorUiStateKind.Recovering;
		if (isOffline)
			return OperatorUiStateKind.Offline;
		if (isLoading)
			return OperatorUiStateKind.Loading;
		if (isUnavailable)
			return OperatorUiStateKind.Unavailable;
		return hasContent ? OperatorUiStateKind.Ready : OperatorUiStateKind.Empty;
	}
}
