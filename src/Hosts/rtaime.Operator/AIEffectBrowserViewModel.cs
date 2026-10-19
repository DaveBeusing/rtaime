// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using rtaime.Client;

namespace rtaime.Operator;

/// <summary>Provider evidence and descriptor tests are observational; only explicit Program approval permits activation.</summary>
public sealed class AIEffectBrowserViewModel : INotifyPropertyChanged
{
    private readonly OperatorControlClient _client;
    private readonly Func<bool> _canControl;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _hasEvidence;
    private OperatorAICapabilityDescriptor? _selected;
    private OperatorAIShowcaseDescriptor _lastObservation = OperatorAIShowcaseDescriptor.Unavailable;
    private bool _approved;
    private bool _busy;
    private ulong? _testedRevision;
    private string? _testedProgramSource;
    private DateTimeOffset? _catalogAt;
    private string _status = "UNVERIFIED";
    private string _testStatus = "NOT TESTED";
    private string _execution = "BYPASS · no confirmed effect";
    private string _recovery = "Refresh installed provider evidence before testing.";

    public AIEffectBrowserViewModel(OperatorControlClient client, Func<bool> canControl, Func<bool> enabled, Func<bool>? hasEvidence = null)
    {
        _client = client;
        _canControl = canControl;
        _enabled = enabled;
        _hasEvidence = hasEvidence ?? canControl;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !_busy && _canControl());
        TestCommand = new AsyncRelayCommand(TestAsync,
            () => !_busy && _canControl() && _hasEvidence() && !_enabled() && Selected?.Available == true && Fresh);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<OperatorAICapabilityDescriptor> Capabilities { get; } = [];
    public ICommand RefreshCommand { get; }
    public ICommand TestCommand { get; }

    public OperatorAICapabilityDescriptor? Selected
    {
        get => _selected;
        set
        {
            if (_busy || _selected == value) return;
            _selected = value;
            InvalidateApproval();
            PublishSelection();
            RaiseCommandState();
        }
    }

