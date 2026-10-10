// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using rtaime.Client;

namespace rtaime.Operator;

public sealed class NdiSourceDiscoveryViewModel : INotifyPropertyChanged
{
    private readonly OperatorControlClient _client;
    private OperatorDiscoveredSourceDescriptor? _selectedSource;
    private string _status = "NDI discovery has not been refreshed.";
    private bool _isBusy;

    public NdiSourceDiscoveryViewModel(OperatorControlClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Sources = [];
        InputHealth = [];
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        AdoptCommand = new AsyncRelayCommand(AdoptSelectedAsync, () => !IsBusy && SelectedSource is not null);
    }

    public ObservableCollection<OperatorDiscoveredSourceDescriptor> Sources { get; }
    public ObservableCollection<OperatorMediaInputHealthDescriptor> InputHealth { get; }
    public ICommand RefreshCommand { get; }
    public ICommand AdoptCommand { get; }

    public OperatorDiscoveredSourceDescriptor? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (ReferenceEquals(_selectedSource, value))
                return;
            _selectedSource = value;
            OnPropertyChanged();
            if (AdoptCommand is AsyncRelayCommand command)
                command.RaiseCanExecuteChanged();
        }
    }

    public string Status
    {
        get => _status;
        private set
        {
            if (string.Equals(_status, value, StringComparison.Ordinal))
                return;
            _status = value;
            OnPropertyChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value)
                return;
            _isBusy = value;
            OnPropertyChanged();
            if (RefreshCommand is AsyncRelayCommand refresh) refresh.RaiseCanExecuteChanged();
            if (AdoptCommand is AsyncRelayCommand adopt) adopt.RaiseCanExecuteChanged();
        }
    }

    public async Task RefreshAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            var discovery = await _client.GetMediaSourceDiscoveryAsync().ConfigureAwait(true);
            Replace(Sources, discovery.Sources);
            var health = await _client.GetMediaInputHealthAsync().ConfigureAwait(true);
            Replace(InputHealth, health);
            Status = discovery.Availability == "AVAILABLE"
                ? $"{Sources.Count} NDI source(s) observed. Discovery does not change Preview or Program."
                : $"NDI discovery {discovery.Availability}: {discovery.Failure?.Message ?? "provider unavailable"}";
        }
        catch (Exception exception)
        {
            Status = $"NDI discovery unavailable: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AdoptSelectedAsync()
    {
        if (SelectedSource is null || IsBusy)
            return;
        var selected = SelectedSource;
        IsBusy = true;
        try
        {
            var result = await _client.AdoptMediaSourceAsync(selected.DiscoveredSourceId).ConfigureAwait(true);
            Status = result.AlreadyAdopted
                ? $"{result.Name} is already in the production source catalog."
                : $"{result.Name} adopted. Preview and Program remain unchanged.";
            var health = await _client.GetMediaInputHealthAsync().ConfigureAwait(true);
            Replace(InputHealth, health);
        }
        catch (Exception exception)
        {
            Status = $"NDI source adoption rejected: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
            target.Add(value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
