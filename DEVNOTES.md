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
[Sinking] ListLimit / TrimLimit / TrimReferenceLength / FloodLimit / DeckFloodingSpeed — умолчания в `Release/Profiles/*.cfg` (Base Game: 45°/8°/40% как в игре). TRIM по длине: tan θ = tan TrimLimit · √(TrimReferenceLength / L). .cfg на Advanced со старыми значениями при запуске переводится на новые, чтобы не стать Custom. [General] Profile (Advanced).
[UI] DetailedView, ColorWeaponStatus, FloodingOneArrowAt 0.001, FloodingTwoArrowsAt 0.25, FireOneArrowAt 0.001, FireTwoArrowsUpAt 0.25, FireTwoArrowsDownAt 0.25.

## Открытые вопросы
* Жалоба «пожары растут в разы быстрее» при Warships 5: по коду рост не зависит от X; ждали логов LogFires (1.0 vs 5.0).
* Порог роста течи без DC (< 19%) в `FloodingCompartment.OnFixedUpdate` не компенсируется множителем корпуса.
* Крен от дисбаланса затоплений и скорость опрокидывания — см. «Физика корабля: крен, гибель, обломок».

## Решения и итоги ревизии 2026-10-09 (пакеты 1–4, закрыты и проверены в игре)
* DC Priority / задачи вручную: `DcControl.IsEnabled` = Enabled && DetailedView; умный Auto DC и «команда остаётся на месте» — `DcControl.AutoOn` (Model).
  Задача хранится на команде (`Team.Task/.Doing`, слот k = k-я команда секции `s.Teams`); уход из секции сбрасывает задачу (`Leave()`).
  DcAuto: секция с работой оставляет себе одну команду; `Team.Back` = повернула назад; `DcTeams.Out(comp)` — тонет/уничтожен.
  Выбитые: считаются таймеры, добавленные с прошлого тика. `RecallPrefix` при пропуске ставит `_dcTeamsLastRecall = now`.
  Свободная команда остаётся на месте, пока на корабле есть работа (`DcAuto.AnyWork`, последние 10 с), иначе — в резерв пешком.
* UI-возвраты: `Keep(tb)` запоминает исходную привязку/Inlines TextBlock раз и навсегда (`RestoreBlock`); `PutBackAuto` вынимает содержимое из текущего
  родителя первым; `AddPriorityCard` пропускает ToggleButton секций (у сетки секций тоже 5 колонок); открытые меню — список `OpenMenus`
  (не счётчик); одна ошибка `UiTick` не выключает вид (50 подряд — выключает); при выключенном Detailed View остаётся шестерёнка
  (иначе вид не включить правкой окна). `[UI] DetailedDamageControl` → `DetailedView` (старый ключ переносится).
* Сейвы: ключи `AlternativeDamageModel_HullMult` (Compartments) и `_IntegrityMult` (системы); без ключа корпуса, но с ключами команд/DC — значения как есть
  (старая сборка); иначе множитель сохранения = 1; системы без ключа решаются вместе с корпусом (`PendingSys`/`TakePendingSystems`). Патчи Save/Load ставятся независимо от DcControl.Init.
* `BadKey` (ключи без `= \n \t \ " ' [ ]`) — BepInEx иначе бросал исключение в ApplyValues. Ввод не-числа в окне возвращает прежнее значение.
* Шкалы вкладки по режиму/типу (`ListScale`, `TrimScale`, `SinkAt`, `IsSub`): ADC выкл. — игровые точки (45°/8°/40%); подлодки — уровень 50%, крен/дифферент не топят.
  Тренды/«Hit N s ago»: после паузы > 1 с отсчёт заново; скачок = попадание, только если больше тренда ×2. Без новых кистей на тик (`SetText/SetBrush`).
* Механика: зажигательные попадания — транспайлер OnHitUnit `IncendiaryFrac` (3 места: доля 0.5/0.25/0.125 / m × HullFireVulnerability);
  FloodingSpeed — только вода через пробоины (`_shipFloodingCoefficient * _floodingRate * 0.5f * dt`, 1 место), ветка тонущего корабля — как в игре.
  TrimFlooding/SinkTrim/DcTeams.InSync/CheckPrefix — данные корабля в CWT, общие буферы.
* Не делались (решение rgreat): Base Game как точная копия игры по крену/дифференту; уход свободных команд из секций < 50%; сглаживание дифферента;
  ReleaseAll для кораблей вне LiveHulls; BecomeOwn после ↺; зажигалки/FireProbability систем (игра их не использует).
* Правило работы: сначала trace/лог, потом правка; шаблоны транспайлеров сверять с IL (`ilspycmd -il`).

## Функции 2026-10-09
* [FireAndFlooding] UnattendedFireGrowth/Time/GrowthRate/MaxGrowth: постфикс RepairFire отмечает время тушения секции, постфикс CheckFire (раз в FireCheckTime) поднимает
  `_fireGrowRate` безнадзорного пожара (до MaxGrowth %/с макс. силы); строка «Unattended N s: grows faster» в подсказке; в колонке игры «default: x».
* Системы: колонка «game: N» (масштабируемые по водоизмещению — «tonnage dependent»), подсказка `ModuleTip` — итоговое Integrity на 500…20000 т (`ScaledIntegrity` = v × t^¼, гражданские ½, итог ≥ 10).
* `[Effects]` (вкладка Visual Effects, видна и при выключенном ADC): ExtraSmokeEmitters/ExtraSmokeMinLength — `FireSmoke`: 5 точек пожара вместо 3 (только новые корабли);
  SmokeBySeverity + SmokeStartOpacity/EndOpacity/ModerateFrom/SevereFrom — вид пожара по доле максимума, прозрачность слабого вида.
  Выводы по эффектам: стадии дыма игра переключает только в CheckFire (мод — `FireSmoke.Tick` раз в 1 с); `CreateFireEffect` без проверки `transform.Find("pointlight")` — транспайлер `SafeChild`;
  частота частиц не помогает (больше спрайтов = прозрачнее на глаз), материал дыма, судя по всему, использует альфу частицы как порог — поэтому меняем альфу стартового цвета ×k;
  у «shipfire_s*» цвет — градиент (копия с альфой ключей × k, исходный `Detached` сразу копируется — иначе k перемножались до нуля); «shipfire_m_part_2» малого эффекта не излучает.
  `ResetLooks` возвращает альфу. SmokeStartOpacity по умолчанию 10.
* Закладка Damage Control обновляется сразу (`RegisterView`: IsVisibleChanged, DataContextChanged; `UnitChanged` каждый кадр → `Kick`).
* Окно настроек: вкладки User Interface, Visual Effects, Damage Model, Debug, Hull, Fire, Flooding, DC Teams (видны модельные только при ADC); Fire & Flooding разделена на Fire и Flooding,
  «Water coming in» — на вкладке Flooding, группа «Flooding rate»; [Sinking] DeckFloodingSpeed («Water over the deck», × default, затопление при выходе за предел крена/дифферента).
  Debug: Logging (LogShipIntegrity/LogFires/Debug/LogHeel) и Visualisation (ShowDeckCorners/ShowHullCollider/HideShipModel; применяются сразу, без Apply).
  Строка профиля на Damage Model вставляется и после цикла по полям (иначе пропадала).
* Debug-визуализация `DeckCorners` (ShowDeckCorners): 2 осевые точки (нос/корма) + пара борт/борт на каждую из 5 секций (`Sections = 5`, `PointCount = 12`); линии от текущей волны до палубы.
  Коллайдер — только `coll_hull*` (живые MeshCollider из `ObjectBaseParameters._meshCollidersNames`; `UseKinematicCollision` деактивирует их родителя — для рейкаста
  `ActivateForRaycast/RestoreAfterRaycast`, оверлей — ребёнок `_compartmentsTransform` с копией мирового трансформа).
  Алгоритм: если все куски читаемы (`mesh.isReadable` проверяется ДО чтения — иначе Unity пишет ошибку в лог игры и отдаёт пустой массив) — точный по рёбрам (`TryHullTriangles`,
  `DeckCornerAtMesh/HeightAtMesh`: сечение плоскостью z, угол = крайняя по X точка, затем самая высокая в полосе ~3% ширины); иначе для всего корабля — рейкаст-свип
  (`DeckCornerAtRay`: горизонтальные лучи снаружи внутрь + узкая полоса; координаты переводятся в мировые перед `Collider.Raycast`; Collider/Ray/RaycastHit резолвятся лениво по реальному
  экземпляру коллайдера, `RaycastHit.point` — свойство). Резервы: `NearestHeight`, `TopAt` (по bbox). ShowHullCollider — полупрозрачный оверлей + жёлтый wireframe рёбер;
  HideShipModel прячет MeshRenderer'ы, кроме оверлеев, и восстанавливает прежний `.enabled`. Точки задуманы для анализа «черпает ли борт воду по секциям».
* Дифферент на ускорении времени: игра при `TimeCompression > 100` обнуляет углы (`Vessel.OnFixedUpdate`, не трогаем); выше 10× она растягивает `GameTime.fixedDeltaTime`, а
  `applyMainThrust` двигает корабль `transform.Translate` ДО `Compartments.OnFixedUpdate`, тогда как `_centerOfGravity = worldCenterOfMass` ещё в старой позиции — плечо сил смещено на Δ = v·fixedDeltaTime,
  лишний тангаж растёт линейно по TimeCompression/10 (замер wp_pt_p6: ≤10× 2.6°, 20× 3.3, 50× 5.5, 100× 9.1). Фикс — `src/TimeCompressionFix.cs` (транспайлер после чтения `worldCenterOfMass`:
  `CoG` сдвигает на `v·(GameTime.fixedDeltaTime − Time.fixedDeltaTime)`; только надводные; при Enabled и `[General] FixHighTimeCompressionTrimError`, не зависит от ADC).
  Отвергнуто: гейт «затопление от дифферента только при уже затопленной секции» и множитель `FixFactor` в TrimFlooding.
