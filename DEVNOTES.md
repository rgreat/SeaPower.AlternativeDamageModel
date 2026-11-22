# Alternative Damage Model — заметки для продолжения работы (для Claude)

Мод для Sea Power: BepInEx 5 + Harmony, загружается через Anchor Chain (Workshop 3380210757), устроен как Smart Windows
(тот же `AnchorChainEntry`/`ModState`, тот же csproj, сборка в песочнице — см. DEVNOTES Smart Windows).
Весь доступ к игре — через рефлексию по именам (в csproj нет ссылки на Seapower-Scripts).


## Как работать с этими заметками (для Claude: не раздувать контекст)
- Этот файл — только основное (сборка, правила, архитектура, подводные камни). **Не читать целиком чужие разделы**: подробная история функций — в `DEVNOTES-ARCHIVE.md`.
- Нужная тема: `grep -n "ключевое слово" DEVNOTES-ARCHIVE.md`, затем `sed -n 'N,Mp'` по номерам строк из индекса ниже (читать только этот раздел).
- Общий код окон настроек (SettingsKit/Hub, вкладки, поиск, Ctrl+C, язык) описан в `seapower.alternativebase/README.md`, не здесь.
- В чате: большие файлы только через `grep`/`sed -n`/`Read` с offset+limit; логи — `grep`/`tail`, не целиком; вывод команд ограничивать (`| cut -c1-200 | head`).
- Новые записи: основное — сюда (коротко); подробности и история отладки — в архив, в конец, отдельным разделом.


## Состояние релиза
- 2026-11-18: 2.2.0 опубликована в Steam (журнал изменений от 18 нояб 5:38pm; коммит "Version 2.2.0").
- 2026-11-22: версия 2.3.0 подготовлена (Ignore own helicopters в Collisions, закладки не уплывают, чистка кода). Не выложена, пока пользователь не скажет; не закоммичена.

