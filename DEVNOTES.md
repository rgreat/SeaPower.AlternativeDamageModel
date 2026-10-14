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
* Крен от дисбаланса затоплений и скорость опрокидывания — см. «Анализ крена».

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

## 2026-10-10…14: карточка Hull Damage / Flooding, крен, оружие
* `DamageControlUi.Panels.cs` `RenderHullFlood`: к значениям карточки дописывается « (NN%)» (нет суффикса при 0%). Hull: урон = 100 − `IntegrityPercentage`; с ADC слова по урону:
  None <1, Slight ≤25, Moderate ≤50, Heavy ≤75, Structure Destroyed >75 (слова — через игровой `IntegrityToDamageControlConverter`). Flooding: слово игры, % = min(100, 100·FloodingPercentage/SinkAt) (шкала LEVEL).
  Слово считаем сами каждый тик (`FloodingPercentageToDamageControlStringConverter` по `FloodingPercentage`): ручная установка `TextBlock.Text` заменяет привязку игры (раньше показывало только None/Slight).
  Цвета: классы 0–2 как в игре, Heavy — оранжевый (`BHeavy`), последний класс — красный (`BCritical`); и для Flooding (по значению игры 1/10/20/30), и для Hull без ADC (99/75/25/1).
  `RemoveExtras` возвращает игровой текст/цвет. Advanced: Warships 4.0, DCTeamEffectiveness 3.0.
* Затопление от крена: делает мод — `TrimFlooding.FloodList` (при |крен| > `Limits.ListFlood`, не sinking/capsized, все отсеки низкого борта: игровой `AddFloodingForHeavyListing` ÷10 × DeckFloodingSpeed);
  игровой вызов выключен (`HeavyList/HeavyListNeg` при ADC возвращают ±float.MaxValue). Прежний Harmony-патч метода убран (не срабатывал при инлайне JIT).
  Опрокидывание на |крен| > 70° (`_capSized` + `Sink(All)`) — правильное поведение игры, остаётся (отключение пробовали и откатили).
  Sink() вызывают только Compartments.OnFixedUpdate (уровень >40 → SinkLevel, дифферент >8° с водой → SinkTrim, крен >70° → capsize), DestroyByExplosion (целостность <10%) и внешние системы; «скрытого» механизма воды нет.
* `[General] LogHeel` (Debug → Logging): раз в 2 с при крене ≥8° или дифференте ≥3° — крен, дифферент, y, подъёмная сила/вес, vy, submerged, % воды, sinking/capsized.
  По логам: до 70° подъёмная сила ≈ вес, корабль не тонет от геометрии; после capsize — «лежит на боку» (~1.6°/с) — см. анализ ниже.
* `[Sinking] FloodImbalanceEffect` («List from uneven flooding»): реализация (`TrimFlooding.SkewTick/CfvPost`, постфикс на `_currentFloodingValue`) не дала эффекта, скрыта в окне и игнорируется
  (`TrimFlooding.Imbalance = 1`); ключ остаётся в профилях = 1.0 для совместимости. Требует нового подхода (ниже).
* Раскраска оружия на нижней панели (`src/WeaponStatusColors.cs`, `[UI] ColorWeaponStatus`, по умолчанию вкл.): раз в секунду обход визуального дерева от `MainGameView.Instance` (бюджет 40000 узлов),
  `TextBlock` с `DataContext` = `WeaponEntry`, текст = `Label` или `AmmoAmountText`; состояние по `WeaponSystems[].Inoperable.Value`: часть — жёлтый (240,208,74), все — красный (236,70,56), цело — `ClearValue`.
  Вызов `Tick` из `Update()` (не зависит от `_uiReady`), при выключении `ClearAll()`.
* Слияние с GitHub: история `rgreat/SeaPower.AlternativeDamageModel` была отдельной; ~25 локальных коммитов свёрнуты в один поверх 2d94749 (крестик ✕ рисуется Path-ом, ресайз окна за края/углы;
  `UserConfig/AlternativeDamageModel.cfg`, `.gitignore`).

## Анализ крена: центр масс и вода (2026-10-14, без изменений кода)
* Масса тела постоянна (`displacement/40`), вода массу НЕ добавляет. `rigidbody.centerOfMass` задаётся в Init: `(0, y_среднего_отсека, z_среднего_отсека) + [Physics] CenterOfMass`; дальше только при sink/capsize
  (`(0,0,0)` / при capsize `(0,1,0)`; подлодки `(0,-0.5,0)`). Вес воды и свободные поверхности центр масс не смещают.
