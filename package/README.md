Content warning: mod is entirely vibe-coded, but had extensive playtesting, even if just on tenderfoot (for the sake of time\convenience)


![Peakle](https://raw.githubusercontent.com/loweffortslop/peakle/main/art/logo.png)

White Knuckle style climbing and inventory handling. Two independently working hands. 

Works in multiplayer

![Climbing a palm, a grip ring on each hand](https://raw.githubusercontent.com/loweffortslop/peakle/main/docs/screenshots/climbing.jpg)
![A lollipop in one hand, a bugle in the other](https://raw.githubusercontent.com/loweffortslop/peakle/main/docs/screenshots/two-hands.jpg)
![Floating slots on a backpack held in hand](https://raw.githubusercontent.com/loweffortslop/peakle/main/docs/screenshots/backpack-slots.jpg)

## Climbing
- **LMB / RMB** grab with your left / right hand. Hold to keep holding, let go to release
- Anything PEAK lets you climb is a hold, and so are ropes, vines, chains and pitons
- You hang and swing from your holds instead of sticking to the wall
- **Jump** while hanging to grab jump
- **Crouch** with no movement input holds you still
- Catching a wall mid-fall slides you down it and costs grip
- Each hand has its own grip, shown as a ring on the hand and beside the crosshair
- PEAK's status effects shrink your grip, the same way they shrink the stamina bar. Extra stamina keeps a spent hand
  holding on

## Items
- Each hand holds its own item. **LMB / RMB** uses the item in that hand
- Look at a teammate and use an item to give it to them (food, bandages, etc)
- **Q / E** put the left / right hand's item in that side's pocket, or take it back out
- **1 / 3** drop the left / right item, hold to throw. **2** swaps hands
- Grab things to interact with them: pick items up, open luggage, cook at a lit fire

## Backpacks
- **Tab** shows your backpack in front of you. Grab it to take it off into your hand.
  You can't rummage in the backpack on your own back. That's for your teammates, like in vanilla.
- A backpack on the ground, in your hand or on a teammate's back shows its slots.
  Look at a slot and click a hand to take an item out or put one in.
- With a backpack in your hand, **Tab** also shows a slot to put it on.
- Light a teammate's rocket pack, or refuel their jetpack, from the slot in the middle.

## Teammates
- Look at a knocked-out scout and **hold Tab** to carry them. Hold Tab again to put them down.
- Grab any scout to drag them, pull them up a ledge, or free them from webs and thorns.
- Or grab a teammate and climb them.

## Controller support and rebinding
Gamepads are supported. Peakle's controls have their own settings, separate from PEAK's Controls menu (which is
left vanilla): rebind them in **Mod Settings** ([ModConfig](https://thunderstore.io/c/peak/p/PEAKModding/ModConfig/)),
under *Controls - Keyboard* and *Controls - Gamepad*, or in `BepInEx/config/peakle.hands.cfg`.
The gamepad defaults follow White Knuckle's:

| | Gamepad | Keyboard / mouse |
|---|---|---|
| Left / right hand | LT / RT | LMB / RMB |
| Left / right pocket | LB / RB | Q / E |
| Drop / throw left / right | D-pad left / right | 1 / 3 |
| Swap hands | Y | 2 |
| Backpack / carry | D-pad down | Tab |
| Hold still while hanging | Hold B | Hold Ctrl |

## Multiplayer settings
Gameplay settings (everything under *Feel*, *Catch Slide* and *Scouts*) come from the host: in someone else's lobby
your own values for those are ignored, so nobody can make climbing easier for themselves. HUD, visuals and controls
stay your own.

## Settings
Every setting is in `BepInEx/config/peakle.hands.cfg`, or in game with
[ModConfig](https://thunderstore.io/c/peak/p/PEAKModding/ModConfig/).

## Known issues
- Emotes are disabled while a hand is holding something.
- Difficulty is much lower due to new mechanics. Either play on high ascents, or tweak the settings if you want a challenge

## Credits
- [White Knuckle](https://store.steampowered.com/app/3195790/White_Knuckle/) by Dark Machine Games, whose climbing
  this copies. Play it.
- [PEAK](https://store.steampowered.com/app/3527290/PEAK/) by Aggro Crab and Landfall.
- Built with [BepInEx](https://github.com/BepInEx/BepInEx) and [Harmony](https://github.com/pardeike/Harmony).
- Code written with Claude Code (Anthropic's Claude). No AI-generated art or audio.
