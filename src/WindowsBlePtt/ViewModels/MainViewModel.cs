using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using WindowsBlePtt.Ble;

namespace WindowsBlePtt.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly PttBleClient _client = new();
    private readonly Dispatcher _dispatcher;

    public ObservableCollection<DiscoveredViewModel> Discovered { get; }

    private DiscoveredViewModel? _selectedDevice;
    public DiscoveredViewModel? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (_selectedDevice == value) return;
            _selectedDevice = value;
            OnPropertyChanged();
            if (value is not null) _ = PairAsync(value);
        }
    }

    public bool HasDevicesOrScanning => IsScanning || Discovered.Count > 0;

    private string _statusHeadline = "No button paired";
    public string StatusHeadline
    {
        get => _statusHeadline;
        private set { _statusHeadline = value; OnPropertyChanged(); }
    }

    private string _statusSubline = "Tap Scan to find your BLE PTT button";
    public string StatusSubline
    {
        get => _statusSubline;
        private set { _statusSubline = value; OnPropertyChanged(); }
    }

    private bool _isTransmitting;
    public bool IsTransmitting
    {
        get => _isTransmitting;
        private set { _isTransmitting = value; OnPropertyChanged(); }
    }

    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            _isScanning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDevicesOrScanning));
        }
    }

    private bool _hasConnection;
    public bool HasConnection
    {
        get => _hasConnection;
        private set { _hasConnection = value; OnPropertyChanged(); }
    }

    private string? _connectedName;
    public string? ConnectedName
    {
        get => _connectedName;
        private set { _connectedName = value; OnPropertyChanged(); }
    }

    public MainViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;
        Discovered = new ObservableCollection<DiscoveredViewModel>();
        Discovered.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDevicesOrScanning));

        _client.DeviceDiscovered += OnDeviceDiscovered;
        _client.DiscoveredListCleared += () => RunOnUi(() => Discovered.Clear());
        _client.StateChanged += OnStateChanged;
        _client.PressedChanged += pressed => RunOnUi(() => IsTransmitting = pressed);
    }

    public void StartScan()
    {
        _client.StartScan();
        IsScanning = true;
        StatusHeadline = "Scanning…";
        StatusSubline = "Press your BLE PTT button to wake it";
    }

    public void StopScan()
    {
        _client.StopScan();
        IsScanning = false;
    }

    public async Task PairAsync(DiscoveredViewModel device)
    {
        IsScanning = false;
        await _client.ConnectAsync(device.Address, device.Name);
    }

    public async Task DisconnectAsync()
    {
        await _client.DisconnectAsync();
    }

    private void OnDeviceDiscovered(Discovered device)
    {
        RunOnUi(() =>
        {
            if (Discovered.Any(d => d.Address == device.Address)) return;
            Discovered.Add(new DiscoveredViewModel(
                Address: device.Address,
                Name: device.Name ?? "Unknown PTT",
                Rssi: device.Rssi));
        });
    }

    private void OnStateChanged(ConnectionState state)
    {
        RunOnUi(() =>
        {
            switch (state)
            {
                case ConnectionState.IdleState:
                    StatusHeadline = "Idle";
                    StatusSubline = "Tap Scan to find your BLE PTT button";
                    HasConnection = false;
                    ConnectedName = null;
                    IsScanning = false;
                    break;
                case ConnectionState.ScanningState:
                    StatusHeadline = "Scanning…";
                    StatusSubline = "Press your BLE PTT button to wake it";
                    IsScanning = true;
                    break;
                case ConnectionState.ConnectingState:
                    StatusHeadline = "Connecting…";
                    StatusSubline = string.Empty;
                    HasConnection = false;
                    IsScanning = false;
                    break;
                case ConnectionState.ConnectedState connected:
                    StatusHeadline = IsTransmitting ? "Transmitting" : "Idle";
                    StatusSubline = $"Connected to {connected.Name ?? FormatAddress(connected.Address)}";
                    HasConnection = true;
                    ConnectedName = connected.Name ?? FormatAddress(connected.Address);
                    IsScanning = false;
                    break;
                case ConnectionState.DisconnectedState disc:
                    StatusHeadline = "Disconnected";
                    StatusSubline = $"Waiting for next press to reconnect · {disc.Reason}";
                    HasConnection = false;
                    IsScanning = false;
                    break;
                case ConnectionState.ErrorState err:
                    StatusHeadline = "Error";
                    StatusSubline = err.Message;
                    HasConnection = false;
                    IsScanning = false;
                    break;
            }
        });
    }

    private static string FormatAddress(ulong address)
    {
        var bytes = BitConverter.GetBytes(address).Take(6).Reverse().ToArray();
        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record DiscoveredViewModel(ulong Address, string Name, short Rssi)
{
    public string FormattedAddress
    {
        get
        {
            var bytes = BitConverter.GetBytes(Address).Take(6).Reverse().ToArray();
            return string.Join(":", bytes.Select(b => b.ToString("X2")));
        }
    }
    public string SubtitleText => $"{FormattedAddress} · {Rssi} dBm";
}
