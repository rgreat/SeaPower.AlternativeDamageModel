Alternative Damage Model
A mod for Sea Power.

FEATURES

Upgraded Damage Control interface

1. Damage control teams as people.
   Number of teams by displacement (warships, submarines, civilian ships).
   Teams walk between sections: muster time plus the walk at their speed. A team on the move takes no orders; a transfer can be cancelled.
   A knocked-out team recovers and returns to the reserve; knocked out twice, it is lost.
2. Smarter Auto DC while transfers take time: a team does not leave a section with fire or flooding still to fight, every job gets the nearest free team, more teams go where fire or water is bad or rising fast, idle teams wait where they are while there is work, then go back to the reserve.
3. DC Priority for the whole ship: Auto (the game's order), Fire first or Flooding first.
4. Orders to single teams with Auto DC off: click a team to give it a task, move it to another section or send it to the reserve. Besides the game's tasks there is counterflooding: the team lets water into the side of its compartment opposite to the heel (into the side with less water when the ship is upright), twice as fast as teams pump out; only by hand, up to a limit (settings "Counterflooding", "Counterflood limit"). A fire does not spread into a section where a damage control team is (setting "Team stops fire spread"). A team ordered to counterflood pumps out only where that does not make the list or trim worse.
5. Detailed Damage Control tab:
   Hull integrity of each side next to the section name, with a trend arrow: yellow / orange / red while it can still be welded, grey when it is welded as far as it can be.
   One icon per team (DC1, DC2...) showing what it is doing; teams on the move and recovering, with their time.
   Water and fire in % with trend arrows. Their tooltips explain why: leaks, who is plugging or pumping, a hit, list or trim putting water over the deck...
   Water shown yellow / red while damage control can still get it out, grey when it cannot.
   Hull Damage and Flooding at the top right also show the overall % of the whole ship, e.g. Moderate (15%), nothing added at 0%; with Alternative Damage Control the hull damage words change at 25 / 50 / 75%.
   Weapon buttons of the selected unit's bottom panel are coloured by weapon state: part of that type inoperable - yellow, all of it - red (setting UI / ColorWeaponStatus).
   LIST / TRIM / LEVEL gauges; their tooltip is a plan of the ship with the water in every compartment.
   A legend behind the (i) next to the tab's name.

Advanced Damage Model (fully customizable)

Preconfigured:

1. Stronger hulls. Hull integrity of warships, civilian ships and submarines is multiplied by your own factor (per ship too). A hit takes the same share as in the game, so ships survive more hits. Side effects are tunable: gun shell damage tiers, fires and flooding from hits. Systems (weapons, sensors, engines...) can be made stronger too, all or by module type.
   Ships would not just explode after a few hits. More chances she will go down while fighting.
2. No instant sinking from list or trim. From the red zone of the LIST gauge the low side floods fast; from the red zone of the TRIM gauge the end that goes down floods fast, compartment after compartment. The trim limit depends on the ship's length; for ships whose hull collider is complete the red zones of LIST and TRIM start where the first deck-edge point touches the flat sea, counted separately for port, starboard, stern and bow from the lowest deck point on that side (a flooded side has a red zone that starts right away, an intact one much later). The ship sinks when too much water is in it, as in the game; for ships whose hull collider is complete it also needs both central compartments (or at least 5 of the 10) to be under the sea. A compartment whose deck edge stays under the sea for 3 seconds (the "Time under water" setting) starts taking on water that damage control cannot stop, faster the deeper it is (0.1% per second per metre; the "Water over the deck" setting is a multiplier of it); a full compartment passes the water to the lowest neighbour, and on along the chain. The more water in the ship, the higher its centre of mass (setting "Centre of mass rise"): a heavily flooded ship loses its stability and turns over, keel up, instead of lying on its side. Works for ships whose hull collider is complete. For such ships it replaces the fast flooding from the LIST/TRIM red zones (the same water would come in twice); ships without a usable collider keep the old rules. When a ship starts to sink its centre of mass no longer jumps to the hull origin, as the game does it ("Keep centre of mass" setting). The extra pull the game applies to a deep sinking ship builds up more smoothly ("Sinking pull ramp" setting; "Sinking pull" scales its maximum). The linear drag of a destroyed ship is multiplied (5× by default, "Wreck drag" setting): the wreck loses its way sooner. The sinking-bubbles effect of a sunk ship stops emitting 10 minutes after the sinking starts ("Stop bubbles after" setting; 0 = never), to save resources in long missions.
3. Fire and flooding speeds. How fast fires grow, burn the hull, spread to the next sections and are put out; how fast water comes in and is pumped out. Optionally, a fire nobody fights grows faster and faster, up to a set limit.
4. More fire smoke points: ships from 75 m (adjustable) get five fire and smoke points, one per section, instead of the game's three. Fire looks follow the fire's strength (moderate from 50%, severe from 90% by default), and the small fire's smoke fades in as the fire grows (Settings → Effects → Tune visuals).
5. One switch (Settings → Damage Model → Alternative Damage Control) turns the whole damage model off and on during a mission; the Damage Control tab view, DC Priority and tasks given by hand stay.

COMPATIBILITY

- Supports Sea Power version 0.8.4.
- Requires Anchor Chain.
- Can be switched on/off in the game's Mods menu.

INSTALL

1. Install the Anchor Chain preloader as described in the documentation (https://seapower-modders.github.io/AnchorChain/install-anchor-chain.html).
2. Subscribe to this mod.
3. Restart the game.

USAGE

- Unit Status → Damage Control: point at icons, arrows and gauges for details.
- Settings in the game: the gear next to (i). Profiles: Base Game (as close to the unmodified game as possible), Advanced (the mod's damage model), or your own (Save as / Rename / Delete). Any profile file put into the mod's Profiles folder is listed too. Changes apply at once, also to ships at sea.
- Saved games keep the state of the damage control teams and the ship's DC Priority.
