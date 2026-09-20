// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;

namespace rtaime.Tests.Unit;

public sealed class OperatorUiStateTests
{
	[Fact]
	public void Empty_content_becomes_ready_when_content_arrives()
	{
		Assert.Equal(
			OperatorUiStateKind.Empty,
			OperatorUiStateMachine.Resolve(hasContent: false));

		Assert.Equal(
			OperatorUiStateKind.Ready,
			OperatorUiStateMachine.Resolve(hasContent: true));
	}

	[Fact]
	public void Loading_becomes_ready_when_loading_finishes()
	{
		Assert.Equal(
			OperatorUiStateKind.Loading,
			OperatorUiStateMachine.Resolve(hasContent: false, isLoading: true));

		Assert.Equal(
			OperatorUiStateKind.Ready,
			OperatorUiStateMachine.Resolve(hasContent: true));
	}

	[Fact]
	public void Loading_failure_becomes_error()
	{
		Assert.Equal(
			OperatorUiStateKind.Loading,
			OperatorUiStateMachine.Resolve(hasContent: false, isLoading: true));

		Assert.Equal(
			OperatorUiStateKind.Error,
			OperatorUiStateMachine.Resolve(hasContent: false, isLoading: false, hasError: true));
	}

	[Fact]
	public void Offline_recovery_becomes_ready()
	{
		Assert.Equal(
			OperatorUiStateKind.Offline,
			OperatorUiStateMachine.Resolve(hasContent: true, isOffline: true));

		Assert.Equal(
			OperatorUiStateKind.Recovering,
			OperatorUiStateMachine.Resolve(hasContent: true, isRecovering: true));

		Assert.Equal(
			OperatorUiStateKind.Ready,
			OperatorUiStateMachine.Resolve(hasContent: true));
	}

	[Fact]
	public void Error_has_precedence_over_other_transient_states()
	{
		Assert.Equal(
			OperatorUiStateKind.Error,
			OperatorUiStateMachine.Resolve(
				hasContent: true,
				isLoading: true,
				isOffline: true,
				isUnavailable: true,
				hasError: true,
				isRecovering: true));
	}

	[Fact]
	public void Recovering_has_precedence_over_offline_and_loading()
	{
		Assert.Equal(
			OperatorUiStateKind.Recovering,
			OperatorUiStateMachine.Resolve(
				hasContent: true,
				isLoading: true,
				isOffline: true,
				isRecovering: true));
	}

	[Fact]
	public void Unavailable_is_distinct_from_empty()
	{
		Assert.Equal(
			OperatorUiStateKind.Unavailable,
			OperatorUiStateMachine.Resolve(hasContent: false, isUnavailable: true));

		Assert.Equal(
			OperatorUiStateKind.Empty,
			OperatorUiStateMachine.Resolve(hasContent: false));
	}
}