* Затопление от дифферента (TrimFlooding.Tick): `(after − before) / 10 · DeckFloodingSpeed` от игрового `AddFloodingForHeavyListing`.
* DC-вкладка: подсказка затопления — строка «Trim N.N° bow/stern down: water over deck» (`TrimFlooding.FloodsNow`); «In reserve» не показывает идущих в резерв; процент Hull у секции — серый,
  когда заварено до предела (`WeldedUp`), иначе жёлтый ≥75, оранжевый 25–75, красный <25 (`HullColor`).
* `SaveConfig`: без пустых строк внутри секций.

# Состояние на 2026-10-15 (свёрнутый журнал; старые записи по дням объединены по темам, история неудачных заходов убрана)

## Карточка Hull Damage / Flooding, крен, оружие
* `DamageControlUi.Panels.cs` `RenderHullFlood`: к значениям карточки дописывается « (NN%)» (нет суффикса при 0%). Hull: урон = 100 − `IntegrityPercentage`; с ADC слова по урону: None <1, Slight ≤25, Moderate ≤50, Heavy ≤75, Structure Destroyed >75 (слова — игровой `IntegrityToDamageControlConverter`). Flooding: слово игры, % = min(100, 100·FloodingPercentage/SinkAt) (шкала LEVEL). Слово считаем сами каждый тик (`FloodingPercentageToDamageControlStringConverter`): ручная установка `TextBlock.Text` заменяет привязку игры.
  Цвета: классы 0–2 как в игре, Heavy — оранжевый (`BHeavy`), последний — красный (`BCritical`); Flooding по значению игры 1/10/20/30, Hull без ADC 99/75/25/1. `RemoveExtras` возвращает игровой текст/цвет. Advanced: Warships 4.0, DCTeamEffectiveness 3.0.
* Затопление от крена делает мод: `TrimFlooding.FloodList` (|крен| > `Limits.ListFlood`, не sinking/capsized, все отсеки низкого борта: игровой `AddFloodingForHeavyListing` ÷10 × DeckFloodingSpeed); игровой вызов выключен (`HeavyList/HeavyListNeg` при ADC возвращают ±float.MaxValue; Harmony-патч метода не срабатывал из-за инлайна JIT). Для кораблей с точными палубными точками (`SubmergedFlooding.Covers`) эта часть НЕ применяется (один источник «через борт», строки List/Trim в подсказках скрыты).
* `[Sinking] FloodImbalanceEffect` не дал эффекта: скрыт в окне, игнорируется (`TrimFlooding.Imbalance = 1`), ключ остаётся = 1.0 в профилях.
* `[General] LogHeel` (Debug → Logging): раз в 2 с при крене ≥8° или дифференте ≥3° — крен, дифферент, y, подъёмная сила/вес, vy, submerged, % воды, sinking/capsized.
* Раскраска оружия (`WeaponStatusColors.cs`, `[UI] ColorWeaponStatus`, вкл.): раз в секунду обход дерева от `MainGameView.Instance` (бюджет 40000 узлов), `TextBlock` с `DataContext` = `WeaponEntry`; по `WeaponSystems[].Inoperable.Value`: часть — жёлтый (240,208,74), все — красный (236,70,56), цело — `ClearValue`. `Tick` из `Update()`, при выключении `ClearAll()`.
* Git: история `rgreat/SeaPower.AlternativeDamageModel` была отдельной; локальные коммиты свёрнуты поверх 2d94749 (крестик ✕ Path-ом, ресайз окна за края/углы; `UserConfig/AlternativeDamageModel.cfg`, `.gitignore`).

## Физика корабля: крен, гибель, обломок
* **Что в игре.** Масса постоянна (`displacement/40`), вода массу не добавляет. `rigidbody.centerOfMass` — `(0, y_средн.отсека, z_средн.отсека) + [Physics] CenterOfMass` в Init; дальше только в `Sink()` (`(0,0,0)`, при `_capSized` `(0,1,0)` = 67 м вверх; подлодки `(0,-0.5,0)`). Вода влияет ТОЛЬКО на подъёмную силу отсека: `F_i = (m·g/10)·s_i/(ρ + w_i)`, `w_i = MaxFloodingValueWhenSinking(2.0)·flooding/max`, `s_i = clamp((уровень_волны − y_i + H/2)/H, 0..1)`, `H = bounds.y·0.5`; полный отсек теряет максимум 71%. 5 пар точек, x = ±bounds.x/2; `AddForce(сумма)` + `AddTorque(Σ (r_i − CoG) × F_i)`. Крен создаёт разница сил бортов × плечо; GM_eff ∝ B²/(ρ·H) − c (c = CenterOfMass.y). На боку (~90°) момента нет — корабль «лежит»; после capsize CoM `(0,1,0)` выше точек — момент неустойчивый, докатывает медленно (~1.6°/с). До 70° подъёмная сила ≈ вес — от геометрии не тонет.
* **Sink() вызывают** `Compartments.OnFixedUpdate` (уровень >40% → SinkLevel; дифферент >8° с водой > Minor → SinkTrim; крен >70° → `_capSized` + Sink), `DestroyByExplosion` (целостность <10%) и внешние системы. `Sink()` физику НЕ пересоздаёт: `_isSinking`, `_sinkTime`, `IsControllable = false`, `Formation.DetachUnit`, `sink()` отсеков (`_floodingRate += 0.05`), `MakeInoperable`, `setDestroyedFlag()` (`IsDestroyed`), эффект пузырей, `LaunchFlares`, `SpawnLifeRafts`, COM. Затем при `_isSinking`: каждый тик `constraints = 0` (в Init 10 = X и Z заморожены); при y < −2 сила вниз; отсеки с y < −0.2 получают `_floodingRate ≥ 0.8`. `isKinematic` — только по `_turnOffRigidbodyTime` (касание Terrain Chunk). Опрокидывание на >70° — правильное поведение игры, остаётся. «Плоты» к рывку отношения не имеют.
* **CapsizeFix** (`[Sinking] CapsizeFactor`, окно «Centre of mass rise», Advanced 1, Base Game 0): `CapsizeFix.Tick` (из `TeamTickPrefix`, до чтения `worldCenterOfMass`) для надводных кораблей с ≥2 секциями ставит `centerOfMass = база + (0, rise, 0)`, rise = Factor·GM(LevelFull)·min(вода/LevelFull, 2.5); GM(w) = x²/(H·(плотность + w)). При Factor 1 остойчивость уходит примерно с четверти воды (центр из ini уже выше точек плавучести). При `_capSized` не трогает игровой (0,1,0). Подлодки — без изменений.
* **KeepCentreOfMass** (`[Sinking] KeepCentreOfMass`, «Keep centre of mass», Advanced true / Base Game false) — Harmony-патч `Compartments.Sink` (`SinkComPrefix/Postfix` → `CapsizeFix.BeforeSink/AfterSink`): для надводных кораблей, ещё не sinking, COM запоминается до вызова и возвращается после; для опрокинутого CapsizeFix его не трогает. Это решило «перезапуск физики» при гибели от крена (подтверждено пользователем).
* **SinkPull** (`SinkPull.Tick`, префикс OnFixedUpdate). Игра: при `_isSinking` и y < −2 `AddForce((0, a, 0), Acceleration)`, `a = max(−10, min(y+2, t/3))` — на деле `y+2` (временной член не работает): 1 ед./с² на единицу глубины, до −10 при y ≤ −12. Мод добавляет разность: `want = Factor·a` при Ramp 0; при Ramp>0 `want = −10·Factor·S(depth/Ramp)`, S = x²(3−2x). Ramp 30 оказался слабее игровой кривой на малых глубинах (на 10 ед. 2.6 против 10) — мелкие корабли всплывали; поэтому по умолчанию Ramp = 0 (игровая), `SinkPullFactor` 0.5 (Advanced; Base Game 1 / 0). Окно: «Sinking pull», «Sinking pull ramp».
* **WreckDrag** (`[Sinking] WreckDragFactor`, Advanced 5 / Base Game 1, «Wreck drag»): постфикс `OnFixedUpdate`; для уничтоженных надводных кораблей умножает `linearDamping` (игра пишет его каждый тик: `Lerp(outOfWaterDrag, submergedDrag, _submergedVolume)`); записанное значение запоминается, чтобы не умножить дважды. Угловое сопротивление не трогается.
* **BubblesOff** (`[Sinking] BubblesStopMinutes`, Advanced 10 / Base Game 0 = никогда, «Stop bubbles after»): `SinkingBubblesEffect` игра создаёт один раз в конце `Sink()` (родитель — трансформ корабля, `localPosition = Effect._position`) и не останавливает. `BubblesOff.Tick`: через N·60 игровых секунд после `_sinkTime` — среди детей трансформа объекты с `localPosition ≈ _position` (и именем с `_singleEffect`), у `ParticleSystem` `Stop(false, StopEmitting=1)`. Тип `ParticleSystem` ищется через загруженные сборки (модуль не подключён). Trace: «BubblesOff: … stopped (N particle systems)». Если 0 систем — нужен trace.
* **После уничтожения (`IsDestroyed`).** `Compartments.OnFixedUpdate` для уничтоженного идёт как обычно, но `WaveSampleJob` берёт только `!IsDestroyed`: `_currentWaterLevel` замирает — мод пишет его сам из `DeckCorners.QueryWave`. Когда все 10 палубных точек глубже уровня — `State.Deep`, опрос волн выключается. `SinkAllowed` для уничтоженных не применяется.
* **Гибель от воды (с точными точками).** Порог 40% (`SinkLevel`) срабатывает, только если оба центральных отсека (порт+штарборт средней секции) под водой ИЛИ под водой ≥ 5 из 10 (`SubmergedFlooding.SinkPoints`) — «под водой» как в правиле перелива (непрерывно ≥ `DeckFloodingDelay` с). Иначе порог = `float.MaxValue`. `AdmPlugin.SinkLevel(float, comp)`, транспилер как для `SinkTrim`. Подлодки (порог 50) и корабли без точек — как раньше. Шкала LEVEL 100% = LevelFull; может стоять на 100%, не погибая.
* `Vessel.OnFixedUpdate` выравнивает eulerAngles у не уничтоженных кораблей при TimeCompression > 100 (мод не менял).

