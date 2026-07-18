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

1. Clone `https://github.com/habibrehmansg/infopanel` alongside this folder
   (or adjust the `ProjectReference` path in the .csproj to wherever you put it).
2. `dotnet restore` / open in Visual Studio, build in Release.
3. **Verify the output folder before copying anything.** InfoPanel loads
   plugins via .NET's `AssemblyDependencyResolver`, which needs a
   `InfoPanel.PeripheralBattery.deps.json` file sitting next to the DLL, plus
   every NuGet dependency DLL actually present in the same folder (not just
   referenced from the NuGet cache). Check `bin\Release\net8.0-windows\`
   contains all of these before moving on:
   - `InfoPanel.PeripheralBattery.dll` and `.deps.json`
   - `HidSharp.dll`
   - `Grpc.Net.Client.dll`, `Grpc.Net.Client.Web.dll`, `Grpc.Core.Api.dll`
   - `Google.Protobuf.dll`
   - (plus whatever transitive dependencies those pull in - just copy the
     *entire* output folder, don't cherry-pick files)

   If `.deps.json` or any of those DLLs are missing, the plugin host fails
   with an error like `Dependency resolution failed ... Failed to locate
   managed application` and the plugin silently won't load. The `.csproj`
   already sets `GenerateDependencyFile` and `CopyLocalLockFileAssemblies` to
   make sure a plain `dotnet build` produces everything needed - if you still
   don't see these files, try `dotnet publish -c Release --no-self-contained`
   instead, which guarantees a complete, self-sufficient output folder.
4. Copy the **entire output folder's contents** (not just the DLL) to:
   `%ProgramData%\InfoPanel\plugins\InfoPanel.PeripheralBattery\`
5. Restart InfoPanel (or use "Add Plugin from ZIP" / the runtime plugin manager
   if you're on a build that supports it).
6. In InfoPanel's Plugins settings, you'll see toggles for each device and a
   poll interval.

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