## Индекс архива (строка в DEVNOTES-ARCHIVE.md — раздел; размер в символах)
- 653 — Изменения настроек (944)
- 662 — Вкладка Damage Control (Unit Status) — `src/DamageControlUi.cs` (10412)
- 751 — Команды DC как люди — `src/DcTeams.cs` (4368)
- 792 — Оптимизированный Auto DC — `src/DcAuto.cs` (включается сам, пока перемещения занимают время) (2272)
- 815 — Профили настроек — `src/AdmPlugin.Profiles.cs` (5318)
- 861 — Окно настроек — `src/SettingsWindow.cs` (3303)
- 890 — Подсказки (3322)
- 922 — Главный выключатель, пожары, окно настроек (версия с разделами User Interface / General) (7692)
- 994 — Решения и итоги ревизии 2026-10-09 (пакеты 1–4, закрыты и проверены в игре) (2827)
- 1016 — Функции 2026-10-09 (105290)
- 1338 — Game 0.9.0 (2026-11-11/12): compatibility check against 0.8.7 (static, decompiled both Seapower (1300)
- 1341 — Settings window: "profile: x", reset to the profile (2026-11-12) (743)
- 1344 — Debug trace additions (2026-11-12) (38125)
- 1429 — Settings window: one-line tab strip and screenshots of the tabs (settings kit, after the last r (1125)
- 1432 — Settings window frame and row editors (settings kit: SettingsKit.cs, after the last release) (2467)
- 1440 — Switch between the mods' settings windows (after the last release) (1433)
- 1446 — Tabs, search, revert (kit) (746)
- 1451 — Ctrl+C copies the text under the mouse (kit) (764)
- 1454 — One shared Language (hub + kit) (1056)

## Механика игры (Seapower-Scripts)
* `Compartments.Init()` (private, из конструктора `Compartments(ObjectBase)`; создаётся в `Vessel`/`Submarine`):
  `X = 2.5·∛Displacement` (Displacement ≤ 0 → 8000); civilian (имя ini `civ_*`) ×0.5, иначе Submarine ×0.5;
  ArmorType Minor ×1.2 / Moderate ×1.25 / Heavy ×1.3 (нет ключа → minor, неизвестное значение → None);
  `[Physics] IntegrityOverride > 0` заменяет итог; затем `_fireMult = X/1800`; LifeRaft/SeaMine → X = 0.1, 1 отсек;
  `_maxTotalIntegrity = X·2·5`, `_maxLimitIntegrity = 5X` (лимит потопления), 10 `FloodingCompartment` с X.
* `Blastzone.CheckForInstakill`: лимит потопления `_limitIntegrity -= урон`; ракеты ×2, торпеды ×3.5 (Vessel), подлодка+торпеда ×1.5;
  для `Ammunition.Type.Projectile` делитель по ступеням `_maxLimitIntegrity` (пороги 20/70/115/165/240/300; 30 чтений поля).
  Пробитие брони здесь не учитывается.
* Зажигание: `OnHitUnit` (`GetWeaponDamage / compartments._maxIntegrity`) и `ProcessCompartmentHit`
  (`num /= floodingCompartment._maxIntegrity`, дальше `/2`, `/20`); там же ниже чтение `_maxIntegrity` для затопления — его НЕ трогать.
  Пороги состояния пожара ∝ X, урон системам от огня зависит только от FireState.
* Модули/системы: прочность из ini/`modules.ini` (`ScalesWithDisplacement` = ×D^0.25), от X корпуса не зависят.
* Point value: `PointValueRoleScorers.ShipEffectiveIntegrity(sop)` (только Vessel) дублирует формулу и читает IntegrityOverride;
  кэш `persistentDataPath/cache/taskforce_pointvalues.json` с ключом `PointValueCache.ComputeGlobalHash()`.
* Сейвы хранят абсолютный `_currentIntegrity` отсеков; max пересчитывается при загрузке.

## Что делает мод (хуки)
* `Compartments.Init` — транспайлер: перед `_fireMult = _maxIntegrity/1800` вставлен `ApplyHull(this)` (метки переносятся:
  туда прыгает ветка IntegrityOverride). Множитель: [HullIntegrityPerShip] →
  Submarines → CivilianShips → Warships. Состояние (множитель, исходный X) — `ConditionalWeakTable` по Compartments.
* `CheckForInstakill` — каждое `ldfld _maxLimitIntegrity` → `dup; ldfld; call ShellTierLimit(comp, v)` = `v / (mult·HullShellVulnerability)`.
* `OnHitUnit` / `ProcessCompartmentHit` — `FireIntegrity*` = `X / (m × HullFireVulnerability)` (с 1.9.0: игровой X, корпус огонь не сдерживает;
  по тестам rgreat при Warships 5 правдоподобно V_eff = 1/5, поэтому множитель корпуса в пожар НЕ компенсируется, в отличие от снарядов и затопления).
* `ProcessCompartmentHit` — затопление: `damage / X` → `FloodIntegrityFc` = `X / (mult·HullFloodingVulnerability)` (компенсировано: пробоина как в игре;
  деление на mult пробовали — по тестам rgreat для затопления неверно, откатили);
  все 4 `FloodingCompartment.CurrentIntegrityPercent` (пороги надводной течи 50%/25% и шанс вывести команду DC)
  → `FloodPercent` = `100 − 100·(X − cur)/(X/(mult·V))`, т.е. процент от игровой прочности при том же уроне.
  Затопление — проценты объёма отсека, откачка DC абсолютная (`teams·_floodingRepair`), поэтому без компенсации пробоины при ×N
  давали в N раз меньше воды.
* Пожар внутри отсека (рост, стадии, переброс, тушение, урон) целиком ∝ X — от множителя не зависит.
  Диагностика `LogFires`: постфиксы `SystemCompartment.SetFireState` (смена стадии) и `AddCompartmentFire` (возгорание от попадания);
  работает и при выключенном моде.
* Галочка в Mods: `ModState` (usersettings.ini [LoadOrder]); перечитывается в Awake, в `FileManager.SaveDirectories` (Apply)
  и на первом `MoveNext` экрана загрузки (до спавна кораблей). Выключен → все хуки отдают игровые значения.
* Транспайлеры проверены офлайн (Cecil → CodeInstruction → наш транспайлер): Init 1 вставка, Instakill 30, OnHitUnit 1,
  ProcessCompartmentHit: пожар 1, затопление 1, процент 4. При другом числе — предупреждение в логе (игра обновилась).

## Настройки (.cfg)
Описания (Description) = подсказки окна настроек; сверены с реализацией (Auto DC, перемещения, выздоровление с переходом в резерв,
LIST/TRIM без мгновенного потопления, FloodLimit как в игре, что убирает DetailedView — в т.ч. шестерёнку). Группы
вкладки Sinking: «Faster flooding from» (List, Trim, длина) и «Sinking from» (вода).
[General] Debug, LogShipIntegrity, LogFires (DumpXaml скрыт: не пишется в .cfg; строку `DumpXaml = true` под [General] можно
дописать руками — читается из orphans (`HiddenBool`), строка с false удаляется при запуске).
[FireAndFlooding] FlameUpSpeed, FireHullDamageSpeed, ExtinguishSpeed, FloodingSpeed, PumpOutSpeed (1.0 = как в игре) — транспайлеры «call XxxMul(float)»
сразу после значения, на котором строится формула: SystemCompartment.OnFixedUpdate `_fireGrowRate`·dt (1 место),
Compartments.RepairFire `_crew._fireSeveretyRepair` (1; снижает и интенсивность, и рост), FloodingCompartment.OnFixedUpdate
`_shipFloodingCoefficient · _floodingRate` (2: обычное и при тонущем корабле; принудительное заполнение `_maxFloodingRate` не трогаем),
Compartments.RepairFlooding `_crew._floodingRepair` (1), SystemCompartment.OnFixedUpdate `Globals._shipFireCoefficient` (1: урон
корпусу от огня; системам — нет). Проверено офлайн. [HullIntegrity] Warships 2.0, CivilianShips 2.0, Submarines 1.0.
IntegrityOverride из ini корабля всегда загружается игрой, затем к нему применяется множитель мода (параметра-исключения больше нет).
[HullIntegrityPerShip] (в тестовых сборках [PerShip], переносится) — строки `имя_ini = множитель` (заменяет групповой). Имена заранее неизвестны, поэтому строки не биндятся:
BepInEx хранит их как orphans и пишет обратно без комментариев; читаем из `ConfigFile.OrphanedEntries` (рефлексия),
нормализуем к виду `3.0`. Комментарий к разделу BepInEx не умеет: `SaveConfig()` после каждого `Config.Save()`
вставляет `## ...` под `[HullIntegrityPerShip]` (или дописывает пустой раздел с комментарием); `SaveOnConfigSet = false`, сохранение — в SettingChanged.
Строка-пример `example_ini_file_name` из тестовой сборки удаляется.
Строка `[HullIntegrity] PerShip = a=x, b=y` из первой тестовой сборки переносится в [HullIntegrityPerShip].
Множители — тип `Factor` со своим TOML-конвертером (пишется всегда с точкой: `2.0`, `0.5`); конвертер float не трогаем (он общий для всех плагинов).
[SideEffects] HullShellVulnerability 1, HullFloodingVulnerability 1 (1 = попадание как в игре; множитель корпуса сокращается),
HullFireVulnerability 1 (1 = пропорционально новому корпусу: при ×5 пожар от попадания в 5 раз слабее).
Point value не трогаем (раздела [PointValue] и хуков ShipEffectiveIntegrity / ComputeGlobalHash больше нет).
Старые ключи IncludeShipsWithFixedIntegrity и [PointValue] удаляются из .cfg при запуске.
[DamageControlTeams] WarshipTeams, SubmarineTeams, CivilianTeams (тонны=команды), DCTeamEffectiveness 0.5, TransferSpeed 50 м/мин,
MusterTime 60 с, HealTime 90 с, HealTimeSpread 0, KnockoutsToDeath 2 (TransferSpeed 0 = без времени на путь; оба 0 = перемещения как в игре).
[Sinking] ListLimit / TrimLimit / TrimReferenceLength / FloodLimit / DeckFloodingSpeed — умолчания в `Release/Profiles/*.cfg` (Base Game: 45°/8°/40% как в игре). TRIM по длине: tan θ = tan TrimLimit · √(TrimReferenceLength / L). .cfg на Advanced со старыми значениями при запуске переводится на новые, чтобы не стать Custom. [General] Profile (Advanced).
[UI] DetailedView, ColorWeaponStatus, FloodingOneArrowAt 0.001, FloodingTwoArrowsAt 0.25, FireOneArrowAt 0.001, FireTwoArrowsUpAt 0.25, FireTwoArrowsDownAt 0.25.

## Открытые вопросы
* Жалоба «пожары растут в разы быстрее» при Warships 5: по коду рост не зависит от X; ждали логов LogFires (1.0 vs 5.0).
* Порог роста течи без DC (< 19%) в `FloodingCompartment.OnFixedUpdate` не компенсируется множителем корпуса.
* Крен от дисбаланса затоплений и скорость опрокидывания — см. «Физика корабля: крен, гибель, обломок».

## Only game 0.9.0 is supported (2026-11-12)
Code and texts for older game builds removed: ReserveHits no longer falls back to `Globals._enemyTaskforce` (needs `Taskforce.IsAI`, else the feature is off with a trace line), comments about 0.8.x behaviour dropped, README "Supports Sea Power version 0.9.0 only". Kept on purpose: the "(game updated?)" guards (they warn when a future build moves something), the mod's own old-settings/old-save conversions (ConvertOld, saves of older mod builds), and the Loc.G / ModuleNames fallbacks (they cover "not loaded yet", not a game version).

## Own helicopters: no "Avoiding collision", no contact (2.3.0)
Seen in a trace (Hermes R12): list / trim and the red-zone limit of Auto DC jump right after `Collisions: ... (Helicopter)` lines, and the ship showed the status "Avoiding collision". Game code (0.9.2 and 0.9.3 beta, same): `CollisionAvoidanceJobScheduler.CreateAndSchedule` sets the mask bit 8192 ("ignore this unit") with `objectBase._homeBase == objectBase && objectBase._ignoreInCollisionAvoidanceAfterLaunch` - the object against itself (the non-job `ObjectCollisionAvoidance.CheckCollisionAvoidance` has the right test, `vehicle.Object._homeBase == ParentObjectBase`). So the bit is never set for a helicopter, and a carrier whose helicopter is within reaction range and 100 ft in height goes into `VesselStates.AvoidingCollision`: rudder = -50 * `GetAvoidanceRudderInput()` (the sign follows the side the helicopter is on) and the telegraph changes; `Vessel.applyTiltAndPitchForces` turns the turn rate (x TiltFactor) into the list offsets. Second part: the helicopter is kinematic, the ship is not; a contact of the helicopter's colliders with the hull gives the ship an impulse (the game has no IgnoreCollision for that pair).
`src/OwnHelicopters.cs`, option `[Collisions] OwnHelicopters` (GUI "Ignore own helicopters"; Advanced on, Base Game off): postfix of `CollisionAvoidanceJobScheduler.Complete` walks `ObjectsManager._listOfAllUnits`: a ship whose `CollisionAvoidance.UnitToAvoid` is a helicopter with `_homeBase == ship` gets UnitToAvoid = null (the setter itself is not patched: a small property setter may be inlined); every helicopter with a ship as home base gets all its colliders / the ship's colliders on `Physics.IgnoreCollision` once per (helicopter, ship) pair (memo in a ConditionalWeakTable; re-done when the home base changes). Cost: one pass over the units per Complete, reflection only on a match. Side effect: when the nearest thing in the job's result is an own helicopter, a farther real threat is not avoided in that frame (the job gives one UnitToAvoid).
The settings page "Collisions" keeps all its rows (groups Collisions, Hull damage, Speed after a blow; "On Contact" merged into Collisions). The row of pages (General, Hull, ...) now sits above the scrolled rows (`_pageHost`; inside the body only while screenshots are taken) - it used to scroll away with the rows. Trace lines: "OwnHelicopters: ... collider pairs ignore each other", "a ship no longer avoids its own helicopter ... cleared N times".
Not tried in the game. Check: a carrier launching / recovering helicopters keeps its course (no "Avoiding collision" status, no rudder swing), list / trim stay quiet, the trace has the "collider pairs" line once per helicopter, no more `Collisions: <carrier>: ... (Helicopter)` lines; Base Game profile keeps the game's behaviour.