## Палубные точки и затопление «через борт»
* **DeckCorners** (`src/DeckCorners.cs`): 5 пар борт/борт (`PointCount = 10`), точка сдвинута к оси на 25% x (`InwardShift`), высота палубная. Только точный алгоритм по мешу (`TryHullTriangles` + `DeckCornerAtMesh`); рейкаст-путь удалён. Нечитаемые меши (isReadable = false) читаются из GPU (`TryGpuMesh`: `GetVertexBuffer/GetIndexBuffer` + `AsyncGPUReadback`, синхронно; Float32, подмеши с baseVertex, индексы 16/32); не вышло — точек нет (`PointsExact = false`, старые правила, оверлей оранжевый; голубой = точные). Проверка длины: длина по z кусков `coll_hull*` (× lossyScale, в метрах: `UnityToMetre = 67.200066`) против `ObjectBaseParameters._length`; расхождение > 10% (`MaxLengthMismatch`) — точек нет (по trace все пять кораблей 3–6%). В trace при выборе: куски коллайдера, длина. `PointsOf(unit)` — кэш `ConditionalWeakTable` (`Exact`, `Points`, `ShipT`, `Hull`) для любого корабля. `Select`: `_unit` ставится сразу после проверки компартментов, вид модели работает всегда; `LocalPoints` в try/catch.
* **ShipModelView** (`[General] ShipModelView` Show | Translucent | Hide, Debug → Visualisation, `Kind.Choice`, применяется сразу; старый `HideShipModel` игнорируется). Translucent: копии материалов на `Sprites/Default` (основная текстура + цвет, очередь 3000); корпус (рендерер или родитель с «hull» в имени, `IsHullPart`) — текстура с альфой 255 (`OpaqueCopy`: Blit в RT ≤2048 → Texture2D; оригинал, если >2% прозрачных пикселей), альфа 0.3; субмодели — оригинальная текстура, альфа ×0.3. Игровые sharedMaterials запоминаются/возвращаются, копии уничтожаются (`RestoreShipModel`); список рендереров пересматривается раз в ~60 кадров. Hide — `Renderer.enabled = false`. Оверлей Hull Collider альфа 0.2; wireframe `hull_wire` вторым материалом (`Hidden/Internal-Colored`, ZTest Always, альфа 0.15) виден сквозь море; цифры высоты у точек — мелкие 7-сегментные глифы (один Mesh `ADM_DeckCorners/levels`, лицом к камере, высота ≈ 0.6% расстояния; зелёные над точкой, красные под ней).
* **SubmergedFlooding** (`src/SubmergedFlooding.cs`, из `TeamTickPrefix` после `TrimFlooding.Tick`; только Model, надводные, точные точки). Точка ↔ отсек: та же сторона (знак x) и ближайшая по z (у игры порт −x, секция 0 на носу). Глубина = (`_currentWaterLevel` − мировой y точки)·67.2 м. Пока > 0 — таймер в игровых секундах (`[Sinking] DeckFloodingDelay`, «Time under water», 3 с); всплыла — сброс. Потом приток `Rate·глубина` %/с (`Rate = 0.1 × [Sinking] DeckFloodingSpeed`, «Water over the deck»; 0 = выкл) прямо в `_currentFlooding`, мимо `_floodingRate` (DC его не затыкает). Перелив: полный отсек (`≥ _maxFlooding`) отдаёт приток своему соседу (BFS от каждого полного: сначала его соседи — секции до/после на том борту и тот же номер другого борта — самый низкий по палубной точке не полный, иначе соседи соседей). Подсказки (`FloodCause`): «Deck edge N m under water for t of M s», «+x %/s», «Overflow from <имя секции>», имена — по `_sectionNames` (из `HeaderOrig`).
* **Пожар под водой** (`PutOutFires`, из `Tick` до проверки `Rate`; `[FireAndFlooding] SubmergedFireDecay`, «Fire under water», Advanced 1 / Base Game 0): глубина секции = среднее двух бортов; `FireSeverity −= Decay·глубина/100·_maxFireSeverity·dt`, `_fireGrowRate = 0`. Работает при наличии `Depth` (точек); для уничтоженных тоже.
* **Уровень моря**: `DeckCorners.SeaLevel()` = `Ocean.Instance.level` (подтверждено по `WaveSampleJob`), иначе 0 (в trace строка о том, откуда).
* **Шкалы и красные зоны LIST/TRIM** (`SubmergedFlooding.Margins/TouchAngle`, `Panels.Dial`): абсолютные пределы — угол, при котором первая палубная точка стороны касается плоского моря (из текущей позы; ось — центральная линия на уровне моря, середина отсеков по z; `h(a,b) = (Quaternion.Euler(b,0,a)·p).y`; шаг 0.5° + бисекция 24 итерации; уже под водой ⇒ 0, нет касания до 89° ⇒ 89). Шкала list = max(port,stbd)/0.75 (≥4°), trim = max(stern,bow)/0.75 (≥0.67°) — статическая (по касанию в покое), красные зоны несимметричны (`Dial(..., out setRed)`), пределы сглаживаются (0.2 за тик UI, обновление при сдвиге >0.3°). Цифры красные при значении в сторону ≥ предела стороны. Подпись под схемой — ЗАПАС НАКЛОНА от текущего положения: «Tilt to the sea: list A° port / B° stbd · trim C° stern / D° bow» (port = Lport − list, stbd = Lstbd + list, stern = Lstern + trim, bow = Lbow − trim). Корабли без точных точек — старые шкалы (`Limits.ListFull`, `Limits.Trim(length)`, 75% циферблата). Допущение: Density отсеков 0.5; `TrimAngle` ≠ 0 не вычитается.

