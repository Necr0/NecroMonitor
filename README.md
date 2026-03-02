# NecroMonitor ☠

A lightweight always-on-top desktop widget that displays real-time **CPU and GPU temperatures** and load percentages. Designed to be game-friendly — uses GDI+ rendering (no DirectX/Vulkan hooks), so it won't conflict with any game overlays.

<img width="235" height="120" alt="image" src="https://github.com/user-attachments/assets/85a63ac2-98ca-4c75-8dc8-246d4e33cc8b" />


## Features

- **CPU & GPU temps** — color-coded: 🟢 green (<60°C), 🟡 yellow (60-80°C), 🔴 red (>80°C)
- **CPU & GPU load bars** — real-time utilization percentage
- **Always on top** — stays visible over borderless-fullscreen games
- **No overlay conflicts** — pure GDI+ rendering, no DirectX hooks
- **Hidden from Alt+Tab** — doesn't clutter your task switcher
- **Never steals focus** — `WS_EX_NOACTIVATE` window style
- **Draggable** — click and drag anywhere to reposition
- **Tiny footprint** — minimal CPU/RAM usage (~15-25 MB)

## Requirements

- Windows 10/11
- [.NET 9 Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)
- **Run as Administrator** (required for hardware sensor access via LibreHardwareMonitor)

## Build & Run

```bash
cd NecroMonitor
dotnet build
dotnet run
```

Or publish a self-contained single-file executable:

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The output will be in `bin/Release/net9.0-windows/win-x64/publish/NecroMonitor.exe`.

> **Note:** The app must be run as Administrator to read hardware sensors. The included `app.manifest` handles the UAC elevation prompt automatically.

## Usage

| Action | Result |
|---|---|
| **Left-click + drag** | Move the widget |
| **Right-click** | Context menu (Reset position / Exit) |

## How It Works

- Uses [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) for cross-vendor sensor access (Intel, AMD, NVIDIA)
- WinForms with owner-drawn GDI+ — zero GPU acceleration means zero interference with games
- Periodically re-asserts `HWND_TOPMOST` so it stays visible even over borderless-fullscreen windows
- `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` flags keep it out of the taskbar and prevent focus stealing

## License

MIT
