# True VR plan

Goal: play Order Up!! *inside* the kitchen, not in front of a screen. Stereo 3D, head tracking, a body with
hands, and grabbing food and tools directly with the Quest controllers.

## How it works in an emulator

The game draws its 3D kitchen through a camera: a set of numbers (a view and projection matrix) that the
game sends to the PS3 graphics chip with every draw. RPCS3 sees those numbers. Once the mod knows which
of them is the camera, it can:

1. **Head tracking**: replace the game's camera with the headset's position and rotation.
2. **Stereo 3D**: draw the scene from each eye, with the eyes about 6.4 cm apart, and send the images to the
   headset as a real 3D view instead of a flat screen.
3. **Free movement**: move that camera with the left stick.

Hands and interaction come next:

4. **Hands**: draw hand models at the controller positions inside the same 3D scene.
5. **Direct grabbing**: when a hand closes (grip) on a food item or tool, translate the 3D hand position into
   the game's own pointer and "grab" input, so the game's logic does the rest.

## Steps

| Step | What | State |
|---|---|---|
| A | Find the camera | **Done** (from the 0.5.0 logs, see below) |
| B | Stereo + head tracking in the kitchen and restaurant | **Built in 0.6.0**, waiting for a headset test |
| C | Comfort: recenter, levelled horizon, snap turn, left-stick movement | **Built in 0.6.0** |
| D | Hands drawn in the scene; grab and use items with the hands | **Built in 0.7.0**: body, arms, animated hands; the pointer follows the hand |
| E | Menus and 2D screens on the virtual screen | **Built in 0.6.0** (automatic switch) |

Each step ships as one release once it works end to end, not as small pieces.

## Step A results (0.5.0 research logs, one restaurant day)

- Every 3D draw reads vertex constants **c1..c4 = the rows of one model-view-projection matrix**.
- The projection is a standard right-handed perspective (w = -z) with no off-centre terms; its picture shape
  matches the 1280x736 render target exactly (aspect 1.739).
- Kitchen camera: near 1, far 20, pitched 45° down at the counter. Restaurant camera: near 2, far about 150,
  level. The kitchen zooms between stations by changing the field of view.
- 2D programs (HUD, menus, cursor) do not use c1..c4.
- Units look like feet (a station close-up is about 4 units away), so 1 unit = 0.3048 m by default
  (`worldscale=` in `config/orderupvr_state.txt` changes it).

## How 0.6.0 renders in 3D

- The projection's depth terms (A, B) can be read exactly from any MVP whatever the model matrix, so each draw
  is classified: same A/B as the main camera = 3D scene draw; flat draws on the same render target = 2D overlay;
  everything else (shadow maps, other passes) is left alone.
- The main camera's projection P is read from the first draw each frame whose picture shape matches the render
  target. For a 3D draw the mod applies `X3 = Peye · Eye · Level · P⁻¹` after the game's vertex program, through
  RPCS3's viewport (scale/offset) matrix. This replaces the game's projection with the headset eye's projection
  and view, for any model, with no shader changes.
- 2D overlays get `X2 = Peye · Eye · Level · S`, where S puts the overlay on a plane that exactly fills the game
  camera's view at the median distance of the scene's objects. The HUD therefore sits in the world.
- Level: world up is voted from the objects' own up axes (most scenery is not rotated), and removes the game
  camera's pitch and roll so the real and game horizons agree.
- Eyes alternate every frame (the game runs at 60 fps, so 30 per eye). Each eye image is submitted as an OpenXR
  projection layer with the pose it was rendered from, so the runtime reprojects head motion at full headset rate.
  `stereo=0` in the state file switches to one head-tracked image for both eyes (no depth, full rate).
- Pointing: the controller ray is followed into the game camera's space to the overlay plane and projected with
  the game's own camera, which gives the PS Move pointer position. The laser and ring are drawn at that point.
- The game's own camera still decides the view and which objects exist (it culls what it cannot see), so
  looking far past the game's view can show empty areas.

## 0.7.0 changes after the first headset test

- Double image: the shown buffer could hold an older frame than the one just drawn, so eye images were paired with
  the wrong eye pose. The renderer now tracks, per render target, which frame's eye view it holds (scene target ->
  draws that read it -> the shown display buffer) and tags the picture by the shown buffer.
- HUD: the game draws the 3D scene into its own target and copies it onto the display buffer, then draws the HUD
  there. Draws that read the scene are copies (left alone); other flat draws on the display buffers become the
  in-world overlay, in the eye view of the picture already there.
- Void: clears of the scene and screen targets use a key colour that the headset makes see-through, over a room
  drawn by the mod.
- Body and hands: drawn with D3D11 by the mod at headset rate as their own layer (grip pose, IK arms, finger curl).

## Risks and open questions for the headset test

- Screen-space effects (glow, fades) may look off in one eye.
- Objects the game culls outside its own view are missing when looking far to the side.
- The world-up vote and the HUD distance need confirmation in the real kitchen.
- If anything looks wrong, hold the left stick click for 1 second to go back to the big screen.
