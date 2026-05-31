using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace WindowsBlePtt.Ble;

/// <summary>
/// WinRT-based BLE central for "Zello-style" PTT buttons using the HM-10 / TI CC254x
/// transparent-UART profile (service 0xFFE0, characteristic 0xFFE1). Notify byte 0:
/// 0x01 = pressed, 0x00 = released. Verified against the Focket PTT-Z01.
/// </summary>
public sealed class PttBleClient : IAsyncDisposable
{
    public static readonly Guid ServiceUuid = new("0000ffe0-0000-1000-8000-00805f9b34fb");
    public static readonly Guid CharacteristicUuid = new("0000ffe1-0000-1000-8000-00805f9b34fb");

    public event Action<ConnectionState>? StateChanged;
    public event Action<bool>? PressedChanged;
    public event Action<bool>? ScanningChanged;
    public event Action<Discovered>? DeviceDiscovered;
    public event Action? DiscoveredListCleared;

    private BluetoothLEAdvertisementWatcher? _scanWatcher;
    private BluetoothLEAdvertisementWatcher? _reconnectWatcher;
    private BluetoothLEDevice? _device;
    private GattCharacteristic? _characteristic;
    private DateTimeOffset _scanStartedAt;
    private readonly ConcurrentDictionary<ulong, byte> _seen = new();
    private ulong _targetAddress;
    private string? _targetName;
    private bool _autoReconnectEnabled;

    public ulong CurrentTarget => _targetAddress;
    public ConnectionState State { get; private set; } = ConnectionState.Idle;
    public bool Pressed { get; private set; }
    public bool IsScanning { get; private set; }

