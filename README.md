# Alternative Damage Model

**Version 2.2.0**

Alternative Damage Model: a deeper, more realistic damage model for Sea Power. Fires, flooding, wounded damage control teams and a Damage Control interface that lets you fight for your ship.

## Requires **Anchor Chain** or **R.A.T.S. Keel**

## Features

### Upgraded Damage Control interface

1. **Damage control teams as people.**
   - Number of teams by displacement (warships, submarines, civilian ships).
   - Teams walk between sections: muster time plus the walk. A team on the move takes no orders; a transfer can be cancelled.
   - Hits wound teams (also teams amid a fire they do not fight, and teams in the reserve hit through the central compartment). A wounded team is concussed for a while, walks to the reserve and works slower; at 0% it is lost. Optional fatigue: a tired team works at half speed until it rests. Health and fatigue are in the hint and on the icon.
   - Auto DC rests wounded (under 33%) and exhausted teams. In a critical state (sinking; water 20%+ with a hole open; hull under 50% with fire or leaks; the fires averaging 30% or more over all sections) only the wounded go. Fresh teams relieve tired ones; the last team at a fire, or in a section whose hull is under 19%, stays.
   - With the Engine out of order teams work at half effectiveness (not on systems; setting "DC efficiency without engines").
2. **Smarter Auto DC:** teams stay until fire and flooding are dealt with, every job gets the nearest free team, more teams go where fire or water is bad or rising. Order: Flooding Rate > Fire Rate > Fires > Counterflooding > Flooding > Flooded Structure (hull that keeps the water in) > Systems > Structure. The first team in a burning section fights the fire; one team counterfloods, first at a critical heel; no pumping that worsens the list or trim; a little water (under 10%) is no job. A lost engine and the fires at it come first; systems marked "!" go first too. Free teams finish small repairs when nothing else is left.
3. **DC Priority** for the whole ship: Auto, Fire first or Flooding first. "Limit Speed" (on by default): off removes the game's speed cut by flooding.
4. **Orders to single teams** with Auto DC off: a task, another section or the reserve. Counterflooding lets water into the side opposite to the heel, only where that takes off the list or trim. A fire does not spread into a section with a team; a section under the sea puts its fire out.
5. **Detailed Damage Control tab:**
   - Hull integrity of each side with a trend arrow (grey when welded as far as it can be); one icon per team showing its work; teams on the move with their time.
   - Water and fire in % with trend arrows; tooltips explain why. Grey water cannot be pumped out.
   - Overall % at Hull Damage / Flooding; weapon buttons coloured by weapon state.
   - Map icons under a ship: fire, flooding, hull, engine (your own ship: not for damage that cannot be repaired any more); a greyer icon for a ship that will sink. Contacts by what the sensors tell: direct sight - all, sonar - the engine, radar - a guess ('?'). Unit Status of a contact: a Damage State tab with a sinking forecast.
   - Status card (Stable, a yellow Uncontrolled Fires, a red Critical, Sinks in ~N min, Sinking); LIST / TRIM / LEVEL gauges, their tooltip a plan of the water in every compartment ("Show deck levels in 3D").
   - Tooltips: every hit on the ship (Hull Damage / Flooding / Fires); a system's damage, repair speed, time to the next level and who repairs it or what it waits for. Tooltips update while open.
   - Several systems of a section can be prioritised with "!" (repaired in turn). List and trim are saved with the game. A legend behind (i).

### Advanced Damage Model (fully customizable)

Preconfigured:

1. **Stronger hulls.** Hull integrity of warships, civilian ships and submarines times your factor (per ship too); a hit takes the same share as in the game. The armour classes of the WW2 mod (LightCruiser ... SuperHeavy), which the game takes for no armour, can be switched on. Side effects of hits (shell damage tiers, fires, flooding) are tunable; systems can be made stronger too, all or by module type.
2. **Collisions between ships (own tab).** The game only lets ships pass through each other. With the mod a blow slows the ships (across the contact almost all the closing speed is lost, the lighter ship the most; along it a part, so ships side by side mix their speeds; AI ships optionally; the engines then speed the ship up again; the water holds the struck ship back, and a strong blow shoves it aside) and each ship loses hull where the hulls touch, for as long as they stay in contact; any strong blow opens leaks. The damage is the energy the blow wears down, the bow taking several times less than the side. Damage, flooding from collisions, slowest damaging impact, bow strength, slowing, water resistance, speed loss when pressed together and an option to push the ships apart are set; 0 = off.
3. **No instant sinking from list or trim.** From the red zone of LIST / TRIM the low side or end floods fast. With a complete hull collider the red zones start where the first deck-edge point touches the sea; a ship with 5 sections sinks when both central deck points of its middle section, or 6 deck points, stay under the sea for 10 seconds. A compartment whose deck edge is under the sea for 3 seconds takes on water that cannot be stopped. Fire reopens plugged leaks; a section flooded above 75% loses its systems and its fire; teams amid a fire they do not fight work at 75%. Wreck drag; no jump of the centre of mass when sinking starts; small AI ships keep their speed; an unarmed surface ship counts as civilian.
4. **Fire and flooding speeds:** growth, hull burning, spread and extinguishing of fires; inflow and pumping of water (also while plugging holes, from the side with more water); the ship's speed effect (a table); small ships (1000 t down to 50 t) burn and flood up to 3 times faster; no plugging above a set speed; system and hull repair speed; a fire growth limit; a gun shell's fire may start with a size, as for bombs and missiles; gun shells stopped by the armour may still start a fire next to the hit compartment (a share of the chance of a shell that gets through, 0 = off).
5. **One switch** (Settings → Damage Model → Alternative Damage Control) turns the damage model off and on during a mission; the tab view, DC Priority and orders by hand stay.

## Compatibility

- Supports Sea Power version 0.9.0 and newer.
- Loaders: Anchor Chain or R.A.T.S. Keel.
- Can be switched on/off in the game's Mods menu.
- Special effects moved to Alternative Special Effects; install it to keep them. Settings are taken over on its first start.
- Settings also open from the main menu and the pause menu (with other Alternative mods they share one "Alternative Mods" entry).
- Settings and your own profiles are kept in Sea Power_Data/StreamingAssets/user/AlternativeDamageControl.

## Install

1. Subscribe to [Anchor Chain](https://steamcommunity.com/workshop/filedetails/?id=3380210757) or [RATS Keel](https://steamcommunity.com/workshop/filedetails/?id=3835527459) (temporarily unavailable) and follow instructions on mods page.
2. Subscribe to this mod.
3. Start **Sea Power**.
4. Enable mod in game's **Mod Manager** and press **Apply** button.

## Usage

- **Unit Status → Damage Control:** point at icons, arrows and gauges for details.
- **Settings:** the gear next to (i). Profiles: Base Game, Advanced (the mod's damage model) or your own (Save as / Rename / Delete). Changes apply at once, also to ships at sea. Next to every setting: its default and the game's value.
- **Language:** the game's language (9 languages) or Settings > User Interface > Language. Texts are in the lang folder (machine translations, corrections welcome).
- **Saved games** keep the state of the damage control teams and the ship's DC Priority.
