<div align="center">

# Project Quasar

**A community preservation project bringing the original Xbox Steel Battalion to Windows through static recompilation**

### Experimental development · No playable build yet

[Can I play it?](docs/PLAYER_GUIDE.md) · [Project progress](docs/PROJECT_STATUS.md) · [Community and controller plans](docs/community-compatibility-preservation.md)

</div>

> **For players.** The Windows build has reached Mission 00, started the vehicle, and driven using mapped controls. A new pilot also reloaded after restarting the game. Rendering and speed still prevent reliable play, and Mission 00 has yet to be completed. The Windows launcher can be built from source for setup and controller preview. A playable download and a launcher-compatible game runtime are pending. [Launcher guide](docs/LAUNCHER.md)

## The game and its controller

<table>
  <tr>
    <td width="50%" align="center">
      <a href="https://commons.wikimedia.org/wiki/File:Steel_Battalion_controllers.jpg"><img src="https://thumb.wikimedia.org/wikipedia/commons/thumb/e/e7/Steel_Battalion_controllers.jpg/1280px-Steel_Battalion_controllers.jpg" alt="Original Steel Battalion controller and foot pedals" width="100%" /></a><br />
      <sub>Original controller and pedals · Photo by John Tregoning · <a href="https://creativecommons.org/licenses/by/2.0/">CC BY 2.0</a></sub>
    </td>
    <td width="50%" align="center">
      <a href="https://www.purexbox.com/games/xbox/steel_battalion"><img src="https://images.purexbox.com/screenshots/137992/large.jpg" alt="Gameplay screenshot from the original Xbox version of Steel Battalion" width="100%" /></a><br />
      <sub>Original Xbox gameplay · <a href="https://www.purexbox.com/games/xbox/steel_battalion">Pure Xbox screenshot gallery</a></sub>
    </td>
  </tr>
</table>

These externally hosted images show the original hardware and game. They are not stored in this repository; image rights remain with their respective creators and publishers.

## What this project is

The goal is to translate the original Xbox version of *Steel Battalion* (2002) ahead of time into a Windows application and recreate the Xbox services it needs. This is called **static recompilation**. The project is not an emulator packaged under another name.

Two independent screen-gated new-pilot runs used the same native Windows AOT build. The game played the intro, loaded the world resource it needed, and removed the white loading fade through the original game logic. Both runs showed Hatch response and at least 60 seconds of continuing mission updates afterward. [Project status](docs/PROJECT_STATUS.md)

## Controller compatibility

The original 40-button Steel Battalion controller and pedals are central compatibility targets, alongside keyboard, mouse, gamepad, HOTAS and custom controller profiles. Mission 00 has accepted Hatch, Ignition, all five startup switches, Start, first gear, acceleration and steering through mapped controls. Targeting and weapon effects still need verification. Physical controller adapters have yet to be tested in gameplay.

## What you can do here

