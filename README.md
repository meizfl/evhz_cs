# 🖱️ evhz_cs

A cross-platform CLI tool for measuring the **polling rate** (Hz) of input devices — mice, keyboards, gamepads — in real time.

Works on **Linux** (via `/dev/input/eventX`) and **Windows** (via `GetCursorPos` + `QueryPerformanceCounter`).

---

## Features

- 📊 Real-time Hz display per device — latest and rolling average
- 🐧 Linux: monitors all `/dev/input/eventX` devices simultaneously
- 🪟 Windows: tracks mouse movement with high-resolution timer
- 🔇 Non-verbose mode for clean output
- ✅ No external dependencies — pure .NET BCL

---

## Requirements

- [.NET 10](https://dotnet.microsoft.com/download)
- Linux: read access to `/dev/input/event*` (may require `sudo` or membership in the `input` group)
- Windows: no special permissions needed

---

## Build & Run

```bash
# Clone
git clone https://github.com/your-username/InputPollRateMonitor
cd InputPollRateMonitor

# Build
dotnet build -c Release

# Run
dotnet run
```

Or build a standalone executable:

```bash
# Linux
dotnet publish -c Release -r linux-x64 --self-contained true

# Windows
dotnet publish -c Release -r win-x64 --self-contained true
```

---

## Usage

```
InputPollRateMonitor [options]
```

| Option | Description |
|---|---|
| `-n`, `--nonverbose` | Show only final averages, suppress per-event output |
| `-h`, `--help` | Show help message |

Press **Ctrl+C** to stop monitoring and print final averages.

### Example output (verbose)

```
Monitoring 3 device(s). Move your mouse or press keys...

Logitech G Pro X: Latest   125 Hz, Average   124 Hz
Logitech G Pro X: Latest   126 Hz, Average   125 Hz
Logitech G Pro X: Latest   124 Hz, Average   124 Hz
^C
Stopping...

=== Final Averages ===
Average for Logitech G Pro X:   125 Hz
Average for AT Translated Set 2:   45 Hz
```

### Example output (non-verbose, `-n`)

```
Monitoring 3 device(s). Move your mouse or press keys...
^C
Stopping...

=== Final Averages ===
Average for Logitech G Pro X:   125 Hz
```

---

## How it works

### Linux

Reads raw events from `/dev/input/eventX` devices. Each `input_event` struct carries a kernel timestamp (`tv_sec` + `tv_usec`). When a relative (`EV_REL`) or absolute (`EV_ABS`) event arrives, the interval since the previous event is converted to Hz.

All detected devices are monitored in parallel on separate threads.

> **Permissions:** If you get "Could not open /dev/input/eventX", either run with `sudo` or add your user to the `input` group:
> ```bash
> sudo usermod -aG input $USER
> # log out and back in
> ```

### Windows

Polls the cursor position via `GetCursorPos` every 1 ms. When the position changes, `QueryPerformanceCounter` timestamps the event and computes Hz from the interval since the last movement. This approach works without requiring raw input registration or a message pump.

> ⚠️ The 1 ms polling loop means the measured Hz is capped at ~1000 Hz. For mice rated above 1000 Hz, use the Linux backend or a raw input approach.

### Rolling average

Each device maintains a circular buffer of the last 64 Hz samples. The displayed average is the mean of all samples collected so far (up to 64), so it stabilises after ~1 second of movement.

---

## Platform support

| Platform | Status |
|---|---|
| Linux | ✅ Supported |
| Windows | ✅ Supported |
| macOS | 🚧 Not yet implemented |

macOS contributions are welcome — see [Contributing](#contributing).

---

## Contributing

Pull requests are welcome. Some ideas for improvements:

- macOS support via IOKit HID events
- Windows raw input (`WM_INPUT`) for >1000 Hz mice
- Keyboard / gamepad detection on Windows
- JSON output mode

To contribute:

1. Fork the repo
2. Create a feature branch (`git checkout -b feature/macos-support`)
3. Commit your changes
4. Open a pull request

---

## License

MIT — see [LICENSE](LICENSE) for details.
