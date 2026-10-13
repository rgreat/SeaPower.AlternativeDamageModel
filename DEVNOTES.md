# Alternative Damage Model — заметки для продолжения работы (для Claude)

Мод для Sea Power: BepInEx 5 + Harmony, загружается через Anchor Chain (Workshop 3380210757), устроен как Smart Windows
(тот же `AnchorChainEntry`/`ModState`, тот же csproj, сборка в песочнице — см. DEVNOTES Smart Windows).
Весь доступ к игре — через рефлексию по именам (в csproj нет ссылки на Seapower-Scripts).

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
* `OnHitUnit` / `ProcessCompartmentHit` — `FireIntegrity*` = `X / HullFireVulnerability` (новый X: сильный корпус горит пропорционально слабее;
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

## Перечитывание .cfg на лету
`WatchConfigFile()` в Update: раз в 1 с сравнивает время записи и размер .cfg; если файл изменён и 0,5 с не меняется —
`Config.Reload()` (SettingChanged в это время игнорируется флагом `_reloading`, файл НЕ перезаписывается), `ReadSettings()`,
`RescaleLiveHulls()`. Свою запись (`SaveConfig`) запоминаем (`RememberCfgStamp`), чтобы не перечитывать её.
Изменение из игры (Configuration Manager) → SettingChanged → ReadSettings + SaveConfig + RescaleLiveHulls.
`RescaleLiveHulls`: для кораблей в миссии (`LiveHulls`, слабые ссылки на Compartments) пересчитывает множитель по сохранённым
ini/группе; при изменении масштабирует на k = new/old всё, что ∝ X: Compartments `_maxIntegrity, _fireMult, _maxTotalIntegrity,
_maxLimitIntegrity, _limitIntegrity`; FloodingCompartment `_maxIntegrity, _currentIntegrity, _lowestIntegrity` (кроме float.MaxValue);
SystemCompartment `_maxFireSeverity, _maxfireGrowRate, _minfireGrowRate, _fireGrowRate, FireSeverity` → состояние в % сохраняется.
`TotalIntegrity` игра пересчитывает сама каждый тик. Point value пересчитывается только при запуске игры.

## Вкладка Damage Control (Unit Status) — `src/DamageControlUi.cs`
Noesis используется напрямую (ссылка на `Noesis.NoesisGUI.dll` в csproj), игровые типы — рефлексией. Весь код с Noesis — в
`DamageControlUi`; из плагина его вызывают только методы с NoInlining (PatchUi/ApplyUiSettings/UiTick), ошибки → вид выключается.
* Вид: постфикс конструктора `SeapowerUI.DamageControlView` → слабые ссылки; `Tick` 4 раза/с обрабатывает видимые.
  DataContext вида = `DamageControlViewModel` (Compartments, DamageSummaries[5], AssignDamageControlTeam, ReturnDamageControlTeam).
* Разметка — `DamageControlView.xaml` (выгружен через DumpXaml): Grid.Row 2 — общий Grid из 5 столбцов: в строке 0 пять
  TextBlock-названий, в строке 1 пять Grid «− DC Teams: N +» (Button Command = Return/AssignDamageControlTeam,
  CommandParameter = индекс отсека), в строке 3 пять ListBox (ItemsSource = DamageSummaries[i]).
  ВНИМАНИЕ: индекс 0 = Forward (столбец 4), индекс 4 = Aft (столбец 0) — сопоставлять по Grid.Column, не по порядку.
  ItemTemplate строки: Grid > TextBlock Text="{Binding Description}" (+ значки ремонта/«!»). ListBoxItem: Visibility Collapsed
  при DamageIntensity = 0, Foreground по DamageIntensity 1..4. Строка 3 корня — Border > StackPanel с заголовком
  «Auto Damage Control Status» и строкой помощи (`damagecontrolautopriorityexplanation`).
* Игра каждый кадр присваивает `floodStates[i].DamageIntensity` (Compartments.OnUpdate); сеттер всегда уведомляет, а
  TrulyObservableCollection на любое изменение свойства элемента шлёт Replace → элемент строки пересоздаётся каждый кадр.
  Префикс `DcIntensityPrefix(int value, int ____damageIntensity)` пропускает присваивание того же значения (только пока вид
  включён) → `DamageControlUi.StableRows = true`: строки пересоздаются только при смене стадии, в них рисуем Inlines
  (цветные стрелки); пропавший элемент ищется заново каждый тик (поддерево ListBox). Без префикса — «текстовый режим»:
  переписывается `DamageSummary.Description` (+/-). Режимы не смешиваются: любое изменение Description тоже пересоздаёт элемент.
* Поиск: название — TextBlock-сосед ListBox с тем же Grid.Column (запасной вариант — по порядку); команды — TextBlock-сосед
  кнопки «+» с CommandParameter == i. Неудача (нет 5 списков) → предупреждение со счётчиками и дамп
  `BepInEx/AlternativeDamageModel.dctab.txt` (один раз за запуск).
* Вывод: переписываются `TextBlock.Inlines` (Run + InlineUIContainer с Path-стрелками/иконками). Перерисовка — когда меняется
  показываемое или игра сама переписала текст (биндинг: Text != запомненный после нашей отрисовки).
  Заголовок: имя + `ip% / is%` (цвет по стадии ≥75 белый, ≥25 оранжевый, иначе красный) + одна стрелка при любом изменении
  integrity (зелёная вверх / красная вниз); при ≥99.95/99.95 только имя. Flooding: `p% ▲▲ / s% ▼` (пороги [UI] Flooding*At, %/с);
  Fire: `f%` от `_maxFireSeverity` + стрелки (FireOneArrowAt, FireTwoArrowsUpAt, FireTwoArrowsDownAt). Тренды — по игровому времени.
* (старое) Строка помощи панели «Auto Damage Control Status» — самый длинный TextBlock вне колонок и кнопок: к игровому тексту
  дописывается значок ⓘ и «Numbers: Port % / Starboard %…», на строку вешается всплывающая легенда (`Legend()`: круг с
  силуэтом корабля — левая половина красная (Port), правая зелёная (Starboard), как ходовые огни; расшифровка чисел, стрелок и значков).
* Панель «Auto Damage Control Status» — в одну строку: заголовок (первый TextBlock той же StackPanel) скрыт (Collapsed) и
  вписан в начало строки помощи своим цветом; при режиме DC Priority ≠ Auto вместо игрового текста — порядок этого режима.
  Наш текст «Numbers: …» и (i) оттуда убраны. (i) с легендой (подсказка) — в заголовке вкладки: RadioButton полосы вкладок
  Unit Status (DataContext = UnitStatusTabViewModel, его Content == vm вкладки), в его TextBlock дописывается InlineUIContainer.
* `src/DamageControlUi.Panels.cs` (добавляются вместе с карточкой DC Priority и убираются с ней):
  - карточка «Hull Damage / Flooding» (Border в столбце 4 сетки карточек > Grid из 2 строк): третья RowDefinition + Grid
    «Fires:» по образцу строки Flooding; значение — худшая стадия пожара (SystemCompartment.FireState: Minor/Moderate/Severe)
    и число горящих секций, цвет — кисти игры `Brush.TextBox.Foreground*` (TryFindResource);
  - карточка LIST / TRIM / LEVEL в Grid под карточками (тот, где Image профиля корабля, 3 столбца), столбец 0, Margin top 36
    (у этой строки Margin −30). Углы — как в отладке F10 (Damage: «Listing» = transform.localEulerAngles.z; дифферент = .x),
    сглажены экспоненциально, τ = 0,5 с игрового времени (устанавливается за 1–2 с); при смене юнита — сразу без сглаживания.
    Знаки: +z — крен на левый борт (стрелка влево) — проверить; дифферент: +x — стрелка вправо; подписи шкалы: слева STERN, справа BOW (по проверке в игре).
    Полоса шкал серая (160,166,174), чтобы белая стрелка читалась. Шкалы = `Limits` (ListFull 31,5°, TrimFull 7°, LevelFull 28% = первые шкалы −30%), красное — последние 25% каждой шкалы
    LEVEL (16×56, верх опущен на 2 px): слои — тёмный фон, вода синим снизу, риски по обоим бокам каждые 1/8 высоты (2,5 px),
    в верхних 20% вместо рисок красные полосы 3 px, рамка поверх; процент краснеет от 80% шкалы. Превью — `ref/icons/level.py`.
* Ускоренное затопление раньше — `src/Limits.cs` + транспайлер `Compartments.OnFixedUpdate` (константы ldc.r4 → вызов, при
  выключенном моде возвращает игровое): 40% затопления → тонет (теперь 28%), |дифферент| > 8° при затопленной секции → тонет
  (7°), |крен| > 45° → `AddFloodingForHeavyListing` (вода через кромку палубы) (31,5°). Опрокидывание 70° не трогаем;
  у подлодок порог 50% не трогаем. Проверено офлайн: по 1 месту на константу.
* Значки команд DC (16×16) и картинка легенды — векторные Path; данные генерируются: `ref/icons/gen2.py` (shapely → path data
  `F0 …`, слои «контур + цвет», `icons.json`), `legend.py` (`pic.json`), превью `sheet.py` / `legend.py` (cairosvg).
  В C# — `IconLayers` и `Pic*` в DamageControlUi.cs, вставлены из сгенерированного (менять через генератор и вставлять заново).
  Стиль (согласован): плоские фигуры в 2–3 тона без обводки, прямые грани, мало скруглений; капля и пламя — с бликом.
  Капля, пламя, ручная колонка с каплей, горелка над швом (дуга-звезда, раскалённый шов, штрихи искр), ключ + отвёртка,
  «By priority» в меню — шестерня в кольце из двух стрелок (IconByPriority = 99);
  матрос аварийной партии по фото ВМС США: красная каска с жёлтой светоотражающей полосой, бежевый огнезащитный
  капюшон вокруг лица и на плечах, тёмно-синий комбинезон с белой нашивкой.
* Приоритеты и задачи команд — `src/DcControl.cs` (без UI): режим на корабль Auto/Fire/Flooding; по слоту на команду
  (задача вручную или 0 = по приоритету; удаляется сначала «по приоритету», затем последний). `CheckDamageControl(i)` заменён
  префиксом (пока мод включён): группы команд с ручной задачей зовут свой `Repair*` с временным `DamageControlTeamsNumbers[i]` =
  размер группы (Repair* берут силу из этого числа; в finally число возвращается), нет работы → в общую группу; общая группа — первая
  задача по порядку режима (Auto = игровой Leak>Fire>Pump>Hull>Systems; Fire = Fire>Leak>…; Flooding = Leak>Pump>Fire>…);
  ничего и Auto DC → `ReturnDCTeamToPool` как в игре. Auto DC включён → ручные задачи сбрасываются. `CheckDCTeamsAllocation`:
  в режиме Fire свободная команда идёт к сильнейшему пожару, Flooding — после пробоин к большей воде; иначе игровой выбор.
  Вызов private Repair* — `AccessTools.MethodDelegate<Func<object,int,bool>>`, запасной путь — Invoke.
* UI: значки команд — Border(Icon) в InlineUIContainer; при Auto DC off — подсветка под мышью, клик → ContextMenu
  (5 задач + «By priority»), ручная задача — жёлтое подчёркивание. Карточка «DC Priority» (3 ToggleButton) добавляется в Grid
  карточек (тот, где 5 ColumnDefinitions и есть видимый ToggleButton Auto DC), столбец 3, справа; стиль копируется с карточки Auto DC.
  Старые постфиксы Repair* для отображения задач удалены — задачи теперь знает DcControl.
* Вкладка переиспользуется при выборе другого юнита: DataContext (vm) тот же, меняется vm.Compartments. При смене Compartments
  — Restore текстов, повторный поиск; карточка «DC Priority» остаётся, её кнопки берут `st.Comp` (текущий юнит) — раньше
  замыкание держало первый юнит, и выбор «сбрасывался» на Auto. Режим хранится на Compartments (ConditionalWeakTable) —
  отдельно для каждого корабля, до конца миссии.
* Сохранения: режим DC Priority и ручные задачи — ключ `AlternativeDamageModel_DC = mode:t,t|t||…` в секции юнита
  (постфиксы Compartments.SaveStateToFile / LoadStateFromFile; не ';' — IniHandler считает его комментарием).
  Состояние корпуса игра хранит сама (абсолютные integrity отсеков; max пересчитывается Init с множителем мода).
  `_limitIntegrity` (лимит потопления) игра НЕ сохраняет — после загрузки он полный (так и в игре без мода).
* Карточка «DC Priority» проверяется каждый тик: если её нет в дереве (новая разметка после загрузки) — добавляется заново.
* (i) в заголовке вкладки опущен на 3 px (TranslateTransform): InlineUIContainer стоит на базовой линии текста.
* Раскладка верха вкладки: игровая карточка Auto DC (столбец 2 сетки карточек) опустошается и скрывается, её содержимое
  (подпись + ToggleButton, привязка Compartments.AutoDC) переносится в нашу карточку «Auto DC + DC Priority» — строка с профилем
  корабля, столбец 0, своей ширины (HorizontalAlignment Left). Карточка LIST/TRIM/LEVEL — та же строка, столбец 2, своей ширины
  (Right). Симметрию и центр картинки не навязываем (экономия места); ширины столбцов игры не меняем. Карточки Stretch по высоте
  строки, Margin top 30 → зазор 6 сверху и снизу, как между карточками. `PutBackAuto` возвращает содержимое Auto DC.
* Картинка профиля корабля (Image в столбце 1 строки профиля): MaxWidth 330 → 600, MaxHeight 256 → 300 и не выше, чем нужно
  боковым карточкам (`FitPicture` каждый тик: DesiredSize их содержимого + SideTop − PictureLift): высокая картинка катера
  становится уже, а не раздвигает ряд. Margin top 5 → 0
  (`PictureLift`; было −56 — пользователь: «слишком высоко»). При выключении — игровые значения.
* Карточки «Damage Control Teams» / «Teams Knocked Out»: игровое значение (Grid.Row 1) и «Available / Total» скрыты, вместо них
  наши TextBlock: свободные команды (DamageControlTeamsNumbers[5]) — значками матроса (TaskIdle), подпись «Free (of N)»
  (DamageControlTeamDefaultsNumber); выбывшие (vm.KnockedOutDamageControlTeamsCount) — значком IconKnocked = 98 (серый матрос с
  красным крестом). Значки 22 px (Viewbox). Каждая команда видна на вкладке один раз: свободные — тут, остальные — в отсеках.
* Задачи DC (устарело, до DcControl): префикс `CheckDamageControl(i)` → idle; постфиксы `RepairFloodingRate/RepairFire/RepairFlooding/RepairStructure/
  RepairSystems(index)` с `__result == true` → задача отсека (в игре все команды отсека делают одно и то же).
* Выключение (галочка мода или DetailedView=false): Inlines возвращаются к исходному тексту.
* XAML игры: ассеты `NoesisXaml` (Noesis.NoesisGUI.dll; поля `uri`, `content` = текст XAML) в `Sea Power_Data/*.assets`.
  `[General] DumpXaml = true` → `DamageControlUi.ExportXaml`: `Resources.FindObjectsOfTypeAll<NoesisXaml>()` → файлы в
  `BepInEx/AlternativeDamageModel_xaml/<uri>`; запускается через 1 с после старта/перечитывания настроек и после создания
  каждой вкладки Damage Control (ассеты грузятся, когда их UI впервые показан). Вручную — AssetRipper / UABEA.
* НЕ проверено в игре: распознавание разметки вкладки (XAML недоступен) — смотреть trace при Debug = true.

## Команды DC как люди — `src/DcTeams.cs`
* Число команд (вместо игрового 1/2/3 по 7000/24000 т): таблицы [DamageControlTeams] WarshipTeams / SubmarineTeams /
  CivilianTeams «тонны=команды» (от стольких тонн); ставится в ApplyHull (Compartments.Init) через DamageControlTeamDefaultsNumber
  и SetDamageControlTeamsNumbers(5, n). NumberOfDCTeams из ini корабля (`_numberOfDCTeamsOverride` > 0) — остаётся игровым.
* DCTeamEffectiveness (0.5): транспайлер `call TeamEffMul` после каждого чтения Crew._floodingRateRepair / _fireSeveretyRepair /
  _floodingRepair / _structureRepair / _systemRepair в Repair* (1/1/1/1/2 места; проверено офлайн).
* Свои объекты команд (Pool / Moving / InSection / Injured / Dead, число выбиваний), счётчики игры (DamageControlTeamsNumbers)
  держатся в согласии; при расхождении (сброс игры, загрузка) — Reconcile.
  - SendDCTeamToCompartment (префикс): из резерва −1, команда Moving к секции; по прибытии — резерв +1 и игровой Send с флагом
    _bypass (он же выбирает борт откачки). ReturnDCTeamToPool (префикс): из секции −1, Moving в резерв, по прибытии резерв +1.
  - Время перемещения = MusterTime + |(i+0,5)/count − 0,5| · длина корабля (ObjectBaseParameters._length) / TransferSpeed (м/мин),
    округление до 10 с, минимум 10 с. Moving-команда ни в одном счётчике → приказы ей невозможны.
  - Выбивание (Blastzone): игра уменьшает счётчик секции и добавляет таймер в `_damageControlTeamsDisabledTimers` → в префиксе
    Compartments.OnFixedUpdate находим секцию, где наших больше, чем у игры, берём команду: Knocks+1; 1 → Injured на
    HealTime + время перемещения из той секции; 2 → Dead навсегда. Таймеры игры очищаются (игра их не обработает).
  - RecallAllTeams (Auto DC раз в 60 с отзывает всех) — пропускается, пока мод включён (иначе команды всё время шли бы).
  - Сохранение: ключ `AlternativeDamageModel_Teams = state/section/left/total/knocks|…` (игра число команд не сохраняет;
    при загрузке выставляем и счётчики).
* Перемещения между секциями и отмена (только при выключенном Auto DC): `MoveToSection(from, to)` — время
  `TransferTime(comp, a, b)` = сборы + |pos(a) − pos(b)|·длина/скорость (−1 = резерв в середине корабля); слот ручной задачи
  этой команды убирается (`DcControl.RemoveSlot`). `CancelTransfer(team)` — назад к `From`, время = потрачено − сборы (без новых
  сборов; собиравшаяся команда возвращается сразу). Team.From хранится, в сохранении 6-е поле.
* UI перемещений: команда в пути видна и в карточке «In Transfer», и в точке назначения — серым значком со временем: в строке
  команд секции (после работающих) или, если идёт в резерв, в карточке «Damage Control Teams». Клик (Auto DC off) — меню
  «Cancel Transfer». Меню работающей команды: задачи, «By priority», «Move to section» — секции по порядку на экране
  (по Grid.Column списков), текущая серая и недоступна.
* Перестройка значков и открытые меню: пока открыто наше меню (`_menusOpen`, события ContextMenu Opened/Closed), строки
  команд и карточки команд не перестраиваются (меню закрылось бы вместе со значком); после закрытия — `_forceRender`.
  Карточки перестраиваются только при смене состава/состояний команд; время под значками обновляется на месте (`x.Clocks`),
  в «In Transfer» желтоватое (236,212,110), когда сборы прошли и отмена уже не мгновенная. В точке назначения время не пишем.
* Подсветка: команда в пути показана в двух местах; её рамки — одна группа (`Groups[teamRef]`), MouseEnter/Leave подсвечивает все.
* (i): подсказка-легенда ToolTipService Placement = Top (над кнопкой, Noesis переворачивает вниз, если не влезает),
  ShowDuration = int.MaxValue (без автоскрытия).
* UI: карточка «Damage Control Teams» — свободные (значок матроса), убитые (IconDead 95, красный крест), идущие в резерв;
  подпись «In reserve». Карточка «Teams Knocked Out» → «In Transfer» (заголовок и строка «Available In»
  скрыты/заменены): IconTransit 97 (серый) + оставшееся время, подсказка — куда идёт; за ними выбитые (IconInjured 96, серый с
  медзнаком, время под ним) — перенесены сюда, в первой карточке время не влезало.
* Легенда (i): тексты укорочены (TaskName короткие — они же в подсказках и меню команд), правый столбец 300 px, строки значков
  с TextTrimming и MaxWidth 270 — длинная строка не вылезет за рамку.
  WrapPanel, значки 22 px (Viewbox). LEVEL: процент — от шкалы (0…100%, 100% = верх = начало потопления).

## Оптимизированный Auto DC — `src/DcAuto.cs` (включается сам, пока перемещения занимают время)
Префикс CheckDCTeamsAllocation (через DcControl.AllocPrefix, первым), раз в 2 с игрового времени на корабль (v3, по отзыву
«слишком часто перемещаешь»):
* Работа секции = первая по порядку режима DC Priority: leak (_floodingRate) / fire (FireSeverity, _fireGrowRate) / water
  (FloodingDelta, % отсека) / hull (IntegrityDelta / _maxIntegrity ≥ 5%) / systems (повреждение ≥ Medium). Рост пожара (доля
  максимума/с) и воды (%/с) — сглаженная производная (τ 10 с).
* **Удержание**: команда НЕ уходит сама из секции, где есть незаконченные пожар/затопление (Auto: leak, fire, water; Fire: только
  fire; Flooding: только leak/water) — `holds[i]`; её команды учитываются первыми (target = here) и источником не бывают.
* Сколько надо: 1 на секцию с работой; секции с пожаром/водой +1, если «плохо» (leak ≥ 0,5, пожар ≥ 50% максимума, вода ≥ 33%),
  и ещё +1, если быстро растёт (пожар ≥ 0,5% максимума/с, вода ≥ 0,3%/с) — до 3. Раздача по рангу; в одном ранге сначала
  секции, где команда уже есть/идёт (там закончат быстрее), потом по размеру. Команда в пути = уже на месте (here + coming).
* Источники: первая команда секции — ближайшая (TransferTime) свободная: резерв, команда в секции без работы, лишняя
  (here > target) в секции без пожара/воды. Если свободных нет и работа — пожар/вода, можно снять команду с hull/systems,
  пробывшую там ≥ 60 с (Team.Arrived / View.Stay). Вторая и третья команды — только свободные.
* Задержка: работа должна держаться 4 с, прежде чем кто-то пойдёт (пожар от попадания, потушенный сразу, никого не двигает).
* Отправка: DcTeams.Send (через пропатченный Send → идёт) или MoveToSection. Без работы команда остаётся на месте:
  DcControl.StayWhenIdle → не ReturnDCTeamToPool. Игровой отзыв всех раз в 60 с выключен (DcTeams.RecallPrefix).
* Всё это — только при `DcTeams.TransfersOn` (TransferSpeed > 0 или MusterTime > 0); оба 0 → перемещения мгновенные, как в игре:
  игровые Send/Return, отзыв, игровая раздача (+ режимы DcControl), простаивающие команды возвращаются в резерв.
  Параметра OptimizedAutoDC больше нет (из .cfg удаляется).
* Константы в начале класса (Period, Debounce, MinStay, Tau, HullMin, *Bad, *Fast) — в .cfg не выносились.


## Профили настроек — `src/AdmPlugin.Profiles.cs`
* Профиль = все ключи разделов [HullIntegrity] [SideEffects] [Systems] [FireAndFlooding] [DamageControlTeams] [Sinking] + строки
  [HullIntegrityPerShip] и [SystemTypeIntegrity]. [General] (переключатель, логи, Debug, `Profile = имя`) и [UI] — общие.
* `AlternativeDamageModel.cfg` пишется без комментариев BepInEx (описания/тип/умолчание): `SaveConfig()` после `Cfg.Save()`
  вычищает строки `#`; `Cfg.SaveOnConfigSet = false` сразу после создания. Описания остаются в коде — подсказки окна настроек.
* `SystemProfiles()` возвращает КОПИЮ кэша `_system`: `ProfileNames()` дописывает в список свои профили — раньше они попадали
  в кэш, становились «встроенными», и окно сравнивало их со значениями из кода (ложные точки изменений на вкладках).
* Хранение БЕЗ дублирования: `AlternativeDamageModel.cfg` (`Cfg`) — ТОЛЬКО [General] и [UI]. Значения профиля привязаны к
  второму `ConfigFile Pcfg` (путь `~profile-in-memory.cfg`, файл не создаётся: SaveOnConfigSet=false, Save не вызывается),
  per-ship/per-type строки — его orphans. Источник значений — файл активного профиля (системный или свой); изменения в игре
  пишутся в файл своего профиля (системный → Custom.cfg).
* Старт: `TakeOldProfileValues()` вынимает из старого cfg разделы профиля (и старые форматы per-ship), `CleanCfg()` удаляет из
  cfg всё лишнее (кроме [General] DumpXaml=true); `StartProfile()`: файл профиля из `Profile=` (нет — Advanced, или старые
  значения сохраняются под этим именем); старые значения ≠ профиль → сохраняются как свой профиль / Custom.
* Слежение за файлами (`FileWatch`, раз в 1 с, применение через 0.5 с тишины): cfg → `ReloadConfig()` (смена `Profile=` →
  ApplyValues нового; нет такого — остаётся прежний); файл активного профиля → `ReloadProfile()`. Свои записи — Remember().
* Системные (встроенные): все `Release/Profiles/*.cfg` рядом с dll, сколько угодно; Base Game и Advanced есть всегда
  (без файла — из кода). В списке — по алфавиту, перед профилями пользователя (тоже по алфавиту); свой профиль с именем
  системного не показывается. Base Game — колонка «game:» в окне, Advanced — профиль нового cfg. Эти файлы и есть значения по умолчанию:
  чтобы изменить умолчания в релизе, правится файл, код не трогается. Значения `Cfg.Bind` в коде — только запасные для
  отсутствующего файла/ключа (с предупреждением в логе). Только чтение. Значения встроенного профиля всегда берутся
  из его файла (новые умолчания релиза подхватываются сами; старая миграция FloodLimit/TrimLimit удалена).
* Свои: `StreamingAssets/user/AdvancedDamageControl/Profiles/<имя>.cfg` (раньше BepInEx/config/AlternativeDamageModel/Profiles — переносятся оттуда один раз, старая папка удаляется) (тот же формат ini). Имя ≤ 40, без символов пути и `; # [ ]`,
  не совпадает со встроенными.
* `SyncProfile()` (Pcfg.SettingChanged, напр. Configuration Manager): значения ≠ профиль → встроенный становится Custom (файл Custom.cfg
  перезаписывается), свой — пересохраняется. Сравнение `SameValue`: числа как числа, списки без пробелов.
* `Commit(selected, values, globals)`: выбранный профиль без изменений → он; изменённый встроенный → Custom; свой → под своим
  именем; применяется сразу (ApplyValues: SetSerializedValue под `_reloading`, orphans Pcfg [HullIntegrityPerShip]/[SystemTypeIntegrity], слежение за файлом профиля, ReadSettings,
  SaveConfig, RescaleLiveHulls).
* Base Game: корпус ×1, команды как в игре (военные 0=1, 7000=2, 24000=3; подлодки 0=1, 5000=2; гражданские 0=1),
  эффективность 1.0, перемещения мгновенные (TransferSpeed 0, MusterTime 0 → игровой Auto DC), HealTime 90 ± 30 (игра:
  случайно 60–120 с), KnockoutsToDeath 0, пороги 45° / 8° / 40%. Отличие от игры: выбитые выздоравливают параллельно
  (в игре — очередью, таймер следующего от конца предыдущего).
* Превышение TRIM больше НЕ топит корабль сразу (в игре: дифферент > 8° при затопленной секции → Sink): `SinkTrim` возвращает
  float.MaxValue (кроме спасплотов — 1 отсек: та же проверка их выравнивает; и только если префикс OnFixedUpdate установлен),
  а `TrimFlooding.Tick` (префикс Compartments.OnFixedUpdate, вместе с DcTeams.Tick) при |дифферент| > предела зовёт
  `AddFloodingForHeavyListing()` на каждом борту — крайнего ещё не полного отсека погружающейся оконечности (нос вниз = +x →
  с секции 0; корма → с последней); полный (_currentFlooding ≥ _maxFlooding) — следующий, и т.д. Крен за ListLimit — как в игре: только заливает низкий борт;
  опрокидывание на 70° и вода за FloodLimit по-прежнему топят. Шаг поля Trim в окне — 0,1°.
* TRIM по длине: `Limits.Trim(L)` (единственный способ; расчёт по высоте палубы — HullEnds, коллайдеры/меши — пробовали,
  считал неверно, удалён по просьбе rgreat; ключ TrimHullFactor удаляется из .cfg): tan θ = tan TrimLimit · √(TrimReferenceLength / L) (концы уходят под воду на L/2·sin θ,
  надводный борт растёт ~√L), 1…30°. Advanced 7° @ 100 м (60 м 9°, 150 м 5,7°, 250 м 4,4°), Base Game TrimReferenceLength 0 =
  8° для всех. Транспайлер: `ldc.r4 8; ldarg.0; call SinkTrim(float, object)`; шкала TRIM — того же корабля.
* Новые ключи: [DamageControlTeams] HealTimeSpread (±с), KnockoutsToDeath (0 = никогда); [Sinking] ListLimit / TrimLimit /
  FloodLimit (были константами `Limits`; теперь static, ставятся в ReadSettings; шкалы приборов следуют им).

## Окно настроек — `src/SettingsWindow.cs`
* (i) — синий диск, шестерёнка — серая шестерня (как в первой версии); обе 14×14 в рамке `HeaderIcon` (Opacity 0.9 → 1 под
  мышью) и обе в ОДНОМ InlineUIContainer (горизонтальная StackPanel, отступ 6 px, сдвиг панели 3 px вниз): два отдельных
  контейнера налезали друг на друга / стояли на разной высоте. Окружности — две полудуги.
* Шестерёнка после (i) в заголовке вкладки; PreviewMouseLeftButtonDown (Handled) →
  `Open(st.View)` (сама вкладка как якорь: у элемента внутри InlineUIContainer родителя по VisualTreeHelper может не быть).
* Окно кладётся в самую верхнюю Grid/Canvas над вкладкой (не ItemsHost): Grid — по центру, RowSpan/ColumnSpan на всю сетку;
  Canvas — Left/Top по центру; ZIndex 30000. Popup, созданный в коде, в игре НЕ показался (первая версия) — остался только
  запасным вариантом, если такой панели нет. Перетаскивание за заголовок — TranslateTransform. Первое открытие пишется
  в лог («Settings window opened (in Grid)»), ошибка — предупреждением. Кнопки/ComboBox/TextBox/CheckBox — стили игры (неявные).
* User Interface: «Damage Control tab» → Detailed view (применяется сразу, как AlternativeDamageControl:
  `LiveKey`, `DamageControlUi.RefreshNow()` — вкладка Damage Control перестраивается / восстанавливается на следующем тике);
  группа «Trend arrows» (пороги стрелок) видна только при Detailed view = on.
* Профиль: ComboBox (встроенные помечены «(built-in)»), Save as… / Rename… / Delete — маленькие диалоги поверх окна (оверлей).
  Правка встроенного → список переключается на Custom, «based on <профиль>». Delete активного → в работу Advanced.
* Вкладки Hull / Fire & Flooding / DC Teams / Sinking / Display; строка: подпись с жёлтой точкой (≠ профилю), редактор
  (−/TextBox/+ с шагом и пределами; таблица «тонны/команды» с +/−; CheckBox), колонка «game:» (значение Base Game),
  ↺ (значение профиля). Внизу — описание настройки под мышью (= Description из .cfg), панель фиксированной высоты 108 (6 строк): окно не дёргается.
* Размер окна по умолчанию 960 × 990 ПИКСЕЛЕЙ ЭКРАНА: окно живёт в панели окна Unit Status (свой корень UI), UI игры
  масштабирован; после первой раскладки (LayoutUpdated) k = px на единицу (PointToScreen), размер = 960/k × 990/k (не больше
  экрана − 40 px), затем окно центрируется по экрану (UnityEngine.Screen), а не по панели. Раньше размер урезался по
  ActualHeight корня (это окно Unit Status) — окно выходило низким. меняется за любой край (полосы 5 px) и угол (16 px; справа внизу — штриховка) — `ResizeHandle(sx, sy)`; при
  левом/верхнем крае противоположная сторона стоит на месте (560–1600 × 420–1400),
  область настроек — строка Grid со звёздочкой (забирает остаток высоты); размер сохраняется до выхода из игры; у окна по центру Grid смещение корректируется на половину прироста (левый верхний угол на месте). Display — не профиль.
* Работа на копии; Apply/OK → `AdmPlugin.Commit`. Внешнее изменение .cfg при открытом окне без правок → `Refresh`.
* Пока TextBox в фокусе — `SeaPower.InputHandler.typingActive = true` (как у текстовых полей игры), иначе горячие клавиши.
* Крестики (закрыть окно, убрать корабль/тип из списка) — нарисованный `Cross(size)` (Path 1,5 px, по центру кнопки, Padding 0):
  символ «✕» шрифта игры сидел низко, обрезался снизу и был не по центру.

## Подсказки
* Команды DC: «DCn: что делает (секция)» — номер = место в списке команд корабля (DcTeams, стабильно: Reconcile теперь
  оставляет команды на местах, недостающие/лишние двигает). Слот k значка секции = k-я команда InSection этой секции;
  «Move to section» двигает именно её (MoveToSection(..., slot)). Плюс: задача вручную, сколько в секции, выбивания.
  Ходящие/выздоравливающие/погибшие/резерв — `RefTip` (откуда/куда, время, сборы). Подсказки — свой TextBlock (`LiveTip`): текст при открытии
  и 4 раза в секунду, пока открыта (`OpenTips`, ToolTipOpening/Closing, `UpdateOpenTips` из Tick). Собственная подсказка значка (`Icon`) убрана — перекрывала.
* Стрелки (корпус, Flooding, Fire) — Border вокруг стрелки (больше зона наведения) с `LiveTip` → `DamageControlUi.Causes.cs`:
  - вода: течь (_floodingRate), кто затыкает/качает (по номерам, `_drainPortSide` — какой борт первым), крен за ListLimit на
    этот борт (вода через борт), рост течи (<19% и нет команды), тонет, «минимум до сварки» (MinimumFlooding);
  - пожар: собственный рост (_fireGrowRate / max), попадание (скачок > 3 п.п. за выборку → `Col.FireHitT`), кто тушит,
    стадия (Severe от 50%: распространение, системы, корпус). В игре пожар сам НЕ гаснет — только командой;
  - корпус: пожар в секции, попадание (скачок > 1,5 п.п. → `HullHitP/S`), кто варит, предел сварки (MaxAllowedIntegrity:
    минимум + 20%).
* Карточка LIST/TRIM/LEVEL: подсказка снизу (`DamageControlUi.Schematic.cs`) — как панель F10 игры: корабль сверху, нос справа,
  левый борт сверху, порядок секций как на вкладке (`_order`). Ячейки (5 × P/S) обрезаны контуром корпуса (Canvas.Clip),
  залиты снизу до уровня воды; в центре — стрелки как в строке Flooding (тренды `Col.RP/RS` через `_colsShown`) и % под ними
  (цвет как у строки Flooding). Без подписей. Заголовок: вода в корабле и FloodLimit. Строится раз на карточку, обновляется 4 р/с.
  Размер 236×76 (вдвое меньше первого). Без заголовка; % всегда белые; верхний ряд сдвинут на 6 px вниз (к осевой) —
  в игре текст там стоял высоко. В ячейке Grid размером с ячейку (носовая — 70% ширины): % строго по центру,
  стрелки (6×5) над ним с отступом 21 px снизу — число не прыгает, когда стрелки появляются.
* Подсказки (команды, стрелки) — короткие строки (≤ ~35 символов), без длинных пояснений: «Port: 23%, +0.40%/s / • Leak 0.80 /
  • DC1 plugging / • Min 12% until welded». Прочерк в пустых карточках команд убран (WrapPanel MinHeight 24 держит высоту).
* Цвет строки Flooding (и % на схеме): серый — DC уже не может убрать воду (течи нет и воды не больше, чем останется после
  сварки до MaxAllowedIntegrity: MinimumFlooding·(100−allowed%)/(100−integrity%)); жёлтый — может, < 25%; красный — может, ≥ 25%.
  «Flooding» — по худшему борту, каждый % — по своему (`FloodState`).
* Приборы LIST/TRIM: риски равномерно с шагом в половину красной зоны (11,25° при 75%), на границах красных зон есть риска;
  в красной зоне риски красные, через одну длиннее. Ось стрелки — две полуокружности (прежняя почти замкнутая дуга
  смещала центр на 2,5 px).
* Меню команды «Move to section» → «To Reserve» (`DcTeams.ToReserve(comp, section, slot)`: при перемещениях — идёт в
  резерв, иначе сразу; именно та команда, по которой кликнули).
* Значок Systems (ключ с отвёрткой): стержень отвёртки светлый (206,212,220).

## Главный выключатель, пожары, окно настроек (версия с разделами User Interface / General)
* `[General] AlternativeDamageControl` (глобальная, не профиль): `Model = Enabled && _admOn` — все хуки повреждений и DC
  (корпус, побочные эффекты, скорости огня/воды, распространение огня, пороги, TrimFlooding, DcTeams.IsEnabled, DcControl,
  сохранение состояния команд). UI (вкладка) идёт по `Enabled` (галочка мода). Переключается на ходу: ReadSettings → при
  выключении `ReleaseTeams` (идущие прибывают, выбитые/погибшие — в резерв), RescaleLiveHulls → множитель корпуса 1 / свой
  и число команд `DcTeams.Retarget` (GameCount — правило игры / ModCount — таблицы; убавляет сначала резерв, потом секции).
  HullState теперь пишется и при выключенной модели (Disp, TeamsIni), чтобы включение подхватило корабли в море.
  DcTeams.IsTracking (мод вкл.): при выключенной модели только Reconcile — номера команд на вкладке. Приказы командам и
  кнопки DC Priority при выключенной модели недоступны (`DamageControlUi.ModelOn`).
* `[FireAndFlooding] FireSpreadPercent` (% за проверку; профиль; игра = 0.01333 = `GameFireSpreadPct`): транспайлер
  SystemCompartment.CheckSpreadFire — после `ldc.r8 0.9998666644096375` вызов `FireSpreadThreshold` = 1 − p/100 (≈ игре → без
  изменений). В игре проверка раз в FireCheckTime (10 с, options) при тяжести > X/600 (≈ 50% максимума).
  Старый ключ FireSpreadChance (× игры) переводится при чтении профиля / старого cfg: p = k × 0.01333 (`ConvertOld`).
* Factor пишется как "0.0####" (до 5 знаков — для 0.01333).
* Окно: вкладки User Interface (вид вкладки, стрелки), General (Alternative Damage Control — применяется сразу, без Apply;
  логи), Hull, Fire & Flooding (Fire growth, Fire damage to the hull, Fire spread chance, Water coming in), DC Teams
  (Team effectiveness, Extinguishing, Pumping out, ...), Sinking. Выключен ADC → вкладки модели и строка профилей скрыты.
* Single ships: редактируемый ComboBox; список — `ShipNames` (UnitMetadataProvider.UnitData, корабли и подлодки; имя —
  ClassName (нативное/НАТО, как в игре), при совпадении + DefaultName, затем + [ini]); ввод фильтрует (KeyUp, до 300),
  «+ Add ship» берёт ini по имени или введённый ini. В строках — имя, в подсказке — ini.
* (i) и шестерёнка — сразу: постфикс конструктора `SeapowerUI.UnitStatusView` → `RegisterStatusView`; раз в секунду (пока не
  найдено) ищем RadioButton вкладки с Content = DamageControlViewModel и декорируем (`DecorateHeader`, CWT-состояние,
  `RestoreHeaders` при выключении). Якорь окна настроек — Unit Status.

* Строгий учёт команд (DcTeams): инвариант — объектов команд не больше, чем `DamageControlTeamDefaultsNumber` (total), и
  каждая команда учтена один раз. Игра сама кладёт все команды обратно в резерв (`RepairAllDamage`: RepairAll / ремонт,
  DebugRessurect) — раньше наши «в пути / выбыла / погибла» оставались сверху (так у rgreat 2 команды превратились в 4).
  Reconcile: вне счётчиков игры допускается total − (сумма счётчиков) объектов; лишние (сначала погибшие, затем выбывшие,
  затем идущие) занимают недостающие места счётчиков, новых объектов не создаётся; восстановленным сбрасываются выбивания.
  Tick (и при выключенной модели) зовёт Reconcile, если счётчики разошлись ИЛИ объектов больше total. Таймеры выбывания
  игры снимаются только сопоставленные с нашей командой (остальные игра вернёт сама; раньше чистились все → команда терялась).
  Load: total = числу сохранённых команд. Изменение числа команд кораблём в море (Retarget) пишется в лог с именем корабля.

* Разделение: `DcControl.IsEnabled = Enabled && DetailedView` — DC Priority и задачи командам вручную работают при
  любом Alternative Damage Control; перемещения (Move to section / To Reserve / отмена) — только с ним (`ModelOn`).
  Сохранение: режим/задачи — при включённом детальном виде, команды — при включённой модели.
* ADC выкл.: карточка «In Transfer» — снова игровая «Teams Knocked Out» (её заголовок, значение, «Available In»; наши скрыты).
* Ускоренное затопление с красной зоны шкал: крен > 0,75·ListLimit (`Limits.ListFlood`), дифферент > 0,75·предела
  (`Limits.TrimFlood`); ListLimit/TrimLimit — полные шкалы. LEVEL (FloodLimit) — по-прежнему потопление, как в игре.
* Схема затоплений — подсказка всей карточки LIST/TRIM/LEVEL (у TRIM больше нет своей); под схемой строка пределов:
  «List limit … · Trim limit … (L m) · floods faster from the red zones» (ADC выкл.: значения игры).
* Окно настроек: строка профилей — на вкладке General между «Damage model» и «Logging» (скрыта при выключенном ADC).

* Настройки — НЕ BepInEx/config: `Sea Power_Data/StreamingAssets/user/AdvancedDamageControl/AlternativeDamageModel.cfg`
  (как cameraoffset: Application.streamingAssetsPath/user/<мод>), свой `ConfigFile Cfg` вместо `BaseUnityPlugin.Config`;
  старый BepInEx/config/com.rgreat.seapower.alternativedamagemodel.cfg переносится туда один раз (File.Move, без *.moved; оставшийся от тестовых сборок *.moved удаляется);
  при ошибке — BepInEx/config. Логи/trace/dctab по-прежнему в BepInEx.
* (i)/шестерёнка сразу: вкладки Unit Status — `UnitStatusTabViewModel` (Kind = DamageControl), их Content (VM вкладки)
  создаётся ТОЛЬКО при первом открытии — поэтому прошлое сравнение с Content не срабатывало. Теперь: постфикс конструкторов
  UnitStatusTabViewModel → `RegisterTab` (Kind = DamageControl) + постфикс UnitStatusView → поиск RadioButton с этим
  DataContext (сразу и раз в 0,5 с, пока не найдено).
* Single ships: только UnitData["vessels", id] без «@» (ключи `<id>@<loadout>` — доп. загрузки, их игра добавляет отдельно).
* [Systems] Integrity (× игры, профиль) + [SystemTypeIntegrity] (orphans «ModuleType = Integrity» — ЯВНОЕ значение вместо
  modules.ini; в профиле — `Profile.Modules`): в ApplyHull и RescaleLiveHulls `ScaleSystems(obp)` — все `obp._systems`:
  множитель = своё / `ModuleNames.GameIntegrity(тип)` (поправки игры — displacement^¼, мин. 10, значение из ini корабля —
  сохраняют пропорцию), иначе [Systems] Integrity; `_maxIntegrity = исходное × множитель` (исходное в CWT, идемпотентно),
  CurrentIntegrity — тот же процент. ADC выкл. → ×1.
  `ModuleNames`: типы и Integrity из игры (`SeaPower.SystemLibraries.Modules`: SectionNames, Get(name).Integrity /
  ScalesWithDisplacement), запасная таблица 0.8.4; Integrity 0 (Inert) не в списке. В окне при выборе типа подставляется
  значение игры, колонка «game: N» (× displ.^¼ для масштабируемых).
  Старый [SystemsPerType] (множители) переводится: Integrity = k × значение игры (`ConvertOld`).
* Сохранения игры: целостности в сейве абсолютные (секции корпуса, пожары, CurrentIntegrity систем). Мод пишет рядом
  множители, с которыми сохранено: `AlternativeDamageModel_HullMult` в секции Compartments (всегда, и в campaign state),
  `AlternativeDamageModel_IntegrityMult` в секции каждой системы (постфикс BaseSystem.SaveStateToFile). При загрузке
  (постфиксы LoadStateFromFile; Compartments.Init/ApplyHull к этому моменту уже прошёл) значения пересчитываются на
  текущий/сохранённый: _currentIntegrity, _lowestIntegrity, FireSeverity, _fireGrowRate; CurrentIntegrity систем (≤ max).
  Пропуск: нет ключа (старые сейвы / без мода), _campaignRepair, нет данных состояния. Работает и при выключенном моде
  (постфиксы не смотрят на Enabled). Команды DC и DC Priority сохранялись и раньше (AlternativeDamageModel_DC / _Teams).
* FireProbability систем (modules.ini) игра 0.8.4 читает в BaseSystem._fireProbability, но НИГДЕ не использует (пожары от
  попаданий — Blastzone.AddCompartmentFire без участия систем) — множитель к нему ничего бы не дал; не сделан, rgreat спросить.
* Окно: надписи «Not part of a profile…» на User Interface / General убраны (сдвигали элементы).

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
[Sinking] ListLimit 31.5, TrimLimit 5.2, TrimReferenceLength 100, FloodLimit 40 (как в игре; было 28, потом 33.6). TRIM:
tan θ = tan TrimLimit · (Lref/L)^0.442 — первая версия (7° @ 100 м, √) −30% для коротких, −20% для длинных: 30 м 8,8°, 60 м 6,5°,
150 м 4,4°, 200 м 3,8°, 300 м 3,2°. .cfg на Advanced со старыми значениями (FloodLimit 28/33.6, TrimLimit 7) при запуске
переводится на новые, чтобы не стать Custom. [General] Profile (Advanced).
[UI] DetailedView, FloodingOneArrowAt 0.02, FloodingTwoArrowsAt 0.25, FireOneArrowAt 0.05, FireTwoArrowsUpAt 1.0, FireTwoArrowsDownAt 0.25.

## Открытые вопросы
* Не проверено в игре. Point value считается при старте игры: смена галочки без перезапуска может не пересчитать кэш.
* Порог выбывания команд DC (`CurrentIntegrityPercent < 50`) в ProcessCompartmentHit теперь идёт через FloodPercent (как в игре);
  порог роста течи без DC в `FloodingCompartment.OnFixedUpdate` (< 19%) не компенсируется.
* Жалоба: «пожары растут в разы быстрее» при Warships 5 — по коду рост не зависит от X; ждём логов LogFires (1.0 vs 5.0).


## Ревизия, пакет 1 — команды DC (2026-10-09)
* `DcControl.IsEnabled` (Enabled && UI) — DC Priority и задачи вручную; `DcControl.AutoOn` (Model) — умный Auto DC и
  «свободная команда остаётся на месте». CheckPrefix/AllocPrefix работают при любом из двух; без UI — режим Auto и без задач
  вручную (= игра + StayWhenIdle). StayWhenIdle требует `DcAuto.Ok`.
* Задача вручную хранится на команде: `DcTeams.Team.Task` / `.Doing` (что делала). Слот k = k-я команда секции в порядке
  `s.Teams` (`DcTeams.SectionTeams`). Уходит из секции (Return/Move/ToReserve/выбита/Reconcile в другое место) → `Leave()`
  сбрасывает задачу. Меню команды держит саму команду (`SlotOf` в момент клика). `RemoveSlot` удалён.
  Сейв DcControl тот же формат; при загрузке задачи → `Pending[i]`, раздаются командам при первом CheckPrefix.
* DcAuto: секция с любой работой (и свежей, < 4 с) оставляет себе одну команду — забрать её может только запасной путь
  (огонь/вода, команда там ≥ 60 с).
* DcTeams.Save: 7-е поле — сколько команда уже в секции (`Arrived`); Load: секции вне −1..4 → резерв, InSection без секции → Pool.
* `DcTeams.Out(comp)`: тонет или уничтожен (`_baseObject.IsDestroyed`) — Tick и DcAuto не работают.
* Выбитые: считаются только таймеры, добавленные с прошлого тика (`Ship.Timers`, ведётся и при выключенной модели), и
  снимаются с конца списка (новые).
* `RecallPrefix(comp)` при пропуске ставит `_dcTeamsLastRecall = now` (иначе OnFixedUpdate звал RecallAllTeams каждый тик).
* Прибытие: только секции 0..4; при исключении в Send резерв откатывается. `Team.Back` — повернула назад (Cancel), без сбора:
  в UI не «mustering».
* Исправлено (после пакета 1): выключение Detailed View теряло игровую кнопку Auto DC — `Restore` обнулял `st.Priority` до
  `PutBackAuto`, содержимое оставалось в нашей карточке; после включения Discover не находил карточку Auto DC → только
  игровой вид. Теперь `PutBackAuto` вынимает содержимое из текущего родителя (VisualTreeHelper) и вызывается первым; очистка
  карточки — и при `st.Cols == null`; Restore каждого вида в своём try. Одна ошибка `UiTick` больше не выключает вид навсегда
  (только 50 подряд). Окно настроек: оставшееся невидимым с прошлого экрана открывается заново, Close снимает его с
  фактического родителя.
* Возврат игрового вида: текст «DC Teams: N» после выключения Detailed View был числом на момент разбора вкладки (или пустым).
  Причина пустоты: после повторного разбора (Valid → Discover) «исходным» сохранялся наш текст (иконки → ""). Теперь
  `Keep(tb)` при первом знакомстве с TextBlock запоминает раз и навсегда: привязку Text, сами объекты Inlines (Run со своими
  привязками) и текст (`FirstText` — им берутся TeamsOrig/HeaderOrig). `RestoreBlock` возвращает эти Inlines и привязку
  (`UpdateTarget` или `SetBinding`) — игра снова показывает живое значение.
  Запасной путь для «DC Teams: N» — `WithCount` (последнее число заменяется текущим количеством).
* Шестерёнка окна настроек теперь видна при включённом моде всегда: при выключенном Detailed View на вкладке — только
  шестерёнка (без (i)), иначе включить вид обратно можно было только правкой cfg. `DamageControlUi.Tick(now, active, on)`;
  `HeaderState.Full` — какой набор иконок стоит; смена набора перерисовывает заголовок. Заголовок возвращается к тексту
  игры только при выключенном моде. В trace «settings:» пишет состояние detailed view и damage model.
* `[UI] DetailedDamageControl` переименован в `[UI] DetailedView` (визуализация). Старый ключ из cfg при старте переносится
  (orphan → значение) и удаляется. `[General] AlternativeDamageControl` (модель повреждений) — без изменений.
* Возврат карточки Auto DC: `st.AutoStack` — наша панель, куда перенесено содержимое; PutBackAuto вынимает оттуда, затем из
  логического (`FrameworkElement.Parent`) или визуального родителя. Если содержимое терялось, игровая кнопка Auto DC пропадала,
  `AddPriorityCard` не находил видимый ToggleButton в сетке из 5 колонок («summary grid not found») и верхние панели не менялись.
* Шестерёнка: окно открывается от самой нажатой шестерёнки (`Open(gear, hs.Anchor)` — второй якорь запасной). Без панели (Grid/Canvas) — Popup, который в Noesis не показывается.
* `AddPriorityCard`: поиск строки сводки — первый видимый ToggleButton, у предка-Grid из 5 колонок дочерний Border. У сетки
  секций тоже 5 колонок: когда в строках секций появляются ToggleButton (после повреждений), находилась она, card = null →
  «summary grid not found», верхний ряд не перестраивался, при выключении «Auto DC card put back: no card». Теперь такой
  ToggleButton пропускается и пробуется следующий.
* Меню команд: счётчик `_menusOpen` заменён списком открытых меню `OpenMenus` (`MenusOpen()` выкидывает закрытые и те, чья
  иконка исчезла). Счётчик залипал > 0, если Closed не приходил (вид выключен/заменён при открытом меню) — тогда иконки команд
  больше не перерисовывались, и после выключения Auto DC оставались старые, без кликов (меню не открывались).
* Заголовок вкладки: при первом наведении текст с (i)/шестерёнкой уезжал вверх — инлайн-контейнер иконок (14 px, стоит на
  базовой линии) делал строку выше после повторного измерения. Панель иконок получила Margin top −6: в раскладке она ниже
  высоты букв и строку не увеличивает; рисуется там же (RenderTransform 0,3 как был).
* Срез «этап 1» (2026-10-09): `/mnt/user-data/outputs/AlternativeDamageModel_stage1.zip`, локально git-тег `stage1`
  в /home/claude/adm. Временные trace-строки диагностики (menu click, no team object, card put back, restore: priority,
  settings window: anchor) удалены.

## Ревизия, пакет 2 — сейвы, профили, окно настроек
* Патчи Save/LoadStateFromFile (Compartments, BaseSystem) ставятся независимо от DcControl.Init.
* Сейв без ключа множителя корпуса: если в секции есть ключи команд/DC мода (старая сборка) — значения как есть; иначе это сейв
  без мода → множитель сохранения = 1 (ванильный сейв с ×5 больше не даёт 50% корпуса). Системы без ключа откладываются
  (`PendingSys` по объекту юнита) и решаются вместе с корпусом (`TakePendingSystems`), который грузится сразу после них.
* `AlternativeDamageModel_IntegrityMult` пишется только для систем, которые мод масштабирует (SysState, т. е. корабли), и только
  если игра записала CurrentIntegrity.
* `BadKey`: ключи [HullIntegrityPerShip]/[SystemTypeIntegrity] без `= \n \t \ " ' [ ]` (BepInEx их не принимает и бросал
  исключение в ApplyValues → мод не стартовал). Проверка в окне (Add), при чтении профиля (строка пропускается, Trace) и
  `PutLine` в ApplyValues (по ключу, в try).
* TakeOldProfileValues: списки кораблей/типов профиля заменяются строками cfg, только если такие строки в cfg есть.
  ReloadConfig: убранные CleanCfg строки удаляются и из файла (SaveConfig) + предупреждение.
* «Save as» под именем активного профиля: значения сразу применяются (ApplyValues).
* Окно: ввод не-числа в поле возвращает прежнее значение (раньше становился минимумом).
* Описание [General] AlternativeDamageControl: DC Priority и задачи вручную относятся к виду и остаются.
* Исправлено: сейв этой сборки, сделанный при выключенном в меню Mods моде, — у систем нет SysState → нет ключа, а у корпуса ключ
  есть («1.0»), и системы брались как есть (20 из 40 при Integrity ×2). Теперь системы без ключа в сейве с ключом корпуса = ×1
  (не масштабированы тогда) и пересчитываются; «как есть» — только сейвы старых сборок (ключей корпуса нет, есть ключи команд/DC).

## Ревизия, пакет 3 — вкладка Damage Control
* Шкалы по режиму и типу (`ListScale`, `TrimScale`, `SinkAt`, `IsSub` в Panels): ADC вкл. — Limits мода; выкл. — игровые
  точки (вода через палубу 45°, утопление при 8°) как начало красной зоны (75% шкалы: 60° / 10.67°), уровень 40%. Подлодки:
  уровень 50% всегда (игра: `FloodingPercentage > 50`, мод его не меняет), крен/дифферент их не топят — значения не краснеют,
  на схеме «Submarine: list and trim flood nothing», в подсказке затопления нет «water over deck». Порог «water over deck»
  при выкл. ADC — 45°.
* Смена юнита (новая VM): строки старого юнита возвращаются к тексту игры (Restore без карточки) до сброса Cols.
* Тренды/«Hit N s ago»: после паузы > 1 с реального времени (вкладка скрыта) отсчёт заново; скачок считается попаданием, только
  если больше того, что даёт сам тренд за это время (×2) — на ускорении времени не ложные.
* Схема: вычисление FloodState убрано (цифры одного цвета), кисть одна.
* Утечки: открытые подсказки (`OpenTip` с владельцем) выбрасываются, когда иконка не видна/не в дереве (правило `k > 8`
  убрано); `Groups` чистятся каждый тик (`PruneGroups`); при выключении вида Groups/OpenTips/OpenMenus очищаются.
* Без новых кистей на тик: `SetText`/`SetBrush` (только при изменении), ленивые общие кисти BDanger/BDimText/BWhite/BPastMuster.
* Поиск строк Flooding/Fire: каждый тик первые 8 промахов (2 с), дальше раз в секунду.
* Тексты: «lost (knocked out N times)» по KnocksToDie; FireCause — корпус горит на всех стадиях, системы понемногу и при Minor.

## Ревизия, пакет 4 — механика и производительность
* Зажигательные попадания (`_ap._addFire` 3/2/1 → доля 0.5/0.25/0.125 максимума пожара): транспайлер OnHitUnit вставляет
  `IncendiaryFrac` после каждой из трёх констант (ldc.r4 → stloc, за ≤6 инструкций до — ldfld _addFire): доля / m × HullFireVulnerability
  (m — множитель корпуса этого юнита; юнит берётся из `FireIntegrity`, вызванного чуть раньше в том же попадании —
  `_lastFireComp`). Ожидается 3 места.
* FloodingSpeed: только «_shipFloodingCoefficient * _floodingRate * 0.5f * dt» (вода через пробоины; IL: ldfld _floodingRate, mul, ldc.r4 0.5 — сверено с DLL); ветка тонущего корабля
  («… * dt * _floodingMultiplicatorWhenSinking») — как в игре: FloodingSpeed 0 больше не держит тонущий корабль на плаву. Ожидается 1 место.
* Тик физики: TrimFlooding — постоянные данные корабля (судно ли, число секций, объект, длина) в CWT, дифферент проверяется
  первым; SinkTrim — число секций в CWT; DcTeams.InSync — общий буфер; DcControl.CheckPrefix — общий список команд секции
  (`SectionTeams(comp, i, into)`).

## Состояние на 2026-10-09 (ревизия завершена)
* Этапы ревизии 1–4 закрыты и проверены в игре. Срезы: `AlternativeDamageModel_stage1..4.zip`, git-теги `stage1..stage4`
  (локально, /home/claude/adm).
* Не делались (по решению rgreat): Base Game как точная копия игры по крену/дифференту (п. 4 ревизии); уход свободных команд
  из секций < 50%; сглаживание дифферента / условие повреждений для TrimFlooding; ReleaseAll для кораблей вне LiveHulls;
  BecomeOwn после ↺; Commit без перехвата ошибок записи; зажигалки/FireProbability систем (игра не использует).
* Правило работы: сначала trace/лог, потом правка; шаблоны транспайлеров сверять с IL (`ilspycmd -il`).

## 2026-10-09: новые функции
* Auto DC: свободная команда остаётся на месте, только пока на корабле есть работа (`DcAuto.AnyWork`: работа в любой секции за
  последние 10 с; `Ship.LastWork`); иначе CheckPrefix возвращает её в резерв (пешком), как игровой Auto DC.
* [FireAndFlooding] UnattendedFireGrowth (false) / UnattendedFireTime (60 с) / UnattendedFireGrowthRate (0.5 % макс. роста за
  проверку): постфикс RepairFire (true → время тушения секции), постфикс CheckFire (раз в FireCheckTime): горит дольше
  UnattendedFireTime с начала пожара или последнего тушения → _fireGrowRate += rate% × _maxfireGrowRate (как попадание;
  обычное попадание добавляет 0.1–0.8%). Строка «Unattended N s: grows faster» в подсказке пожара. В окне время/скорость
  видны только при включённом флажке (`ShownWhen`).
* Single system types: колонка «game: N» обычным серым; масштабируемые по водоизмещению — светло-голубым в две строки
  «game: N» / «tonnage dependent»; подсказка (`ModuleTip`) — только цифры: итоговое Integrity этой системы на боевом корабле
  500/1000/3000/5000/10000/20000 т («game» и, если значение своё, «yours»); панель помощи — те же цифры одной строкой (`ModuleHelp`) (`ScaledIntegrity` = игровая формула LoadModuleParameters:
  v × t^¼, гражданские ½, не меньше ×1, итог не меньше 10).
* [Effects] ExtraSmokeEmitters (true) / ExtraSmokeMinLength (100 м) — `FireSmoke`: постфикс Compartments.Init — 5 точек
  вместо 3 (Compartment1..3FirePosition: нос между секциями 0/1, середина = секция 2, корма между 3/4 → секции 1/3 на 2/3 пути
  к концевой точке, 0/4 на 4/3; высота/борт — как у концевой; не дальше 45% длины от середины), _firesCount = 5, массивы
  _currentFires/_fireDecal/эффектов пересоздаются; постфикс UpdateFireState — у каждой точки пожар своей секции. Только новые
  корабли. Вкладка Effects перед General (не модельная — видна при выключенном ADC).
* Окно настроек: 1040 × 990 (было 960, +80 под вкладку Effects).
* Описание TransferSpeed сокращено.
* Дым пожаров (игра): эффекты переключаются ступенями (small/moderate/severe по FireState: Severe ≥ maxInt/600 ≈ 50% макс.,
  Moderate ≥ /1200, иначе small; пороги /300 и /1800 — дубликаты), и только в CheckFire (раз в FireCheckTime 10 с); после
  попадания updateFireEffect идёт по старым _currentFires. Мод: `FireSmoke.Tick` (из TeamTickPrefix, раз в 1 с Time.time) —
  при смене стадии любой секции вызывает UpdateFireState + updateFireEffect. CreateFireEffect: `transform.Find("pointlight").gameObject`
  без проверки (NRE при отсутствии света → нет звука, остальные точки не обновлены) — транспайлер Find+get_gameObject →
  `FireSmoke.SafeChild`. При Debug в trace — состав каждого эффекта пожара (ParticleSystem: rate/bursts/size/life/max/loop,
  Light, Audio; через рефлексию, модули частиц/звука не подключены) — для решения о плавной плотности.
* Эффекты пожара игры (из trace): shipfire_s / shipfire_s_2 / shipfire_m_2 / shipfire_l — корневая система «дым с огнём в
  спрайтах» rate 5/с, life 6–16 с; «shipfire_m_part_2» (клубы, rate 1, life 10–30); у m/l ещё «fireemitter» (rate 3, life 2),
  у l — «Fire_Flares» (max 25); Light pointlight 0.5; Audio 0.5. Всплесков нет, всё потоком.
* [Effects] SmokeBySeverity («Tune visuals», true) / SmokeStartOpacity (0%) / SmokeEndOpacity (100%) / SmokeModerateFrom (50%) / SmokeSevereFrom (90%):
  вид пожара по силе (доля макс.), а не по стадиям игры (25/50%) — `Restage` в постфиксе UpdateFireState переписывает
  _currentFires (не трогая «нет пожара»); урон/распространение по стадиям игры не меняются. `FireSmoke.Tick` с опцией раз
  в секунду вызывает UpdateFireState и updateFireEffect при смене вида. Прозрачность — только у слабого вида: альфа
  стартового цвета дымовых систем (корень и «shipfire*», пламя не трогается) от StartOpacity при 0 до 100% к ModerateFrom
  (на средней/сильной стадиях прозрачность давала обратный эффект — убрано). Режимы цвета Color/TwoColors, новые частицы.
  Выключение опции возвращает альфу (`ResetLooks`). Trace: «smoke density: '…' N smoke systems, colour modes …».
* Плотность дыма слабого пожара — не альфа, а частицы в секунду (EmissionModule.rateOverTimeMultiplier × k). С альфой в игре
  новые частицы были не видны до ~38% и резко проявлялись к 40%: по всей видимости, материал дыма использует альфу частицы как
  порог (dissolve/cut-off), а не как прозрачность. Ключ SmokeStartOpacity → SmokeStartDensity.
* Диагностика (Debug, `FireSmoke.Probe`, раз в секунду на горящую точку): сила, стадия игры, вид, k, rate после установки,
  живые частицы. Итог прогона: rate реально меняется (2.81 при k 0.56), живых спрайтов ~14 при k 0.56 и ~23 при k 0.96;
  «shipfire_m_part_2» малого эффекта не излучает вовсе (loop False, stop). При смене вида старый эффект гаснет ~5 с (n 19→0),
  новый набирает частицы с нуля. На глаз пользователя больше спрайтов = прозрачнее, а не плотнее.
* Частоту частиц больше не трогаем (на глаз с ней и без неё одинаково). Снова только прозрачность: альфа стартового цвета
  новых частиц × k, k линейно от SmokeStartOpacity при 0% силы пожара до SmokeEndOpacity к
  SmokeModerateFrom; оба значения задаёт пользователь, по умолчанию 100/100 (как в игре). Ключи *Density → *Opacity.
  Trace: «smoke opacity: '…' N smoke systems, colour modes M a=…», в probe — альфа нового цвета вместо rate.
* Probe показал: у основного дыма слабого эффекта («shipfire_s*») стартовый цвет — градиент (a -1 = не обработан), так что
  прозрачность до видимого дыма не доходила; применялась лишь к «shipfire_m_part_2», который в слабом эффекте не излучает.
  Теперь для режимов Gradient / TwoGradients делается копия градиента с альфой всех ключей × k (Gradient — класс, исходный
  не трогаем). В probe для градиента a = наибольший ключ альфы. При Debug в trace — «smoke: effects switched (…)» в момент
  реального вызова updateFireEffect (play+emit в probe — состояние системы, а не событие включения).
* Градиент, прочитанный из startColor, остаётся связан с градиентом самой системы: после установки нашей копии «исходный»
  градиент тоже менялся, и каждое новое k умножалось на предыдущее (a: 0.56 → 0.32 → 0.19 … → 0, дым исчезал полностью).
  Теперь в `LookOf` исходный цвет сразу копируется по ключам (`Detached`), и все k считаются только от этой копии.
* Подписи: «Tune visuals» (было Looks by fire strength), «Moderate fire look from», «Severe fire look from».
* Закладка Damage Control обновляется сразу, а не только по таймеру 0.25 с: `RegisterView` подписывается на IsVisibleChanged
  (закладка открыта) и DataContextChanged (другой юнит), а между тиками каждый кадр дешёвая проверка `UnitChanged` (другой
  view model или другой корабль в том же). Любое из этого — `Kick`: работа на следующем кадре, и ещё секунду каждый кадр,
  пока вид не распознан (перед Discover — `UpdateLayout`, чтобы строки списков появились сразу); неудачи в эту секунду не
  считаются отказами (иначе через 3 попытки ушёл бы дамп/предупреждение) и не ждут 1 с до следующей попытки.
* Вкладка настроек General → «Damage Model». [FireAndFlooding] UnattendedFireMaxGrowth (0.5 %/с от макс. силы пожара):
  предел, до которого безнадзорный пожар поднимает скорость роста; UnattendedFireGrowthRate по умолчанию 0.25. У строк
  Unattended fire в колонке игры «default: x» (в игре таких параметров нет). Умолчания: ExtraSmokeMinLength 75,
  стрелки FloodingOne/FireOne 0.001, FireTwoArrowsUp 0.25 (остальное уже совпадало: Profile Advanced, Debug/Log* off).
