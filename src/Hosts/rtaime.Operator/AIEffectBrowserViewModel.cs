// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using rtaime.Client;

namespace rtaime.Operator;

/// <summary>Selection and descriptor tests are observational; only the explicit activation command changes the effect.</summary>
public sealed class AIEffectBrowserViewModel : INotifyPropertyChanged
{
    private readonly OperatorControlClient _client;
    private readonly Func<bool> _canControl;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _hasEvidence;
    private OperatorAICapabilityDescriptor? _selected;
    private bool _approved;
    private bool _busy;
    private ulong? _testedRevision;
    private DateTimeOffset? _catalogAt;
    private string _status = "UNVERIFIED";
    private string _testStatus = "NOT TESTED";
    private string _execution = "BYPASS · no confirmed effect";
    public AIEffectBrowserViewModel(OperatorControlClient client, Func<bool> canControl, Func<bool> enabled, Func<bool>? hasEvidence = null)
    {
        _client = client; _canControl = canControl; _enabled = enabled; _hasEvidence = hasEvidence ?? canControl;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !_busy && _canControl());
        TestCommand = new AsyncRelayCommand(TestAsync, () => !_busy && _canControl() && !_enabled() && Selected?.Available == true && Fresh);
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<OperatorAICapabilityDescriptor> Capabilities { get; } = [];
    public ICommand RefreshCommand { get; }
    public ICommand TestCommand { get; }
    public OperatorAICapabilityDescriptor? Selected
    {
        get => _selected;
        set { if (_busy || _selected == value) return; _selected = value; InvalidateApproval(); Changed(); Changed(nameof(Detail)); RaiseCommandState(); }
    }
    public string Detail => Selected?.Detail ?? "Refresh provider evidence, then select an installed capability.";
    public string Freshness => _catalogAt is not { } at ? "UNVERIFIED · no current provider evidence" :
        !Fresh ? "STALE · provider evidence expired; refresh and test again" :
        $"Provider evidence refreshed {Math.Max(0, (DateTimeOffset.UtcNow - at).TotalSeconds):0} s ago · expires after 30 s";
    public string Status { get => _status; private set { _status = value; Changed(); } }
    public string TestStatus { get => _testStatus; private set { _testStatus = value; Changed(); } }
    public string Execution { get => _execution; private set { _execution = value; Changed(); } }
    public bool Approved
    {
        get => _approved;
        set { _approved = value && CanApprove; Changed(); Changed(nameof(CanActivate)); }
    }
    private bool Fresh => _catalogAt is { } at && DateTimeOffset.UtcNow - at < TimeSpan.FromSeconds(30);
    public bool CanApprove => !_busy && _canControl() && !_enabled() && Fresh && Selected?.Available == true &&
        _testedRevision is { } revision && _client.Snapshot?.Production.Revision.Value == revision;
    public bool CanActivate => Approved && CanApprove;
    public void InvalidateApproval()
    {
        _approved = false; _testedRevision = null;
        TestStatus = "NOT TESTED · approval required";
        Changed(nameof(Approved)); Changed(nameof(CanActivate)); Changed(nameof(CanApprove));
    }
    public async Task RefreshAsync()
    {
        if (_busy || !_canControl()) return;
        await RunAsync(async () =>
        {
            InvalidateApproval(); _catalogAt = null; Capabilities.Clear(); _selected = null;
            foreach (var capability in await _client.GetAIEffectCatalogAsync()) Capabilities.Add(capability);
            _catalogAt = DateTimeOffset.UtcNow;
            Status = Capabilities.Count == 0 ? "UNAVAILABLE · no installed capability advertised" : "INSTALLED · provider evidence refreshed";
            Changed(nameof(Selected)); Changed(nameof(Detail));
        });
    }
    public async Task TestAsync()
    {
        if (!TestCommand.CanExecute(null)) return;
        await RunAsync(async () =>
        {
            InvalidateApproval(); TestStatus = "TEST PENDING · Program effect remains bypassed";
            var selected = Selected!;
            var revision = _client.Snapshot?.Production.Revision.Value;
            var result = await _client.TestAIEffectAsync();
            if (result.Status != "TESTED" || result.EffectVisible || result.Enabled || result.Provider != selected.Provider ||
                !_canControl() || !Fresh || revision != _client.Snapshot?.Production.Revision.Value)
            {
                TestStatus = "TEST REJECTED · " + (result.Failure?.Message ?? result.Status);
                return;
            }
            _testedRevision = revision;
            Status = selected.Status;
            TestStatus = $"TESTED · {result.InferenceTime.TotalMilliseconds:0.0} ms · sample {result.SourceSequence}. No visual output was changed.";
        });
    }
    public void Observe(OperatorAIShowcaseDescriptor result)
    {
        if (!_hasEvidence())
        {
            InvalidateApproval(); Execution = "STALE / UNAVAILABLE · refresh before operating"; return;
        }
        if (_testedRevision is { } revision && revision != _client.Snapshot?.Production.Revision.Value) InvalidateApproval();
        Execution = !result.Enabled ? "BYPASS · effect disabled" :
            result.Status == "RUNNING" && result.EffectVisible ? $"EXECUTED · source {result.SourceSequence} → applied {result.AppliedSequence}" :
            result.Status == "ARMED" ? "ACCEPTED · waiting for usable inference" :
            "FALLBACK · " + result.Status + " · " + (result.Failure?.Message ?? "clean Program continues");
        RaiseCommandState();
    }
    private async Task RunAsync(Func<Task> action)
    {
        _busy = true; Status = "PENDING"; RaiseCommandState();
        try { await action(); }
        catch (Exception exception)
        {
            InvalidateApproval(); _catalogAt = null;
            Status = exception is NotSupportedException ? "UNSUPPORTED · transport has no capability/test contract" : "UNVERIFIED / REFRESH REQUIRED";
            TestStatus = exception.Message;
        }
        finally { _busy = false; RaiseCommandState(); }
    }
    public void RaiseCommandState()
    {
        if (!Fresh || !_hasEvidence()) { _approved = false; Changed(nameof(Approved)); }
        if (!_hasEvidence()) Execution = "STALE / UNAVAILABLE · refresh before operating";
        Changed(nameof(Freshness)); Changed(nameof(CanApprove)); Changed(nameof(CanActivate));
        (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (TestCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
