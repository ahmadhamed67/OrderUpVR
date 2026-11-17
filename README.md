# Order Up VR

A VR mod for **Order Up!!** (PS3, Europe, `BLES01470`, v01.04) running on RPCS3, for PC VR headsets through OpenXR (developed for Quest 3 over Link / Air Link / Virtual Desktop).

## Install

1. Download **[OrderUpVR-Setup.exe](https://github.com/ahmadhamed67/OrderUpVR/releases/latest/download/OrderUpVR-Setup.exe)**.
2. Run it and pick three folders: your RPCS3 folder, your Order Up!! game folder, and where to install the mod.
3. Press **Play** in the Order Up VR launcher.

The launcher checks this repository for new versions by itself (and has a "Check for updates" button), downloads them, and shows the changelog. It also updates itself.

Windows may warn about the setup file because it is not code-signed.

To remove it, use Uninstall in the launcher's Settings or in Windows' Settings > Apps > Installed apps. You can keep your settings and game cache for a quick reinstall or remove everything; your own RPCS3, firmware, saves and game files are never touched.

## What you need

- Your own RPCS3 install with PS3 firmware installed.
- Your own copy of Order Up!! (`BLES01470`).
- A PC VR headset with an OpenXR runtime active.

The mod installs its own modified RPCS3 build in a separate folder. It does not change your RPCS3 install or your game files, and it contains no firmware, game files or keys.

## Status

Early test build. See the [changelog](release/changelog.json) and the [releases page](https://github.com/ahmadhamed67/OrderUpVR/releases).

| Feature | State |
|---|---|
| Game on a large virtual screen in the headset | Works in a headset simulator; not yet confirmed on a real headset |
| VR controller as the game's PlayStation Move pointer and buttons | Built; aim accuracy not yet confirmed |
| Real stereo 3D and head-tracked camera | Not started |

## Controls (0.1.0)

- Aim a controller at the screen to point. The hand that pulls its trigger becomes the pointing hand.
- Trigger = T, grip = Move, A/B/X/Y = Cross/Circle/Square/Triangle, left menu = START, right stick click = SELECT.
- Calibration screen: point at the middle of the screen, press X (the game's Square), then squeeze the grip.
- Left stick click recentres the screen; right stick up/down moves it nearer or farther; left stick up/down resizes it.

## Source layout

```
launcher/     Setup + launcher (single exe, C# + WPF on the .NET Framework built into Windows)
patches/      Changes to RPCS3 (against commit fb6737b1c) that add the VR module
package/      Per-game RPCS3 config shipped with the mod
release/      Changelog
scripts/      Build and release scripts
layer/        Early diagnostic Vulkan layer (not shipped)
tools/        Development helpers
docs/         Design notes and control research
```

Building the modified RPCS3: check out RPCS3 at `fb6737b1c`, apply `patches/0001-orderup-vr.patch`, put the OpenXR SDK headers in `3rdparty/openxr/include` and `openxr_loader.lib` on the library path, then build the `rpcs3` target.

## Licence

GPL-2.0, the same as RPCS3 (see [LICENSE](LICENSE)). The OpenXR loader is © The Khronos Group, Apache-2.0. Order Up!! is the property of its owners; this project is not affiliated with them or with Sony.
