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

## Camera memory research (0.8.1)

Goal: find the game's camera in PS3 memory so the mod can (1) widen the game's field of view, so it culls less
and draws more of the world around you, (2) turn the game's own camera with your head, and (3) find other state such
as the chef chosen in the profile.

The renderer already knows the camera exactly each frame: the view rotation is the rotation most unrotated scenery
draws share (so a per-object model matrix cannot fool it), and the projection terms come from the MVP rows.

Method, fully automatic during normal play (log/orderup_research.log):
1. Every 25 s in a 3D scene (12 scans at most), scan guest memory 0x00010000-0x40000000 for the camera in every
   common form: 3x3 rotation (4 layouts, row strides 3 and 4), forward/backward vectors, quaternions (xyzw/wxyz, of
   the view and of its inverse, either sign), yaw/pitch in radians and degrees (several conventions), ten
   field-of-view forms, and the camera position once a 4x4 matrix follows the camera. Separate caps per kind so
   common single values cannot crowd out the rest.
2. Every half second, re-check candidates; drop those that stop following the camera.
3. When the picture has been still for half a second, change a candidate a little and watch the first 3D draw's
   matrix for up to 8 frames, then restore it. Rotations, vectors and quaternions are tested one by one; single
   values (field of view, angles) are changed in groups of up to 512 and a reaction is narrowed down by halves to
   the one value that matters (about 10 tests instead of thousands). "Moved the picture" marks values that drive
   rendering; "rewrote" means the game recomputes them every frame, so their source is the next target.
4. Memory capture for offline analysis: a copy of all readable memory while the camera is still (A), then, once the
   camera has turned more than 3 degrees (or anything changed after two minutes), the pages that changed (any form
   of a moving camera must be among them) plus pages holding field-of-view values, both versions, in
   log/orderup_camdump.bin.z (at most 2000 pages). tools/analyze_camdump.py searches it for every camera form
   matching camera A in copy A and camera B in copy B, and as a fallback lists unit directions that turned by the
   camera's own angle. So a form nobody predicted can still be found without another play session.

## What the game files show (offline analysis, 0.9.0)

- `USRDIR/EBOOT.BIN` is a disc SELF (APP key revision 0x16); it decrypts with the keys in RPCS3's own key vault.
  The program is SuperVillain Studios' engine with its own compiled script language (`.csl`, "MLSC" bytecode, names
  and strings readable). Kitchen behaviour lives in scripts: `kitchen_ui.csl` has `MoveStationLeft`,
  `MoveStationRight`, `EnterKitchenView`, `SetCameraName("KitchenCam")`; tutorials mention station views
  "up", "down" and "sidescroll".
- `USRDIR/Chef.wad` ('0DAW' v4): 5251 entries named by CRC32 (no final xor) of their path, e.g.
  `wad/startup_scripts.wad`. Header 0x30 bytes, then n x (name hash, index) sorted by hash, then n x (offset, size).
  119 entries are nested WADs with the same layout whose files follow the table as one zlib stream.
  `tools/order_up_wad.py` reads a player's own copy.
- Kitchen stations are named camera nodes in the scene data: `KitchenCam`, `CookingCam`, `fryer_cam`,
  `fryer_ticket_cam`, `cutting_area_cam`, `plate_cam`, `trash_cam`, `sink_cam`, `patron_cam`, `grater_cam`,
  `buffet_cam`. The intro movies come in male and female versions (`movies/cin01m.pam`, `cin01f.pam`).
- Station change buttons: Cross and Circle (Quest A and B).

## Room mode and look-to-change station (0.9.0)

The renderer knows each 3D draw's view-space transform VM = V*M (MVP with the known projection removed). Between
two frames, static objects satisfy VM_now = Delta * VM_before, where Delta = V_now * V_before^-1 is the camera's
motion. Its rotation comes from the rotation most unrotated scenery shares; its translation is the value most
paired objects agree on (pairs by draw order with a +-3 shift, rotation-checked; moving objects disagree and are
outvoted). D = V_anchor * V_now^-1 is accumulated (D <- D * Delta^-1) and every 3D draw becomes
Peye * E * Level(up in anchor space) * D_predicted * P^-1, so the kitchen is fixed in the room and a station change
shows the new station where it really is. D for the frame being drawn is predicted at constant velocity from the
last two frames (logged as "worst guess off by"). Three unsolved frames in a row (a cut) re-anchor at the new view.
Simulated: 40 frames of motion with culled, moving and rotated objects recover D to 1e-14.

