# True VR plan

Goal: play Order Up!! *inside* the kitchen, not in front of a screen. Stereo 3D, head tracking, a body with
hands, and grabbing food and tools directly with the Quest controllers.

## How it works in an emulator

The game draws its 3D kitchen through a camera: a set of numbers (a view and projection matrix) that the
game sends to the PS3 graphics chip with every draw. RPCS3 sees those numbers. Once the mod knows which
of them is the camera, it can:

1. **Head tracking**: replace the game's camera with the headset's position and rotation.
2. **Stereo 3D**: draw each frame twice, once per eye, with the eyes about 6.4 cm apart, and send both
   images to the headset as a real 3D view instead of a flat screen.
3. **Free movement**: move that camera with the left stick (smooth or teleport, with comfort options).

Hands and interaction come next:

4. **Hands**: draw hand models at the controller positions inside the same 3D scene.
5. **Direct grabbing**: when a hand closes (grip) on a food item or tool, translate the 3D hand position into
   the game's own pointer and "grab" input, so the game's logic does the rest. Moving the hand moves the item;
   flicking the wrist flips the burger, using the Move motion data that 0.4.0 already sends.

## Steps

| Step | What | Needs |
|---|---|---|
| A | Find the camera: which shader constants are the view/projection, and how the kitchen's camera zooms between stations | Research data from normal play (built into 0.5.0) |
| B | Stereo + head tracking on the kitchen (seated, camera at the counter) | Step A |
| C | Comfort: recenter, height, smooth or snap turn, left-stick movement | Step B |
| D | Hands drawn in the scene; grab and use items with the hands | Step B, and mapping 3D hand positions to the game's pointer |
| E | Menus and 2D screens stay on the virtual screen; cutscenes on a cinema screen | Step B |

Each step ships as one release once it works end to end, not as small pieces.

## What step A needs from you

Version 0.5.0 records the game's camera data while you play, without slowing the game:
`log/orderup_research.log` (camera-like matrices sampled every 20 s) and the game's vertex shader programs.
After one normal session that reaches the kitchen and a few stations, press **Collect logs** in the launcher
and send the zip. That is the data needed to locate the camera without trial-and-error runs.

## Risks

- PS3 games often combine model, view and projection into one matrix per object. Then stereo needs the
  projection split out in the shader rather than a single swapped number. The research data shows which case
  this game is.
- Effects drawn in screen space (shadows, glow, HUD) may need per-eye handling.
- The game's own fixed camera cuts (zooming into a station) must be blended with free head movement so
  they don't cause motion sickness.
