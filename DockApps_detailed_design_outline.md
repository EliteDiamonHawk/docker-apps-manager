# DockApps — Detailed Application Design Outline



# 57. Core Domain Models

Keep Docker/Compose state separate from WPF-specific view state.

Suggested models:

```text
RegisteredApp
DockerDesktopStatus
DockerSessionState
ComposeProjectStatus
ContainerStatus
AppRuntimeStatus
LaunchRequest
ManagerRequest
OperationResult
```

Example `RegisteredApp`:

```csharp
public sealed class RegisteredApp
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ComposeFile { get; init; }
    public required string ProjectName { get; init; }

    public string? Url { get; init; }
    public string? IconPath { get; init; }

    public bool OpenBrowser { get; init; } = true;
    public int StartupTimeoutSeconds { get; init; } = 60;
    public int StopTimeoutSeconds { get; init; } = 30;
}
```

The exact implementation may evolve, but the core model should remain independent of WPF.

---

# 58. Core Service Interfaces

Define interfaces around external behavior so Docker logic can be tested without running Docker.

Suggested interfaces:

```text
IDockerDesktopService
IDockerContainerService
IComposeService
IAppRegistry
IDockerOwnershipService
IShortcutService
IBrowserService
IProcessRunner
IAppLifecycleService
```

Example:

```csharp
public interface IDockerDesktopService
{
    Task<DockerDesktopStatus> GetStatusAsync(
        CancellationToken cancellationToken = default);

    Task<OperationResult> StartAsync(
        CancellationToken cancellationToken = default);

    Task<OperationResult> StopAsync(
        bool force = false,
        CancellationToken cancellationToken = default);
}
```

This prevents Docker process invocation logic from leaking into views or view models.

---

# 59. Process Runner Abstraction

All external command execution should pass through one process service.

For example:

```text
IProcessRunner
```

Responsibilities:

- execute `docker`
- execute `docker compose`
- execute Docker Desktop CLI
- capture stdout
- capture stderr
- expose exit code
- support cancellation
- enforce operation timeout
- prevent shell injection
- optionally stream log output

Do not build shell command strings when arguments can be passed separately.

Prefer:

```text
FileName = "docker.exe"
Arguments = [
    "compose",
    "-f",
    composeFile,
    "-p",
    projectName,
    "up",
    "-d"
]
```

rather than constructing an unescaped command line manually.

---

# 60. Docker Desktop Service

`DockerDesktopService` owns Docker Desktop lifecycle control.

Responsibilities:

```text
GetStatusAsync()
StartAsync()
StopAsync()
WaitUntilReadyAsync()
GetSessionIdentityAsync()
```

The service should distinguish between:

```text
Docker Desktop process running
```

and:

```text
Docker Engine ready to accept commands
```

Docker Desktop may be visible/running while the engine is still starting.

---

# 61. Compose Service

`ComposeService` controls registered applications.

Suggested operations:

```text
GetProjectStatusAsync(app)
StartProjectAsync(app)
StopProjectAsync(app)
RestartProjectAsync(app)
GetLogsAsync(app)
```

The service should always use:

```text
-f <composeFile>
-p <projectName>
```

to avoid accidental project ambiguity.

---

# 62. Container Service

`DockerContainerService` inspects the entire Docker environment.

Responsibilities:

```text
ListRunningContainersAsync()
ListAllContainersAsync()
ClassifyContainers()
```

Each container model should retain enough metadata to determine:

- container ID
- name
- state
- health
- Compose project label
- Compose service label
- image
- exit code where available

---

# 63. App Lifecycle Service

`AppLifecycleService` coordinates Docker and Compose rather than implementing raw process calls.

For example:

```text
OpenAppAsync(appId)
StopAppAsync(appId)
SoftStopAsync()
HardStopAsync()
```

This should contain the business rules discussed throughout this specification.

The WPF UI should call this service rather than directly invoking Docker.

---

# 64. `OpenAppAsync` Detailed Algorithm

Suggested logic:

```text
Resolve app ID
    ↓
App exists?
├── No → return AppNotRegistered
└── Yes
     ↓
Open/focus app window
     ↓
Determine Docker status
     ↓
Docker ready?
├── Yes → continue
└── No
     ↓
   Was Docker already in the process of starting?
   ├── Yes → await shared startup task
   └── No
        ↓
      begin Docker startup
        ↓
      record whether DockApps initiated startup
        ↓
      wait for engine readiness
     ↓
Query Compose project
     ↓
Already running/healthy?
├── Yes → do not restart
└── No
     ↓
   Start project
     ↓
   wait for readiness
     ↓
Refresh app status
     ↓
Open URL if configured
     ↓
Return success/partial/error result
```