* Вода влияет ТОЛЬКО на подъёмную силу отсека: `F_i = (m·g/10)·s_i/(ρ + w_i)`, `w_i = MaxFloodingValueWhenSinking(2.0)·flooding/max`, `s_i = clamp((уровень_волны − y_i + H/2)/H, 0..1)`, `H = bounds.y·0.5`.
  Полный отсек теряет максимум 71% подъёмной силы (при ρ=0.8 остаётся 29%).
* Точки: 5 пар, x = ±bounds.x/2, y = trimDepth (+ z·sin(trimAngle)) — по высоте на уровне CoM при CenterOfMass.y = 0. `AddForce(сумма)` + `AddTorque(Σ (r_i − CoG) × F_i)`; гравитация в CoM момента не даёт.
  Крен создаёт только разница сил бортов × плечо B; остойчивость — изменение s_i при крене (линейно в пределах H, затем клэмп) минус «маятниковый» член `W·c` от CenterOfMass.y: GM_eff ∝ B²/(ρ·H) − c.
  Оценка: φ ≈ 0.07·s·H/B (рад) от одного залитого отсека; борт целиком — в ~5 раз больше; после насыщения (высокий борт вышел из воды) момент больше не растёт.
  Поэтому множитель дисбаланса упирался в потолок 71% и в неизменные плечо/жёсткость.
* На боку (~90°) плечо x = B·cosφ → 0, момента нет — корабль «лежит»; после capsize CoM `(0,1,0)` выше точек приложения — момент неустойчивый, докатывает медленно.
* Идея: вести `centerOfMass.x` (и немного .y — свободная поверхность) самим модом по воде в отсеках: `dx = Σ w_i·x_i / (M + Σ w_i)`; момент = F_total·dx, клэмпы s не мешают; игра пишет CoM только в Init/sink.
  Учесть двойной счёт с потерей подъёмной силы (она уже эквивалентна добавленному весу). Ускорение переворота после capsize — постоянное угловое ускорение (`AddTorque(..., ForceMode.Acceleration)` вдоль forward).

## 2026-10-14: рывок высоты панели с картинкой корабля
* Причина: `AddPriorityCard` ставил картинке `MaxHeight` 300, а `FitPicture` (по высоте боковых карточек) срабатывал только когда у карточек уже был `DesiredSize` — то есть на следующем тике; строка рисовалась высокой, затем сжималась.
  Теперь `FitPicture` вызывается сразу после создания карточек и в конце каждого `Render` (после `RenderExtras`); в первый раз перед замером делается `img.UpdateLayout()` (`ViewState.PicFitted`).

## 2026-10-14: HideShipModel → модель корабля на 70% прозрачная
* Вместо отключения MeshRenderer'ов (`DeckCorners.ApplyHideModel`): всем MeshRenderer/SkinnedMeshRenderer корабля (с подмоделями; кроме оверлея ShowHullCollider) ставятся копии материалов с прозрачностью Standard-шейдера (как игра в `OBJLoaderHelper.EnableMaterialTransparency`: _Mode 3, _SrcBlend 5, _DstBlend 10, _ZWrite 0, _ALPHABLEND_ON, очередь 3000) и альфой `_Color` = 0.3 (`GhostAlpha`).
  Игровые sharedMaterials запоминаются и возвращаются как были, копии уничтожаются; список рендереров пересматривается раз в ~60 кадров. Ключ [General] HideShipModel и его смысл («Debug») прежние, подпись в окне «Ship's 3D model 70% transparent».
* Допущение: материалы кораблей — Standard-совместимые (есть _Color/_Mode); для другого шейдера прозрачность может не примениться (тогда модель выглядит как обычно).

