# DockApps

DockApps makes registered Docker Compose applications behave like Windows applications.

## Build

```powershell
dotnet build DockApps.sln
dotnet publish src/DockApps.Launcher/DockApps.Launcher.csproj -c Release -r win-x64 --self-contained true
dotnet publish src/DockApps.Manager/DockApps.Manager.csproj -c Release -r win-x64 --self-contained true
```

The launcher executable is named `dockapps.exe`. Place it beside `DockApps.Manager.exe` and add that directory to PATH to use `dockapps <app-id>`.

Configuration is stored in `%LOCALAPPDATA%\DockApps\apps.json`.