---

# 65. Shared Docker Startup Task

Docker startup should be deduplicated.

The manager can maintain:

```csharp
Task<OperationResult>? _dockerStartupTask;
```

When the first app needs Docker:

```text
create startup task
```

When another app arrives during startup:

```text
await the same startup task
```

After completion:

```text
clear startup task reference
```

Protect the reference with appropriate synchronization.

---

# 66. App Operation Deduplication

The same app should not be started or stopped concurrently by multiple requests.

Maintain a per-app operation lock.

Conceptually:

```text
firefly lock
homepage lock
actual lock
```

Example:

```text
OPEN_APP firefly
OPEN_APP firefly
```

The second request should:

- focus the existing window
- await the existing start operation if one is already active
- not start another Compose process

---

# 67. Global Docker Lifecycle Lock

Docker Desktop start/stop is global.

Use an async synchronization primitive around:

```text
start Docker
stop Docker
ownership update
hard stop
```

This prevents situations such as:

```text
Firefly is starting Docker
```

while:

```text
Hard Stop is simultaneously trying to stop Docker
```

The manager should serialize incompatible Docker lifecycle transitions.

---

# 68. Request Queue

Named-pipe requests should be accepted quickly and placed onto a manager request queue.

Conceptually:

```text
Named Pipe Server
      ↓
parse request
      ↓
validate request
      ↓
enqueue ManagerRequest
      ↓
return acknowledgement
```

The request processor then handles it asynchronously.

This keeps IPC responsive even when Docker operations take several seconds.

---

# 69. IPC Message Format

JSON is sufficient.

Example open request:

```json
{
  "version": 1,
  "type": "open-app",
  "appId": "firefly",
  "requestId": "..."
}
```

Manager response:

```json
{
  "version": 1,
  "accepted": true,
  "requestId": "..."
}
```

The launcher only needs confirmation that the manager accepted the request.

It does not need to remain running until Docker finishes.

---

# 70. IPC Versioning

Include a protocol version from the beginning.

Example:

```json
{
  "version": 1
}
```

If launcher and manager versions become incompatible later, the manager can return a clear error instead of silently misinterpreting messages.

---

# 71. Launcher Responsibilities in Detail

The launcher should remain intentionally minimal.

Responsibilities:

1. Parse the app ID or manager request passed by the shortcut.
2. Validate that some request was supplied.
3. Attempt to contact the manager.
4. If unavailable:
   - start manager
   - wait for named-pipe readiness
5. Send request.
6. Receive acknowledgement.
7. Exit.

The launcher should not:

- inspect containers
- modify state files
- start Docker directly
- stop Docker
- parse Compose files
- show the full management UI

---

# 72. Launcher Timeout Behavior

The launcher should not wait forever for Manager startup.

Example:

```text
Start Manager
    ↓
Wait up to 10 seconds for IPC pipe
```

If unavailable:

```text
show small native error dialog
```

Example:

```text
DockApps Manager could not be started.

[ OK ]
```

No terminal window should appear.

---

# 73. Manager Startup Modes

The manager may receive an initial startup request.

Examples:

```text
DockApps.Manager.exe --initial-app firefly
```

or the launcher may always send requests through IPC.

Prefer keeping Manager startup generic and using IPC for requests so only one pathway needs to be maintained.

---

# 74. App Window ViewModel

Suggested `AppWindowViewModel` properties:

```text
AppName
DockerStatus
AppStatus
Services
Url
IsBusy
CanOpen
CanStop
StatusMessage
ErrorMessage
```

Commands:

```text
OpenCommand
StopCommand
RefreshCommand
ViewLogsCommand
CloseCommand
```

The view model should subscribe to operation/status updates from the lifecycle layer.

---

# 75. Manager Window ViewModel

Suggested properties:

```text
DockerStatus
DockerOwnershipStatus
RegisteredApps
UnregisteredContainers
IsRefreshing
CanSoftStop
CanHardStop
StatusMessage
```

Commands:

```text
RefreshCommand
OpenAppCommand
StopAppCommand
SoftStopCommand
HardStopCommand
RegisterAppCommand
EditAppCommand
RemoveAppCommand
```

---

# 76. WPF Architecture

Use MVVM.

Recommended separation:

```text
View
    ↓
ViewModel
    ↓
Application services
    ↓
Core services
    ↓
Docker/Windows
```

Views should contain minimal code-behind.

Code-behind is acceptable for purely visual/window-specific behaviors, but lifecycle rules should not live there.

---

# 77. Dependency Injection