- **Learn the current state:** read the [player guide](docs/PLAYER_GUIDE.md) and [project progress tracker](docs/PROJECT_STATUS.md).
- **Follow the technical work:** see [research and architecture](docs/milestone-1-research.md) and the [audio DSP integration plan](docs/audio-dsp-integration-plan.md).
- **Share community knowledge:** visit [Steel Battalion Online on Reddit](https://www.reddit.com/r/steelbattaliononline/) or the [community Discord](https://discord.gg/steelbattalion).

When a tested release exists, players will need to provide their own legally obtained game copy. This repository will not include the ISO, XBE, extracted game code, textures, audio, or cinematics. A future launcher version may offer an option to add the game to Steam as a non-Steam shortcut.

## Progress at a glance

| Work area | Progress | Current result |
| --- | --- | --- |
| Overall project milestones | [#####---------------] 2/8 | Boot and compatibility remain active. See the [project status](docs/PROJECT_STATUS.md). |
| Research and architecture | [####################] 3/3 | Approach and technical limits documented. |
| XBE analysis and decompilation | [##########----------] 2/4 | XBE parsing and ahead-of-time translation output are available; per-function correctness and complete runtime dependencies are not yet verified. |
| Initial code translation | [####################] 100% | Translation output exists; correctness is not established by output count. |
| Windows build | [####################] 100% | A local build completes. |
| Boot and compatibility | [################----] 4/5 | Campaign entry, mission loading, cockpit and a pilot reload have been demonstrated. Reliable play remains open. |
| Mission loading | [####################] 1/1 | AOT ran the intro, loaded the required terrain, and reached the original cockpit. |
| First mission frame | [####################] 1/1 | Two launches on the same reference build showed the cockpit and Hatch response. Graphics artifacts remain. |
| Mission 00 completions | [--------------------] 0/2 | Requires two successful runs on one build, the original results, and confirmed saved progression after restart. |
| Mission performance | [--------------------] 0/2 | Running samples measured 1.64 and 2.71 FPS. The 30 FPS playability target and the later 60 FPS target remain open. |
| CPU raster throughput | [##########----------] 2/4 | The raster regression fixture and live CPU samples verified costs in diagnostics and pixel shading. Four workers remain the default. |
| GPU scene rendering | [#####---------------] 1/4 | A narrow splash-render path is verified; general NV2A scene rendering, mission GPU use, and image parity remain unverified. |
| Virtual controller input | [#############-------] 2/3 | Menus, startup and basic driving have been exercised. Target lock, weapon effects and physical hardware remain open. |
| Guest DSP and audio | [█-------------------] 1/2 | r340 captured a `put_pixel` list-field write; r355 logged invalid list nodes but lacks writer/surface correlation. r356 did not reproduce. Audible output and a safe fix are unverified. |
| Storage and incremental builds | [##################--] 3/4 | Latest targeted audit: candidate history 5.87 GiB and nested historical logs 4.49 GiB allocated; the protected ISO/extracted-data/save floor is 9.85 GiB. r522 recovered no space and added 1.76 MiB of evidence. No whole-workspace rescan was needed; warm build/test caches remain. See the [r522 cleanup manifest](analysis/current-cleanup-manifest-r522-20261031.md). |
| Campaign, controller, launcher | — | Launcher setup and controller preview are available from source. Gameplay, runtime handoff to the game and physical controller verification are pending. |

These bars describe engineering work, not playable features or campaign completion. See the [detailed tracker](docs/DEVELOPMENT_STATUS.md) for field-by-field evidence.

## Who makes this?

Most of the research, code, and documentation in this repository have been produced with **ChatGPT Codex**. GitHub maintainer and project contact: [@cstout816](https://github.com/cstout816). This is a community preservation effort; reported progress separates completed engineering work from game behavior that has actually been verified.

## Code and third-party licenses

XboxRecomp's core source is MIT-licensed. Its xemu-derived APU and NV2A register components remain LGPL-2.1-or-later. The corresponding license text and attribution notice are in [`LICENSES/`](LICENSES/). These notices cover software components, not Steel Battalion game files. Release packaging still requires a complete license review.

## Technical approach

The project uses [sp00nznet/xboxrecomp](https://github.com/sp00nznet/xboxrecomp) for ahead-of-time Xbox x86 translation; it does not use full-system emulation. The graphics work currently reproduces a narrow splash-logo draw, while general graphics, kernel, audio, filesystem, controller, and campaign compatibility still need work. See the [development tracker](docs/DEVELOPMENT_STATUS.md) for what has actually been verified.

The work prioritizes campaign reliability and the original Steel Battalion controller before visual enhancements. See the [community compatibility and preservation plan](docs/community-compatibility-preservation.md) for planned controller support, campaign checks, accessibility, and future Line of Contact research.

## Performance and hardware use

The runtime uses CPU workers for general Xbox graphics rasterization. The RTX 4070 SUPER has been verified for presentation and a narrow splash draw. A running Mission 00 sample measured 2.71 FPS at 640×480. The pixel shading routine was active on the two hottest threads at capture. A usable mission view and the 30 FPS target remain the immediate goals.

This independent project is not affiliated with or endorsed by Capcom or Microsoft. Product names and trademarks belong to their respective owners. A Windows release will be considered only after compatibility work and license review.
