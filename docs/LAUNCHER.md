# Project Quasar launcher

The launcher is a Windows x64 .NET 10 WinForms app with a separate update bootstrap. Build with the x64 .NET 10 SDK. The portable package uses the x64 .NET 10 **Desktop Runtime** installed on the PC. Microsoft currently lists Windows 11 and applicable Windows 10 LTSC/Enterprise versions. [Microsoft Windows support and installation instructions](https://learn.microsoft.com/en-us/dotnet/core/install/windows)

## Build, package and test

Run these from the repository root.

```powershell
dotnet build launcher/App/App.csproj -c Release -t:Rebuild
dotnet build launcher/Bootstrap/Bootstrap.csproj -c Release -t:Rebuild
dotnet build launcher/PatchTool/PatchTool.csproj -c Release -t:Rebuild
pwsh -NoProfile -File launcher/Package-Launcher.ps1 -OutputDirectory launcher/artifacts/quasar-launcher-final
powershell -NoProfile -ExecutionPolicy Bypass -File launcher/Test-Launcher.ps1 `
    -PortablePackage launcher/artifacts/quasar-launcher-final/quasar-portable-0.2.0-win-x64.zip `
    -PreviewDirectory launcher/artifacts/launcher-dpi
```

The packaging command requires PowerShell 7 and a new output directory. Extract the portable ZIP to a user-writable location, then open `ProjectQuasar.exe`. The portable ZIP contains this bootstrap, its matching DLL, dependency and runtime configuration JSON files, and `Core.dll`. Its `bundled/` directory contains `Quasar.Launcher.exe`, its matching DLL and JSON files, and a separate `Core.dll`. The launcher-update ZIP contains only the five files from `bundled/`. The release manifest is generated beside the ZIPs. Game data, runtime packages, drivers, signing keys, and player files stay separate.

The charcoal and slate launcher uses an original Quasar icon and a cockpit display drawn by the launcher. Use Tab to move through controls, Enter or Space to activate a sidebar item, and Ctrl+Tab or Ctrl+Shift+Tab to cycle through views. Windows high-contrast colors take precedence when enabled. Windows 11 uses a dark native window frame. [Microsoft DWM frame attributes](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute)

The test command builds the Windows launcher, bootstrap, runtime patch tool and two versions of a fixture executable. It uses synthetic XBE/XDVDFS files and inputs, generated signing keys, local ZIP packages and an in-memory release feed. It exercises keyboard navigation, all four views at 100%, 150%, and 200% using a DPI layout fixture, settings and profile migration, real bootstrap self-update, patched-runtime interruption and recovery, rollback, and verified full-package fallback on a wrong base. With `-PortablePackage`, it extracts the local ZIP, starts its actual `ProjectQuasar.exe`, activates Launch game, and checks the exact handoff received by the stub. The fixture writes to temporary directories using `--user-root`. Use `-PreviewDirectory` for fixture screenshots.

## Setup

- In **Play**, select your own legally obtained image or an extracted game root with `default.xbe`. The image reader accepts bare XDVDFS and the documented XGD1 layout with game partition at `0x18300000`. It checks the volume descriptor, finds the root XBE and checks the `XBEH` header and retail title ID `43430002`. This identifies the supported title and structure. A release runtime should also verify any exact disc revision and required files it depends on. [Project disc research](milestone-1-research.md)
- Runtime and data checks must pass before Play enables. A selected local development runtime replaces the managed runtime for Play. Only select executables you trust. Diagnostic candidates and the existing `your_game_recomp*` binaries belong to the protected diagnostic workflow.
- Settings, the profile library, and launcher-managed saves live under `%LOCALAPPDATA%\Project Quasar`. The library is stored in `profiles\library.json` and the launcher writes the selected profile as `profiles\active.json` for the runtime handoff. An existing active profile seeds the new library. Use `ProjectQuasar.exe --user-root <absolute directory>` for a separate player-data directory outside the installation. Reset backs up the settings file before restoring defaults. Game files remain at the selected location.
- **Graphics** shows the saved request and the settings that will be sent at launch. Original is 640×480 with 4:3 preservation. The runtime owns internal rendering resolution and simulation timing. Effective settings depend on the runtime’s declared display capabilities and the user’s experimental-settings opt-in.

## Runtime contract v1

Place a manifest such as the following next to the local development runtime and select it in Play. The names and versions below are examples.

```json
{
  "schema": 1,
  "version": "1.0.0",
  "platform": "win-x64",
  "executable": "Quasar.Runtime.exe",
  "workingDirectory": ".",
  "dataFormats": ["extracted"],
  "capabilities": ["input-profile-v1", "original-640x480"],
  "minimumLauncher": "0.1.0",
  "maximumLauncherExclusive": "1.0.0"
}
```

The executable and working directory resolve relative to the manifest and stay within its directory. Managed runtimes use the fixed filename `quasar-runtime.json`. The launcher validates the Windows AMD64 executable and starts it directly with individual Windows argument values. It clears inherited `RECOMP_*` and `QUASAR_*` variables from the child environment. Local runtime selection is explicit. The launcher does not translate the contract into diagnostic flags.

| Arguments | Consumer |
| --- | --- |
| `--quasar-contract 1` | Validate the protocol before startup. |
| `--game-data <absolute path> --game-data-format <extracted or xdvdfs>` | Load user-owned data from the selected directory or image. Advertise only the implemented formats in `dataFormats`. |
| `--save-dir <absolute path>` | Redirect writable Xbox storage to the separate launcher save directory. |
| `--input-profile <absolute JSON path>` | When declaring `input-profile-v1`, load schema 1 and consume device states in the runtime’s XID mapping. |
| `--graphics-json <JSON argument>` | When declaring `original-640x480`, consume `mode`, `width`, `height`, `aspect`, `vSync`, `frameCap` and `experimental`. The size refers to output. A zero cap delegates to the runtime’s original timing. |

Graphics capabilities are `original-640x480`, `borderless`, `fullscreen`, `output-resolution`, `widescreen`, `vsync` and `frame-cap`. The base capability requires windowed 640×480 and a preserved 4:3 viewport. Each additional capability requires the corresponding presenter functionality. The launcher clamps and filters options when preparing the process.

The current game source has a compiled-in `YOUR_GAME_DIR` and passes `RECOMP_SAVE_DIR` to `xbox_path_init`. A supported game build needs the contract parser before game-data access and path initialization. The runtime needs to use the chosen image or extracted root for both the XBE and DVD reads, and use the contract’s save directory for all writable Xbox storage. It also needs presenter consumers for any graphics capabilities it advertises. The fixture records the handoff. Existing game builds have no contract manifest and remain outside this launcher.

## Controls

Select a named profile from the dropdown or add a profile from a preset, a blank profile, or an imported schema-1 JSON file. Search cockpit actions by label, group, or binding ID. Select an action, click **Click to bind**, then press the input or move its axis. Bind again to add an alternate. **Edit / calibrate** accepts direction, minimum, center, maximum, inversion and dead zone. Select the physical-maintained option for a hardware switch or detent. Live meters show mapped stick and pedal levels. Conflicting sources appear in the editor and block Play when the runtime consumes the profile.

The Windows backends use keyboard/mouse while the launcher has focus, XInput slots for Xbox-compatible pads, and Windows joystick slots for PlayStation pads, joysticks, HOTAS and pedals exposed through that API. The generic path supports up to six axes, 32 buttons and a POV hat per slot. Profiles can combine slots. Windows may reorder slots on reconnect or show a controller through multiple APIs. Use the named device and live controls to confirm assignments after rearranging devices. The starter mappings provide a few common buttons and axes and can be edited for each device.

The mapper models momentary buttons, physical maintained positions and edge-triggered keyboard/gamepad alternatives. It writes the same 26-byte Steel Battalion report fields documented by the existing XboxRecomp patch. Disconnect releases momentary values and clears switch latches and selected positions owned by that device. For native integration, feed those reports into the Steel Battalion route at `usb_gamepad_report` in `src/usb/usb_gamepad.c`, preserving the generic Xbox path and first-port semantics. The launcher frees its preview device handles before Play so a compatible runtime can open them. [Project controller research](community-compatibility-preservation.md) · [Existing report-path fixture](../tools/tests/steel-battalion-xid-startup.c)

The original controller target is a passive Xbox-controller-to-USB adapter forwarding **0A7B:D000**, with an explicitly installed WinUSB driver and a registered device interface. The launcher never installs or replaces drivers. When it receives a valid report on interrupt IN endpoint `0x82`, it prioritizes that report in preview. Indicator lights are shown separately based on interrupt OUT endpoint `0x01`. Sending LED reports requires the runtime to route its USB output into a hardware backend. Hardware verification is pending for original input, lights and generic devices. [USB report reference](https://xboxdevwiki.net/XID#Steel_Battalion_Controller)

## GitHub update and signing

The launcher checks the latest stable GitHub Release in `cstout816/project-quasar`. Manual checks and optional startup checks offer updates for review. The Updates view shows byte progress for each signed download and reports verification, pending restart, and rollback availability. Play requires no connection. Launcher and runtime versions advance independently. A runtime-only update preserves the active launcher’s established compatibility range. The staged runtime must also accept the resulting launcher version before the bootstrap can switch the pair. [GitHub Releases API](https://docs.github.com/en/rest/releases/releases#get-the-latest-release)

- The release manifest is `quasar-win-x64.json` and its detached binary signature is `quasar-win-x64.sig`. Schema 1 uses `platform: "win-x64"`, `contract: 1`, `minimumRuntime` and `maximumRuntimeExclusive`. It includes at least one `launcher` or `runtime` package record. Each has a canonical three-part `version`, `asset`, compressed `size`, uncompressed `expandedSize` and hexadecimal `sha256`. Asset names follow `quasar-launcher-<version>-win-x64.zip` and `quasar-runtime-<version>-win-x64.zip`.
- Embed the production RSA-3072 public key in SubjectPublicKeyInfo PEM using `Package-Launcher.ps1 -PublicKeyPem <path>`. To sign locally, also pass `-SigningKeyPem <secure private PEM path>`. The script checks the pair and signs the exact JSON bytes with RSA-PSS/SHA-256. Keep the private key on the signing host and outside the repository. Use a separate fixture key for local tests. Keep the production updater disabled until the trusted public key is embedded.
- For any future runtime release, make a flat runtime ZIP containing `quasar-runtime.json`, redistributable executables and required DLLs, matching .NET dependency/runtime config JSON if needed, and applicable license notices. Audit the runtime for embedded or adjacent proprietary material before distribution. Add its package record with the ZIP SHA-256 and exact compressed and expanded sizes, then regenerate the detached signature over the final manifest bytes. The launcher script builds only the launcher assets. Release signing and publication are maintainer actions.
- The bootstrap stages complete, immutable version directories. After the launcher exits, it atomically selects a trial in `installation.json`. The trial launcher confirms a visible startup and reports its PID and version. The bootstrap commits the pair after readiness and retains the previously active pair. On an interrupted startup it restores the saved active selection. Manual rollback stages the last working pair. The bootstrap is a stable entry point outside the launcher update ZIP. Replace it via a new portable package if the bootstrap installation protocol needs to change.

Production GitHub signing and release installation have yet to be verified. The local fixture suite establishes package verification and recovery behavior without publishing. A runtime becoming active after these checks establishes contract compatibility. Gameplay, device behavior and saved progression require separate runtime validation.

## Incremental runtime updates

Launcher 0.2.0 can use a smaller patch when GitHub supplies one for the installed managed runtime version. The patch reuses unchanged files and matching aligned 16 KiB blocks inside changed files. Affected binary files are reconstructed from the installed bytes and the downloaded changes. This is a runtime update operation and requires no game compilation on the player’s PC.

The updater checks the signed patch ZIP, hashes every reused base file, and verifies every reconstructed file against the signed target. A signed target tree fingerprint ties the patch output and the full ZIP to the same files. The complete result is staged into a separate version directory and promoted on restart. The previous version remains available for rollback, so patching saves download bandwidth and still needs disk space for a full staged runtime. A missing or unsuitable patch uses the signed full package. A modified base also uses the full package after fingerprint verification fails. A patch with a bad signature, archive hash, archive path or reconstructed target is rejected.

To generate an optional patch, use two already-built, audited runtime release directories extracted from the corresponding full ZIPs. The tool takes the base directory, target directory and new patch output path. The filename includes the runtime versions from the directories’ manifests.

```powershell
dotnet run --project launcher/PatchTool/PatchTool.csproj -c Release -- `
    'releases/runtime-1.1.0' 'releases/runtime-1.2.0' `
    'quasar-runtime-1.1.0-to-1.2.0-win-x64.patch.zip'
```

Publish the patch only when it is smaller than the full ZIP. Keep the full ZIP available for fresh installations, changed bases and launchers that use schema 1. For patch-aware launchers, publish a separately signed `quasar-win-x64-v2.json` and `quasar-win-x64-v2.sig`. Use the same platform, contract, compatibility range and full package metadata as the schema-1 release. Set `schema` to `2` and put the patch tool’s output record in the `runtimePatches` array. Each record has `baseVersion`, `asset`, `size`, `sha256` and `targetTreeSha256`. Multiple patches must have different base versions and the same target tree fingerprint. Sign the exact v2 JSON bytes using the existing RSA-PSS/SHA-256 release key. Launcher 0.1.0 reads the original schema-1 filenames.
