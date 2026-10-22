Version 1.3.0.

[h1]Features[/h1]

[h3]Upgraded Damage Control interface[/h3]
[olist]
[*][b]DC teams as people.[/b] Team count by displacement. Teams muster/walk between sections; moving teams take no orders; transfers can be cancelled. Knocked-out teams recover in reserve; knocked out twice are lost. Wounds reduce speed; at 0% team is lost. Optional fatigue: after work, half speed until rested. Auto DC rests wounded (<33%) and exhausted teams; on badly damaged ships only to 50% health and fresh. If Engine (not propulsion) is out, DC effectiveness is 50% except system repair ([Advanced] Systems > “DC efficiency without engines”; 100 = off).
[*][b]Smarter Auto DC.[/b] Fastest-growing fire/leak first; nearest free team per job; more teams to severe/rising fire or water; idle teams wait while work remains, then return to reserve; +1 team/10 s up to 4 per section.
[*][b]DC Priority.[/b] Auto, Fire first, or Flooding first. “Limit Speed” (on by default): off removes game’s flooding speed cut.
[*][b]Manual orders.[/b] With Auto DC off: assign task, move team, send to reserve. Includes counterflooding (manual, limited), “Team stops fire spread”, “Fire under water”, and pumping only if list/trim does not worsen.
[*][b]Detailed DC tab.[/b] Side hull integrity with trend arrows; one icon per team with status/time; water/fire % with trend arrows and explanatory tooltips; LIST/TRIM/LEVEL gauges with compartment plan; weapon buttons coloured by state; legend behind (i).
[*][b]Forecast and tooltips.[/b] Forecast card: Stable / Sinks in ~N min (cause) / Sinking; trend excludes direct hit damage; engine-out warning. Fires classed by strength. Saved hit stats/trend. Tooltips for every hit and damaged system. Wound chance table.
[*][b]Map and contacts.[/b] Four icons under ship icons: fire, flooding, hull, engine; sinking ships greyer (UI > Map > “Ship damage icons”). Contacts: all four in direct sight, engine by sonar, radar “?” guess for slow ship hit recently; no ESM. Non-player Unit Status gains Damage State tab (icons in words, list/trim, sinking forecast with source).
[*][b]DC Teams tab[/b] split into Count / Movement / Work / Health / Wounds / Fatigue; health/fatigue switches off in Base Game.
[/olist]

[h3]Advanced Damage Model (fully customizable)[/h3]
Preconfigured:
[olist]
[*][b]Stronger hulls/systems.[/b] Hull integrity multipliers for warships, civilians, submarines and per ship; systems stronger all or by module type. Tunable shell damage tiers, fires and flooding from hits.
[*][b]No instant sinking from list/trim.[/b] Red LIST/TRIM zones cause fast flooding; trim limit depends on length. For complete hull colliders, red zones start at deck-edge immersion; sinking also needs central compartments flooded. Compartments under water for 3 s take unstoppable water (deeper = faster); full compartments pass water to lowest neighbour. Heavily flooded ships can capsize keel-up. Replaces old LIST/TRIM fast flooding for such ships; others keep old rules. Centre of mass no longer jumps on sinking (“Keep centre of mass”); wreck drag 3× by default; sinking bubbles stop after 10 min (“Stop bubbles after”; 0 = never). Ships can be excluded from Deck Points in Flooding tab. Unarmed surface ships count as civilian. AI ships <50 m ignore flooding speed limit. Fire reopens plugged leaks (limited); >75% flooded section loses systems and fire goes out; teams in burning section not fighting fire work at 75%.
[*][b]Fire/flooding speeds.[/b] Tunable growth, hull burn, spread, extinguishing, inflow and pumping; “Pumping while plugging holes”; ship speed affects fires/inflow; teams cannot plug above set speed; unfought fires can grow faster up to limit; pumping switches sides at most every 10 s.
[*][b]More fire/smoke points[/b] by ship length: <25 m one, <50 m two, <75 m three, ≥75 m five. Fire visuals follow strength; small-fire smoke fades in.
[*][b]One switch.[/b] Settings > Damage Model > Alternative Damage Control toggles the model during a mission; DC tab view, priority and manual tasks remain.
[/olist]

[h3]Fixes[/h3]
[list]
[*]Fire/flooding arrows no longer slide onto % text.
[*]DC teams no longer leave a section with hull or systems work.
[*]Performance: ship speed read once per frame; one UI tree walk every 2 s.
[/list]

[h1]Compatibility[/h1]
[list]
[*]Supports Sea Power version 0.8.6.
[*]Requires Anchor Chain.
[*]Can be switched on/off in the game's Mods menu.
[/list]

[h1]Install[/h1]
[olist]
[*]Install Anchor Chain preloader as described in the [url=https://seapower-modders.github.io/AnchorChain/install-anchor-chain.html]documentation[/url].
[*]Subscribe to this mod.
[*]Restart the game.
[/olist]

[h1]Usage[/h1]
[list]
[*][b]Unit Status[/b] → [b]Damage Control[/b]: point at icons, arrows and gauges for details.
[*][b]Settings:[/b] gear next to (i). Profiles: [i]Base Game[/i], [i]Advanced[/i], or custom (Save as / Rename / Delete). Profiles in mod's Profiles folder are listed. Changes apply at once, also at sea.
[*][b]Saved games[/b] keep DC team state and ship DC Priority.
[/list]