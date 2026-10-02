# DockApps

DockApps gives Docker Compose projects a Windows app-style launcher. Register a Compose project once, then start it from the manager, a shortcut, or the command line.

## What it does

- Starts Docker Desktop when a registered app needs it.
- Starts and stops a Compose project and reports container state and health.
- Opens the app URL after startup when you configure one.
- Shows service logs and lets you refresh the current Docker state.
- Creates Start Menu, desktop, and manager shortcuts.
- Tracks whether DockApps started the current Docker session before it attempts an automatic stop.

DockApps does not delete Docker images, volumes, or other Docker data. The manager includes separate Soft Stop and Hard Stop actions; review the confirmation message before using Hard Stop.

## Requirements

- Windows
- .NET 8 SDK
- Docker Desktop with the Docker CLI available

The projects target `net8.0-windows` and build for `win-x64`.

## Build

From the repository root:

```powershell
dotnet restore DockApps.sln
dotnet build DockApps.sln
dotnet test DockApps.sln
```

To publish self-contained Windows binaries:

```powershell
dotnet publish src/DockApps.Launcher/DockApps.Launcher.csproj `
  -c Release -r win-x64 --self-contained true

dotnet publish src/DockApps.Manager/DockApps.Manager.csproj `
  -c Release -r win-x64 --self-contained true
```

Copy the published `dockapps.exe` and `DockApps.Manager.exe` into the same directory. Add that directory to `PATH` if you want to invoke DockApps from PowerShell or Command Prompt.

## Register an app

1. Start `DockApps.Manager.exe`.
2. Select **Register App**.
3. Enter an app name and ID.
4. Choose the Compose file and enter its Compose project name.
5. Optionally add a URL, icon, startup timeout, and stop timeout.
6. Save the registration.

The ID becomes the command-line name. For example, an app registered with the ID `paperless` starts with:

```powershell
dockapps paperless
```

Open the manager from the command line with:

```powershell
dockapps --manager
```

The launcher starts the manager when needed, sends the request over a local named pipe, and exits after the manager accepts it.

## Configuration and state

DockApps stores local state here:

```text
%LOCALAPPDATA%\DockApps\apps.json
%LOCALAPPDATA%\DockApps\state.json
```

`apps.json` contains registered applications. `state.json` records the Docker session that DockApps started so the manager can avoid stopping a session it does not own.

## Project layout

| Project | Purpose |
| --- | --- |
| `src/DockApps.Core` | Docker, Compose, registry, lifecycle, shortcut, and IPC services |
| `src/DockApps.Manager` | WPF manager and app detail windows |
| `src/DockApps.Launcher` | Command-line entry point and manager activation |
| `tests/DockApps.Core.Tests` | Core service and lifecycle tests |

## License

See [LICENSE](LICENSE).
