# Order Up!! in VR: what is still flat, and the plan

Studied with the headset simulator (`tools/Sim.ps1`, below) in Burger Face, on 2026-11-18. Each frame the game draws:

1. a depth-only pass of the scene (67 draws),
2. a 1280x1280 shadow map from the light (79 orthographic draws, no colour target),
3. a small 1024x588 shadow mask of the scene, blurred twice,
4. the colour pass of the scene (~150 draws, the blurred mask bound in texture slot 1),
5. the HUD: 5-11 flat draws (no MVP) straight into the scene's target, then the copy to the screen.

The game only draws what its camera sees: every station camera has a field of view of 22-32 degrees tall
(float property records, value at +0x0c, camera object at +0x04, type 0x00344d60 at +0x20; the camera object keeps
the same value at +0x64 after its position stored twice). That is why customers, the street outside the windows and
machines disappear as soon as you look away from where the game's camera points.

## Fixed in 0.12.0

| What | Before | Now |
|---|---|---|
| Order tickets, timers | stuck to your head (they read a scene copy in slot 1, so the mod took them for a copy) | a panel in the room at 1.5 m, natural size, readable |
| Blue band at the top | the ticket rail's background, stuck to your head | part of the ticket panel |
| Pause menu (Resume, Options, Quit) | huge, far away, follows the scene depth | a panel in front of you; pointing at it works |
| Shadow map | bent as if it were HUD | left alone |
| Things outside the camera's view | not drawn (customers vanish, grey windows) | wide view: every camera is widened to up to 80 degrees tall while you are inside the game, restored for cutscenes and menus |
| Which picture is "the scene" | the first 3D draw (a shadow mask in Burger Face) | the target most 3D draws go to |

## Still flat, in the order to do them

1. **Order tickets as objects.** Draw them with the mod as paper tickets on the rail above the pass (3D, both hands,
   touch one to open its steps). Needs: the ticket list (read from the HUD draws' textures or the game's order
   objects in memory) and the pointer mapping for a touched ticket. The HUD panel stays as a fallback.
2. **The game's cursor** (the pointing hand icon): hide it inside the game; the chef's own hand and the laser dot
   point instead. Needs: recognising the cursor draw (its texture) among the HUD draws.
3. **Button prompts** (Accept / Back with PlayStation Move icons): replace with Quest controller pictures.
4. **Pause menu as a VR panel** drawn by the mod in the new card style, with the game's three choices mapped to it.
5. **The sets end where the camera cannot see**: Burger Face has no ceiling and its left wall and floor stop in mid
   air. Close the sets with mod-drawn ceilings and walls in each restaurant's colours (measured from the scene).
6. **Loading screens** (the manager on blue): show in the diner, not as a flat picture.
7. **Menus** (title, Quick Play, career): today on the diner's menu board; plan: real buttons in the diner.
8. **Cutscenes**: big screen today; next: inside the game for still shots, with a fade on every cut.
9. **Picking food with your hand** goes through the game's screen position: hook the game's picking so a 3D ray
   from the hand selects, independent of the camera.
10. **Shadows** are softer with the wide view (the shadow map covers a bigger area).

## The headset simulator

`ORDERUP_VR_SIM=<folder>` runs the mod without a headset: a virtual head looks where `<folder>/sim.txt` says and
the game renders for it exactly as for a headset. `tools/Sim.ps1` drives it:

    Sim.ps1 start                 start the test install (new rpcs3.exe copied in)
    Sim.ps1 quick 1               calibrate, Quick Play, Burger Face
    Sim.ps1 look 70 0             turn the head (degrees; m70 for -70)
    Sim.ps1 shot name             name_eye0.png, name_eye1.png, name_flat.png, name_state.txt
    Sim.ps1 io 'BTN:64;WAIT:300;BTN:0'   buttons (2 Move, 4 Cross, 8 Circle, 16 Square, 64 Start)
    Sim.ps1 drawlog               every draw of one frame with its classification (drawlog.txt)
    Sim.ps1 peek 3005c9c0 60 | find 26.2 26.3 | poke 3005c9e4 70   game memory

The see-through background shows as magenta in the pictures. `rpcs3.exe --ouvr-cards <folder>` draws every
headset card to PNG.

## Next: real grabbing and placing (VR rebuild plan)

The game plays with one pointer: point at an ingredient, hold the T button to pick it up, move the pointer to a station
or the plate, release; motions (chop, flip, grate, pull lettuce apart) come from the Move's sensors. Today the chef's hand
drives that single pointer (hands mode), so reaching for an item and holding GRIP picks it up. The rebuild:

1. Find the game's own "item under the pointer" and "drop on station" calls in the EBOOT and the compiled scripts
   (kitchen_ui.csl and friends) and call them with a 3D ray from each hand instead of a screen position, so both hands
   can hold things at once.
