Version 1.4.0.

[h1]Features[/h1]

[h3]Upgraded Damage Control interface[/h3]
[olist]
[*][b]Damage control teams as people.[/b]
Number of teams by displacement (warships, submarines, civilian ships).
Teams walk between sections: muster time plus the walk at their speed. A team on the move takes no orders; a transfer can be cancelled.
A hit that knocks a team out of its section wounds it and it walks to the reserve, where it recovers; teams in a burning section are wounded too unless at least one of them fights the fire; at 0% it is lost. Wounded teams work slower. A team wounded by a hit is also concussed: out of action in the reserve for a set time (Concussion time). A hit on the central compartment can also wound a team resting in the reserve (Wounds in reserve, with its own chance multiplier). Optional fatigue: after some minutes of work a team works at half speed until it rests in the reserve. Health and fatigue are in the hint; a wounded team's icon has a health bar, a tired one is highlighted; "Wounded" / "Tired" is added to the speed lines. Auto DC rests the teams: a team under 33% health or exhausted goes to the reserve and is used again after a full rest; on a ship in a bad way (sinking, or fire, water, hull at the third stage or worse) only up to 50% health and fresh (not tired). While the ship's Engine (not the propulsion) is out of order, damage control teams lose half of their effectiveness (not at repairing systems); setting "DC efficiency without engines" on the Systems tab ([Advanced] 50%); shown in the teams' hints and in the Forecast line.
[*][b]Smarter Auto DC[/b]: a team does not leave a section with fire or flooding still to fight, every job gets the nearest free team, more teams go where fire or water is bad or rising fast, idle teams wait where they are until the work in the ship is really done, then go back to the reserve. The fastest growing fire or leak comes first: Fire Rate > Flooding Rate > Fires > Flooding > Systems > Structure (DC Priority Fire and Flooding move their group up).
[*][b]DC Priority[/b] for the whole ship: Auto (the game's order), Fire first or Flooding first. A "Limit Speed" button under it (on by default): off removes the game's cut of the ship's speed by flooding.
[*][b]Orders to single teams[/b] with Auto DC off: click a team to give it a task, move it to another section or send it to the reserve. Besides the game's tasks there is counterflooding: the team lets water into the side of its compartment opposite to the heel (into the side with less water when the ship is upright), twice as fast as teams pump out; only by hand, up to a limit (settings "Counterflooding", "Counterflood limit"). A fire does not spread into a section where a damage control team is (setting "Team stops fire spread"). A team ordered to counterflood pumps out only where that does not make the list or trim worse. A section under the sea puts its fire out: the fire loses as many % per second as metres the section's deck edge is under water (setting "Fire under water").
[*][b]Detailed Damage Control tab:[/b]
Hull integrity of each side next to the section name, with a trend arrow: yellow / orange / red while it can still be welded, grey when it is welded as far as it can be.
One icon per team (DC1, DC2...) showing what it is doing; teams on the move and recovering, with their time.
Water and fire in % with trend arrows. Their tooltips explain why: leaks, who is plugging or pumping, a hit, list or trim putting water over the deck...
Water shown yellow / red while damage control can still get it out, grey when it cannot.
Hull Damage and Flooding at the top right also show the overall % of the whole ship, e.g. Moderate (15%), nothing added at 0%; with Alternative Damage Control the hull damage words change at 25 / 50 / 75%.
Weapon buttons of the selected unit's bottom panel are coloured by weapon state: part of that type inoperable - yellow, all of it - red (setting UI / ColorWeaponStatus).
Four small icons under a ship's icon on the map (yours too; setting UI / ShowEnemyDamage): fire (total strength), flooding, hull (classes of the Damage Control panel), engine - yellow / orange / red - and a greyer map icon for a ship that will sink. A contact: all four in direct sight; by sonar the engine only; by radar a guess ('?') of a slow ship hit lately; nothing by ESM. Option UI / HideUnobservedTargets3D (off by default) hides targets not seen visually in 3D.
Unit Status of a contact: instead of an empty window - a Damage State tab with a simplified panel (icons in words, sinking forecast), as far as the sensors tell.
Damage Control tab: a "Forecast" card in the middle of the summary row (Stable / Sinks in ~N min by flooding or hull damage / Sinking).
LIST / TRIM / LEVEL gauges; their tooltip is a plan of the ship with the water in every compartment. Check box "Show deck levels in 3D" in that plan shows the deck points in 3D.
Point at "Hull Damage / Flooding / Fires": a list of every hit on the ship (time, ammunition, section and side); over 20 hits - the count by ammunition.
Point at a damaged system in a section's list: its damage level, integrity, repair speed and what it waits for.
A legend behind the (i) next to the tab's name.
[/olist]

[h3]Advanced Damage Model (fully customizable)[/h3] 

Preconfigured:
[olist][*][b]Stronger hulls.[/b] Hull integrity of warships, civilian ships and submarines is multiplied by your own factor (per ship too). A hit takes the same share as in the game, so ships survive more hits. Side effects are tunable: gun shell damage tiers, fires and flooding from hits. Systems (weapons, sensors, engines...) can be made stronger too, all or by module type.
Ships would not just explode after a few hits. More chances she will go down while fighting.
[*][b]No instant sinking from list or trim.[/b] From the red zone of the LIST gauge the low side floods fast; from the red zone of the TRIM gauge the end that goes down floods fast, compartment after compartment. The trim limit depends on the ship's length; for ships whose hull collider is complete the red zones of LIST and TRIM start where the first deck-edge point touches the flat sea, counted separately for port, starboard, stern and bow from the lowest deck point on that side. The ship sinks when too much water is in it, as in the game; for ships whose hull collider is complete it also needs both central compartments (or at least 5 of the 10) to be under the sea. A compartment whose deck edge stays under the sea for 3 seconds (the "Time under water" setting) starts taking on water that damage control cannot stop, faster the deeper it is (0.1% per second per metre; "Water over the deck" multiplies it); a full compartment passes the water to the lowest neighbour. It replaces the LIST/TRIM red-zone fast flooding for such ships; others keep the old rules. When a ship starts to sink its centre of mass no longer jumps to the hull origin ("Keep centre of mass" setting, Effects tab). A destroyed ship's horizontal movement is damped 3× by default ("Wreck drag"); sinking is not slowed. The sinking-bubbles effect of a sunk ship stops emitting 10 minutes after the sinking starts ("Stop bubbles after" setting, Effects tab; 0 = never). Ships can be taken off Deck Points in the Flooding tab (old rules for them). A surface ship with no weapons is counted as civilian. Setting "Small AI: no speed limit" (Damage Model tab): AI ships under 50 m keep their speed in spite of flooding. Fire reopens plugged leaks: a burning section grows a plugged leak back by 0.5% of its largest leak a second at fire 100%, up to 50% of it (Flooding tab, "Fire reopens leaks"). A section flooded above 75% loses its systems - they become inoperable and cannot be repaired while it stays flooded - and its fire is put out at 10% a second. Teams that work in a section with a fire without fighting it work at 75% effectiveness (Fire tab, "Work amid fire").
[*][b]Fire and flooding speeds.[/b] How fast fires grow, burn the hull, spread to the next sections and are put out; how fast water comes in and is pumped out; a team plugging holes also pumps out water at a set share of the pumping speed (setting "Pumping while plugging holes"). The ship's speed can speed up or slow down fires and incoming water (a table of speed points, "Ship speed" in the Damage Model tab); teams cannot plug holes above a set speed. Optionally, a fire nobody fights grows faster and faster, up to a set limit. Pumping starts from the side with more water and switches sides at most every 10 seconds.
[*][b]More fire smoke points:[/b] the number of fire and smoke points depends on the ship's length instead of the game's fixed three: under 25 m one, under 50 m two, under 75 m three, from 75 m five (one per section). Fire looks follow the fire's strength, and the small fire's smoke fades in as the fire grows (Settings → Effects → Tune visuals).
[*][b]One switch[/b] (Settings → Damage Model → Alternative Damage Control) turns the whole damage model off and on during a mission; the Damage Control tab view, DC Priority and tasks given by hand stay.
[/olist]

[h1]Compatibility[/h1]
[list]
[*]Supports Sea Power versions 0.8.6-0.8.7.
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
[*][b]Settings in the game:[/b] the gear next to (i). Profiles: [i]Base Game[/i], [i]Advanced[/i] (the mod's damage model), or your own (Save as / Rename / Delete). Any profile file put into the mod's Profiles folder is listed too. Changes apply at once, also to ships at sea.
[*][b]Language:[/b] the mod's texts follow the game's language (9 languages); another one is chosen in Settings > User Interface > Language. Texts are in the lang folder (machine translations, corrections welcome).
[*][b]Settings window:[/b] next to every setting - its default and the game's value, or "no analogue" where the game has no such mechanic.
[*][b]Saved games[/b] keep the state of the damage control teams and the ship's DC Priority.
[/list]