Pointing maps the hand into the anchor's view, then by D^-1 into the current camera's view before projecting.

Stations (after 0.17.11): the game's camera follows you. Each frame the mod finds the station row the game camera looks
at (the kitchen's station nodes from Chef.wad, the camera from 0x003821d8) and where you are on it, in the game's
world. When you reach (eye -> hand ray onto the counter or the row) or walk past the edge of what the camera frames
in the game's own (not widened) view, toward a station it does not frame yet, the mod presses Cross or Circle for
you (after 0.45 s reaching or 0.7 s standing there; never while gripping, in a menu or dialog, or in a cutscene).
Which button goes which way is learned from the camera's own move, seen from where it looked before (your own A/B
or the mod's first press), and stored as `station_cross` in orderupvr_state.txt; a button that does not take the
view where it was meant to is not pressed again from that camera spot. The old head-turn trigger (`lookswitch`) is
gone. A TRIGGER or GRIP press past the edge of the game's picture waits (the game does not get it) while the camera
goes to the station that way, then lands there; with no station that way it lands on the picture's edge when it is
within 20% of it, else two short buzzes say nothing can be reached there.

HUD cards (after 0.17.11): every card of the game's HUD is its own plane in the kitchen. Each game frame the renderer
lays out the cards from the UI tree (rv_layout_cards): order tickets (direct children of `order_reel*`) hang from the
rail, tilted and swaying; the assistants (`AsstChef1/2`, `assistant_*`: one card per group with its timer bar and
ingredient) stand in a row on a stand on the counter at your left; gesture, utensil and spice cards (`gesture_icon_*`,
`spice_*`) stand on a shelf on the counter at your right (a plank higher when a row is full); the recipe card
(`orderbig`, `orderbig_ticket` from ticket_reel.csl create_big_ticket, category C_RECIPE) is on a clipboard in your left
hand, or stands on the counter while that hand is untracked or holds the EXIT button. Stand, shelf and counter spot
are placed with the HUD, at the kitchen's own counter top when its layout is known. Each matched HUD piece is drawn on
its card's plane (one renderer value per card and level, so the matrix is re-uploaded); pieces the UI tree does not
place exactly (a ticket's scaled dish icon) go onto the smallest card holding their middle; an assistant's own 3D
portrait (another lens) goes onto its card. The cards drawn each frame (plane + game-screen rectangle) go back to the
main thread in `cam_info::cards`; touch and rays hit those exact planes first (nearest card), and the spot hit is the
same spot of the game's screen, so the press lands on that element. The flat panels stay for pieces nothing matches.
Inside a menu (hiring, buying spices) assistants and spices stay part of the menu. Log: "Cards: N tickets on the rail ...".

Menus within reach (after 0.17.11): "a menu is open" comes from the game's UI tree (uitree::menu_open: an element whose
name makes it a menu, covering at least 4% of the screen, or a named dialog card 400-1200 x 200-700 px) while the game
draws something of it (vrui::menu_drawn, from the renderer's marks below; the renderer gets it in g_views), no longer
from "the menu panel drew something last frame": that latched on for the whole kitchen (every unmatched HUD piece went
to the menu panel and kept it seen), so the panel never came to a dialog that opened, and it caught the hand in front of
the food. While a menu is open, only the unmatched HUD pieces on the part of the screen its menus and dialogs cover go
onto the menu panel; the rest of the HUD stays where it is. The menu panel is pointed at only while a menu is open (or a
menu group was drawn on it), and then before the cards. The UI tree now keeps each element's parent, class (vtable),
name flag and whether it is the centred copy.
Each menu's buttons become keys (vrui::find_keys): elements the scripts name as buttons; items of a list (two or more
siblings of the same size side by side or one below the other: the restaurant list, the title and pause menus); small
elements in the lower part of a dialog card (OK, Yes/No). Only inside a menu or dialog, and only where the game really
draws something: the renderer marks the smallest UI element each of the game's flat draws lands in (rv_mark_drawn,
`g_drawn`), menus and lobby too. A key holding two or more keys is a box (dropped); a key inside a key is its label.
In the lobby (true VR, the game's flat picture) the menu goes onto the menu stand: a lectern 0.44 m in front of the spot
you stand on, its 0.72 m screen 0.34 m below your eyes, sloping back 35 degrees, with a wooden bezel, a post and a foot
(part of the lobby: it moves with it). The game's picture is that screen (the quad layer moves there); loading screens
and films go back to the big screen. Inside the game the menu panel opens 0.55 m in front of you, 0.24 m below your
eyes, sloping back 25 degrees, each time a menu opens, and it is tested before the cards while a menu is open. Every key
is a raised rim round the game's button (never over its picture); your fingertip pushes it in and presses at its top
(stand: 1.5 cm above the screen; menu panel: 3.1 cm, where the menu's stacked pieces are), or point at it and pull the
TRIGGER. The pointer snaps to the button's middle with no smoothing, a press reaches the game for at least 90 ms, a
light tick when you reach a key and a firm one when it goes in. The live pictures show the game's flat picture where
the left eye sees it (big screen or stand), not only as the inset. Logs: "Menu keys: N (...): x,y wxh named|list|dialog
vt <class>" (which class the buttons are), "Menu stand: ...", "Menu: key K of N pressed (fingertip|trigger, ...)";
the track line ends with "| menu M keys N key K [touch] stand S".

Grabbing what you touch (after 0.17.11): the game picks up what is under its own pointer when GRIP goes down, and the
old grab only made a picture follow your hand (one try per grip, by model origin within 0.6 units, else by the pointer
within 0.15 of the screen: the live logs show "nothing at your hand" every time). Now (grab_pre on the main thread,
rv_hold_frame on the renderer) closing your hand in the kitchen (not on a ticket, card or menu, not the EXIT hand) holds
the game's GRIP back for up to 0.2 s while the renderer looks at what the game drew: the thing within 32 cm of your grab
point (a thing in the line from your eyes through your hand counts nearer), else the thing seen within 7 degrees behind
your hand (food out of reach), else the thing under the game's pointer; only things inside what the game's own camera
frames. Found: the game's pointer goes to its middle 40 ms before GRIP goes down and stays there 180 ms, then moves over
0.3 s to your grab point seen from the game camera (where the food is drawn, in your hand), or moves with your hand's
pointer when your hand is outside that camera's view. Not found: GRIP goes on at once as before and the renderer keeps
looking while you hold it; when your closed hand reaches food, GRIP is let go 90 ms and pressed again on it. No drop spot
takes the pointer while the game is picking up. Holding food in one hand, the other hand's stick walks or turns. Answer
from the renderer: g_cam.grab_id/grab_state/grab_nx/grab_ny for g_views.hold_id. Logs: "Grab: N parts in your hand
(touched|seen behind your hand|under the game's pointer, ...)", "Grab: nothing at your hand to hold yet (...)",
"Grab: the game's pointer goes onto the food at your hand (...)".

Height against the kitchen (after 0.17.11): standing with a known floor (the headset's stage space, else the lowest a
controller reached while held: a controller lying still does not count; forgotten when the headset is recentred), your
eyes go to your real eye height above the kitchen's floor, so your real floor is its floor even beyond the scale's limits
(0.85-1.35 m per unit). Seated: the chef's 1.6 m scale, and the kitchen's counter tops 0.45 m below your eyes (a table,
not your lap). The stations and holders use the kitchen's counter height you were placed at (vrui::counter_y). The
automatic re-placement compares the scale it would give (no more re-placing every 20 s for very tall or short players)
and waits while you grab, a menu is open or a press is held. interior::pick keeps the kitchen you were placed against
(interior::sticky) unless another is clearly nearer, so height, rail and stations use the same kitchen; the rail, trash
and oven only use a kitchen whose floor agrees with the camera's height. Diagnostics: every live picture's summary ends
with "|| st seated|standing unit U eyes E (real R) floor guessed|headset|hand | place N rule .. eye Xu cam Yu kit PKG
used|rejected|none pick PKG | rail kitchen|world|room trash .. oven .. | sf row|no-row PKG Nst cross S [held T|G] [going X|O]
| cards tN aN sN rN | on TARGET [touch] | eyes L x R y ms [together] | pic z ms old". Decisions (Interior:, Height,
Camera height, Restaurant floor, Recentred, UI tree:, Stations:, Comfort, Cards:, Menu keys, the start) also go out as
Sentry events, logger "state", one issue per kind (fingerprint state/<kind>), rate-limited (2 s, UI tree 10 s; a burst
sends its newest line with a count). Lines logged before the stream starts are kept and sent once it does, marked
"(before the stream)".

## Findings from the 0.9.0 session (issue #1, OrderUpVR_logs_20261117_215838.zip)

- Room mode held: 59-900 frames per 15 s solved with 76-217 objects each, prediction off by at most 2.7 cm, no re-anchors.
- Black right eye in the restaurant cutscene: the game presents each picture twice there; eyes alternated per present,
  so every new picture went to the same eye. 0.9.1 alternates per frame with 3D draws.
- Camera found in memory. A camera object at a fixed address in the game's data: 4x4 view matrix (rows) at
  0x003821d8, camera position at 0x00382218 (matches -R^T t of that matrix in both captures), projection terms
  next to it (0x00382028 sx). The research also confirmed a field-of-view value (degrees) that moves the picture
  and is kept by the game (0x38c5bf5c in that session; heap, so it must be found per session).
- Chef models: Models/Characters/PC_MaleChef and PC_FemaleChef with mesh parts (base, limbs, hat, eyes, nose,
  chin, eyebrows) and skeletal animations (idle, walk, run, chop, mix, ...); the script function IsMaleChef tells
  which chef the profile uses.

## Chef models: where they are and what is still unknown

- Female chef: nested WAD 0x3dc1261e in Chef.wad (40 files, 910 KB). Male chef: in 0x6ce6f2f7 / 0xdfaab736.
  Inner file names are hashed differently from the outer table (path CRCs do not match); files are identified by
  content.
- `pc_femalechef.csl` (script): parts femalechef_base, _limbs, _hat, _eyes, _eyebrows, _nose, _chin.
  `assets_pc_femalechef.csl`: 14 animations (neutral, idle, idle_alt, fidget, gesture, walk, run, chop, mix,
  addIngredients, acknowledge) and their footstep/sound events.
- 0xb4162e7e (256 KB): a serialized scene. 0x240-byte material records (shader hash, six texture hashes, colour,
  alpha cutoff 0.04), then large float arrays within +-1 (normals/tangents/rotations). No plain index lists.
- Files starting 0x00001b5b/0x00001b5c: serialized animation graph objects. Files padded with 0xAB: keyed
  animation tracks (u32 key + xyz, quaternions).
- Graphics WADs (gfx_*) hold DXT textures only (e.g. gfx_fortified_chef_intro 0x1b65e354, 149 files).
- The program has a "geom-jobchain" (SPURS): geometry is decoded/skinned on the SPUs at draw time, so the mesh is
  stored in an SPU-oriented layout; no archive of the 120 contains plain 16-bit index runs.
- The player chef is drawn by the game only in the Fortified Chef cutscenes (worldmapmenu.csl, IsMaleChef ->
  Challenger_PCMale/PCFemale, Fortified_PCMale/PCFemale), so a capture from the draw calls needs that scene.

## The game's own chef as your body (0.10.0)

Decoded from the game files (OrderUpVR_chef.inl; offline check: `rpcs3.exe --ouvr-chef-test <USRDIR> <out prefix>`):
- Model files: female `b4162e7e` (nested WAD `3dc1261e`, also `6ce6f2f7`), male `c3572b34` (nested WAD `dfaab736`).
  Body textures are 1024x1024 DXT1 files named `images/imgdat/<hash>.imgdat.ps3` (CRC32 without inversion):
  female `a486983b`, male `d9e02c46` (found by searching the nested WAD tables).
- Geometry is Edge: per segment a 16-byte SPU config (byte 0 0x83 = full vertex data, 0x81 = shadow copy with positions
  only; byte 6 0x33 = compressed triangle list + skinning; u16 at +8 vertices, +10 indices, +20 compressed index size).
  The compressed index stream (8-byte header: count of explicit indices, ?, flag-byte count, bit width) is decoded by
  running the game's own SPU job (spurs/spu_codef.ppu.sprx, decrypted with RPCS3's decrypt_self; job image at segment
  +0x100, wrapper entry LS 0x6e60, context: config at +0, index count at +0x190, IO end at +0x17c, output pointer
  written to +0x50) in a 62-instruction SPU interpreter.
- After the stream: positions (float3, padded to 16), normal+tangent (2 x float3), RSX block (RGBA float4 + UV float2),
  then skin weights (4 x (weight byte, bone byte), weights sum 255).
- Bones are per-segment local indices; the mod classifies them by where their vertices are in the T pose (arms are
  horizontal at y ~0.83: shoulder < 0.34, forearm < 0.56, hand < 0.86, fingers beyond; head above 1.05; legs below
  0.33). Pose: chef eyes at your eyes (scale = eye height / 1.45), two-bone arms (stretching when you reach further than
  the chef can), hands matched to the controller grip frame (fingers forward, palms inward), fingers curl with GRIP.
- Mirror: stencil mirror in the lobby room (glass marks the stencil, depth reset to far, room and chef reflected).

## 0.11.0: cutscenes, walls, scale, automatic chef, hands

- Cutscene detection (rv_room_update): a camera cut, or a camera move over 1.5 m / 45 degrees while no button was pressed for 3 s, sets `cinema`; the renderer then leaves the game's draws alone (mono, big screen in the diner). After 75 still frames the kitchen is anchored afresh in 3D.
- Station-local measurements, kept until the next anchor: `hud_depth` (20th percentile depth of on-screen objects, times 0.92), the restaurant extent (4th-96th percentile of object origins in the levelled anchor frame) used as stick walls, and the world scale (5th percentile object height = floor; 1.62 m / camera height).
- Chef choice: 48-byte slices of each chef's DXT1 body texture are searched in RSX local memory (0xC0000000, 256 MB) every 5 s; when only one chef's pieces are present, that chef is used.
- Hands: both chefs' hands have a hand bone, a two-bone thumb (forward of and below the palm) and two two-bone fingers. Joints scale with each chef's arm length; the hand is shrunk to about 24 cm; the OpenXR grip frame maps to the hand with wrist-to-knuckles = grip (0, -0.866, -0.5).

## Arms and the EXIT button (body-exit pass)

- Arms: the cartoon chef's shoulders sit about 0.68 m below and 0.34 m behind your eyes and its arms are 0.46 m long, so
  reaching to a controller in front of your chest stretched them 1.6-2.3 times. Now each arm starts at your own shoulder
  (`chef::real_shoulder`: a neck 10 cm below and 8 cm behind your eyes, shoulders 13 cm under it and 17 cm out, scaled
  with your eye height, facing `body_yaw`) and has a person's upper arm and forearm (0.199 and 0.156 x eye height). The
  chef's sleeves are drawn along those bones at a fixed length for you. Reaching further than the arm, the hand stays on
  the controller and the shoulder end slides toward it (never a stretch, never a gap at the wrist). The simple body does
  the same. In the plain diner (its mirror shows the whole chef) the arms blend over onto the chef's own shoulders and
  may stretch up to 1.3x there. Log: `Arms: from your own shoulders, upper arm ...`.
- EXIT button: comes out only when the left GRIP closes over your left shoulder (within 25 cm, not below its top, not in
  front of it) and stays closed there for 0.4 s (a growing buzz), never while either hand holds food. A grip there is not
  a press for the game and does not switch the pointer hand. The red cap must be held all the way down for 0.25 s to
  close the game. Logs: `Exit button: not taken out (...)`, `pushed all the way down`, `came up again`.