## 2026-10-14: ShipModelView — Show / Translucent / Hide
* `[General] HideShipModel` (bool) заменён на `ShipModelView` (строка Show | Translucent | Hide, по умолчанию Show; старый ключ остаётся orphan'ом и игнорируется). В окне (Debug → Visualisation) — выпадающий список (`Kind.Choice`, `Field.Options`, `ChoiceEditor`), применяется сразу (`_liveGlobals`).
* `DeckCorners.ModelView` 0/1/2: Translucent — копии материалов с альфой 0.3 (см. запись выше), Hide — `Renderer.enabled = false` с запоминанием прежнего `.enabled`; смена режима пересоздаёт состояние (`_appliedView`).

## 2026-10-14: Translucent — прозрачные копии на шейдере Sprites/Default
* Правка шейдерных ключей Standard (`_Mode`, `_ALPHABLEND_ON`) на копии материала корабля не давала прозрачности: шейдеры кораблей из asset bundle'ов, вариант с blend может быть вырезан/не использоваться. Теперь `MakeGhost` строит материал на `Sprites/Default` (его игра сама использует, всегда блендит): копируются основная текстура (`_MainTex/_BaseMap/_BaseColorMap/_Albedo/…`, scale/offset) и цвет (`_Color/_BaseColor/_Tint`), альфа 0.3, очередь 3000. Освещения нет (отладочный вид). Резерв при отсутствии шейдера — прежний способ.
  В trace (Debug) пишутся имена шейдеров материалов корабля: `Ghost model: material shader '…'`.

## 2026-10-14: Translucent — альфа текстур
* Полигоны выглядели прозрачнее 70%: `Sprites/Default` умножает цвет на альфу текстуры, а у альбедо-текстур кораблей в альфе лежит другое (глянец и т.п.). `OpaqueCopy`: основная текстура копируется один раз (Blit в RT ≤ 2048 px → Texture2D) с альфой 255; если у текстуры > 2% почти прозрачных пикселей (вырез: поручни, такелаж, сетки) — оставляется оригинал. Копии уничтожаются в `RestoreShipModel` (`ForgetOpaqueTextures`).

## 2026-10-14: Translucent — корпус и субмодели по-разному
* Корпус (рендерер или его родитель до корня корабля с «hull» в имени — в модах бывают hull-субмодели; `IsHullPart`) — текстура с альфой 255 (ровно 70% прозрачности); остальные субмодели — оригинальная текстура, альфа текстуры умножается на 0.3 (прозрачнее 70%, как было в прежней версии, которая хорошо смотрелась на субмоделях). В trace: `Ghost model: hull|sub <имя>`.

## 2026-10-14: точки DeckCorners — только секционные, ближе к оси
* Убраны осевые точки носа и кормы: остались 5 пар борт/борт (`PointCount = Sections * 2 = 10`). Найденная точка борта сдвигается к продольной оси на 25% своего x (`InwardShift`, локальные координаты корабля; высота — палубная, как найдена). Нужны для анализа затопления по секциям.

## 2026-10-14: цвет оверлея Hull Collider
* Оверлей коллайдера корпуса: голубой — точки корабля посчитаны полным (сеточным) алгоритмом, оранжевый — запасным (рейкаст, нечитаемые меши; `DeckCorners.PointsExact`). Альфа 0.35 → 0.2.

## 2026-10-14: удалены запасные варианты расчёта точек
* Из `DeckCorners` убран рейкаст-путь целиком (Collider.Raycast через рефлексию, активация/восстановление коллайдеров, `DeckCornerAtRay`, `CentreHeightAtRay`, `TopAt`, `HeightAtMesh`, `TryEdgeMesh`). Остался только точный алгоритм по мешу (`TryHullTriangles` + `DeckCornerAtMesh`).
* Корабль с нечитаемым мешем (isReadable = false, напр. civ_ms_andizhan) точек не получает: `PointsExact = false`, `_points = null`, оверлей Hull Collider оранжевый, затопление идёт по старым правилам. Запись про цвет выше: «оранжевый» = «точек нет», не «рейкаст».
* Открыто: новое правило затопления «через борт» для кораблей с точными точками не задано; точки пока считаются только для выбранного корабля.

## 2026-10-14: чтение нечитаемых коллайдерных мешей из GPU
* Меш с `isReadable == false` (Read/Write выключен при импорте; civ_ms_andizhan) больше не отключает точки: `TryGpuMesh` берёт `Mesh.GetVertexBuffer/GetIndexBuffer` и читает их через `AsyncGPUReadback` (синхронно, `WaitForCompletion`, один раз при выборе корабля). Меш в GPU есть — его рисует оверлей Hull Collider. DX11 и DX12 readback поддерживают.
* Поддержано: позиции Float32, подмеши-треугольники (с `baseVertex`), индексы 16/32 бит. Прочее (или ошибка readback) — как раньше: точек нет, `PointsExact = false`, оранжевый оверлей, старые правила. Результат пишется в trace: `GPU read of '<mesh>': N vertices, M triangles` — по нему проверять на andizhan.
* Визуальный (рендер) меш как источник отвергнут: включает надстройки, палубный угол определится неверно.

## 2026-10-14: Select — модель не зависит от коллайдера и точек
* Жалоба: у части кораблей не видно ни оверлея Hull Collider, ни полупрозрачной/скрытой модели. Вероятная причина: `Select` запоминал `_unit` только в конце, а любой ранний выход (нет `coll_hull*`, нет ObjectBaseParameters/transform, исключение в `LocalPoints`) оставлял `_unit = null`, и `Tick` считал корабль «не живым» — отключался в том числе вид модели. Теперь `_unit` ставится сразу после проверки компартментов; вид модели работает всегда, оверлей — при наличии `coll_hull*`; `LocalPoints` обёрнут в try/catch (в trace — полный стек). Для каждого раннего выхода пишется строка в trace.
* Каркас (hull_wire) для нечитаемых мешей тоже строится через `TryGpuMesh`.
* Не подтверждено: если у каких-то кораблей модель всё равно не видна — нужен trace/имя корабля (возможно, рендереры не MeshRenderer/SkinnedMeshRenderer или лежат вне GameObject юнита).

## 2026-10-14: диагностика кусков коллайдера корпуса
* Жалоба: у usn_dd_gearing_fram2_mod коллайдер корпуса короче модели (нет оконечности). По ini корпус — три куска `coll_hull_1..3` (`usn_dd_gearing_fram2_mod_coll_hull_N`); overlay рисует их `sharedMesh` как есть. Причина не выяснена (возможно, один из мешей отсутствует/пуст в бандле мода, либо куски смещены).
* В trace при выборе корабля: по каждому куску — имя объекта, меш, число вершин, читаемость, активность, границы в локальных координатах корабля (x/y/z); пропущенные куски (нет компонента / `sharedMesh == null`) тоже пишутся.
* Идея (не сделана): если длина по z коллайдера заметно меньше длины модели — считать точки неполными (оранжевый оверлей, старые правила).

## 2026-10-14: проверка длины коллайдера корпуса
* `ColliderLengthOk` (Select): длина по z объединённых кусков `coll_hull*` (× `lossyScale.z`) сравнивается с длиной корабля из ini (`ObjectBaseParameters._length`, м). Расхождение > 10% (`MaxLengthMismatch`) — коллайдер «плохой»: точек нет, `PointsExact = false`, оранжевый оверлей, старые правила затопления; одно WARN на корабль. Длина неизвестна (≤ 0.5) — проверка пропускается. В trace всегда: `hull collider length X m, ship length Y m (N% off)`.
* Исправление: длина коллайдера считалась в единицах Unity, а `Length` из ini — в метрах, поэтому «плохими» стали все корабли (разница ≈ в 67 раз). Мир игры: `METER_TO_UNITY = 0.0148809375` (1 ед. = 67.2 м). Теперь длина коллайдера переводится в метры (`UnityToMetre = 67.200066`). По trace все пять кораблей получаются в пределах 3–6% — проверка проходит.

## 2026-10-14: затопление «палубная точка под водой» (SubmergedFlooding)
* Новый файл `src/SubmergedFlooding.cs`, вызов из `TeamTickPrefix` (префикс `Compartments.OnFixedUpdate`, после `TrimFlooding.Tick`), только при `Model` (ADC) и для надводных кораблей с точными точками (`DeckCorners.PointsOf(unit).Exact`; иначе — старые правила).
* Точки теперь считаются для любого корабля, не только выбранного: `DeckCorners.PointsOf` — кэш `ConditionalWeakTable<unit, ShipPoints>` (`Exact`, `Points`, `ShipT`, `Hull`), считается при первом тике/выборе; `Select` берёт данные из кэша. Если корабль ещё не готов (нет obp/transform) — не кэшируется, повтор в следующий тик.
* Привязка точка ↔ отсек: игра — порт = −x, штарборт = +x, секция 0 на носу (+z); у наших точек секции идут от кормы, поэтому сопоставление по `FloodingCompartment._object.transform.localPosition`: та же сторона (знак x), ближайшая по z.
* Правило (все 10 отсеков, независимо от повреждений): глубина = (`FloodingCompartment._currentWaterLevel` − мировой y точки) × 67.2 м. Пока глубина > 0 — таймер в ИГРОВЫХ секундах (`GameTime.fixedDeltaTime`, на ускорении времени растягивается игрой, поэтому «10 с» = 10 игровых); всплыла — таймер сбрасывается. После `Delay = 10 с`: приток `Rate · глубина` %/с (`[Sinking] SubmergedFloodingRate`, умолч. 0.1 % за с на метр; Base Game = 0 — как в игре) прибавляется прямо к `_currentFlooding`, мимо `_floodingRate` — DC его не затыкает (откачка уменьшает воду, но приток идёт, пока точка под водой).
* Перелив: если отсек полон (`_currentFlooding ≥ _maxFlooding`), остаток притока идёт в соседний не полный с самой низкой палубной точкой (соседи: секции до/после на том же борту и тот же номер другого борта); если сосед тоже полон — цепочка продолжается от группы полных. Перелив касается только этого притока (приток через пробоины и от крена/дифферента — как раньше).
* Окно: Settings → Damage Model → Flooding → «Deck edge under water». Подсказки стрелок Flooding (`FloodCause`): «Deck edge N m under water for t of 10 s», «…: +x %/с (0.1 %/с per m)» (+ «full: goes to Port 3»), у принимающего — «Overflow from Starboard 2: +x %/с».
* Не проверено в игре: что `_currentWaterLevel` — мировая высота волны у точки отсека (выглядит так по `ReadWaveSampleHeights`); на волнении точка может «мигать» над водой, и тогда 10-секундный счёт сбрасывается — по ТЗ «подряд».

## 2026-10-14: один источник «через борт» + цифры уровней
* Слишком много источников в подсказке (trim + палубная точка + перелив): для кораблей, где работает `SubmergedFlooding` (`Covers`: ADC вкл., `SubmergedFloodingRate > 0`, точные точки, надводный), старое затопление от крена/дифферента (`TrimFlooding.FloodList` и трим-часть `Tick`) НЕ применяется, а строки «List/Trim … water over deck» в подсказках скрыты (`FloodsNow` → false). Остальные корабли и Base Game (rate 0) — по-старому. Гейт «не sinking/capsized» и пороги `Limits.ListFlood/TrimFlood` прежних правил не менялись.
* Отладочная отрисовка точек (`DeckCorners.Model`): материал линий — `Hidden/Internal-Colored` с `_ZTest = Always`, `ZWrite` off, очередь 4000 → линии видны сквозь поверхность моря (резерв: Sprites/Default, тогда без «сквозь»). Рядом с каждой точкой — высота над волной в метрах («+1.2» зелёным, «-0.4» красным): мелкие цифры как набор отрезков (7-сегментные глифы, один динамический Mesh `Lines` в `ADM_DeckCorners/levels`), всегда лицом к `Camera.main`, высота ≈ 1.2% расстояния до камеры. Шрифт/TextMesh не нужны.

## 2026-10-14: подсказки стрелок не мерцают
* Причина: строки Hull/Flooding/Fire перерисовывались целиком (`Inlines.Clear()` + новые стрелки) при любом изменении текста (проценты растут постоянно) — вместе со стрелкой пересоздавалась и её подсказка, открытая подсказка закрывалась и открывалась снова.
* Теперь `Draw(tb, ref Drawn, force, List<Seg>)` (`DamageControlUi.cs`): строка описывается списком кусков (`Run_`/`Arr_`); пока структура та же (те же стрелки: направление, число, цвет) — меняются только `Run.Text`/`Foreground` на месте, стрелки и их `LiveTip` остаются. Пересоздание — при смене структуры (стрелка появилась/исчезла/сменила вид), при перезаписи текста игрой (`tb.Text != Shown`) и по сбросу (`*Sig == null`, как раньше). `*Sig` теперь просто «ok»/null-флаг, состояние — в `Col.HeaderD/FloodD/FireD`.
* Остаточно: сама стрелка пересоздаётся, когда меняется её вид (две стрелки ↔ одна, вверх ↔ вниз) — подсказка в этот момент закроется.
