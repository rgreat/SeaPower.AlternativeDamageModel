[h1]Features[/h1]

[h3]Upgraded Damage Control interface[/h3]
[olist]
[*][b]Damage control teams as people.[/b]
Number of teams by displacement (warships, submarines, civilian ships).
Teams walk between sections: muster time plus the walk at their speed. A team on the move takes no orders; a transfer can be cancelled.
A knocked-out team recovers and returns to the reserve; knocked out twice, it is lost.
[*][b]Smarter Auto DC[/b] while transfers take time: a team does not leave a section with fire or flooding still to fight, every job gets the nearest free team, more teams go where fire or water is bad or rising fast, idle teams wait where they are while there is work, then go back to the reserve.
[*][b]DC Priority[/b] for the whole ship: Auto (the game's order), Fire first or Flooding first.
[*][b]Orders to single teams[/b] with Auto DC off: click a team to give it a task, move it to another section or send it to the reserve.
[*][b]Detailed Damage Control tab:[/b]
Hull integrity of each side next to the section name, with a trend arrow: yellow / orange / red while it can still be welded, grey when it is welded as far as it can be.
One icon per team (DC1, DC2...) showing what it is doing; teams on the move and recovering, with their time.
Water and fire in % with trend arrows. Their tooltips explain why: leaks, who is plugging or pumping, a hit, list or trim putting water over the deck...
Water shown yellow / red while damage control can still get it out, grey when it cannot.
Hull Damage and Flooding at the top right also show the overall % of the whole ship, e.g. Moderate (15%), nothing added at 0%; with Alternative Damage Control the hull damage words change at 25 / 50 / 75%.
Weapon buttons of the selected unit's bottom panel are coloured by weapon state: part of that type inoperable - yellow, all of it - red (setting UI / ColorWeaponStatus).
LIST / TRIM / LEVEL gauges; their tooltip is a plan of the ship with the water in every compartment.
A legend behind the (i) next to the tab's name.
[/olist]

[h3]Advanced Damage Model (fully customizable)[/h3] 

Preconfigured:
[olist][*][b]Stronger hulls.[/b] Hull integrity of warships, civilian ships and submarines is multiplied by your own factor (per ship too). A hit takes the same share as in the game, so ships survive more hits. Side effects are tunable: gun shell damage tiers, fires and flooding from hits. Systems (weapons, sensors, engines...) can be made stronger too, all or by module type.
Ships would not just explode after a few hits. More chances she will go down while fighting.
[*][b]No instant sinking from list or trim.[/b] From the red zone of the LIST gauge the low side floods fast; from the red zone of the TRIM gauge the end that goes down floods fast, compartment after compartment. The trim limit depends on the ship's length; for ships whose hull collider is complete the red zones of LIST and TRIM start where the first deck-edge point touches the flat sea, counted separately for port, starboard, stern and bow from the lowest deck point on that side (a flooded side has a red zone that starts right away, an intact one much later). The ship sinks when too much water is in it, as in the game; for ships whose hull collider is complete it also needs both central compartments (or at least 5 of the 10) to be under the sea. A compartment whose deck edge stays under the sea for 3 seconds (the "Time under water" setting) starts taking on water that damage control cannot stop, faster the deeper it is (0.1% per second per metre; the "Water over the deck" setting is a multiplier of it); a full compartment passes the water to the lowest neighbour, and on along the chain. The more water in the ship, the higher its centre of mass (setting "Centre of mass rise"): a heavily flooded ship loses its stability and turns over, keel up, instead of lying on its side. Works for ships whose hull collider is complete. For such ships it replaces the fast flooding from the LIST/TRIM red zones (the same water would come in twice); ships without a usable collider keep the old rules.
[*][b]Fire and flooding speeds.[/b] How fast fires grow, burn the hull, spread to the next sections and are put out; how fast water comes in and is pumped out. Optionally, a fire nobody fights grows faster and faster, up to a set limit.
[*][b]More fire smoke points:[/b] ships from 75 m (adjustable) get five fire and smoke points, one per section, instead of the game's three. Fire looks follow the fire's strength (moderate from 50%, severe from 90% by default), and the small fire's smoke fades in as the fire grows (Settings → Effects → Tune visuals).
[*][b]One switch[/b] (Settings → Damage Model → Alternative Damage Control) turns the whole damage model off and on during a mission; the Damage Control tab view, DC Priority and tasks given by hand stay.
[/olist]

[h1]Compatibility[/h1]
[list]
[*]Supports Sea Power version 0.8.4.
[*]Requires Anchor Chain.
[*]Can be switched on/off in the game's Mods menu.
[/list]

[h1]Install[/h1]
[olist]
[*]Install the Anchor Chain preloader as described in the [url=https://seapower-modders.github.io/AnchorChain/install-anchor-chain.html]documentation[/url].
[*]Subscribe to this mod.
[*]Restart the game.
[/olist]

[h1]Usage[/h1]
[list]
[*][b]Unit Status[/b] → [b]Damage Control[/b]: point at icons, arrows and gauges for details.
[*][b]Settings in the game:[/b] the gear next to (i). Profiles: [i]Base Game[/i] (as close to the unmodified game as possible), [i]Advanced[/i] (the mod's damage model), or your own (Save as / Rename / Delete). Any profile file put into the mod's Profiles folder is listed too. Changes apply at once, also to ships at sea.
[*][b]Saved games[/b] keep the state of the damage control teams and the ship's DC Priority.
[/list]