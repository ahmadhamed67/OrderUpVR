# Architecture & roadmap

## Why a Vulkan layer first

RPCS3 → Vulkan loader → **VK_LAYER_ORDERUP_vr** → NVIDIA driver.

The layer sees every Vulkan call RPCS3 makes: swapchain creation, every draw, every present. That gives us:

- the final game image each frame (presentation path for VR),
- the command stream (useful for spotting the 3D scene vs. HUD passes),
- a place to run OpenXR without touching RPCS3's source.

It's limited because the layer cannot easily reach PS3 guest memory or RSX transform constants by their meaning.
Head-driven camera and true stereo (M3/M4) therefore move into an RPCS3 source patch built from the matching
commit `fb6737b1c`.

## Milestones

### M1: Interception (done, builds)
Passthrough layer that tracks swapchains (forcing `TRANSFER_SRC` usage so frames can be copied out), counts draws per frame
and logs FPS. **Exit check:** a run of the game produces a log with RPCS3's swapchain size and stable draw counts in
menus vs. gameplay.

**First game run (2026-11-16, log `layer_20261116_230159.log`):**
- The layer loaded into RPCS3 (Vulkan API 1.2) on the RTX 3060. The game booted and kept running, with no crashes.
- Swapchain: 1920x1053 at first, then **1920x1080, format 44 (`B8G8R8A8_UNORM`)**, 4 images. Creation with the added `TRANSFER_SRC` usage was accepted, so frames can be copied out for M2.
- Draws per frame: about 128 and 86 during boot and logos, **1** during the FMV movies (the PAMF video shown as a single textured quad), and about 13–14 on the menu.
  The draw count already tells movies, menus and gameplay apart. That is useful for picking the render mode (flat screen vs. head-tracked).
- FPS: 42–56 with the layer. A baseline without the layer is still to be measured to confirm the overhead is small.
- Still needed: draw counts and FPS from actual kitchen gameplay.

### M2: OpenXR cinema screen
- Load `openxr_loader.dll` at runtime. Needs the OpenXR SDK headers/loader, which must be downloaded (Khronos OpenXR-SDK, Apache-2.0).
- Create a **separate** VkInstance/VkDevice on the same physical GPU, using `XR_KHR_vulkan_enable2` requirements.
- At `vkQueuePresentKHR`: copy the presented image into an exportable image (`VK_KHR_external_memory_win32` +
  shared semaphore), import it on the XR device, blit into the XR swapchain, and submit an `XrCompositionLayerQuad`
  (later a cylinder layer) about 3 m away in a LOCAL space. The flat window keeps working.
- Recenter hotkey. Comfort: the screen is world-locked, so head motion never moves the image.
- **Exit check:** the game is playable on a stable virtual screen at full frame rate.

### M3: Head-driven camera
Order Up!! is played from a mostly fixed first-person view of each kitchen station. That is a good fit for 3DoF look-around.
1. Build RPCS3 from `fb6737b1c` (needs Qt 6.8+ via aqt or the Qt installer; LLVM is optional, interpreter fallback).
2. Use RPCS3's debugger / memory viewer and the shader log to find the view matrix: the vertex-constant register
   range that changes when the game camera changes station but stays put for HUD draws.
3. Post-multiply headset orientation onto that view matrix in the RSX transform-constant upload path
   (`rsx::thread` / `VKGSRender` constant upload), with HUD draws left untouched.
- **Exit check:** looking around in the headset turns the in-game camera without HUD distortion.

### M4: Stereo
Alternate-eye rendering is the first target (same camera hook, ±IPD/2 lateral offset on even/odd frames, each
frame routed to its eye's XR image). It is cheap to implement and works with RPCS3's single-pipeline model. A real
double-draw stereo pass comes later if performance allows. HUD and menus get rendered at a fixed depth.

### M5: Input
The PS3 release supports a pointer-style control scheme (PS Move; to be verified in the game's options).
Map the VR controller ray to RPCS3's mouse-driven Move/pointer emulation and the triggers/buttons to Move buttons.

## Data still needed (from real play sessions)
- Layer logs from menu, gameplay and cutscenes (M1 exit check).
- Short recordings of each kitchen station and transitions.
- Which headset and runtime are used (SteamVR / Meta Link / Virtual Desktop).