    public string Detail => Selected?.Detail ?? "Refresh provider evidence, then select an installed capability.";
    public string CapabilityIdentity => Selected is { } capability
        ? capability.Kind + " · " + capability.CapabilityId : "No capability selected";
    public string ProviderIdentity => Selected is { } capability
        ? capability.Provider + " · " + capability.ProviderId : "UNVERIFIED · no installed provider selected";
    public string ModelEvidence => ModelDetail(model => model.ModelVersion);
    public string InputContract => ModelDetail(model => model.InputContract);
    public string OutputContract => ModelDetail(model => model.OutputContract);
    public string ResourceRequirements => ModelDetail(model => model.ResourceRequirements);
    public string Provenance => ModelDetail(model => model.Provenance);
    public string Availability => Selected?.Status ?? "UNVERIFIED · select a provider-advertised capability";
    public string ProgramTarget => _client.Snapshot is { } snapshot
        ? "PROGRAM · source " + snapshot.Production.Routing.ProgramSourceId + " · revision " + snapshot.Production.Revision.Value
        : "PROGRAM · no authoritative source or revision";
    public string Freshness => _catalogAt is not { } at
        ? "UNVERIFIED · no current provider evidence"
        : !Fresh
            ? "STALE · provider evidence expired; refresh and test again"
            : $"Provider evidence refreshed {Math.Max(0, (DateTimeOffset.UtcNow - at).TotalSeconds):0} s ago · expires after 30 s";
    public string ResultEvidence => _lastObservation.UpdatedAtUtc is { } at
        ? "Runtime observation " + at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)
          + " · source " + (_lastObservation.SourceSequence?.ToString(CultureInfo.InvariantCulture) ?? "UNVERIFIED")
          + " · applied " + (_lastObservation.AppliedSequence?.ToString(CultureInfo.InvariantCulture) ?? "NONE")
        : "NO CURRENT RESULT · Runtime has not supplied a timestamped observation";
    public string Recovery => _recovery;
    public string NextStep
    {
        get
        {
            if (!_hasEvidence()) return "Synchronize the Operator and restore Runtime readiness before testing or applying.";
            if (!_canControl()) return "Complete or recover the pending authoritative operation.";
            if (_enabled()) return "Use AI OFF to request safe bypass; wait for Runtime confirmation.";
            if (!Fresh) return "Refresh installed provider evidence. No download or external transfer is started.";
            if (Selected is null) return "Select a capability advertised by the connected provider.";
            if (!Selected.Available) return "This capability is not ready or supported. Inspect the provider and refresh.";
            if (!TestContextCurrent) return "Run TEST on the current committed Program descriptor. No pixels are changed.";
            if (!Approved) return "Review the tested source and explicitly approve applying this effect to Program.";
            return "Use AI ON to request activation. Accepted is not Executed; watch Runtime evidence.";
        }
    }

    public string Status { get => _status; private set { _status = value; Changed(); } }
    public string TestStatus { get => _testStatus; private set { _testStatus = value; Changed(); } }
    public string Execution { get => _execution; private set { _execution = value; Changed(); } }
    public bool Approved
    {
        get => _approved;
        set
        {
            _approved = value && CanApprove;
            Changed();
            Changed(nameof(CanActivate));
            Changed(nameof(NextStep));
        }
    }

    private bool Fresh => _catalogAt is { } at && DateTimeOffset.UtcNow - at < TimeSpan.FromSeconds(30);
    private string? CurrentProgramSource => _client.Snapshot?.Production.Routing.ProgramSourceId.ToString();
    private bool TestContextCurrent => _testedRevision is { } revision
        && _client.Snapshot?.Production.Revision.Value == revision
        && _testedProgramSource is not null && _testedProgramSource == CurrentProgramSource;

    public bool CanApprove => !_busy && _canControl() && _hasEvidence() && !_enabled() && Fresh
        && Selected?.Available == true && TestContextCurrent;
    public bool CanActivate => Approved && CanApprove;

    private string ModelDetail(Func<OperatorAIModelDescriptor, string> selector) =>
        Selected?.Models is { Length: > 0 } models
            ? string.Join("; ", models.Select(selector).Where(value => !string.IsNullOrWhiteSpace(value)))
            : "UNVERIFIED · no package advertised";

    public void InvalidateApproval()
    {
        _approved = false;
        _testedRevision = null;
        _testedProgramSource = null;
        TestStatus = "NOT TESTED · approval required";
        Changed(nameof(Approved));
        Changed(nameof(CanActivate));
        Changed(nameof(CanApprove));
        Changed(nameof(NextStep));
    }

    public async Task RefreshAsync()
    {
        if (_busy || !_canControl()) return;
        await RunAsync(async () =>
        {
            InvalidateApproval();
            _catalogAt = null;
            Capabilities.Clear();
            _selected = null;
            PublishSelection();
            foreach (var capability in await _client.GetAIEffectCatalogAsync()) Capabilities.Add(capability);
            _catalogAt = DateTimeOffset.UtcNow;
            Status = Capabilities.Count == 0
                ? "UNAVAILABLE · no installed capability advertised"
                : "INSTALLED · provider evidence refreshed (not hardware qualification)";
            _recovery = Capabilities.Count == 0
                ? "Confirm an installed provider is running in AIHost; refresh after recovery."
                : "Select an available capability, run a descriptor-only TEST and explicitly approve Program use.";
            Changed(nameof(Recovery));
        });
    }

    public async Task TestAsync()
    {
        if (!TestCommand.CanExecute(null)) return;
        await RunAsync(async () =>
        {
            InvalidateApproval();
            TestStatus = "TEST PENDING · Program effect remains bypassed";
            var selected = Selected!;
            var revision = _client.Snapshot?.Production.Revision.Value;
            var source = CurrentProgramSource;
            var result = await _client.TestAIEffectAsync();
            if (result.Status != "TESTED" || result.EffectVisible || result.Enabled
                || result.Provider != selected.Provider || result.SourceSequence is null
                || !_canControl() || !_hasEvidence() || !Fresh || revision is null || source is null
                || revision != _client.Snapshot?.Production.Revision.Value || source != CurrentProgramSource)
            {
                Status = "TEST REJECTED";
                TestStatus = "TEST REJECTED · " + (result.Failure?.Message
                    ?? (source != CurrentProgramSource || revision != _client.Snapshot?.Production.Revision.Value
                        ? "committed Program source/revision changed" : result.Status));
                _recovery = "Keep Program unchanged, synchronize and repeat provider discovery and TEST.";
                Changed(nameof(Recovery));
                return;
            }
            _testedRevision = revision;
            _testedProgramSource = source;
            Status = selected.Status;
            TestStatus = $"TESTED · {result.InferenceTime.TotalMilliseconds:0.0} ms · sample {result.SourceSequence}. No visual output was changed.";
            _recovery = "Descriptor-only test confirmed. No independent result image exists for A/B comparison.";
            Changed(nameof(Recovery));
        });
    }

    public void Observe(OperatorAIShowcaseDescriptor result)
    {
        _lastObservation = result;
        Changed(nameof(ResultEvidence));
        if (!_hasEvidence())
        {
            InvalidateApproval();
            Execution = "STALE / UNAVAILABLE · refresh before operating";
            _recovery = "Restore Control/Runtime connectivity and synchronize. No automatic reactivation occurs.";
            Changed(nameof(Recovery));
            RaiseCommandState();
            return;
        }
        if (_testedRevision is not null && !TestContextCurrent) InvalidateApproval();
        Execution = !result.Enabled ? "BYPASS · effect disabled" :
            result.Status == "RUNNING" && result.EffectVisible
                && result.SourceSequence is not null && result.AppliedSequence is not null
                ? $"EXECUTED · source {result.SourceSequence} → applied {result.AppliedSequence}" :
            result.Status == "ARMED" ? "ACCEPTED · waiting for usable inference" :
            "FALLBACK · " + result.Status + " · " + (result.Failure?.Message ?? "clean Program continues");
        _recovery = result.Status switch
        {
            "ARMED" => "Wait for a valid Runtime observation; accepted activation is not executed output.",
            "RUNNING" when result.EffectVisible => "Runtime reports applied output. Verify source/time evidence before relying on it.",
            "TIMEOUT" or "TIMED_OUT" or "DEADLINE" => "Inference deadline exceeded. Clean Program continues; check AIHost pressure before a new TEST.",
            "UNAVAILABLE" or "FAILED" or "ERROR" => "Clean Program continues. Inspect provider/AIHost health, then refresh and retest.",
            "STALE" or "EXPIRED" or "MISMATCH" => "Result is stale or source-mismatched. Synchronize Program and retest before approval.",
            _ => result.Enabled
                ? "Clean Program fallback stays authoritative. Inspect Runtime and source evidence before reapproval."
                : "AI is bypassed. Refresh and test before requesting another activation."
        };
        Changed(nameof(Recovery));
        RaiseCommandState();
    }

    private async Task RunAsync(Func<Task> action)
    {
        _busy = true;
        Status = "PENDING";
        RaiseCommandState();
        try { await action(); }
        catch (Exception exception)
        {
            InvalidateApproval();
            _catalogAt = null;
            Status = exception is NotSupportedException
                ? "UNSUPPORTED · transport has no capability/test contract" : "UNVERIFIED / REFRESH REQUIRED";
            TestStatus = exception.Message;
            _recovery = exception is NotSupportedException
                ? "Use the supported local provider transport; this peer cannot discover or test capabilities."
                : "Check provider/Control connectivity and Runtime readiness, then synchronize and refresh.";
            Changed(nameof(Recovery));
        }
        finally { _busy = false; RaiseCommandState(); }
    }

    public void RaiseCommandState()
    {
        if ((!Fresh || !_hasEvidence() || !TestContextCurrent) && _approved)
        {
            _approved = false;
            Changed(nameof(Approved));
        }
        if (!_hasEvidence()) Execution = "STALE / UNAVAILABLE · refresh before operating";
        Changed(nameof(Freshness));
        Changed(nameof(ProgramTarget));
        Changed(nameof(CanApprove));
        Changed(nameof(CanActivate));
        Changed(nameof(NextStep));
        (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (TestCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void PublishSelection()
    {
        foreach (var name in new[]
        {
            nameof(Selected), nameof(Detail), nameof(CapabilityIdentity), nameof(ProviderIdentity),
            nameof(ModelEvidence), nameof(InputContract), nameof(OutputContract),
            nameof(ResourceRequirements), nameof(Provenance), nameof(Availability), nameof(NextStep)
        }) Changed(name);
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
