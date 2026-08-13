# KeychronMicMute

A tiny Windows helper plus QMK keymap for a reliable, system-wide microphone mute key with a hardware RGB indicator on a **Keychron K1 Max ANSI RGB**.

## How it works

```text
K1 Max key -> F24 -> KeychronMicMute.exe -> Windows Core Audio mute
                                      |
                                      +-> Scroll Lock state -> QMK -> red LEDs
```

Core Audio is the source of truth. The helper listens for mute changes made by other software and mirrors the resulting state back to the keyboard through the standard Scroll Lock host LED bit. This works over USB, Bluetooth and 2.4 GHz because Keychron's wireless firmware transports the standard keyboard LED state.

## Windows helper

- Registers global **F24** with `RegisterHotKey` (no keyboard hook).
- Tracks default **Console** and **Communications** capture endpoints and deduplicates them when they are the same device.
- One F24 press mutes both endpoints; the next unmutes both.
- Subscribes to Core Audio endpoint-volume notifications, so external mute changes are reflected on the keyboard.
- Rebinds when audio devices/defaults change.
- Mirrors effective mute to Scroll Lock (`muted = on`).
- Single-instance, no console window, no NuGet dependencies.
- Logs to `%LOCALAPPDATA%\KeychronMicMute\helper.log` (1 MB rotation).

> Scroll Lock is intentionally used as the host-to-keyboard status bit. This means Scroll Lock is genuinely ON while the microphone is muted and can affect applications such as Excel.

### Build

Requires the .NET 8 SDK on Windows.

```powershell
dotnet build src\KeychronMicMute\KeychronMicMute.csproj -c Release
```

### Install / autostart

From PowerShell in the repository root:

```powershell
.\scripts\install.ps1
```

The script publishes a framework-dependent single-file `win-x64` executable, installs it under `%LOCALAPPDATA%\KeychronMicMute`, registers it in the current user's `Run` key, and starts it. No administrator rights are required.

Remove it with:

```powershell
.\scripts\uninstall.ps1
```

## QMK firmware: K1 Max ANSI RGB

The `qmk/k1-max-ansi-rgb` directory is based on Keychron's current K1 Max ANSI RGB keymap from the **`2025q3`** branch of `Keychron/qmk_firmware` (firmware device version 1.1.1).

The firmware customization is intentionally minimal:

- Former Cortana/microphone key on the Windows base layer: **F24** (microphone mute trigger).
- When Scroll Lock is on, the six LEDs around that key are forced to full red: indices **13, 14, 15, 30, 31, 32**. Otherwise the current RGB effect shows through.
- Debounce, sleep behavior, Per-Key RGB, Mix RGB and the rest of Keychron's firmware behavior are left at the `2025q3` defaults.

To build it, clone Keychron's QMK tree and switch to the current firmware branch:

```bash
git clone https://github.com/Keychron/qmk_firmware.git
cd qmk_firmware
git checkout 2025q3
```

Copy `qmk/k1-max-ansi-rgb/keymap.c` and `qmk/k1-max-ansi-rgb/rules.mk` from this repository into a custom keymap directory:

```text
keyboards/keychron/k1_max/ansi/rgb/keymaps/micmute/
```

Then compile:

```bash
qmk compile -kb keychron/k1_max/ansi/rgb -km micmute
```

The resulting build keeps Keychron's 1.1.1-era Launcher features because it uses the `2025q3` K1 Max implementation, including Keychron RGB support and current wireless configuration support.

Flashing custom keyboard firmware always carries risk; keep an official firmware image available for recovery.

## Why not a vendor/raw HID channel?

A custom HID status report would avoid Scroll Lock side effects, but the standard host LED state already has a transport path in USB, Bluetooth and 2.4 GHz, so Scroll Lock is deliberately used as a one-bit status channel.

## License

MIT.