## UI строк Flooding/Fire/Hull и подсказки (`DamageControlUi.cs`)
* `Draw(tb, ref Drawn, force, List<Seg>)`: строка — список кусков (`Run_`, `Arr_`, `Box_`); пока структура та же, меняются только тексты/цвета на месте (стрелки и открытые `LiveTip` не пересоздаются). Пересборка: смена структуры (стрелка появилась/исчезла/сменила вид), текст перезаписан игрой (`tb.Text != Shown`), force. Привязка `TextBlock.Text` снимается на время отрисовки (`ClearBinding`, сохраняется в `Keep`; `RestoreBlock` возвращает). `DcIntensityPrefix` (детальный вид) пропускает смену ступени воды у строки Flooding (`IsFloodSummary`, реестр `Col.FloodSum`) — игра иначе пересоздавала строку (подсказка моргала). Скорости (`RP/RS/RF/RIP/RIS`) сглажены экспонентой `TrendSmooth = 1.5` с, число стрелок с гистерезисом (`Arrows(..., prev)`, `Col.AP/AS/AF`); порог знака у корпуса 0.003. Стрелки появляются/гаснут с задержкой 1–2 с. Trace «DC row rebuilt: …» (40 раз).
* **Проценты — `Seg.Box`** = `InlineUIContainer(TextBlock)` (меняется `Text` ребёнка, Run'ы строки не меняются → подсказка стрелки не моргает). Выравнивание по базовой линии: ребёнок встаёт нижним краем на базовую линию, поэтому отрицательный нижний `Margin = −round(FontSize·BoxDrop)`, `BoxDrop = 0.25` (RenderTransform/копирование шрифта НЕ использовать — от них куски строки теряли ширину). Ширина бокса — явная, `BoxWidth(text, row)`: замер временного `TextBlock` вне дерева с тем же FontFamily/FontSize/FontWeight/FontStyle (`Measure(∞)`, `DesiredSize.Width` вверх +1), кеш; запасная оценка по знакам (`BoxEstimate`); при смене ширины (>0.4) контейнеру переприсваивается `Child`.
* **«Слипание» процентов** (куски строки стоят друг на друге, «3%7%%»): по trace — раскладка инлайнов в Noesis застывает (позиция 2-го бокса остаётся прежней, хотя ширина строки меняется; ширины боксов при этом не менялись), сама не пересчитывается; полная пересборка строки лечит всегда. Поэтому самолечение: `CheckOverlap` (позиции боксов через `TranslatePoint`) в `Draw`; при наложении строка пересобирается (не чаще 1 раза/с на строку, `Drawn.Healed`). Логи: «DC box width: …» (30), «DC boxes overlap: …» (25), «DC row: boxes overlap, the row is rebuilt» (20), «DC box has no width: …» (12). Проверено ли самолечение в игре — ждём feedback.
* **Строка команд (− / иконка / +)**: из XAML игры (`GameFiles/xaml-assets/Scripts/UI/Game/Views/DamageControlView.xaml`) — Grid (Auto) в строке 1; над ним название секции (margin 6,4), под ним строка Grid высотой 5. `CompactTeamRow(holder)` (при поиске «DC Teams» у кнопки +, один раз на ряд): кнопки `Height = TeamBtnHeight` (22, у игры ~32), нулевые вертикальные Padding/Margin, строка под рядом 5 → `TeamGapBelow` 1, поля названия `TeamHeaderGap` 2/2. Trace «DC team row compacted: …» (3 строки). Размеры подбирались вслепую — подстроить по скриншоту.
* Панель с картинкой корабля: `FitPicture` вызывается сразу после создания карточек и в конце каждого `Render` (первый раз с `img.UpdateLayout()`, `ViewState.PicFitted`) — рывка высоты нет.

## Команды DC: новые задачи и правила (`DcControl.cs`)
* `Repairs[task]` — делегаты игровых `Repair*` (+ наши); на время вызова `DamageControlTeamsNumbers[i]` = число команд группы. Задачи: 2 Leak, 3 Fire, 4 Pump, 5 Hull, 6 Systems, 7 Counter. Ручные задачи — только при выключенном Auto DC и подробном виде.
* **Контрзатопление** (`TaskCounter = 7`, только по команде; `Orders`/`DcAuto` не выбирают): `CounterFlood` добавляет в `_currentFlooding` борта `команды · _floodingRepair · PumpOutMul · dt · CounterRate`, не выше `min(CounterLimit, _maxFlooding)`; нечего делать (борт на пределе) → false (команда идёт по приоритету, но откачивает только через `PumpSafe`). Борт: `CounterSide` — крен (Euler z; >0 = левый борт вниз) > 0.3° → в противоположный; иначе в борт с меньшей водой. Настройки `[FireAndFlooding] CounterFloodSpeed` (2.0, «Counterflooding») и `CounterFloodLimit` (50, «Counterflood limit»). UI: иконка 16×16 (серый отсек, синяя вода с бликом, голубая стрелка вверх), пункт меню «Counterflooding» после «Pumping out», строка легенды (i), строка в подсказке затопления «DCn counterflooding». Сохранения пишут задачу 7.
* **PumpSafe** (откачка команд с приказом на контрзатопление): не ухудшает крен/дифферент — нельзя качать борт, который и так выше (крен > 0.3° ⇒ правый запрещён и наоборот), нельзя качать носовую секцию при дифференте на корму > 0.2° и кормовую при дифференте на нос > 0.2° (+ = нос вниз, Euler x); средняя секция дифферент не трогает; как в игре (команды × навык × PumpOutSpeed × dt, до минимума — `FloodingDelta`), нечего качать → false.
* **LeakPumpShare** (`[FireAndFlooding] LeakPumpShare`, Advanced 0.5 / Base Game 0, «Pumping while plugging holes»): пока команда делает Leak (заделка пробоин = игровой `RepairFloodingRate`, не сварка корпуса), заодно откачивает со скоростью `share × откачка` из той же секции: `Repairs[TaskLeak] = LeakWithPump` (оригинал + `PumpFree`: борт с большим `FloodingDelta`); команда в ветке `_rest` (приказ контрзатопления) — через `PumpSafe(share)` (`_safeLeak`).
* **TeamBlocksFireSpread** (`[FireAndFlooding]`, Advanced true / Base Game false, «Team stops fire spread»): префикс `SystemCompartment.IgniteByNeighbour` — если в секции есть команда (`DamageControlTeamsNumbers[номер−1] > 0`; номера отсеков 1-based) перенос огня с соседней пропускается; свои пожары и попадания не затрагиваются.
* Меню команды: «Auto» (бывшее «By priority», задача 0).

## Окно настроек (`SettingsWindow.cs`)
* Закладки: User Interface, Effects, Damage Model, Debug, Hull, Systems, Fire, Flooding, **Sinking**, DC Teams (модельные — все кроме UI/Gen/Fx/Dbg — скрываются при выключенном Alternative Damage Control). «Flooding»: группы «Flooding rate» (Water coming in, Water over the deck, Time under water), «Gauge scales» (ListLimit, TrimLimit, TrimReferenceLength), «Sinking from» (FloodLimit). «Sinking»: «Capsizing» (CapsizeFactor), «Sinking and wreck» (KeepCentreOfMass, SinkPullFactor/Ramp, WreckDragFactor, BubblesStopMinutes). DC Teams → «Work and movement»: LeakPumpShare, CounterFloodSpeed/Limit и др.
* Размер окна: `PixW = 1150f, PixH = 1000f` (UI-единицы пересчитываются в первый показ), ресайз мышью до 1600.
* Профили: Advanced / Base Game (`Release/Profiles/*.cfg`), см. значения у каждой настройки выше. Удалены: `[Sinking] SubmergedFloodingRate` (ключ остаётся неиспользуемым в пользовательских .cfg).

## Не проверено в игре / открыто
* Самолечение слипания %, замер ширины, сжатая строка команд (подгонка по скриншоту), PumpSafe/LeakPump/контрзатопление, SubmergedFireDecay, TeamBlocksFireSpread, WreckDrag, BubblesOff, SinkPull ×0.5 (всплытие мелких кораблей).
* Качество опрокидывания (хватает ли момента довернуть до киля при демпфировании игры) — по feedback; варианты: больше CapsizeFactor, снижение angularDamping, постоянное угловое ускорение (`AddTorque(..., Acceleration)` вдоль forward).
* Идея (не сделана): вести `centerOfMass.x` по воде в отсеках (`dx = Σ w_i·x_i / (M + Σ w_i)`) вместо множителя дисбаланса (`FloodImbalanceEffect`).

## 2026-10-15: bool-настройки профиля не должны быть «global»
* Баг: «Team stops fire spread» сбрасывался в false по Apply. Причина: последним аргументом `Add(...)` строки стояло `true` (global — это глобальные ключи вне профиля, `_globals`), а ключ `FireAndFlooding/TeamBlocksFireSpread` — настройка профиля; Apply писал значение не туда, а профильное читалось заново. То же было у `Sinking/KeepCentreOfMass`. Теперь у обеих строк `global` не указан. Правило: bool профиля — без `true` (как `UnattendedKey`), `true` — только для ключей `[General]/[UI]/[Particles]` и т.п. вне профилей.

## 2026-10-15: палубные точки показываются вместе со схемой затоплений
* `RenderSchematic` (карточка схемы отсеков на вкладке Damage Control) при `ListCard.IsVisible` ставит `DeckCorners.WantedAt = Time.unscaledTime`; `DeckCorners.Tick` рисует линии/цифры точек, если `Model || now − WantedAt < 1 с`. Т.е. пока схема на экране — точки видны, даже при `ShowDeckCorners = false`; ушла схема — через ~1 с гаснут. Корабль тот же (выбранный игроком). Не проверено в игре (зависит от того, что `Render` вызывается, пока вкладка видна).
  Уточнение: «схема затоплений» — это тултип карточки (`ListCard`), а не вся вкладка: точки показываются, только пока тултип открыт (`ToolTipOpening/Closing` → `_schemOpen`, `WantedAt` обновляется в `RenderSchematic`, окно 0.6 с).

## 2026-10-15: подпись под схемой затоплений в две строки
* Строка пределов (`s.Limits`, «Tilt to the sea: list … port / … stbd · trim …») делится на две по первому « · » (`\n`): list в первой, trim во второй; то же для остальных вариантов подписи. Было слишком широко.

## 2026-10-15: подсказки и у процентов; подробный разбор затопления «поднимает / опускает»
* `Seg.Why` теперь и у `Box_` (`Box_(text, col, why)`): в `Draw` при сборке строки у TextBlock процента — `LiveTip(kid, why)` (Hull → `HullCause`, Flooding → `FloodCause`, Fire → `FireCause`). Подсказка есть, даже когда нет стрелки (ничего не движется). Whys обновляются только при пересборке строки (как у стрелок).
* `FloodCause` переписан: заголовок «Port: 54%, +0.35%/s», затем «Raises it:» и «Lowers it:» (всегда оба, пустые — «Nothing raises it now» / строки «No water to pump out»).
  Поднимает: течь `Hole leak L: +L·0.5·FloodingMul %/s` («faster» при `_activateFasterFloodingForSinking`) + кто заделывает; контрзатопление `+команды·PumpPerTeam·CounterRate`; вода через палубу от крена/дифферента (старые правила); палубная точка (глубина, %/с, перелив в/из соседа — `SubmergedFlooding.Reasons`); рост течи без команды (корпус <19%); «The ship is sinking».
  Опускает: откачка `−команды·PumpPerTeam %/с` (игра качает один борт за раз — у другого «after the other side»; `_drainPortSide`), откачка при заделке `−команды·PumpPerTeam·LeakPump` (для борта с большей водой над минимумом, как `PumpFree`), «No water to pump out (at its minimum)», «Pumping is faster than the leak».
* `DcControl.PumpPerTeam(comp)` — %/с на команду (навык экипажа × PumpOutSpeed). Не проверено в игре.

## 2026-10-15: кнопка ↺ в окне настроек возвращает значение ИГРЫ
* ↺ у строки раньше возвращала значение профиля (`FromValue`), теперь — значение игры (`GameValue`): для global-ключей и `Unattended*` — `DefaultValue` записи конфига, для остальных — значение профиля Base Game (`_game`, то, что в колонке «game:»); если игрового значения нет — прежнее поведение (профиль). Подсказка кнопки «Back to the game's value». Кнопка «Reset to profile» внизу окна — как раньше (возвращает профиль). Точки профиля («dots») по-прежнему сравниваются с профилем.

## 2026-10-15: подсказка затопления — пояснения
* «Hole leak» теперь: `+x %/s (intensity L × 0.5 × FloodingMul «Water coming in»)`; L — игровая интенсивность течи `_floodingRate` (приток = коэффициент × L × 0.5 %/с, ×`FloodingSpeed`; Advanced 0.5 → 1.3·0.5·0.5 ≈ 0.3 %/с).
* Откачка при заделке показывается всегда, когда команда заделывает и `LeakPumpShare > 0`: со скоростью, «after the other side» или «nothing to pump». Пусто было потому, что у отсека вода на минимуме: `MinimumFlooding = (100 − целостность%)/2` (подлодки /3), `FloodingDelta = вода − минимум` = 0 → откачивать нечего (игра так и считает). Подпись «No water to pump out» заменена пояснением про повреждённый корпус.

## 2026-10-15: подсказка затопления — скорости притока и откачки отдельно
* Убрана «intensity» из строки течи: «Holes: +x %/s» (полная скорость притока в пробоины, с «(sinking: faster)»).
* «Lowers it» всегда показывает собственные скорости команд — откачка `−команды·PumpPerTeam %/с` и откачка при заделке `−команды·PumpPerTeam·LeakPump %/с` — независимо от того, перебарывают ли они приток (если вода на минимуме — пометка «the water is at its minimum»; «the other side first», если игра сейчас качает другой борт). Итог (приток − откачка) — в первой строке подсказки.

## 2026-10-15: убрана строка «Team busy with other work»
* `OnIt`: команда в секции, занятая другим делом, ничего не меняет в этой величине — строка не выводится (остаются «DCn …», «Busy: DCn fire first» — это влияет, «Team on its way», «No team here»).

## 2026-10-15: убрана строка «No team here»
* `OnIt`: при отсутствии команд в секции строка не выводится (остаётся «Team on its way», если команда идёт).

## 2026-10-15: «No team here» — только если от этого становится хуже
* `OnIt(..., bool worse)`: «No team here» выводится, только когда величина растёт (`rate > 0`; для откачки — ещё и есть что откачивать), т.е. отсутствие команды реально ухудшает положение; иначе строки нет. «Team on its way» — как раньше.

## 2026-10-15: анализ — почему корабль подвсплывает после гибели (без изменений кода)
* Подъёмная сила отсека `F_i = (m·g/10)·s_i/(ρ_i + w_i)`, `w_i = 2·вода_i/max` (в Job и в `Compartments.OnFixedUpdate` одинаково); при полном погружении всех отсеков суммарная = `m·g·mean(1/(ρ+w))`. Утонуть без «тяги вниз» можно, только если mean(1/(ρ+w)) < 1: при ρ = 0.5 (типично) это значит средняя вода > ~25% на ВСЕХ отсеках; отсеки с малой водой несут плавучесть.
* `Sink()` вызывается по геометрии (крен > 70° / дифферент > 8°) независимо от количества воды → корабль «погиб» с малым затоплением и сохраняет плавучесть. После `Sink()` вода идёт медленно: `sink()` даёт `_floodingRate ≥ 0.05` → `коэф·0.05·0.5 = 0.025 %/с` на отсек (быстрее ×`_floodingMultiplicatorWhenSinking` — только у одного нижнего отсека, `activateFasterFloodingForSinking`); `FloodingSpeed` 0.5 (Advanced) делит это ещё вдвое (≈1 % за 80 с). Плюс палубный приток мода (`SubmergedFlooding`, 0.1 %/с на м погружения после `DeckFloodingDelay`).
* Сила вниз игры (`SinkPull`) действует только при `y < −2` ЕДИНИЦ Unity = 134 м глубины: у поверхности тонущий корабль держится только балансом плавучести. Прежнее объяснение «Ramp слабее игровой кривой» к подвсплытию у поверхности отношения не имело (Ramp/Factor влияют лишь глубже 134 м; сейчас Ramp 0, Factor 0.5).
* Опрокинутый корабль: точки плавучести заданы в системе корпуса на уровне ватерлинии, поэтому перевёрнутый корпус держит те же точки на воде — киль торчит над водой на осадку; к тому же `CapsizeFix` (центр масс вверх) делает перевёрнутое положение устойчивым. `WreckDrag ×5` и угловое сопротивление замедляют движение, но положение равновесия не меняют.
* Проверка: `LogHeel` после переворота (подъёмная сила/вес, vy, вода %). Варианты исправления (не сделаны): (a) после `Sink()` — собственный приток воды во все отсеки (не зависящий от `FloodingSpeed`); (b) небольшая постоянная сила вниз тонущему кораблю у поверхности; (c) убрать плавучесть отсеков после Sink постепенно.

## 2026-10-15: WreckDrag мешал тонуть (исправлено — только горизонталь)
* Жалоба: «утонувший» на ровном киле корабль с >100% воды во всех отсеках не погружается. Физика мёртвых кораблей мод не отключает (`TeamTickPrefix` — void-префикс, оригинал выполняется; для уничтоженных мод ещё и пишет `_currentWaterLevel`). Причина — `WreckDrag ×5`: у почти всех кораблей `SubmergedDrag/OutOfWaterDrag = 25`, ×5 = 125; предельная скорость погружения = ускорение/демпфирование, при чистой тяге вниз ~6 м/с² (вода 100%, ρ≈0.5) это 0.05 м/с (3 м/мин) вместо 0.24 м/с у игры — корабль почти не тонет.
* Теперь `WreckDrag.Tick` `linearDamping` НЕ меняет: горизонтальная скорость (x, z) делится ещё раз на `1 + (Factor−1)·drag·fixedDeltaTime` (как применяет демпфирование Unity), вертикальная не трогается. Factor ≤ 1 — без эффекта. `linearVelocity`/`velocity` — рефлексией. Описание/подпись настройки: «horizontal movement». Не проверено в игре.

## 2026-10-15: SinkPullSurface отменён
* Тяга от поверхности (`SinkPullSurface`, заменявшая `SinkPullRamp`) отменена по решению пользователя: предыдущая правка (WreckDrag только по горизонтали) уже починила погружение. Код `SinkPull`, настройки и профили — как до неё (`SinkPullFactor`, `SinkPullRamp`). Факты, найденные при разборе, остаются верными: игровая тяга включается при `y < −2` ед. (134 м); `MaxFloodingValueWhenSinking = 1.05` (не 2), `Density` 0.5/0.45, drag 25.

## 2026-10-15: SinkPull удалён совсем
* По решению пользователя («большая глубина не интересна») удалены `SinkPull.cs`, ключи `[Sinking] SinkPullFactor` / `SinkPullRamp` (из `AdmPlugin`, окна, профилей; старые ключи в пользовательских .cfg просто не используются), вызов из `TeamTickPrefix`. Игровая тяга вниз (при `y < −2` ед. = 134 м) работает как в игре. README: строка про тягу заменена описанием WreckDrag (горизонталь). Записи выше про SinkPull — история.

## 2026-10-15: CapsizeFactor ослаблен вдвое, WreckDrag 3
* `CapsizeFix`: подъём центра масс умножен на `RiseScale = 0.5` (влияние настройки вдвое слабее при том же значении; прежнее действие — `CapsizeFactor` 2.0). Значения в профилях не менялись (Advanced 1.0), описание ключа обновлено.
* Advanced: `WreckDragFactor` 5 → 3 (Base Game 1). README — «3× by default».

## 2026-10-15: из подсказки палубного притока убрано «(0.1 %/s per m)»
* `SubmergedFlooding.Reasons`: «Deck edge N m under water: +x %/s[, full: goes to …]» — без скобок со ставкой на метр.

## 2026-10-15: влияние скорости корабля на затопление и пожары; предел скорости заделки
* Новый `SpeedEffect.cs`: таблица `[FireAndFlooding] SpeedEffect = "узлы=затопление/пожар, …"` (кусочно-линейная по точкам; скорость берётся по модулю; ниже первой точки — первая, выше последней — последняя). Скорость: `Compartments._baseObject.getVelocityInKnots()` (рефлексия; `FloodingCompartment`/`SystemCompartment` → поле `_compartments`).
* Множитель затопления: пробоины (`FloodingMul(v, this)` — транспилер `FloodingSpeedTranspiler` теперь вставляет `ldarg.0` перед вызовом; ветка sinking по-прежнему не трогается), палубный приток/перелив (`SubmergedFlooding.Tick`: `Rate·depth·speedMul`) и старый приток по крену/дифференту (`TrimFlooding`, оба места). Множитель пожара: рост огня (`FlameUpMul(v, this)`). Выключенная модель — без эффекта.
* По умолчанию: Base Game `0=1/1, 50=1/1`; Advanced `0=0.75/1, 10=1/1, 20=1.25/1.25, 30=2/1.5, 40=3/1.75, 50=5/2`.
* Подсказки: «(faster: ship speed)» / «(slower: ship speed)» у строк Holes, Deck edge, Overflow и «Grows … by itself».
* Окно настроек: `Kind.Speed` + `SpeedEditor` (3 строки: speed, knots / flooding rate / fire rate; только значения ≥ 0; «+»/«−» добавляют/убирают последнюю точку; точки сортируются при вводе), группа «Ship speed: effect on flooding and fire» на вкладке Damage Model. Поля профиля на вкладке Damage Model скрыты, пока модель выключена (`!adm && !f.Global`).
* DC Teams: `[DamageControlTeams] MaxPlugSpeed` (узлы, 0 = без предела; Advanced 15, Base Game 0). Быстрее предела `LeakWithPump` возвращает false — команда работает по приоритету дальше; в подсказке затопления: «Ship too fast to plug holes: over N kn».
* Не проверено в игре.

## 2026-10-15: число точек дыма по длине корабля
* `[Effects] ExtraSmokeEmitters` теперь задаёт число точек по длине: <25 м — 1 (весь корабль), <50 м — 2 (секции 0–2 и 3–4, позиции — носовая и кормовая точки из ini), <75 м — 3 (игровые, без изменений), ≥75 м — 5 (как раньше). `FireSmoke.CountFor`. Ключ `ExtraSmokeMinLength` и строка «On ships from» удалены.
* Для 2 точек массивы эффектов длиной 3: игровой `UpdateFireState` для водоизмещения ≥900 т пишет `_currentFires[2]` (иначе IndexOutOfRange); `_firesCount` = 2, лишний слот не используется. Для 1 и 2 точек состояние огня считает `AfterUpdateFireState` (худшее по группе секций); `Sections` учитывает новые счётчики.
* Не проверено в игре. Новые корабли (точки создаются при спавне).

## 2026-10-15: кнопка Limit Speed
* Карточка «DC Priority» (слева от профиля корабля) получила `ToggleButton` «Limit Speed» (по умолчанию включена, состояние на корабль: `DcControl.LimitSpeed/SetLimitSpeed`, `Ship.NoSpeedLimit`; не сохраняется в сейве). Неактивна, пока модель выключена.
* Игра режет скорость затоплениями в `Compartments.UpdateWantedVelocityInKnots`: блок `if (WantedVelocityInKnots > 10f && !flag2)` (открытая пробоина — 10 уз; Minor — telegraph[5]; Moderate — 20/25; Severe — 10/15 по секциям). Транспилер `SpeedLimitTranspiler` вставляет `ldarg.0; call SpeedLimitOff(bool, object)` после загрузки `flag2` (`flag2 || (Model && !LimitSpeed)`) — то же условие, что у аварийного всплытия подлодки: блок пропускается. Ожидается 1 место, иначе Warn.
* Не проверено в игре.

## 2026-10-15: Limit Speed — на одном уровне с Auto DC
* Кнопка перенесена из отдельной строки под Auto/Fire/Flooding в горизонтальную панель рядом с игровым переключателем Auto DC (окно не растёт по высоте). `PutBackAuto` вынимает игровое содержимое и из этой панели.

## 2026-10-15: Limit Speed оформлен как Auto DC
* Рядом с Auto DC: заголовок «Limit Speed» (шрифт и цвет заголовка игры) и под ним переключатель «Enabled»/«Disabled» тех же размеров, что у игрового (Padding/MinWidth/Height/Margin/FontSize копируются с него).

## 2026-10-15: «Lowers it» — только то, что меняет эту сторону сейчас
* `FloodCause`: строки откачки (и «pump while plugging») убраны, когда эта сторона сейчас не откачивается (игра качает одну сторону за раз — «the other side first») или вода уже на минимуме. Пометки «(the other side first)» / «(the water is at its minimum)» удалены.

## 2026-10-15: откачка — с борта, где больше воды
* Игра качает один борт (`_drainPortSide`), пока на нём вода выше минимума, и только потом переключается (старт — левый борт). `DcControl.PumpMoreWater` (обёртка задачи Pump): каждый тик ставит флаг на борт с большим `FloodingDelta`, затем зовёт игровой `RepairFlooding`. Контрзатопление (`TaskCounter`, `PumpSafe`) не затронуто. Подсказки: «сейчас откачивается» = борт с большим delta.
* Не проверено в игре.

## 2026-10-15: смена борта откачки не чаще раза в 10 с
* `DcControl.PumpMoreWater`: борт меняется, когда на текущем вода на минимуме, либо когда на другом воды больше и с прошлой смены прошло ≥ `SwitchEvery` = 10 с работы насоса (таймер на секцию, копится по `fixedDeltaTime` вызовов; старт — 99, т.е. первый выбор сразу по большей воде). `PumpFree` (откачка при заделке) следует тому же флагу. Подсказки снова читают реальный `_drainPortSide`.

## 2026-10-15: галочка «Show deck levels in 3D»
* В карточке List/Trim/Level (под циферблатами; её подсказка — схема затоплений) — `CheckBox` «Show deck levels in 3D». Тултип Noesis не принимает клики, поэтому галочка на самой карточке. Состояние на корабль: `DeckCorners.Pin/IsPinned` (ключ — `Compartments`, `ConditionalWeakTable`); `DeckCorners.Tick` рисует точки, если `Model || схема открыта || UnitPinned()` (только для выбранного корабля, как и раньше). Не в сейве.

## 2026-10-15: Advanced — DCTeamEffectiveness 0.3
* `Release/Profiles/Advanced.cfg`: `DCTeamEffectiveness` 3.0 → 0.3 (опечатка в значении по умолчанию). Base Game — 1.0.

## 2026-10-15: «Team on its way» — последней строкой вне Raises/Lowers
* `OnIt(..., ref bool way)`: «Team on its way» больше не строка списка; выставляет `way`, и `FloodCause`/`FireCause` дописывают её одной последней строкой (без маркера) после «Lowers it». «Hurts hull, systems a little» → «Damages hull and systems».

## 2026-10-15: тексты про урон от огня
* `FireCause`: Severe — «Damages hull and systems. Spreads to adjacent sections.»; Moderate и Minor — «Damages hull and systems».

## 2026-10-15: тексты огня без точек
* `FireCause`: как и остальные строки подсказок — без точек, Severe двумя строками: «Damages hull and systems» и «Spreads to adjacent sections».

## 2026-10-15: подготовка к релизу
* Проверено: все ключи `Pcfg.Bind` есть в обоих системных профилях (лишних нет); отладочные ключи в `UserConfig` выключены; опечатка в `_info.ini` («interace») исправлена; README (все три) дополнены про выбор борта откачки и галочку «Show deck levels in 3D». Версия `1.0.0`, `ApproximateVersion=0.8.4` без изменений. Сборка — 0 ошибок (3 безобидных предупреждения о неиспользуемых полях).
* Пакет для Workshop: содержимое `Release/` (папка мода в игре называется `AlternativeDamageModel`).

## 2026-10-15: «Show deck levels in 3D» — в окне схемы затоплений
* По просьбе пользователя галочка перенесена с карточки List/Trim в окно схемы затоплений (под текстами лимитов). Тултипы Noesis не принимают клики, поэтому схема теперь показывается не как ToolTip, а как оверлей в верхней панели экрана (`TopPanel`: самый верхний Grid/Canvas над карточкой, как у окна настроек), ZIndex 30000, прижат правым краем к карточке под ней: `ShowSchem` по `MouseEnter` карточки, закрытие в `RenderSchematic` (4 раза/с): мышь не над карточкой и не над панелью два замера подряд; также при скрытии карточки (`IsVisibleChanged`) и в `RemoveExtras` (`HideSchem`). Если верхней панели нет — старый тултип (без кликабельной галочки).
* Не проверено в игре: положение оверлея (`TranslatePoint` в координаты верхней панели) и приём кликов.

## 2026-10-15: схема затоплений — над карточкой List/Trim/Level
* `ShowSchem`: панель стоит над карточкой, отступ `SchGap` = 5 px, по центру карточки (размер — `Measure`/`DesiredSize` при первом показе).

## 2026-10-15: схема над карточкой — привязка по нижнему краю
* Высота по `Measure` выходила завышенной (панель висела ~25 px выше): в Grid панель теперь привязана нижним краем (`VerticalAlignment.Bottom`, нижний отступ = высота панели − Y карточки + 5), высота не нужна. Для Canvas — прежний расчёт по `DesiredSize`.

## 2026-10-15: ChangeList.txt
* В корне добавлен `ChangeList.txt` (английский, для релиза); архив теперь включает README*.md/.txt/.BBCode.txt и ChangeList.txt.

## 2026-10-15: выложено в Steam Workshop
* Релиз выложен пользователем (сборка dll 20261015.0114, Release/ + README*, ChangeList.txt). Опорная версия для следующих ChangeList — эта.

## 2026-10-20: новый релиз — задачи
* **Centre of mass rise удалён**: `CapsizeFix.cs` оставил только `KeepCentreOfMass` (BeforeSink/AfterSink); `CapsizeFactor`, `Tick`, расчёт GM убраны из AdmPlugin/настроек/профилей.
* **Sinking → Effects, глобально**: `KeepCentreOfMass`, `WreckDragFactor`, `BubblesStopMinutes` теперь `Cfg.Bind("Effects", …)` (не профильные), строки в TFx, группа «Sinking and wreck»; вкладка Sinking удалена; из профилей ключи убраны, в `UserConfig` добавлены в `[Effects]`.
* **Малые AI без Speed Limit**: `FireAndFlooding/SmallAiNoSpeedLimit` (Advanced true, Base Game false); `SpeedEffect.NoCutFor(comp)`: `_baseObject.IsControllable` = false и длина (DcTeams.ShipLength) в (0; 50) м; используется в `SpeedLimitOff`.
* **Приоритеты Auto DC** (`DcAuto.Order`): Auto — Fire Rate > Flooding Rate > Fires > Flooding > Systems > Structure; Fire — FireRate > Fires > Leak > Pump > Systems > Hull; Flooding — Leak > Pump > FireRate > Fires > Systems > Hull. `FireRate` (=100) — псевдозадача: `_fireGrowRate > 0`, размер = рост; внутри рангов FireRate/Leak секции сортируются по убыванию скорости роста (`Need.ByRate`). `DcControl.Orders` (порядок задач внутри секции): Systems перед Hull. Подсказки приоритетов и `OrderText` обновлены.
* **Команды уходили в Reserve с работой в секции**: `DcAuto` считал работу по порогам (hull ≥ 5 %, системы от Medium), а игровой `RepairStructure/RepairSystems` чинит всё, что повреждено, — секция с лёгким повреждением считалась «без работы», у неё отбирали команду / она получала `returnTeam`. Теперь, если в секции уже стоит команда, работой считается любой остаток корпуса (>0.01 %) и системы с Light-повреждением (пороги действуют только для начала работ).
* **Стрелки уезжали на проценты**: `CheckOverlap` проверял только наложение боксов % друг на друга, а не стрелок; теперь проверяются все `InlineUIContainer` (стрелка с допуском 3.5 px), повторная проверка не чаще 300 мс. Кроме того, если у бокса % меняется ширина (9→10 %, 99→100 %), строка пересобирается, а не «чинится» in place. Не проверено в игре.
* **Deck Points отключаемые по кораблям**: профильный ключ `FireAndFlooding/NoDeckPointsShips` (список ini-имён через запятую; в профилях пустой), редактор на вкладке Flooding (`NoDeckRows`, как Single ships, без значения); `DeckCorners.NoPoints` → `PointsOf` возвращает пустой `ShipPoints` (как нет hull collider → старые правила TrimFlooding). Применяется к кораблям с записью в `Hulls` (мод включён); уже посчитанные в этом бою точки не отменяются.
* **Без оружия — не военный**: опции нет, всегда включено: в `ApplyHull` надводный корабль без `WeaponSystemGun/Launcher/CIWS/Hardpoint` в `obp._systems` считается гражданским (множитель корпуса и таблица команд).
* Не проверено в игре: все пункты (сборка 0 ошибок).

## 2026-10-20: стрелки — профилактика, название опции
* `Draw`: смена цвета любого куска строки (Run, бокс %) теперь сразу пересобирает строку (раньше цвет менялся in place, и стрелки могли уехать до следующей проверки `CheckOverlap`); проверка наложения осталась как страховка.
* Подпись опции малых AI сокращена до «Small AI: no speed limit» (правило: подписи и единицы в окне настроек должны влезать в колонки).

## 2026-10-20: Small AI — начальное значение кнопки; откат патча стрелок + диагностика
* **Small AI: no speed limit** больше не обходит кнопку: `DcControl.LimitSpeed(comp)` при первом обращении к кораблю (первое обновление скорости игрой после спауна) один раз задаёт начальное значение `NoSpeedLimit = SpeedEffect.NoCutFor(comp)` (флаг `Ship.LimitInit`); дальше состояние — кнопки (игрок может включить/выключить). `SpeedLimitOff` вернулся к `!LimitSpeed(comp)`. Кнопка в DC врага показывает выключенное состояние.
* **Откат** патча «любая смена цвета пересобирает строку» (давал дёрганье при смене цвета). Причина съезжания стрелок остаётся неустановленной.
* **Диагностика** (`Draw`, Trace): при смене цвета куска строки на месте пишется `DC colour change in place: #i box/run 'текст' цвет -> цвет` + позиции всех кусков строки (`Pieces`: индекс, тип, x/ширина/высота от левого края строки), затем 3 следующих вызова `Draw` пишут `DC probe after colour change … was/now` с `Environment.TickCount`. Не более 60 записей за сессию. По логу видно, какой кусок и на сколько сдвигается после смены цвета.

## 2026-10-20 (2): трейс 50f005e5 — причина съезда стрелок; Small AI
* **Стрелки**: по трейсу `DC pieces overlap: #2 x 25.8 w 24, #3 x 26.8 w 22` — стрелка (двойная, ширина 22) встаёт на тот же x, что и бокс % (+1 px её левого отступа), т.е. текстовый блок кладёт два соседних `InlineUIContainer` друг на друга. Случается сразу после пересборки строки (`structure changed`, когда меняется число стрелок на пороге), лечится следующей пересборкой через ~0.5 с. Смена цвета на месте (`DC colour change in place`) позиции не меняла (was = now на 3 кадрах). Исправление: стрелка, идущая за боксом %, теперь лежит в **том же** `InlineUIContainer`, в горизонтальной `StackPanel` рядом с боксом (оба `VerticalAlignment.Bottom`, как раньше — по базовой линии); текстовому блоку больше не нужно ставить их рядом. В `Drawn.Inl` запись стрелки остаётся (для сравнений), `Kids` — null; резервный `CheckOverlap` и трейс оставлены.
* **Small AI**: в трейсе нет строки о стартовом значении (его не было, т.к. `NoCutFor` вернул false); добавлен трейс `Limit Speed start value: … [setting on/off, controllable …, length … m]` при первом обращении к кораблю.

## 2026-10-20 (3): вся динамическая часть строки — один InlineUIContainer
* По отзыву: пара «% + стрелка» работает, но вся строка слегка (на 1 px по высоте) сдвигается при смене цвета; строка Flooding (два бокса, разделитель, две стрелки) глючила так же. Теперь всё от первого бокса % до конца строки лежит в **одной** горизонтальной `StackPanel` в одном `InlineUIContainer` (текстовый блок ставит только метку и эту панель): боксы, разделители (теперь `TextBlock`, у всех нижний отступ `-fs*BoxDrop`, чтобы базовая линия совпадала) и стрелки. Высота панели фиксирована (`DesiredSize.Height` первого текста), чтобы строка не меняла высоту при появлении/исчезновении стрелок. Смена текста/цвета боксов и разделителей — на месте (`Kids[i]`); смена текста разделителя или ширины бокса пересобирает строку. Не проверено в игре (риск: пробелы по краям текста разделителей в `TextBlock`).

## 2026-10-20 (4): сдвиг строки на 1 px по высоте
* Трейс 28b13706: x/ширина/высота кусков до и после смены цвета не меняются (бокс 14.4, строка 15), т.е. сдвиг не в разметке, а, вероятно, в округлении: высота бокса 14.4 и панели 11.4 — дробные. Теперь высоты целые: текстовые `TextBlock` группы получают `Height = round(h + drop)`, панель — `th − drop`, `UseLayoutRounding = true`. В `Pieces` добавлен `y` каждого куска (для следующего трейса). Не проверено в игре.
* Limit Speed: в этом трейсе только свои корабли (controllable True), вражеский катер не проверялся.

## 2026-10-20 (5): убран отладочный код строк
* Удалены диагностика смены цвета (`Pieces`, `Drawn.Probe/Before/Why`, трейсы `DC colour change in place` / `DC probe`). Оставлены `CheckOverlap` (страховка) и трейс `Limit Speed start value` (до проверки на вражеском катере).

## 2026-10-20 (6): Small AI — признак «AI»
* В игре `ObjectBase._isControllable` по умолчанию `true` (ставится false лишь для особых случаев), поэтому враги считались «управляемыми», и стартовое значение не менялось. Теперь «AI-корабль» = не сторона игрока (`ObjectBase.IsPlayerObject == false`: враги и союзники). Если при первом обращении произошла ошибка (например, ещё нет taskforce), старт повторяется при следующем обращении.

## 2026-10-20 (7): убран трейс Limit Speed
* Small AI проверен пользователем («все ок»); убраны `SpeedEffect.LastInfo` и трейс `Limit Speed start value`, остался флаг `SpeedEffect.Failed` (повтор при ошибке).

## 2026-10-20 (8): огонь/затопление секций, штраф DC при пожаре
* Новый `SectionEffects.cs` (вызывается из `TeamTickPrefix`, каждый физ. тик, при `Model`): (1) `FireReopenLeaks` (%/с от максимума, при огне 100 %; 0 = выкл) и `FireReopenLimit` (% от максимума): для каждого борта секции запоминается наибольшее `_floodingRate`, при горящей секции (`FireSeverity/_maxFireSeverity` = доля огня) `_floodingRate` растёт на `max·rate·fire·dt`, но не выше `max·limit`; рост от повреждений игрой не трогается, максимум обновляется. (2) `FullFloodLevel` (75 %, среднее воды двух бортов `CurrentFloodingPercent`), `FullFloodSystems`: у систем секции целостность сбрасывается до 19 % максимума (Severe) и вызывается `MakeInoperable()`; держится каждый тик, поэтому ремонт бесполезен; `SectionEffects.Locked` → `Repairs[TaskSystems]` возвращает false, `DcAuto` не считает работу по системам. (3) `FullFloodFireDecay` (%/с от максимального огня): тушение полностью затопленной секции, рост огня обнуляется. «Полное затопление» одно на оба пункта (уровень `FullFloodLevel`).
* `FireWorkEfficiency` (Fire; 75 % = команды без задачи «Fire» работают на 75 % эффективности, 100 = выкл; сначала было «штраф 75 %», изменено по просьбе): `DcControl.Work(task, …)` оборачивает вызовы ремонта; для задач кроме Fire в секции с огнём (`FireIn`) ставит `DcTeams.WorkMul = 1 − penalty`, которое входит в `DcTeams.Eff` (множитель, уже применяемый ко всем рабочим скоростям Crew). Работает только там, где идёт `DcControl.CheckPrefix` (Auto DC мода или подробный вид).
* Профили: Advanced — все включены (0.5 / 50 / 75 / true / 10 / 75), Base Game — выключены (FireWorkEfficiency = 100). Максимальные утечки не сохраняются в сейве (после загрузки максимум = текущая утечка). Не проверено в игре.

## 2026-10-20 (9): колонка «game:» у настроек без аналога в игре
* `FireReopenLimit` и `FullFloodLevel` показывали «game: 50/75» (значение Base Game профиля); теперь, как у `Unattended*`, — «default: …» (`ModOnly(key)` в SettingsWindow).

## 2026-10-20 (10): Flooding — порядок групп, FullFloodLevel убран
* Группы «Fire and leaks» и «Full flooding» перенесены под «Sinking from»; настройка `FullFloodLevel` удалена: «полностью затопленная секция» — константа `SectionEffects.FullLevel` = 75 % (среднее двух бортов). `FullFloodSystems` по умолчанию true (и в коде как запасное значение, и в Advanced.cfg).
* Откуда берутся значения по умолчанию: профили `Profiles/*.cfg` (по умолчанию `Advanced`); значения в коде (`Bind`) — только запасные для отсутствующего файла/ключа; пока используется встроенный профиль, .cfg на каждом старте следует его файлу. Если значения менялись вручную, они сохраняются как профиль «Custom» и новые ключи туда попадают со значениями из кода.

## 2026-10-20 (11): профиль-родитель
* `Profile.Parent`: профиль, из которого сделан пользовательский (Custom и т.д.). `Copy(name)` ставит `Parent = Name` для встроенного источника, иначе сохраняет `Parent` источника; `SyncProfile`/`StartProfile` проставляют родителя явно. Пишется в файл профиля секцией `[Profile]` / `Parent = Advanced` (`WriteProfileFile`), читается `ReadParentName`.
* `LoadProfile` (пользовательский): значения ключей, которых нет в файле (новые настройки релиза), берутся из родителя (рекурсивно, глубина ≤ 3, сам себя нельзя); родитель не найден или не указан → профиль Base Game («из игры»); раньше брались значения из кода. Ships/Modules тоже наследуются, если секции нет в файле. Старые файлы без `Parent` (например, существующий Custom) наследуют от Base Game, пока не пересохранены через окно настроек.

## 2026-10-21: здоровье и усталость DC-команд
* `DcTeams.Team`: `Health` (0..1), `Fatigue` (с). Макс. здоровье `MaxHp(comp)` = max(60, 100·√(т/1000))·`TeamHealth`; водоизмещение запоминается в `SetCount` (отдельная `Disp` — в `Ships` нельзя: GetOrCreate пропустил бы Reconcile). `TeamHealth = 0` — ран нет.
* Выбивание игрой (Blastzone, таймер в `_damageControlTeamsDisabledTimers`) по-прежнему перехватывается в `Tick`; теперь команда получает рану `50·HitWound / MaxHp` (50 пунктов = старые «2 выбивания = смерть» на 1000 т), идёт в резерв (`St.Injured`, время = `TransferTime`), при здоровье ≤ 0 — `Dead`. Параметры Knock-outs (`HealTime`, `HealTimeSpread`, `KnockoutsToDeath`) и `Team.Knocks` удалены (в сохранении поле осталось нулём; добавлены health/fatigue — 8-е и 9-е поля).
* `DcTeams.Condition` (каждый тик): резерв — +`HealthRegen` %/с и −1 с/с усталости; в отсеке — усталость +1 с/с, если `Doing` ≠ Idle/None (предел 2·лимит), рана от огня `FireWound`·уровень пожара (`DcControl.FireLevel` = FireSeverity/_maxFireSeverity) для команды, не тушащей огонь; здоровье ≤ 0 → `Dead` прямо в отсеке (счётчик секции −1).
* Эффективность: `TeamEff` = max(0.25, здоровье) (если ранен) × 0.5 (если устал, `FatigueMinutes`·60 с). `DcControl.CheckPrefix` считает среднее по группе команд, работающих над одной задачей (`_grpEff`), и умножает на него `DcTeams.WorkMul`.
* UI: `Icon(task, team)` — подсветка усталой (янтарный фон), полоска здоровья по верхнему краю (поверх иконки, размер иконок не менялся — центровка не трогалась); `HealthNote` в подсказках; `Names`/`Tail` в Causes — «(Wounded)»/«(Tired)» в конце строк со скоростью, скорость считается в «командах-эквивалентах» (`TeamWork`).
* Настройки (DC Teams → «Health and fatigue»): `TeamHealth`, `HealthRegen`, `FireWound`, `HitWound`, `FatigueMinutes`; все mod-only («default: …»). Advanced: 1 / 0.5 / 1 / 1 / 15; Base Game: 1 / 0.5 / 1 / 1 / 0.

## 2026-10-21 (2): вложенные закладки DC Teams, выключатель здоровья
* Вкладка DC Teams разбита на подвкладки `Subs` = Teams / Work / Health (`SubOf(Field)` по группе; `_sub` — выбранная, `SubBar()` в начале `ShowTab`). Группа «Work and movement» разделена: Transfer speed и Muster time → «Movement» (подвкладка Teams), остальное осталось в Work.
* `TeamHealthEnabled` (bool; Advanced true, Base Game false): выключено → `DcTeams.HealthMul = 0` (ран нет, правила игры), остальные строки группы «Health» скрыты через `ShownWhen` (`HealthKey`). Усталость (`FatigueMinutes`) — отдельная группа «Fatigue», не зависит от выключателя. Base Game: `TeamHealth` и др. остаются в файле, но не действуют.

## 2026-10-21 (3): DC Teams — страницы Count/Work/Health/Fatigue, выключатели над ними, колонки
* Подвкладки: Count (таблицы числа команд), Work (все скорости + бывшие Movement), Health, Fatigue. Выключатели `TeamHealthEnabled` и `FatigueEnabled` (группа «Switches», `SubOf = -1`) выведены строками над панелью страниц; выключенная страница не показывается (`SubShown`), `_sub` при этом сбрасывается на Work. `FatigueEnabled`: Advanced true, Base Game false (FatigueMinutes в обоих 15; при выключателе off `DcTeams.FatigueMin = 0`).
* Колонки строк (`RowGrid`): теперь все Star (метка / редактор 300 / game), с `MinWidth`; веса метки и колонки «game:» считает `FitColumns(tab)` по длине текстов вкладки (≈7.3 px/симв. для метки, 6.3 для «game:»; пределы 150–280 и 70–150), поэтому при ресайзе окна растут и первые колонки. Оценка по длине строки — не измерение, на глаз поправить после проверки в игре.

## 2026-10-21 (4): выключатели DC Teams в одной строке
* `SwitchPanel()`: «Team health» и «Team fatigue» — чекбоксы в одной горизонтальной панели над страницами (отдельная рамка, без колонок `RowGrid`); подсказка по наведению через `Hover`, кнопки сброса и точки «differs» у них нет.

## 2026-10-21 (5): клик по всей области выключателя
* В `SwitchPanel` подпись — `Content` самого CheckBox, рамка без отступа: клик в любом месте подсвеченной области переключает флажок.

## 2026-10-21 (6): подвкладки DC Teams как таб-панель
* `SubBar()` в стиле верхних вкладок (скруглённые сверху, без нижней рамки у активной), страница строк — в рамке-панели `CPanel` (в `ShowTab` строки идут в `rowHost`), активная вкладка сливается с панелью.

## 2026-10-21 (7): панель страницы DC Teams до низа
* Область настроек — ScrollViewer, поэтому высота панели задаётся кодом: `FitPage()` ставит `MinHeight` = высота области − положение панели − отступы (вызов из Loaded / LayoutUpdated панели и SizeChanged скролла; порог 0.5 px против зацикливания). Если контента больше — работает прокрутка.

## 2026-10-21 (8): Recovery speed усталости, Tired after = 60
* `FatigueRecovery` (% скорости накопления; 100 по умолчанию): в резерве усталость убывает `dt·FatigueRecover/100`; подсказка «Rested in» учитывает его. `FatigueMinutes` по умолчанию 60 (код и профили).

## 2026-10-21 (9): второй уровень усталости и кривая скорости
* Уровни: Tired after (60 мин), Exhausted after (120 мин; не меньше Tired — зажим в `AdmPlugin` и `KeepLevels` в окне; равны = второго уровня нет). Скорость `FatigueMul`: SpeedRested (100

## 2026-10-21 (10): нижняя граница скорости
* `TeamEff` при включённой усталости: итог (ранения × усталость) не ниже `SpeedExhausted`.