Use .NET dependency injection in the manager.

Example services:

```text
IProcessRunner
IDockerDesktopService
IComposeService
IDockerContainerService
IAppRegistry
IAppLifecycleService
IDockerOwnershipService
IShortcutService
IBrowserService
```

This makes testing and future replacement easier.

---

# 78. Async Cancellation

Long-running operations should be cancellable when safe.

Examples:

```text
waiting for Docker Desktop
waiting for health checks
reading logs
waiting for Compose startup
```

Use `CancellationToken`.

Do not attempt to cancel an operation in a way that leaves Docker state corrupted.

The UI may expose Cancel only for operations where cancellation semantics are clear.

---

# 79. Manager Idle-Shutdown Coordinator

Create a dedicated service:

```text
IIdleShutdownCoordinator
```

It tracks:

```text
open windows
active requests
active operations
dialogs
```

When all counts reach zero:

```text
start idle timer
```

When new work arrives:

```text
cancel idle timer
```

When timer expires while still idle:

```text
request WPF application shutdown
```

---

# 80. Window Registration

The manager should maintain a window registry.

Conceptually:

```text
Dictionary<string, WeakReference<AppWindow>>
```

or another suitable structure.

Operations:

```text
ShowAppWindow(appId)
FocusAppWindow(appId)
CloseAppWindow(appId)
IsAppWindowOpen(appId)
```

This enforces one GUI window per app.

---

# 81. Global Window Singleton

Like app windows, only one global Manager window should exist.

If the Manager shortcut is clicked repeatedly:

```text
existing manager window → activate/focus
```

Do not create duplicates.

---

# 82. Auto-Close App Window

Per-app configuration may specify:

```text
autoCloseAfterSuccessfulStart = true
```

Behavior:

```text
app reaches ready state
    ↓
open browser
    ↓
show success briefly
    ↓
close app window
```

If startup fails:

```text
do NOT auto-close
```

The window should remain so the user can inspect the error.

---

# 83. App Shortcut Behavior When Already Running

Clicking an app shortcut should remain useful even when the app is already running.

Flow:

```text
shortcut clicked
    ↓
Manager checks app
    ↓
already ready
    ↓
show/focus app window
    ↓
open configured URL
```

No container restart should occur.

---

# 84. Optional Launch Behavior Setting

Some apps may not have browser URLs.

Allow launch behavior such as:

```text
OpenBrowser
ShowStatusOnly
OpenFolder
CustomExecutable
```

Version 1 only needs browser URLs, but the model should not make browser-only behavior impossible to extend later.

---

# 85. Soft Stop Detailed Algorithm

```text
Acquire global Docker lifecycle lock
    ↓
Load registered apps
    ↓
Query which registered projects are running
    ↓
Stop each running registered project
    ↓
Collect failures
    ↓
Refresh all running containers
    ↓
Any container running?
├── Yes
│    ↓
│  leave Docker running
│
└── No
     ↓
   Validate Docker ownership
     ↓
   DockApps-owned?
   ├── Yes → stop Docker Desktop
   └── No  → leave Docker running
    ↓
Update state
    ↓
Return summary
```

If one registered app fails to stop:

```text
continue attempting to stop other registered apps
```

Then show a summary of failures.

---

# 86. Hard Stop Detailed Algorithm

```text
Acquire global Docker lifecycle lock
    ↓
Confirm operation
    ↓
Stop all registered projects
    ↓
Query remaining running containers
    ↓
Stop remaining containers
    ↓
Attempt normal Docker Desktop shutdown
    ↓
If normal shutdown fails
    ↓
offer/use force shutdown behavior
    ↓
Clear DockApps ownership/session state
    ↓
Refresh GUI
    ↓
Exit manager if no windows remain
```

Hard Stop should never automatically delete Docker resources.

---

# 87. Hard Stop Safety Boundary

Hard Stop may stop:

```text
running containers
Docker Desktop
```

Hard Stop must not perform:

```text
docker system prune
docker volume prune
docker image prune
docker compose down -v
```

unless a completely separate future destructive feature is explicitly introduced.

---

# 88. App Removal From Registry

Removing an app from DockApps should **not** delete its Docker resources or Compose files.

Flow:

```text
Remove Firefly from registry?
```

Confirmation should explain:

```text
This removes Firefly from DockApps only.
Its Compose files and Docker data will not be deleted.
```

Optionally remove the generated shortcut.

---

# 89. Registry Validation

On manager startup or registry load, validate entries.

Possible problems:

```text
Compose file missing
Duplicate app ID
Duplicate project name
Invalid URL
Icon missing
Malformed config
```

