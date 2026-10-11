// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class ShowTemplateOperatorTests
{
	[Fact]
	public async Task Draft_import_and_dependency_edits_never_issue_Program_commands()
	{
		var transport = new TemplateTransport();
		var model = Model(transport);
		model.LoadDraft(Template());
		Assert.Equal("DRAFT", model.Status);
		Assert.Equal(0, transport.CommitCalls);
		Assert.False(model.CommitCommand.CanExecute(null));
		await model.PreviewAsync();
		Assert.True(model.CommitCommand.CanExecute(null));
		model.Bindings[0].TargetId = Identity.New().ToString();
		Assert.False(model.CommitCommand.CanExecute(null));
		Assert.Equal("DRAFT", model.Status);
		Assert.Equal(0, transport.CommitCalls);
	}

	[Fact]
	public async Task Draft_rename_and_edits_during_preflight_require_another_preview()
	{
		var transport = new TemplateTransport { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
		var model = Model(transport);
		model.LoadDraft(Template());
		var preview = model.PreviewAsync();
		model.InstanceName = "Changed";
		transport.Pending.SetResult(new(true, 2, 1, ["Add"], []));
		await preview;
		Assert.False(model.CommitCommand.CanExecute(null));
		Assert.Equal("DRAFT", model.Status);
		Assert.Equal(0, transport.CommitCalls);
	}

	[Fact]
	public async Task Rejected_or_stale_commit_preserves_the_draft_and_requires_preflight()
	{
		var transport = new TemplateTransport();
		var model = Model(transport);
		var json = Template();
		model.LoadDraft(json);
		await model.PreviewAsync();
		await model.CommitAsync();
		Assert.Equal(1, transport.CommitCalls);
		Assert.Equal(json, model.DraftJson);
		Assert.False(model.CommitCommand.CanExecute(null));
		Assert.Contains("REJECTED", model.Status);
		Assert.Contains("changed", model.Detail);
		Assert.Equal(2ul, transport.LastRequest!.ExpectedProjectVersion);
	}

	[Fact]
	public async Task Dependency_conflicts_never_enable_commit()
	{
		var transport = new TemplateTransport { Conflict = true };
		var model = Model(transport);
		model.LoadDraft(Template());
		await model.PreviewAsync();
		Assert.False(model.CommitCommand.CanExecute(null));
		Assert.Equal("CONFLICT", model.Status);
		Assert.Contains("Provider absent", model.Detail);
	}

	private static ShowTemplateViewModel Model(TemplateTransport transport) =>
		new(new OperatorControlClient(transport), () => null, _ => throw new InvalidOperationException("Rejected import cannot be confirmed."));
	private static string Template()
	{
		var source = Identity.New().ToString();
		var list = new ShowControlCueList(ShowControlContractVersion.Current, ShowControlCueListId.New(), "Opening",
			[new(ShowControlCueId.New(), "Preview", [new(ShowControlActionId.New(), ShowControlActionKind.SetPreview, sourceId: source)])]);
		return ShowTemplateSerializer.Serialize(ShowTemplateSerializer.Create("Opening",
			new("[]", ShowControlCanonicalSerializer.Serialize(list), [new("source", source, "Camera", "")])));
	}

	private sealed class TemplateTransport : IOperatorControlTransport
	{
		public bool Conflict { get; init; }
		public TaskCompletionSource<ShowTemplatePreflight>? Pending { get; init; }
		public int CommitCalls { get; private set; }
		public ShowTemplateRequest? LastRequest { get; private set; }
		public ValueTask<ShowTemplatePreflight> PreflightShowTemplateAsync(ShowTemplateRequest request, CancellationToken cancellationToken = default) =>
			Pending is null ? ValueTask.FromResult(new ShowTemplatePreflight(!Conflict, 2, 1, ["Add authored definitions"],
				Conflict ? ["Provider absent"] : [])) : new(Pending.Task);
		public ValueTask<ShowControlWorkspaceSnapshot> InstantiateShowTemplateAsync(ShowTemplateRequest request, CancellationToken cancellationToken = default)
		{
			CommitCalls++;
			LastRequest = request;
			return ValueTask.FromException<ShowControlWorkspaceSnapshot>(new InvalidOperationException("Show changed. Preview again."));
		}
		public ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public ValueTask<OperatorMutationResponse> SelectPreviewAsync(SelectPreviewCommand command, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No Program commands during template setup.");
		public ValueTask<OperatorMutationResponse> CutProgramAsync(CutProgramCommand command, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No Program commands during template setup.");
		public ValueTask<OperatorMutationResponse> DissolveProgramAsync(DissolveProgramCommand command, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No Program commands during template setup.");
	}
}