2. Draw the held item in the mod at the hand (the game's own model, like the chef), so it sits in your palm.
3. Stations and plates get mod-drawn highlight volumes; releasing inside one calls the game's drop.
4. A wrong drop shows a short message where you dropped it.
5. The tickets become paper objects on the rail (read from the game's order list), touched to open their steps.

## Restaurant files (lobby, 0.14.0)

Each restaurant package in Chef.wad (diner cca045da, mexican 2d1cd440, italian b95ae814, worldclass baa69b86,
asian 906a2b6d, fastfood 4a13fc83, drive-thru 8c524289, final stage 6ce6f2f7) holds:

- **Scene model** (`u32 1, u32 materials`): material records (variable size; found by `ff ff cd` at +0x10 and 0xfc
  filler; +8 flags, +0x5c material-file hash, +0x60 = 1 for see-through), node table (count, 12 x 0xbe, then 0x60-byte
  records: local bounds, name hash `~crc32(lowercase name)` at +0x34, part count at +0x3c), then the parts. Each part is
  two Edge segments (drawn + shadow copy). Segment header: flags `81 0x 00 06 00 ff 30 00`, vertex/index counts at +8/+10,
  index stream size at +20, position block size at +0x20, attribute block size at +0x32 (24 bytes per vertex = RGBA + UV,
  36 = normal + RGBA + UV). The part's material record id sits 0x198 bytes before its segment.
- **Layout** (`u32 1, u32 0, u32 count`): name (first length u8, then u16), position, quaternion (x y z w), scale.
- **Material files** (`u32 2, u32 count`): per material, its pictures: width, height, format (0x18 DXT1, 0x20 DXT5),
  mip count, image id, byte size. Pictures: `images/imgdat/<id>.imgdat.ps3` in a separate picture package.
- **Scene script** `models/scenes/<name>/<name>.csl` (FITG/MLSC bytecode): NodeProps for gameplay nodes (cameras,
  seats, paths, triggers). Not needed for the lobby.

## Menus as VR objects: next steps

Done (0.14.1): the HUD panels have physical housings in true VR (steel ticket rail, side stands, a wooden menu board
on an easel). The panels themselves are still the game's own HUD pixels mapped onto them.

Next: the UI art (ticket cards, meters, menu buttons) lives in the same packages as `images/imgdat` pictures referenced
by the UI csl scripts; reading those scripts (same bytecode as the scene script, see the lobby's interpreter notes)
gives the button layouts and the actions they call, so menus can be rebuilt as 3D buttons that send the same input.

## Menu system (found in the EBOOT, BLES01470)

The game builds every menu and the kitchen HUD from its UI scripts (`kitchen_ui.csl`, `ticket_reel.csl`,
`spice_rack.csl`, `worldmapmenu*.csl`, `restaurantfrontmenu*.csl`, `restaurantnewspaper_ui.csl`, ...) through
natives such as `MenuNewElement{name, parent, type, pos, dims, image, hilite_image, focusable, events{selected=script}}`,
`ElementSetPosition`, `MenuHide`, `MenuSetFocus`, `MenuSendMessage`.

- Natives: binding table entries (name string, OPD, 0); e.g. ElementSetPosition 0x1b4124, MenuHide 0x1b8080,
  ElementSetBaseDimensions 0x1bbb74, ElementSetImage 0x1b4f04, MenuSetFocusById 0x1ad778.
- Menu system: `*(u32*)0x0036df0c`; open menus: circular list at +0x2c (node +0 next, +4 menu); root element menu+0x10.
- Find element by name: 0x1e2fb0 (each menu root, vtable slot 0x120 compares +0x14c, recurses into +0x18c children).
- Element: +0 vtable (base 0x342c40, panel 0x342e00, image 0x343748); +0x14c name hash = ~crc32(name);
  +0x50 position x y z w (ElementSetPosition -> slot 0xd0 -> 0x1e9548); +0x78/+0x7c base size (slot 0xb0);
  +0xd0 offset (slot 0xe0); +0x40 another vec4 (slot 0xc8); +0x180 flags (bit 2 hidden: slot 0x48; bit 3 dirty);
  +0x18c children list (containers) or texture pointer (images).
- Element types are hashed the same way: sprite 0xcae27061, image 0x3ac2fba0, text 0xc4745838, button 0xc5f953c2,
  panel 0x5d522cf0, slider 0x3038eff8, checkbox 0xe184f712, progress_bar 0x002fba55, model 0x286a8d26.
- The mod (Experimental) logs the live tree to log/orderup_ui.log every 3 s (OrderUpVR_uitree.inl).
Next: from one log, how positions add up to screen pixels and which GPU texture each image draws with; then each
element (plate, trash, oven, food processor, tickets, menu buttons) becomes a 3D object at its kitchen spot, and
touching or dropping on it drives the game's pointer to that element.
