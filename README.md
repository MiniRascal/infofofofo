# InfoPanel Peripheral Battery Monitor

Battery % + charging status for:
- **Razer DeathAdder V4 Pro** - working, uses Razer's documented HID control protocol, confirmed VID/PID
- **Razer Blackshark V3** - working, same protocol family, confirmed VID/PID (0x1532:0x057A)
- **Gamakay TK75HE V2** - working for 2.4GHz mode, via the same local gRPC-Web
  service its own software uses (see below) — not raw HID. No charging status
  is available for this device (the protocol doesn't expose one). Bluetooth
  mode hasn't been verified — the discovery below was for the 2.4GHz dongle
  path specifically, and Bluetooth may route through different software/driver.

## How the Razer devices work

Razer mice/keyboards (and apparently some headsets) all speak the same
90-byte "control report" protocol over a HID feature report — the same one
OpenRazer's Linux driver and the community `razer-battery-checker` tool use.
It's undocumented by Razer but stable and widely reverse-engineered. No
Synapse dependency, no admin rights beyond normal HID access.

Two protocol parameters vary by device family and aren't published anywhere
per-model:
- **transaction id** (0x1F for newer devices incl. DeathAdder V4 Pro, 0x3F for older ones)
- **HID report id** the control channel lives on (0x00 for mice/keyboards, but
  0x07 was found on some Razer audio devices during reverse-engineering)

Rather than hardcode a guess for Blackshark V3, `RazerHidBatteryDevice` finds
Razer HID interfaces by **product name** (not PID, since I couldn't confirm
Blackshark V3's exact PID), then tries each combination of the above until
one round-trips a CRC-valid response. Whichever combo works gets cached. If
Blackshark V3 never connects, it's worth grabbing a USB capture the same way
described for Gamakay below and comparing against what the code tries.

## Building

**Important - avoiding a version mismatch:** InfoPanel loads plugins via
reflection, and it needs the *exact* `InfoPanel.Plugins.dll` your installed
app ships with - not a freshly built one from GitHub source, even from the
same repo. Building against the wrong copy causes a
`Can't find any type which implements ICommand` error at load time, even
though the plugin code itself is fine.

1. On your PC, find your InfoPanel install folder (shown in InfoPanel's own
   logs as "Content root path" - commonly `C:\Program Files (x86)\InfoPanel`).
2. Copy `InfoPanel.Plugins.dll` from that folder into this project at:
   `InfoPanel.PeripheralBattery\lib\InfoPanel.Plugins.dll`
   (create the `lib` folder if it doesn't exist)
3. Build, either:
   - **Via GitHub Actions** (no local .NET install needed): push this
     project to a repo with `lib\InfoPanel.Plugins.dll` committed (GitHub's
     runner has no InfoPanel install of its own, so it needs that file
     checked into the repo), add `.github/workflows/build.yml`:
     ```yaml
     name: Build Plugin
     on:
       push:
         branches: [ main ]
       workflow_dispatch: {}
     jobs:
       build:
         runs-on: windows-latest
         steps:
           - uses: actions/checkout@v4
           - uses: actions/setup-dotnet@v4
             with:
               dotnet-version: '8.0.x'
           - run: dotnet build InfoPanel.PeripheralBattery.csproj -c Release
           - uses: actions/upload-artifact@v4
             with:
               name: InfoPanel.PeripheralBattery
               path: bin/Release/net8.0-windows/
     ```
     push, wait for green, download the artifact.
   - **Or locally**: install the .NET 8 SDK or Visual Studio (".NET desktop
     development" workload), then `dotnet build -c Release` from the project
     folder.
4. **Verify the output folder before copying anything.** Check
   `bin\Release\net8.0-windows\` (or the downloaded artifact) contains all of:
   - `InfoPanel.PeripheralBattery.dll`, `.deps.json`, and `.runtimeconfig.json`
   - `HidSharp.dll`
   - `Grpc.Net.Client.dll`, `Grpc.Net.Client.Web.dll`, `Grpc.Core.Api.dll`
   - `Google.Protobuf.dll`
   - (plus whatever transitive dependencies those pull in - just copy the
     *entire* output folder, don't cherry-pick files)

   Both `.deps.json` and `.runtimeconfig.json` are required - InfoPanel's
   loader uses `AssemblyDependencyResolver`, which needs both to resolve
   dependencies and the target framework. Missing either one produces
   `Dependency resolution failed ... Failed to locate managed application`
   and the plugin silently won't load. The `.csproj` already sets
   `GenerateDependencyFile` and `GenerateRuntimeConfigurationFiles` so a
   plain `dotnet build` produces both.
5. Fully close InfoPanel (including the tray icon), delete any existing
   `C:\ProgramData\InfoPanel\plugins\InfoPanel.PeripheralBattery\` folder
   from previous attempts, then copy the **entire output folder's contents**
   (not just the DLL) into a fresh copy of that folder.
6. Relaunch InfoPanel as Administrator.

(InfoPanel's built-in "Add Plugin"/zip import can also work, but has been
flaky in testing - the manual copy above is the reliable fallback.)

## Gamakay TK75HE V2 — how it actually works

There's no public driver for this board, but its official software is
Electron-based, which means its JavaScript source ships unencrypted inside
`app.asar`. Extracting it (`npx asar extract app.asar out/`) and searching
for "battery" showed the UI doesn't talk to HID directly at all — it talks to
a **local background process** (`iot_driver_v<version>.exe`, spawned by the
app) over **gRPC-Web on `http://127.0.0.1:3814`**. That background process
does the real HID work and hands back already-parsed protobuf messages
(`driver.Device { battery, isOnline, vid, pid, ... }`).

So instead of reverse-engineering raw HID feature reports, `GamakayDriverClient`
just speaks the same gRPC-Web protocol the real app uses — reconstructed into
`driver.proto` from the message definitions found in the JS. This is far more
robust than a hand-parsed byte offset would have been.

**Known limitations, both discovered the same way:**
- **Requires the background driver process to be running.** It's normally
  started by opening the Gamakay app once; check Task Manager for a process
  with `iot_driver` in the name if the plugin reports "driver service not
  reachable."
- **No charging status is available.** There's no such field anywhere in
  this protocol — the app's own UI doesn't show a charging indicator for
  this device either. `Charging` will always read "Not available" for the
  Gamakay specifically (DeathAdder V4 Pro and Blackshark V3 still report
  real charging status via the Razer protocol).
- The protocol also has a second reporting path (`DangleCommonDevice`, for
  combo dongles serving a keyboard+mouse pair) which is handled defensively
  in code, but the TK75HE V2 is keyboard-only so it likely always reports
  through the plain `Device` message instead.

If a future firmware/software update changes the VID/PID or driver
executable name, update the constants at the top of `GamakayDriverClient.cs`.

