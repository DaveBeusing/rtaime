// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

public sealed record MediaBatchItem(string Name, string Path, string? AssetId, string State, string Detail);
public sealed partial class MediaPoolInspectorViewModel
{
    public const int MaximumBatchItems = 128;
    private bool _batchVisible, _batchRunning, _batchPrepared, _batchCancel;
    private bool _batchImport;
    private ulong _batchRevision;
    private string _batchStatus = "Choose Import or Remove selected to prepare a batch.";
    public ObservableCollection<MediaBatchItem> BatchItems { get; } = [];
    public Visibility BatchVisibility => _batchVisible ? Visibility.Visible : Visibility.Collapsed;
    public string BatchStatus { get => _batchStatus; private set => Set(ref _batchStatus, value); }
    public bool BatchRunning => _batchRunning;
    public ICommand PrepareBatchRemoveCommand { get; private set; } = null!;
    public ICommand ExecuteBatchCommand { get; private set; } = null!;
    public ICommand CancelBatchCommand { get; private set; } = null!;
    public ICommand RetryBatchCommand { get; private set; } = null!;
    public ICommand CloseBatchCommand { get; private set; } = null!;
    private bool CanPrepareBatch => !_browsingDisposed && !_catalogBusy && !_batchRunning && _catalogClient is not null &&
        _operator.IsConnected && !_operator.IsStale && !_operator.IsBusy;
    private void InitializeBatch()
    {
        PrepareBatchRemoveCommand = new OperatorShellCommand(PrepareBatchRemove, () => CanPrepareBatch && SelectedItems.Count > 0);
        ExecuteBatchCommand = new AsyncRelayCommand(ExecuteBatchAsync, () => CanPrepareBatch && _batchPrepared && BatchItems.Any(i => i.State == "READY"));
        CancelBatchCommand = new OperatorShellCommand(() => { _batchCancel = true; BatchStatus = "Stopping after the active request is confirmed…"; }, () => _batchRunning && !_batchCancel);
        RetryBatchCommand = new OperatorShellCommand(() =>
        {
            var retry = BatchItems.Where(i => i.State is "FAILED" or "CANCELLED").Select(i => i with { State = "READY", Detail = "Retry requires confirmation." }).ToArray();
            SetBatch(retry, _batchImport);
        }, () => CanPrepareBatch && BatchItems.Any(i => i.State is "FAILED" or "CANCELLED"));
        CloseBatchCommand = new OperatorShellCommand(() => { _batchVisible = false; _batchPrepared = false; BatchItems.Clear(); NotifyBatch(); }, () => !_batchRunning);
    }
    private void PrepareBatchRemove()
    {
        if (!CanPrepareBatch) return;
        if (SelectedItems.Count > MaximumBatchItems) { ShowBatchLimit(); return; }
        var items = SelectedItems.Select(item =>
        {
            var asset = _catalogSnapshot.Assets.FirstOrDefault(a => a.AssetId.ToString() == item.ReferenceId);
            return asset is not null && item.Kind == MediaPoolItemKind.Clip
                ? new MediaBatchItem(asset.DisplayName, asset.SourceLocation, asset.AssetId.ToString(), "READY", "Catalogue record only; source file is retained.")
                : new MediaBatchItem(item.Name, "", null, "SKIPPED", "Only persistent catalogue clips are eligible.");
        }).ToArray();
        SetBatch(items, import: false);
    }
    private Task PrepareBatchImportAsync()
    {
        if (!CanPrepareBatch || _importFilePicker is null) return Task.CompletedTask;
        var paths = _importFilePicker();
        if (paths.Count == 0) return Task.CompletedTask;
        if (paths.Count > MaximumBatchItems) { ShowBatchLimit(); return Task.CompletedTask; }
        SetBatch(paths.Distinct(StringComparer.OrdinalIgnoreCase).Select(path =>
            new MediaBatchItem(path, path, null, string.IsNullOrWhiteSpace(path) ? "SKIPPED" : "READY",
                "ControlHost validates readability, fingerprint, duplicates and decoder support.")).ToArray(), import: true);
        return Task.CompletedTask;
    }
    private void ShowBatchLimit()
    {
        SetBatch([], import: false);
        BatchStatus = "Select at most 128 items. No operation was started.";
        _batchPrepared = false; NotifyBatch();
    }
    private void SetBatch(IReadOnlyList<MediaBatchItem> items, bool import)
    {
        BatchItems.Clear(); foreach (var item in items.Take(MaximumBatchItems)) BatchItems.Add(item);
        _batchImport = import; _batchRevision = _catalogSnapshot.Revision;
        _batchVisible = true; _batchPrepared = true; _batchCancel = false;
        BatchStatus = (import ? "IMPORT" : "REMOVE FROM LIBRARY") + " · " + BatchItems.Count(i => i.State == "READY") +
            " eligible / " + BatchItems.Count + " selected. Review, then confirm. Source files are never deleted.";
        NotifyBatch();
    }
    private async Task ExecuteBatchAsync()
    {
        if (!CanPrepareBatch || !_batchPrepared || _catalogClient is null) return;
        _batchPrepared = false; _batchRunning = true; _batchCancel = false; _catalogBusy = true;
        _catalogError = null; NotifyBatch(); RaiseCatalogState(); CancelHover();
        try
        {
            // Refresh before sending any mutation: a stale preview must be prepared again.
            var fresh = await _catalogClient.GetMediaAssetCatalogAsync();
            SetCatalogSnapshot(fresh);
            if (fresh.Revision != _batchRevision)
            {
                BatchStatus = "STALE · catalogue changed. Prepare the batch again; no mutation was sent.";
                return;
            }
            for (var index = 0; index < BatchItems.Count; index++)
            {
                var item = BatchItems[index];
                if (item.State != "READY") continue;
                if (_batchCancel || _browsingDisposed || !_operator.IsConnected || _operator.IsStale || _operator.IsBusy)
                {
                    BatchItems[index] = item with { State = "CANCELLED", Detail = "Not sent. Prepare retry when connected and synchronized." };
                    continue;
                }
                if (!_batchImport && !_catalogSnapshot.Assets.Any(a => a.AssetId.ToString() == item.AssetId && a.SourceLocation == item.Path))
                {
                    BatchItems[index] = item with { State = "FAILED", Detail = "Asset no longer matches preflight. Prepare again." }; continue;
                }
                BatchItems[index] = item with { State = "PENDING", Detail = "Awaiting authoritative confirmation." };
                BatchStatus = (index + 1) + " / " + BatchItems.Count + " · " + item.Name;
                try
                {
                    // Concurrency is one. Stop admits no further requests, but never cancels an in-flight mutation.
                    var result = _batchImport
                        ? await _catalogClient.ImportMediaAssetsAsync([item.Path])
                        : await _catalogClient.RemoveMediaAssetAsync(new MediaAssetId(Identity.Parse(item.AssetId!)));
                    SetCatalogSnapshot(result.Snapshot);
                    var outcome = result.Items.SingleOrDefault();
                    BatchItems[index] = item with { State = outcome?.Succeeded == true ? "DONE" : "FAILED",
                        Detail = outcome?.Failure?.Message ?? outcome?.Disposition.ToString() ?? "No item confirmation returned." };
                }
                catch (Exception ex) when (IsBatchFailure(ex))
                {
                    BatchItems[index] = item with { State = "UNCONFIRMED", Detail = ex.Message + " Refresh the catalogue before preparing a new batch." };
                    _batchCancel = true;
                }
                await Task.Yield();
            }
            BatchStatus = "Finished · " + BatchItems.Count(i => i.State == "DONE") + " confirmed / " +
                BatchItems.Count(i => i.State == "FAILED") + " failed / " + BatchItems.Count(i => i.State == "UNCONFIRMED") +
                " unconfirmed / " + BatchItems.Count(i => i.State == "CANCELLED") + " not sent. Review individual results.";
        }
        catch (Exception ex) when (IsBatchFailure(ex)) { BatchStatus = "Preflight failed; no mutation sent: " + ex.Message; }
        finally { _batchRunning = false; _catalogBusy = false; Refresh(); RaiseCatalogState(); NotifyBatch(); }
    }
    private static bool IsBatchFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidOperationException or
        ArgumentException or FormatException or NotSupportedException or OperationCanceledException or TimeoutException;
    private void NotifyBatch()
    {
        OnPropertyChanged(nameof(BatchVisibility)); OnPropertyChanged(nameof(BatchRunning));
        foreach (var command in new[] { PrepareBatchRemoveCommand, CancelBatchCommand, RetryBatchCommand, CloseBatchCommand })
            (command as OperatorShellCommand)?.RaiseCanExecuteChanged();
        (ExecuteBatchCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ImportCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}