Do not crash the entire manager because one app entry is invalid.

Mark that app:

```text
Configuration Error
```

and expose a repair/edit action.

---

# 90. Atomic Configuration Writes

Registry and state files should be written atomically.

Suggested pattern:

```text
write temporary file
    ↓
flush
    ↓
replace original
```

This reduces corruption risk if DockApps or Windows closes during a write.

---

# 91. State Recovery

`state.json` is advisory metadata, not the source of truth.

If it is:

- missing
- corrupt
- stale

DockApps should recover conservatively.

Default:

```text
Docker ownership = unknown/external
```

Never assume ownership in a way that could cause unwanted automatic shutdown.

---

# 92. Source-of-Truth Rules

Use these priorities:

### App runtime state

Source of truth:

```text
Docker / Compose
```

### Registered apps

Source of truth:

```text
apps.json
```

### Docker ownership

Source of truth:

```text
validated DockApps state + current Docker session identity
```

### GUI state

Source of truth:

```text
current manager process
```

Never persist unnecessary UI/runtime state.

---

# 93. Docker Not Installed

If a registered app is clicked and Docker Desktop is not installed:

```text
Firefly III

Docker Desktop could not be found.

DockApps requires Docker Desktop for this application.

[ Close ]
```

Do not silently attempt to install Docker.

---

# 94. Docker CLI Missing

Docker Desktop may be installed while the CLI is unavailable or PATH configuration is broken.

DockApps should detect this explicitly and show a useful diagnostic.

Potential recovery:

```text
Locate Docker CLI
```

can be considered later.

---

# 95. Compose File Missing

If an app registry entry points to a missing Compose file:

```text
Firefly III

Configuration error:
Compose file was not found.

C:\SelfHosted\firefly\compose.yaml

[ Edit App ]
[ Close ]
```

Do not create a replacement file automatically.

---

# 96. Docker Engine Unresponsive

Docker Desktop may appear running while the engine is unavailable.

DockApps should distinguish:

```text
Desktop Running
Engine Not Ready
```

and optionally offer:

```text
Retry
Restart Docker
Close
```

Avoid automatically force-restarting Docker unless explicitly requested.

---

# 97. Unexpected External Changes

The user may use Docker Desktop manually while DockApps is open.

Examples:

- stop a container
- start a container
- restart Docker
- remove a container

DockApps should not assume its previous state snapshot remains valid.

Refresh state after every operation and when the user presses Refresh.

Optional short-interval polling may occur only while a relevant GUI window is visible.

---

# 98. Status Polling Policy

Suggested:

```text
App window visible:
    poll every 3–5 seconds if useful

Global manager visible:
    poll every 3–5 seconds if useful

No windows:
    no polling
```

Polling should stop when the window closes.

This keeps DockApps idle overhead at zero once the manager exits.

---

# 99. Browser Failure

If the app becomes ready but opening the browser fails:

```text
App startup still counts as successful.
```

Show:

```text
Firefly III is running.

Could not open the browser automatically.

http://localhost:8080

[ Copy URL ]
[ Open Again ]
```

---

# 100. Logs Window

An app window may expose:

```text
View Logs
```

This can open a temporary DockApps logs window that streams:

```text
docker compose logs
```

Prefer read-only viewing in version 1.

Closing the logs window should terminate the log-follow process.

---

# 101. Shortcut Creation Service

`IShortcutService` should create `.lnk` files using Windows-supported APIs/COM.

Responsibilities:

```text
CreateShortcut(app)
DeleteShortcut(app)
ShortcutExists(app)
```

Do not require PowerShell scripts for final user operation.

---

# 102. Shortcut Targets

Per-app shortcut:

```text
Target:
DockApps.Launcher.exe

Arguments:
firefly
```

Manager shortcut:

```text
Target:
DockApps.Launcher.exe

Arguments:
--manager
```

The shortcut icon should use:

1. configured app icon
2. fallback DockApps icon

---

# 103. No Console Windows

Both manager and launcher should be built as Windows GUI executables so normal use does not flash terminal windows.

External Docker processes should also be launched without visible console windows where practical.

Errors are surfaced in WPF/native dialogs.

---

# 104. Packaging

Publish as Windows x64 initially.

Possible package layout:

```text
DockApps\
├── DockApps.Manager.exe
├── DockApps.Launcher.exe
├── required .NET/runtime files if self-contained
└── assets\
```

Preferred distribution options:

```text
portable ZIP
```

and later:

```text
installer/MSIX
```

---

# 105. Self-Contained vs Framework-Dependent

For a personal utility:

### Framework-dependent

Pros:

- smaller package

