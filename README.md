# Alternative Damage Model

A mod for Sea Power.

## Features

1. **Stronger hulls.** Hull integrity of warships, civilian ships and submarines is multiplied by your own factor (per ship too). A hit takes the same share as in the game, so ships survive more hits. Side effects are tunable: gun shell damage tiers, fires and flooding from hits.
2. **Fire and flooding speeds.** How fast fires grow, burn the hull and are put out; how fast water comes in and is pumped out.
3. **Damage control teams as people.**
   - Number of teams by displacement (warships, submarines, civilian ships).
   - Teams walk between sections: muster time plus the walk at their speed. A team on the move takes no orders; a transfer can be cancelled.
   - A knocked-out team recovers and returns to the reserve; knocked out twice, it is lost.
4. **Smarter Auto DC** while transfers take time: a team does not leave a section with fire or flooding still to fight, every job gets the nearest free team, more teams go where fire or water is bad or rising fast, idle teams wait where they are.
5. **DC Priority** for the whole ship: Auto (the game's order), Fire first or Flooding first.
6. **Orders to single teams** with Auto DC off: click a team to give it a task, move it to another section or send it to the reserve.
7. **No instant sinking from list or trim.** Past the list limit the low side floods fast; past the trim limit the end that goes down floods fast, compartment after compartment. The trim limit depends on the ship's length. The ship sinks when too much water is in it, as in the game.
8. **Detailed Damage Control tab:**
   - Hull integrity of each side next to the section name, with a trend arrow.
   - One icon per team (DC1, DC2...) showing what it is doing; teams on the move and recovering, with their time.
   - Water and fire in % with trend arrows. Their tooltips explain why: leaks, who is plugging or pumping, a hit, list, water that stays until the hull is welded...
   - Water shown yellow / red while damage control can still get it out, grey when it cannot.
   - LIST / TRIM / LEVEL gauges; their tooltip is a plan of the ship with the water in every compartment.
   - A legend behind the (i) next to the tab's name.
9. **Settings in the game:** the gear next to (i). Profiles: *Base Game* (as close to the unmodified game as possible), *Advanced* (the mod's damage model), or your own (Save as / Rename / Delete). Changes apply at once, also to ships at sea.
10. Saved games keep the state of the damage control teams and the ship's DC Priority.

## Compatibility

- Supports Sea Power version 0.8.4.
- Requires Anchor Chain.
- Can be switched off in the game's Mods menu: the game's damage model is used then.
- Do not use together with mods that patch ship hull integrity (IntegrityOverride ini patches): the multiplier would be applied twice.

## Install

1. Install the Anchor Chain preloader as described in the [documentation](https://seapower-modders.github.io/AnchorChain/install-anchor-chain.html).
2. Subscribe to this mod.
3. Restart the game.

## Usage

- Unit Status → Damage Control: point at icons, arrows and gauges for details; (i) for the legend.
- Settings: the gear next to (i), or the file
  `Sea Power/BepInEx/config/com.rgreat.seapower.alternativedamagemodel.cfg`
  (changes in the file are picked up while the game runs).
- Your own profiles are stored in `Sea Power/BepInEx/config/AlternativeDamageModel/Profiles`.

P.S. Works well with the [Camera Offset](https://steamcommunity.com/sharedfiles/filedetails/?id=3676689138) and Smart Windows mods.