    public void StartScan()
    {
        StopScan();
        _seen.Clear();
        DiscoveredListCleared?.Invoke();
        _scanStartedAt = DateTimeOffset.UtcNow;

        _scanWatcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };
        _scanWatcher.Received += OnAdvertReceivedForScan;
        _scanWatcher.Stopped += OnScanStopped;
        _scanWatcher.Start();
        SetScanning(true);
    }

    public void StopScan()
    {
        if (_scanWatcher is null)
        {
            SetScanning(false);
            return;
        }
        _scanWatcher.Received -= OnAdvertReceivedForScan;
        _scanWatcher.Stopped -= OnScanStopped;
        try { _scanWatcher.Stop(); } catch { }
        _scanWatcher = null;
        _seen.Clear();
        DiscoveredListCleared?.Invoke();
        SetScanning(false);
    }

    private void SetScanning(bool scanning)
    {
        if (IsScanning == scanning) return;
        IsScanning = scanning;
        ScanningChanged?.Invoke(scanning);
    }

    private void OnAdvertReceivedForScan(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        // Filter out advertisements observed before we started this scan session — WinRT can
        // surface cached results when the watcher kicks off.
        if (args.Timestamp < _scanStartedAt) return;

        var name = args.Advertisement.LocalName;
        var hasService = args.Advertisement.ServiceUuids.Any(g => g == ServiceUuid);
        var hasMatchingName = !string.IsNullOrEmpty(name) &&
            name.StartsWith("PTT", StringComparison.OrdinalIgnoreCase);
        if (!hasService && !hasMatchingName) return;

        if (!_seen.TryAdd(args.BluetoothAddress, 1)) return;

        DeviceDiscovered?.Invoke(new Discovered(
            Address: args.BluetoothAddress,
            Name: string.IsNullOrEmpty(name) ? null : name,
            Rssi: args.RawSignalStrengthInDBm));
    }

    private void OnScanStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        // OS-side scan ended; usually because of Bluetooth being turned off or because we asked.
    }

    public async Task ConnectAsync(ulong bluetoothAddress, string? name)
    {
        _autoReconnectEnabled = true;
        _targetAddress = bluetoothAddress;
        _targetName = name;
        StopScan();
        StopReconnectWatcher();
        UpdateState(ConnectionState.Connecting);

        await OpenConnectionAsync(bluetoothAddress);
    }

    private async Task OpenConnectionAsync(ulong bluetoothAddress)
    {
        try
        {
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress);
            if (_device is null)
            {
                UpdateState(ConnectionState.Error("Device not in range"));
                ArmReconnect();
                return;
            }
            _device.ConnectionStatusChanged += OnConnectionStatusChanged;

            var servicesResult = await _device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached);
            if (servicesResult.Status != GattCommunicationStatus.Success)
            {
                UpdateState(ConnectionState.Error($"Service discovery failed: {servicesResult.Status}"));
                ArmReconnect();
                return;
            }
            var service = servicesResult.Services.FirstOrDefault();
            if (service is null)
            {
                UpdateState(ConnectionState.Error("PTT service 0xFFE0 not found"));
                ArmReconnect();
                return;
            }

            var charsResult = await service.GetCharacteristicsForUuidAsync(CharacteristicUuid);
            if (charsResult.Status != GattCommunicationStatus.Success)
            {
                UpdateState(ConnectionState.Error($"Characteristic discovery failed: {charsResult.Status}"));
                ArmReconnect();
                return;
            }
            _characteristic = charsResult.Characteristics.FirstOrDefault();
            if (_characteristic is null)
            {
                UpdateState(ConnectionState.Error("PTT characteristic 0xFFE1 not found"));
                ArmReconnect();
                return;
            }

            var cccdStatus = await _characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (cccdStatus != GattCommunicationStatus.Success)
            {
                UpdateState(ConnectionState.Error($"CCCD write failed: {cccdStatus}"));
                ArmReconnect();
                return;
            }

            _characteristic.ValueChanged += OnValueChanged;
            UpdateState(ConnectionState.Connected(bluetoothAddress, _targetName));
        }
        catch (Exception ex)
        {
            UpdateState(ConnectionState.Error(ex.Message));
            ArmReconnect();
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            SetPressed(false);
            UpdateState(ConnectionState.Disconnected("connection lost"));
            if (_autoReconnectEnabled) ArmReconnect();
        }
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var reader = DataReader.FromBuffer(args.CharacteristicValue);
        var bytes = new byte[args.CharacteristicValue.Length];
        reader.ReadBytes(bytes);
        if (bytes.Length == 0) return;

        switch (bytes[0])
        {
            case 0x01: SetPressed(true); break;
            case 0x00: SetPressed(false); break;
        }
    }

    /// <summary>
    /// Windows BLE has no equivalent of Android's connectGatt(autoConnect=true). The closest
    /// pattern is to start a fresh advertisement watcher filtered on the target's BD_ADDR;
    /// when the button advertises again after the next physical press, we drop the watcher
    /// and re-open the connection.
    /// </summary>
    private void ArmReconnect()
    {
        if (!_autoReconnectEnabled || _targetAddress == 0) return;
        StopReconnectWatcher();

        var target = _targetAddress;
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };
        watcher.Received += async (s, args) =>
        {
            if (args.BluetoothAddress != target) return;
            StopReconnectWatcher();
            UpdateState(ConnectionState.Connecting);
            await OpenConnectionAsync(target);
        };
        _reconnectWatcher = watcher;
        watcher.Start();
    }

    private void StopReconnectWatcher()
    {
        if (_reconnectWatcher is null) return;
        try { _reconnectWatcher.Stop(); } catch { }
        _reconnectWatcher = null;
    }

    public async Task DisconnectAsync()
    {
        _autoReconnectEnabled = false;
        _targetAddress = 0;
        _targetName = null;
        StopReconnectWatcher();
        await TearDownConnectionAsync();
        UpdateState(ConnectionState.Idle);
    }

    private async Task TearDownConnectionAsync()
    {
        if (_characteristic is not null)
        {
            _characteristic.ValueChanged -= OnValueChanged;
            try
            {
                await _characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch { }
            _characteristic = null;
        }
        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _device.Dispose();
            _device = null;
        }
        SetPressed(false);
    }

    private void UpdateState(ConnectionState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    private void SetPressed(bool pressed)
    {
        if (Pressed == pressed) return;
        Pressed = pressed;
        PressedChanged?.Invoke(pressed);
    }

    public async ValueTask DisposeAsync()
    {
        _autoReconnectEnabled = false;
        StopScan();
        StopReconnectWatcher();
        await TearDownConnectionAsync();
    }
}

public sealed record Discovered(ulong Address, string? Name, short Rssi);

public abstract record ConnectionState
{
    public sealed record IdleState : ConnectionState;
    public sealed record ConnectingState : ConnectionState;
    public sealed record ConnectedState(ulong Address, string? Name) : ConnectionState;
    public sealed record DisconnectedState(string Reason) : ConnectionState;
    public sealed record ErrorState(string Message) : ConnectionState;

    public static ConnectionState Idle => new IdleState();
    public static ConnectionState Connecting => new ConnectingState();
    public static ConnectionState Connected(ulong address, string? name) => new ConnectedState(address, name);
    public static ConnectionState Disconnected(string reason) => new DisconnectedState(reason);
    public static ConnectionState Error(string message) => new ErrorState(message);
}