Cons:

- requires correct .NET runtime installed

### Self-contained

Pros:

- predictable deployment
- no runtime prerequisite

Cons:

- larger package

A self-contained Windows x64 build is reasonable for simple distribution.

---

# 106. Single-File Publishing

Single-file publish may be evaluated, but should not be mandatory if it complicates:

- WPF resources
- native dependencies
- startup time
- diagnostics

A small folder containing two clean executables is acceptable.

---

# 107. Installer Behavior

A future installer may:

- copy DockApps binaries
- create Start Menu entry
- create Manager shortcut
- create `%LOCALAPPDATA%\DockApps`
- optionally register startup shortcuts for configured apps

It should **not** configure DockApps to run automatically at Windows login by default.

---

# 108. Updates

Automatic updates are outside V1.

Initial releases can be updated manually.

If auto-update is added later, it must account for:

```text
Manager may currently be running
Launcher may be invoked during update
```

Do not introduce a permanent updater service merely for convenience.

---

# 109. Security Principles

DockApps executes commands that control Docker.

Therefore:

1. Treat registry configuration as trusted local configuration.
2. Validate app IDs.
3. Do not concatenate untrusted values into shell strings.
4. Use `ProcessStartInfo.ArgumentList` where possible.
5. Never execute arbitrary registry-provided shell scripts by default.
6. Never expose named-pipe control broadly without considering local-user permissions.
7. Avoid storing secrets.
8. Do not parse `.env` contents unless needed.
9. Never include environment secrets in logs.

---

# 110. Named Pipe Permissions

The manager pipe should be usable by the current interactive user.

Avoid creating a globally writable IPC endpoint if not required.

The objective is:

```text
current user's launchers
        ↓
current user's manager
```

not:

```text
any user/process on machine
        ↓
control Docker through DockApps
```

---

# 111. Privilege Model

DockApps should run as the normal user whenever possible.

Do not require administrator privileges for ordinary operation if Docker Desktop itself works under the current user.

If an operation truly requires elevation:

```text
request elevation only for that operation
```

rather than always running DockApps elevated.

---

# 112. Crash Behavior

If DockApps Manager crashes:

```text
Docker containers continue running
```

because the manager does not own their process lifetime.

On next launch:

```text
query Docker
reconstruct status
```

This is an important resilience property.

---

# 113. Crash During Docker Startup

If Manager exits unexpectedly while Docker is being started:

- Docker may continue starting.
- ownership metadata may be incomplete.

On the next invocation:

```text
validate saved ownership metadata
```

If uncertain:

```text
treat Docker as externally owned
```

This favors safety over automatic shutdown.

---

# 114. Crash During App Startup

If Manager crashes after `compose up`:

```text
containers may still successfully start
```

On next app launch:

```text
query actual Compose state
```

and continue from reality rather than assuming the previous operation failed.

---

# 115. Application Exit Handling

When WPF Manager shuts down:

1. stop accepting new IPC connections
2. finish/cancel safe background operations
3. dispose named pipe server
4. release mutex
5. flush application logs
6. close windows
7. exit

Do not stop Docker merely because Manager itself is exiting.

---

# 116. Testing Strategy Overview

Testing should be split into:

```text
unit tests
integration tests
manual Windows tests
```

Do not require Docker for most Core unit tests.

---

# 117. Unit Tests

Test business rules with mocked services.

Important cases:

### Open app

- Docker stopped → start Docker
- Docker running → do not start Docker
- app already running → do not start Compose
- app stopped → start Compose
- invalid app ID → return error

### App stop

- another container running → leave Docker
- no containers + manager-owned Docker → stop Docker
- no containers + external Docker → leave Docker

### Soft stop

- stop registered projects
- preserve unregistered containers
- shut Docker only when safe

### Hard stop

- stop registered projects
- stop unregistered containers
- stop Docker regardless of ownership

### Ownership

- matching session identity → preserve ownership
- stale identity → ownership becomes unknown

---

# 118. IPC Tests

Test:

- launcher connects to running manager
- launcher starts missing manager
- duplicate manager cannot become primary
- malformed request rejected
- unknown request type rejected
- simultaneous launch requests accepted
- launcher timeout handled

---

# 119. Registry Tests

Test:

- load valid config
- missing config creates safe default
- duplicate app ID rejected
- missing Compose file reported
- malformed JSON does not crash manager
- atomic writes preserve previous file on failure

---

# 120. Idle Shutdown Tests

Test:

```text
no windows + no operations
→ timer starts
```

```text
new request arrives
→ timer cancels
```

```text
operation begins
→ manager remains alive
```

