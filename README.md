# Windows BLE PTT (PoC)

Proof of concept for talking to "Zello-style" Bluetooth LE push-to-talk buttons (Focket PTT-Z01, PRYME BT-PTT-Z, and the broader HM-10 / TI CC254x family) from Windows.

Sibling to the Android PoC at [`dannymcc/android-ble-ptt`](https://github.com/dannymcc/android-ble-ptt) and matches its dark VoxDMR-styled look so a desktop developer can map the two side-by-side. Built to unblock [`voxdmr-site#9`](https://github.com/jcalado/voxdmr-site/issues/9).

## Get the build

Self-contained single-file Windows x64 exe — no .NET install required, just run it.

- **[Releases → Latest build (main)](../../releases/tag/latest-main)** — rolling build from every push to `main`
- Tagged versions (`v0.1.0` etc.) also publish to Releases

Windows SmartScreen will warn on first launch because the exe isn't code-signed. *More info → Run anyway*.

## What it does

- **Scan** for BLE PTT buttons advertising service `0xFFE0` or with a name beginning `PTT`
- **Pair** the one you pick. The pair flow drops the discovered list after pairing so old buttons don't linger in the next scan.
- **TX indicator** goes coral while the button is held, dark on release
- **Auto-reconnect** — Windows has no direct equivalent of Android's `connectGatt(autoConnect=true)`, so the PoC starts a fresh `BluetoothLEAdvertisementWatcher` filtered on the target's BD_ADDR. When the button advertises again after the next physical press, the watcher fires, the client re-opens the connection, and the press round-trips.

## BLE protocol it expects

| Attribute                   | UUID                                | Properties        | Purpose                                            |
| --------------------------- | ----------------------------------- | ----------------- | -------------------------------------------------- |
| Button service              | `0xFFE0`                            | Primary service   | Identifies the PTT button                          |
| Button state characteristic | `0xFFE1`                            | **Notify**, Write | Notifies `0x01` on press, `0x00` on release        |
| CCCD on `0xFFE1`            | `0x2902`                            | —                 | Enabled in WinRT via `WriteClientCharacteristicConfigurationDescriptorAsync(Notify)` |

`0xFFE0` / `0xFFE1` is the classic HM-10 / TI CC254x "transparent UART" BLE profile. The community DIY [`oryjkov/100pct-ptt`](https://github.com/oryjkov/100pct-ptt) uses the same UUIDs and `0x01` / `0x00` encoding.

## Where the BLE code lives

[`src/WindowsBlePtt/Ble/PttBleClient.cs`](src/WindowsBlePtt/Ble/PttBleClient.cs) is the bit a VoxDMR developer cares about. It wraps:

- `BluetoothLEAdvertisementWatcher` for scanning (with a `Timestamp >= scanStart` filter to drop replayed cached adverts)
- `BluetoothLEDevice.FromBluetoothAddressAsync` for the central-side connect
- `GattDeviceService.GetCharacteristicsForUuidAsync` → `GattCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(Notify)` to enable notifications
- `GattCharacteristic.ValueChanged` to surface press / release events
- A dedicated `BluetoothLEAdvertisementWatcher` filtered on the target BD_ADDR for the auto-reconnect path

The C# events (`StateChanged`, `PressedChanged`, `DeviceDiscovered`) are intentionally a thin shape — easy to consume from whatever event/observable pattern VoxDMR already uses.

## Build it yourself

```sh
dotnet build windows-ble-ptt.sln -c Release
dotnet publish src/WindowsBlePtt/WindowsBlePtt.csproj -c Release -r win-x64 --self-contained true -o publish /p:PublishSingleFile=true
```

Requires .NET 8 SDK. The project targets `net8.0-windows10.0.19041.0` so the WinRT Bluetooth namespaces are in reach.

## Permissions

Windows asks for Bluetooth access on first launch via the standard OS prompt — no manifest entry needed for a desktop app. The user grants once; subsequent launches don't prompt again.

## License

MIT
