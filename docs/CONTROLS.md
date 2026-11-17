# Order Up!! (PS3) control scheme: research and in-game confirmation

Date: 2026-11-16. Build tested: `BLES01470` v01.04 on RPCS3 0.0.43-20607.

## Summary

The PS3 port has **two control schemes**, and the game picks per player at boot:

| Scheme | Hardware on a real PS3 | How cooking actions are done |
|---|---|---|
| Wireless controller | DualShock 3 / Sixaxis | Left stick moves a hand pointer; hold ✕ to grab and drag; hold ○ and **draw a gesture with the left stick**; some steps use **Sixaxis tilt** (reported in reviews; not yet reached in my own run) |
| PlayStation Move | Move wand + PlayStation Eye camera | The wand is the pointer; the same gestures are made by moving the wand |

So the "PS3-specific interaction" is motion sensing: either the Sixaxis tilt sensor inside the PS3 pad or the Move wand.
An Xbox controller has neither.

## Evidence

### Published sources
- Push Square announced the PS3 version as a Wii port "with Move support".
- Store listings describe it as supporting the Sixaxis wireless controller with PlayStation Move compatibility.
- SuperPhillip Central's review (dual-analog play) describes: flick the stick right to pull lettuce leaves, push the stick down in rhythm
  to slice tomato, stir with the stick, and "tilt the controller to coat the frying pan's surface" (Sixaxis).
- PlayStation LifeStyle and Gear Diary reviews say Move is optional and the pad works with the analog stick substituted.

### From the game running here
- RPCS3's import log: the executable links `libgem` (Move), `cellCamera`, and `sys_io` (pad).
- With an emulated Move connected, the boot screen says "The game has detected multiple active controllers" and shows a
  DualShock in the Player 1 slot. See `img/controller-select.png`. A controller joins by pressing START.
- Tutorial (Burger Face drive-thru), played with the pad scheme through a keyboard-emulated DualShock:
  - "Point at an item on the Menu Ticket and press and hold ✕ to grab it. Use the left stick to drag it to the cooking area
    and release ✕ to start cooking!" (`img/tutorial-grab-instruction.png`). This worked: the patty went on the grill.
  - "Press and hold ○ to grab the current food item. Utensils appear automatically! While grabbed, draw the on-screen
    gesture to interact with it. Flip the burger!" (`img/tutorial-gesture-instruction.png`)
  - The flip prompt shows a stick icon tracing a half-circle arc: "Hold ○ and use left stick to flip" (`img/flip-stick-gesture.png`).
  - **Open issue:** my scripted flip did not register after three attempts. A keyboard gives only on/off stick directions, so this may be a
    limit of my test input and not of the game. It may also be the same wall the user hit with the Xbox pad. Not resolved yet.

### Scene structure (matters for VR)
- Kitchen overview: a fixed, wide camera on the whole counter (`img/kitchen-tutorial.png`).
- When an item is placed on a station, the camera **zooms into that station** (`img/grill-station.png`). The VR camera work
  has to cope with these animated camera moves.

## What this means for the mod

- Use the **Move scheme** in VR. A Quest 3 controller maps naturally onto the wand: aim = pointer, hand motion = gestures,
  trigger = T, grip = Move button, A/B/X/Y = ✕ ○ □ △, menu = START.
- The Sixaxis scheme stays available as a fallback (a VR controller could also feed tilt), but it is second priority.
- Still to verify in Move mode: which button grabs (T or ✕), how gestures are recognised (pointer path vs. wand acceleration/rotation),
  and whether calibration screens appear.

## Sources
- https://www.pushsquare.com/news/2011/11/former_wii_title_order_up_heads_to_playstation_3_with_move_support
- https://www.superphillipcentral.com/2012/07/order-up-ps3-review.html
- https://geardiary.com/2012/04/14/order-up-for-playstation-3-review/
- https://www.playstationlifestyle.net/?p=162810
- https://forums.rpcs3.net/showthread.php?tid=198165 (RPCS3 thread for the US release, BLUS30904)