```text
timer expires while idle
→ manager exits
```

---

# 121. Integration Tests

Integration tests may run against Docker Desktop on a development machine.

Create tiny test Compose projects such as:

```text
nginx
```

or another harmless local HTTP service.

Test:

- Docker startup
- Compose startup
- readiness detection
- app stop
- soft stop
- unregistered container preservation
- Docker shutdown

Do not use real financial/personal application data for tests.

---

# 122. Manual Acceptance Tests

Before release, manually test:

1. Docker completely stopped.
2. Click Firefly shortcut.
3. Manager appears.
4. Docker starts.
5. Firefly starts.
6. Browser opens.
7. Manager exits when idle.
8. Firefly remains running.
9. Click Firefly shortcut again.
10. Firefly is detected as running.
11. No duplicate startup occurs.
12. Stop Firefly.
13. Docker shuts down if DockApps owns it and nothing else is running.

Repeat with:

- two registered apps
- an unregistered container
- manually started Docker
- Docker startup failure
- malformed app config
- rapid simultaneous shortcuts

---

# 123. Performance Goals

DockApps should remain lightweight.

Desired properties:

```text
Launcher:
very short lifetime

Manager:
exists only while needed

Idle manager:
normally not present

No DockApps background daemon:
0 memory usage when inactive
```

Avoid introducing large always-running services.

---

# 124. Startup Performance

Optimize perceived startup:

1. Launcher should start almost immediately.
2. Manager window should appear before Docker fully starts.
3. Docker startup occurs asynchronously.
4. Status text communicates progress.
5. Browser opens as soon as the app is actually ready.

The user should never wonder whether the shortcut worked.

---

# 125. UX Principle: Docker Is an Implementation Detail

The primary UI should say:

```text
Firefly III
Starting...
Running
Stopped
```

not force the user to reason about:

```text
container IDs
networks
images
volumes
```

Container details may be shown for diagnostics, but the normal abstraction is the application.

---

# 126. UX Principle: Explicit Destructive Intent

Normal controls:

```text
Open
Stop
Soft Stop
```

should be conservative.

Only:

```text
Hard Stop
```

may intentionally interfere with unregistered Docker workloads.

The UI should make that distinction obvious.

---

# 127. UX Principle: No Surprise Shutdown

Automatic Docker shutdown occurs only when DockApps can confidently establish all of the following:

```text
nothing is running
Docker was started by DockApps
current session still matches
```

If there is uncertainty:

```text
leave Docker running
```

The user can always use Hard Stop manually.

---

# 128. UX Principle: Idempotent Shortcuts

Clicking an application shortcut repeatedly should be harmless.

Expected behavior:

```text
not running → start
starting → focus existing progress window
running → open/focus app
error → show/focus error window
```

Never create duplicate app stacks.

---

# 129. UX Principle: Recover From External Control

DockApps must coexist with Docker Desktop.

The user is still free to:

- open Docker Desktop
- stop containers manually
- start containers manually
- inspect logs
- restart Docker

DockApps should rediscover actual state rather than assuming exclusive control.

---

# 130. Initial WPF Screen Set

V1 needs only a small set of windows/dialogs:

```text
AppWindow
ManagerWindow
RegisterAppDialog
EditAppDialog
HardStopConfirmationDialog
LogsWindow
ErrorDialog
```

Do not overbuild navigation.

---

# 131. App Window Suggested Layout

```text
┌────────────────────────────────┐
│ [icon] Firefly III             │
│                                │
│ Overall        ● Running       │
│ Docker         ● Running       │
│                                │
│ Services                       │
│ ● app          Healthy         │
│ ● database     Healthy         │
│                                │
│ localhost:8080                 │
│                                │
│ [ Open ] [ Stop ]              │
│                                │
│ [ Logs ] [ Refresh ]           │
└────────────────────────────────┘
```

Keep it compact.

---

# 132. Global Manager Suggested Layout

```text
┌────────────────────────────────────────┐
│ DockApps                               │
│                                        │
│ Docker Desktop                         │
│ ● Running                              │
│ Started by: DockApps                   │
│                                        │
│ Apps                                   │
│ ┌────────────────────────────────────┐ │
│ │ ● Firefly III          Running     │ │
│ │ ○ Actual               Stopped     │ │
│ │ ● Homepage             Running     │ │
│ └────────────────────────────────────┘ │
│                                        │
│ Other Containers                       │
│ ● postgres-test                        │
│                                        │
│ [ Add App ] [ Refresh ]                │
│                                        │
│ [ Soft Stop ]         [ Hard Stop ]    │
└────────────────────────────────────────┘
```

---

# 133. Visual Style

V1 should prioritize clarity over elaborate theming.

Suggested:

- follow Windows light/dark preference if easy
- native-feeling spacing
- clear status icons
- restrained animations
- no embedded browser UI
- no Electron-style web shell

WPF styling can be improved later without changing Core logic.

---

# 134. App Icons

Each registered app may specify an icon.

Fallback chain:

```text
configured icon
    ↓
Compose/app folder icon if future discovery supports it
    ↓
generic DockApps application icon
```

Do not require an icon to register an app.

---

# 135. App Registration UX

Suggested wizard/dialog:

```text
Add Docker App

Name:              Firefly III
Compose file:      [ Browse... ]
Project name:      firefly
URL:               http://localhost:8080
Icon:              [ Browse... ]

[x] Open URL after start
[x] Create Start Menu shortcut

[ Cancel ] [ Add ]
```

Project name may optionally be inferred, but the user should be able to override it.

---

# 136. Future Auto-Discovery

Not required in V1.

Possible future feature:

```text
Scan selected folders for compose.yaml/docker-compose.yml
```

and offer candidate apps to register.

Do not automatically register every Compose project on the machine without user approval.

---

# 137. Future Protocol Handler

A future version could register:

```text
dockapps://firefly
```

Then shortcuts could invoke a protocol instead of a launcher executable.

This is optional and not required for the initial architecture.

---

# 138. Future Windows Jump Lists

Potential enhancement:

```text
Firefly III
├── Open
├── Stop
└── Logs
```

through Windows Jump Lists.

Not needed in V1.

---

# 139. Future Notifications

Potential optional notifications:

```text
Firefly III is ready
Firefly III failed to start
Soft Stop completed
```

Do not require a resident process merely to support notifications.

---

# 140. Future Tray Mode

If users later request persistent control, an optional tray mode could exist.

It must remain optional.

Default design remains:

```text
no persistent DockApps process
```

---

# 141. Future Cross-Platform Considerations

The selected WPF implementation intentionally targets Windows.

If portability becomes important later, Core should already be sufficiently separated that a new UI/platform layer could reuse:

```text
registry models
Compose abstractions
status models
business rules
```

But cross-platform support is not a current design requirement.

---

# 142. Development Phase 1 — Solution Skeleton

Create:

```text
DockApps.sln

DockApps.Core
DockApps.Manager
DockApps.Launcher
DockApps.Core.Tests
DockApps.Manager.Tests
```

Set project references.

Add dependency injection and logging infrastructure.

No Docker behavior yet.

---

# 143. Development Phase 2 — Process Execution

Implement:

```text
IProcessRunner
ProcessRunner
```

Requirements:

- argument-safe execution
- stdout/stderr capture
- cancellation
- timeout
- hidden console
- exit code
- structured result

Unit test process result handling.

---

# 144. Development Phase 3 — Docker Inspection

Implement:

```text
Docker Desktop status
Docker Engine responsiveness
docker ps JSON parsing
Compose ps JSON parsing
```

At the end of this phase, a debug screen/test should be able to display actual Docker/container state.

---

# 145. Development Phase 4 — Registry

Implement:

```text
apps.json
load/save
validation
RegisteredApp model
```

Create basic registration/edit/remove methods.

No shortcut generation required yet.

---

# 146. Development Phase 5 — App Lifecycle

Implement:

```text
EnsureDockerRunningAsync
StartAppAsync
StopAppAsync
GetAppStatusAsync
```

Test manually using a tiny Compose test app.

At this phase, lifecycle behavior may be triggered by temporary development buttons/tests.

---

# 147. Development Phase 6 — Docker Ownership

Implement:

```text
state.json
session identity
started-by-DockApps tracking
stale-state invalidation
```

Then enable safe automatic Docker shutdown after App Stop.

Before this phase, automatic Docker shutdown should remain disabled.

---

# 148. Development Phase 7 — Manager Singleton + IPC

Implement:

```text
named mutex
named pipe server
request parser
request queue
```

Create launcher client.

Verify:

```text
multiple launcher calls
→ one manager
```

---

# 149. Development Phase 8 — Per-App WPF Window

Implement compact AppWindow.

Support:

```text
starting
running
stopped
degraded
error
```

Buttons:

```text
Open
Stop
Refresh
Logs
```

Ensure Docker operations do not block the UI.

---

# 150. Development Phase 9 — Global Manager Window

Implement:

```text
Docker status
ownership status
registered apps
unregistered containers
Soft Stop
Hard Stop
Refresh
```

Add confirmation for Hard Stop.

---

# 151. Development Phase 10 — Idle Shutdown

Implement manager lifetime tracking.

Test:

```text
open app
close window
wait
manager exits
containers remain running
```

Verify new launcher invocation cleanly starts a new manager later.

---

# 152. Development Phase 11 — Shortcut Generation

Implement Windows `.lnk` creation.

Support:

```text
Start Menu shortcut
Desktop shortcut
custom icon
```

App registration can now automatically generate shortcuts.

---

# 153. Development Phase 12 — Error UX and Diagnostics

Improve:

- Docker missing
- Docker failed to start
- engine unavailable
- Compose failure
- health timeout
- invalid registry
- stale state
- malformed IPC
- browser launch failure

Add copyable errors and lightweight log viewer.

---

# 154. Development Phase 13 — Packaging

Create publish profiles for:

```text
win-x64
```

Evaluate:

```text
self-contained
framework-dependent
single-file
```

Create initial release ZIP.

Optionally add installer after behavior stabilizes.

---

# 155. Development Phase 14 — Real-App Testing

Register real applications such as Firefly III.

Validate:

```text
Docker off → shortcut → ready
Docker on → shortcut → ready
already running → shortcut → browser
one app stopped while another runs
unregistered container present
manual Docker session
Soft Stop
Hard Stop
```

Only after these cases work reliably should the project be considered functionally complete.

---

# 156. V1 Completion Criteria

V1 is complete when all of the following work reliably:

- per-app Windows shortcut
- launcher starts manager when absent
- launcher forwards app request when manager exists
- only one manager process can exist
- one GUI window per app
- Docker starts automatically when required
- app Compose project starts automatically
- already-running apps are detected
- configured app URL opens
- app can be explicitly stopped
- manager distinguishes registered and unregistered containers
- safe automatic Docker shutdown works
- Docker ownership survives manager restarts
- stale ownership is handled conservatively
- Soft Stop works
- Hard Stop works
- global status window works
- manager exits automatically when idle
- containers survive manager exit
- no persistent DockApps daemon/service is required
- normal operation requires no command-line interaction

---

# 157. Core Invariants

These rules should be treated as architectural invariants.

## Invariant 1

There is never more than one active DockApps Manager process for a user session.

## Invariant 2

Closing a DockApps window does not implicitly stop a Docker app.

## Invariant 3

DockApps Manager exiting does not implicitly stop Docker or containers.

## Invariant 4

Docker is the source of truth for runtime state.

## Invariant 5

Automatic Docker shutdown never occurs while any container is running.

## Invariant 6

Automatic Docker shutdown requires confirmed DockApps ownership of the current Docker session.

## Invariant 7

Soft Stop never intentionally stops unregistered containers.

## Invariant 8

Hard Stop may stop unregistered containers but never deletes Docker data.

## Invariant 9

One registered app corresponds to one explicitly identified Compose project.

## Invariant 10

Repeated app shortcut activation is idempotent.

---

# 158. Final Architecture Summary

```text
                   Windows Shortcuts
             ┌──────────┼───────────┐
             │          │           │
          Firefly    Homepage     Actual
             │          │           │
             └──────────┼───────────┘
                        ↓
              DockApps.Launcher.exe
                        │
                        │ named pipe
                        ↓
             ┌──────────────────────┐
             │ DockApps.Manager.exe │
             │                      │
             │ Single instance      │
             │ Request router       │
             │ Lifecycle services   │
             │ App windows          │
             │ Global manager       │
             │ Idle shutdown        │
             └──────────┬───────────┘
                        │
                 DockApps.Core
                        │
         ┌──────────────┼───────────────┐
         │              │               │
         ↓              ↓               ↓
  Docker Desktop   Docker Compose   Container Status
         │              │               │
         └──────────────┼───────────────┘
                        ↓
                  Docker Engine
                        │
             ┌──────────┼─────────┐
             ↓          ↓         ↓
          Firefly    Homepage   Actual
```

When DockApps is not being used:

```text
DockApps.Launcher.exe   not running
DockApps.Manager.exe    not running
```

Docker itself remains running only if Docker workloads still require it or the user deliberately left Docker running.

---

# 159. Product Definition

DockApps is **not a Docker management dashboard**.

It is:

> A lightweight Windows application launcher and lifecycle broker that makes registered Docker Compose applications behave more like ordinary Windows applications while managing Docker Desktop only when necessary.

The normal user experience should be:

```text
Click app shortcut
        ↓
app opens
```

The fact that Docker Desktop, Compose, multiple containers, health checks, ports, and lifecycle coordination exist underneath should normally remain an implementation detail.

That principle should guide every major design decision in the project.
