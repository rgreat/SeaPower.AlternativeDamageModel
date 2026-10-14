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
* [General] ShowDeckCorners (debug, по умолчанию выкл., «Debug visualisation» на вкладке Damage Model): для корабля,
  выбранного сейчас в панели Unit Status, рисует 4 отрезка от текущей волны до оценённой высоты палубы в каждом углу
  корпуса (нос/корма × борт). Коллайдер — строго корпус: у всех проверенных кораблей (кашин, свердлов, шершень, альфа,
  кривак1 — включая китбаш-детали из krivak2) куски корпуса в ini [Colliders] названы coll_hull*, а надстройки/мачты/
  башни/радары и т.п. — иначе; то же самое сравнение имён (точное, затем по префиксу) уже делает сама игра для coll_ph.
  Ключ AssetBundleMeshHullCollider в [Models] кодом игры не используется вообще (проверено по всему дампу) — не подошло.
  В реальности живые MeshCollider'ы корпуса читаются не из ini, а из словаря ObjectBaseParameters._meshCollidersNames
  (ключи — те же имена) через рефлексию (MeshCollider — в UnityEngine.PhysicsModule, не подключенном к мод; sharedMesh
  читается рефлексией, остальное — через Component). Их общий Bounds считается в локальных координатах
  Compartments._compartmentsTransform (та же система, где игра сэмплирует волны для плавучести) — один раз при выборе
  корабля (SwitchUnit у SeapowerUI.ViewModels.UnitStatusViewModel, патч postfix — работает независимо от того, открыта
  вкладка Damage Control или нет); 4 угла верхней грани переводятся в мировые координаты каждый кадр через текущую
  localToWorldMatrix (значит, следуют курсу/качке/крену корабля). Высота волны в каждой точке —
  Ocean.Instance.QueryWaves(x,z,true) через рефлексию (класс Ocean не лежит ни в Seapower-Scripts, ни в подключенных
  UnityEngine-модулях — ищется по имени среди всех загруженных сборок). Линии (LineRenderer, 4 отдельных отрезка) —
  зелёные, если угол палубы выше волны, красные, если волна его заливает. Если у корабля не нашлось ни одного
  coll_hull* — линии для него просто не рисуются (предупреждение в лог один раз на корабль), ничего не угадывается по
  другой геометрии.
* Баг (после проверки в игре): кормовые точки оказались заметно выше, чем видно на 3D-модели — сам Bounds считался
  правильно, но высота всех 4 углов бралась из ОДНОГО общего максимума Y по всему корпусу разом (bMax.y), а не по
  месту. У большинства корпусов нос заметно выше кормы (бак/sheer), так что общий максимум почти всегда даёт высоту
  бака и для кормы тоже — отсюда и разница. Исправлено: вместо одного совмещённого Bounds на весь корпус теперь
  хранится Bounds каждого кусочка корпуса отдельно (`PieceBoxes`), и высота в точке z берётся только у кусков,
  которые там реально есть по длине (`TopAt` — если есть несколько пересекающихся по z, то максимум среди них; если
  ни один не покрывает z, то у ближайшего). Нос и корма теперь получают свою собственную высоту.
* [General] ShowHullCollider (debug, по умолчанию выкл., там же в «Debug visualisation»): для выбранного корабля
  рисует сами коллайдеры корпуса (coll_hull*) как полупрозрачный оверлей — те же меши, что нашёл ShowDeckCorners,
  в виде MeshFilter/MeshRenderer (полупрозрачный материал, Sprites/Default), объект-ребёнок прямо на transform'е
  каждого коллайдера (сам следует за кораблём, без ручного пересчёта каждый кадр). Позволяет наглядно сравнить
  коллайдер с видимой 3D-моделью — например, чтобы отличить ошибку в расчёте высоты от реального несовпадения
  коллайдера с моделью.
* ShowDeckCorners: добавлены 2 центральные точки (по осевой линии, X=0) — нос и корма, тем же способом, что крайние
  (борт). Итого 6 отрезков: нос лево/центр/право, корма лево/центр/право.
* Уточнение от пользователя: «по центру» в прошлый раз подразумевалось иначе — не осевая линия, а 2 точки по бортам
  на середине длины корабля (для анализа крена). Добавлены как ещё 2 точки (midships port/starboard), высота через
  тот же TopAt на z = середина между носом и кормой; две уже добавленные осевые точки (нос/корма по центру) оставлены
  — тоже полезны. Итого 8 отрезков: нос лево/центр/право, корма лево/центр/право, миделя лево/право.
* Баг: ShowHullCollider не работал вообще (оверлей не был виден). Причина: у всех проверенных кораблей включён
  UseKinematicCollision=True, из-за чего игра после загрузки деактивирует ВЕСЬ родительский GameObject, в котором
  лежат неки-нематические coll_hull* коллайдеры (_meshCollidersParent.SetActive(false)) — кинематическую коллизию
  ведёт отдельная система. Оверлей был сделан ребёнком именно этого (неактивного в иерархии) transform'а коллайдера,
  поэтому activeInHierarchy==false и MeshRenderer не рендерится, даже если сам компонент включён. Исправлено: оверлей
  теперь делается ребёнком Compartments._compartmentsTransform (тот же всегда активный transform, на котором стоят
  точки ShowDeckCorners), а мировые позиция/поворот/масштаб коллайдера копируются на него один раз в момент создания
  (SetPositionAndRotation + lossyScale) — Unity правильно считает мировые трансформы даже у неактивных объектов, так
  что копия получается верной, а дальше просто едет вместе с кораблём как обычный активный потомок.
* Баг: «центральные точки часто (но не всегда) показываются слишком высоко». Причина: TopAt брал максимум Y по ВСЕЙ
  bounding box куска корпуса, который покрывает нужный z — это точно для буквального угла коробки, но для любой
  другой (x, z) внутри того же куска (особенно у сужающегося носа/кормы или по самой осевой линии) сильно завышает
  высоту, так как реальная поверхность мели там ниже, чем верх bbox. Исправлено: высота и ширина теперь считаются по
  настоящим треугольникам сетки, а не по bbox. `HullTriangles` один раз переводит вершины всех coll_hull* кусков в
  локальную систему корабля; `HeightAt(x, z)` барицентрическим тестом в проекции XZ находит все треугольники,
  накрывающие точку, и берёт максимум интерполированного Y среди них (TopAt/PieceBoxes остались только как резерв —
  если ни один треугольник не покрывает точку, что может быть в реальном разрыве между кусками). `WidthAt(z)`
  аналогично даёт настоящий левый/правый край корпуса на данном z (min/max X среди вершин треугольников, чей диапазон
  по Z захватывает z), сужаясь к носу/корме вместо постоянной общей ширины корпуса; резерв — общий beam корабля.
* ShowDeckCorners: добавлено ещё 10 пар точек (борт/борт), равномерно распределённых по длине корпуса между носом и
  кормой (помимо уже существующих носовой тройки, кормовой тройки и миделя). Итого теперь 28 отрезков: нос
  лево/центр/право, корма лево/центр/право, миделя лево/право, плюс 10 промежуточных станций лево/право — высота и
  ширина каждой станции считаются той же точной сеточной выборкой (HeightAt/WidthAt), что и остальные точки.
* Настройки: добавлена новая вкладка «Debug» (видна всегда, даже если Alternative Damage Control выключен — как и
  User Interface/Visual Effects/Damage Model). В неё перенесены с вкладки Damage Model все отладочные пункты: группа
  «Logging (BepInEx log)» (LogShipIntegrity, LogFires, Debug trace) и переименованная группа «Debug visualisation» →
  «Visualisation (selected ship)» (ShowDeckCorners, ShowHullCollider). На вкладке Damage Model из этого раздела
  остался только переключатель самой модели (Alternative Damage Control).
* Разбор wp_ptg_komar.ini (пользователь сообщил, что некоторые точки ShowDeckCorners всё ещё показываются заметно
  выше видимой модели): у этого катера всего ОДИН coll_hull-коллайдер (MeshCollider2=coll_hull →
  wp_ptg_komar_coll_hull), не разбитый на куски, как у больших кораблей, и в [Submodels] у него нет отдельной рубки/
  кабины — то есть рубка на этом катере, судя по всему, смоделирована как часть того же самого меша, что и корпус.
  Фильтр по имени (coll_hull*) отрабатывает правильно — других совпадений нет (coll_collider, coll_ssm1/2, coll_ss
  не подходят под префикс). Похоже, не код ошибается, а сам коллайдер игры в этом случае не ограничен одним
  корпусом без надстройки — нужно сравнение через уже существующий ShowHullCollider (полупрозрачный оверлей самого
  коллайдера), чтобы увидеть, совпадает ли высота оверлея с высотой линий ShowDeckCorners на этом катере — пока не
  подтверждено пользователем, доработка кода отложена до результата сравнения.
* Баг: панель выбора профиля («Settings profile») пропала с вкладки Damage Model после переноса Logging/
  Visualisation на новую вкладку Debug. Причина: вставка `_profRow` в `ShowTab` срабатывала только когда ГРУППА
  следующего поля отличалась от "Damage model" (т.е. только перед началом следующей группы на той же вкладке) — а
  после переноса "Damage model" стала единственной группой на вкладке Damage Model, так что это условие больше
  никогда не срабатывало. Исправлено: та же вставка теперь повторяется и после цикла по полям, на случай если
  "Damage model" осталась последней (и единственной) группой вкладки.
* Размер окна настроек увеличен на 10 пикселей по ширине и высоте (1040x990 → 1050x1000), с учётом новой вкладки
  Debug.
* Баг (подтверждён, не особенность ассета): «некоторые точки ShowDeckCorners всё ещё выше видимой модели» —
  причина была именно в промахе HeightAt мимо сетки. Если ни один треугольник не накрывает (x, z) барицентрически
  (WidthAt мог взять X ровно на граничной вершине сетки, либо в меше есть шов/дыра), прежний код сразу откатывался
  на TopAt — максимум Y по bounding box всего куска корпуса. На кораблях с несколькими кусками соседний кусок редко
  оказывался намного выше, so промах был малозаметен; на однокусковых корпусах (komar и, видимо, большинство малых
  катеров — у них весь corpus, включая рубку, может быть одним coll_hull-куском без разбивки) TopAt вырождается в
  «глобальный максимум высоты всего корпуса одинаковый для любых x,z» — то есть именно подозрение пользователя:
  промах почти всегда даёт самую высокую точку корпуса целиком. Исправлено: добавлен промежуточный, более точный
  резерв `NearestHeight` — при промахе ищется ближайшая по XZ-расстоянию точка на самой сетке (перебор всех рёбер
  треугольников, проекция запроса на отрезок, интерполяция Y вдоль него), то есть высота берётся у РЕАЛЬНО
  ближайшей части поверхности, а не у случайного, возможно далёкого и намного более высокого места. TopAt остался
  самым последним резервом — только если у меша вообще нет треугольников, до которых можно измерить расстояние.
* [General] HideShipModel (debug, по умолчанию выкл., «Visualisation (selected ship)» на вкладке Debug): прячет
  3D-модель выбранного корабля (все MeshRenderer в его иерархии, найденные заново при каждом включении — не
  кешируются между кадрами, т.к. состав сабмоделей может меняться от урона/замены вооружения), чтобы
  ShowDeckCorners/ShowHullCollider было видно без самого корпуса. При выключении восстанавливает именно то
  состояние .enabled, которое было у каждого рендерера непосредственно перед включением — а не всегда "true",
  чтобы не включить то, что сама игра прячет по другой причине (урон, HideWithActiveDamageModel и т.п.). Так же
  восстанавливается при смене выбранного корабля — иначе предыдущий корабль остался бы невидимым навсегда.
* Баг: HideShipModel прятал заодно и полупрозрачный оверлей ShowHullCollider. Причина: оверлей — тоже MeshRenderer,
  и он висит на _shipTransform, который сам является частью иерархии того же GameObject корабля, что сканирует
  HideShipModel — так что GetComponentsInChildren<MeshRenderer> неизбежно подхватывал и его. Исправлено: список
  созданных оверлеем объектов (_colliderOverlays) теперь явно исключается из списка «спрятать».
* Отладочные переключатели визуализации (ShowDeckCorners, ShowHullCollider, HideShipModel) теперь применяются сразу
  при клике на чекбокс, без нажатия Apply/OK — раньше они, как и остальные Global-настройки, попадали только в
  промежуточный словарь _globals и писались в реальный ConfigEntry лишь при Commit (Apply/OK). Для этих трёх ключей
  Set в SettingsWindow теперь сразу же, в момент клика, пишет значение напрямую в их ConfigEntry
  (SetSerializedValue) — дальше это как обычное изменение файла настроек: срабатывает Cfg.SettingChanged →
  AdmPlugin.ReadSettings(), и DeckCorners получает новое значение в тот же кадр. Остальные Global-настройки
  (логирование, профиль) по-прежнему ждут Apply/OK, как и раньше.
* Баг (по скриншоту с ShowHullCollider): точки port/starboard иногда заметно уходили за край коллайдера (особенно
  заметно на простых, малополигональных коллайдерах вроде wp_ptg_komar — длинные пологие треугольники). Причина:
  `WidthAt` брал min/max X среди ВЕРШИН треугольников, чей Z-диапазон захватывает нужный z — но вершина длинного
  треугольника может находиться далеко от z запроса вдоль его же наклонной грани, так что X вершины не совпадает с
  X реального края корпуса именно в этой точке. Исправлено: добавлен `SectionAt` — честное сечение сетки меша
  плоскостью z = запрос: для каждого ребра треугольника, которое фактически пересекает эту плоскость, X и Y
  интерполируются ТОЧНО в точке пересечения (а не берутся от вершины); самое левое/правое пересечение — это и есть
  порт/старборд с уже точной высотой в этой же точке (отдельный вызов HeightAt для них больше не нужен). WidthAt
  остался резервом только на случай, если в данном z вообще нет пересечения (не должно происходить внутри
  [zBow, zStern], но на всякий случай оставлено).
* Уточнение от пользователя (по скриншотам с ShowHullCollider — точки местами ниже видимой верхней кромки
  коллайдера, "рваная" линия): предыдущий SectionAt брал самое левое/правое пересечение сечения меша по z вообще
  без учёта высоты — а если сечение корпуса в этом месте шире НИЖЕ ватерлинии (или где-то ещё не у самого верха),
  самая крайняя по X точка может оказаться там, а не у верхней кромки борта. Заменено на TopEdgeAt: среди всех
  пересечений сечения меша по z, лежащих на нужной стороне (x<=0 — порт, x>0 — старборд), берётся то, у которого
  высота МАКСИМАЛЬНА — то есть именно верх борта с этой стороны (аналог "провести рукой вверх по борту до самого
  верха"), а не просто самая широкая точка сечения на любой высоте. WidthAt+HeightAt остались резервом для z без
  пересечений вообще.
* Баг (по скриншоту): после перехода на «брать пересечение с максимальной высотой» часть точек оказалась ВНУТРИ
  корпуса, по центру палубы, а не у борта. Причина: если верх корпуса в сетке не идеально плоский (даже небольшая
  погибь/погиб палубы к центру или просто неровности меша), какая-то внутренняя точка палубы может оказаться по Y
  чуть ВЫШЕ, чем сам край борта — и «максимум по высоте без учёта X» вытягивает точку внутрь. Заменено на двухшаговый
  DeckCornerAt: сначала ищется настоящая крайняя по X точка сечения на этой стороне (без учёта высоты — как в самой
  первой версии); затем среди пересечений в узкой полосе вдоль этого X (`beamTol` — ~3% от общей ширины корпуса,
  чтобы не зависеть от масштаба конкретного корабля) берётся самая высокая — то есть именно угол «борт встречается с
  палубой», а не произвольная точка где-то на одной из двух характеристик (ширина ИЛИ высота) по отдельности.
* Корневая проблема (по лог-строкам BepInEx, присланным пользователем для civ_ms_andizhan): весь подход на основе
  чтения вершин/треугольников меша (`HullTriangles` → `DeckCornerAt`/`WidthAt`/`HeightAt` и т.д., все версии выше)
  в принципе не работает для кораблей, у чьего коллайдера корпуса в настройках импорта выключен Read/Write
  (`Mesh.isReadable == false`) — `Mesh.vertices`/`Mesh.triangles` в этом случае бросают `UnityException`
  ("Not allowed to access vertices/triangles... isReadable is false"), `HullTriangles` ловил это исключение молча
  и отдавал пустой список треугольников, так что для таких кораблей всё тихо деградировало до грубого запасного
  варианта (`TopAt`, по боксам). Это не баг конкретного алгоритма, а архитектурная проблема всего способа.
* Отброшенная идея пользователя (до реализации): идти от борта к центру с шагом, в каждой колонке пускать луч
  сверху вниз и искать точку, где пересечение с коллайдером происходит ровно 2 раза. Пользователь сам же её
  отклонил: "пухлый" выпуклый борт ниже реального угла даёт точно такое же количество пересечений (2), так что
  одно их количество не отличает истинный угол палубы от более низкой точки борта — код для этой идеи не писался.
* Новый алгоритм (заменяет весь подход на чтении меша): высота и порт/старборд теперь ищутся через
  `Collider.Raycast` — рейкастом непосредственно по коллайдеру, без обращения к Mesh.vertices/triangles вовсе
  (поэтому не зависит от isReadable). Для центральной точки — один луч вертикально вниз в x=0 (`CentreHeightAt`).
  Для порт/старборд — идея из присланной пользователем картинки (историческая гравюра сечения корпуса, синие
  диагональные стрелки): искомая точка — не "самая широкая" (может попасть на выпуклость борта ниже ватерлинии) и
  не "самая высокая" (погибь палубы может утащить её к центру) по отдельности, а точка с минимальным расстоянием
  до прямой, наклонённой на угол θ от вертикали (т.е. точка сечения корпуса, максимально выступающая в направлении
  "наружу и вверх" одновременно) — физически это и есть угол, где борт встречается с палубой. Реализовано как
  `DeckCornerAt`: ряд параллельных лучей, все наклонённые на тот же угол θ (идут вниз-и-внутрь), от ряда колонок от
  внешней кромки борта (с запасом за габариты корпуса) до центральной линии; на каждую колонку — один луч, берётся
  первое попадание (`RaycastFirst`, один `Collider.Raycast` на каждый кусок корпуса, ближайшее из всех попаданий);
  из всех попаданий по всем колонкам выбирается то, чья проекция на направление "наружу+вверх" максимальна — это и
  есть угол. Угол θ = 30° (по картинке пользователя). `Collider`/`Ray`/`RaycastHit` резолвятся через рефлексию
  (`ResolveType` пробует UnityEngine.PhysicsModule, затем CoreModule, затем перебирает все загруженные сборки) —
  если это не удалось, фича просто откатывается на `TopAt` (по боксам) для каждой точки, без отказа инициализации.
  Удалён весь код, работавший с вершинами/треугольниками меша напрямую: `Tri`, `HullTriangles`, `HeightAt`,
  `NearestHeight`/`TryEdge`, `WidthAt`, прежний двухшаговый `DeckCornerAt` и `TryCrossing`/`_crossX`/`_crossY`.
  `PieceBox`/`PieceBoxes`/`TopAt` (работают только с `Mesh.bounds`, не требуют isReadable) и весь код отрисовки
  оверлея (`BuildColliderOverlays`, рендерит меш через `MeshFilter.sharedMesh` — это тоже не требует isReadable)
  не трогались.
* Баг (сразу после перехода на Collider.Raycast): "срабатывает только fallback" — на деле попадало вообще везде,
  потому что `Collider.Raycast` отдаёт результат только когда GameObject коллайдера (и КАЖДЫЙ его предок по
  иерархии) активен, а сам компонент Collider включён — а с UseKinematicCollision (на всех проверенных кораблях)
  игра деактивирует именно ту иерархию, где сидят некинематические coll_hull*, передавая столкновения отдельному
  кинематическому коллайдеру — ровно та же деактивация, из-за которой раньше был невидим оверлей ShowHullCollider.
  Поэтому каждый луч в свипе молча промахивался мимо всего, и LocalPoints просто всегда падал в TopAt. Исправлено:
  `ActivateForRaycast`/`RestoreAfterRaycast` — перед самим свипом (один раз на выбор корабля, не на кадр) находит
  у каждого coll_hull*-коллайдера все неактивные предки по иерархии и включает их, плюс включает сам компонент
  Collider, если он был выключен, запоминая только то, что реально поменялось; после свипа всё возвращается в
  точности как было — обёрнуто в try/finally, так что любое исключение внутри свипа всё равно не оставит коллайдер
  навсегда включённым.
* Баг (по присланному trace-логу): в самом логе — `WARN Deck corners: Collider.Raycast not found by reflection`,
  сразу при инициализации мода. Причина: `ResolveType`/`Type.GetType` с именем сборки `UnityEngine.PhysicsModule`
  искал её либо по явному имени (не сработало — видимо, имя/версия сборки не совпадает с ожиданием), либо среди
  уже загруженных в AppDomain сборок (`AppDomain.CurrentDomain.GetAssemblies()`) — а `Init()` отрабатывает прямо
  при старте BepInEx, когда ни один корабль и ни один его коллайдер ещё не существует, то есть physics-модуль
  Unity в домен ещё никто не подгружал. Поэтому поиск по имени всегда возвращал null, и фича целиком откатывалась
  на bounds-only `TopAt`, что и выглядело как "вообще нет эффекта". Исправлено: резолвинг `Collider`/`Ray`/
  `RaycastHit` перенесён из `Init()` в новый `EnsureRaycastReflection` — вызывается лениво, один раз, при первом
  реальном выборе корабля (из `LocalPoints`), когда `hull[0]` — уже существующий живой экземпляр `MeshCollider`
  (сборка physics-модуля к этому моменту гарантированно загружена, иначе такого экземпляра просто не было бы).
  Тип `Collider` находится не по имени сборки, а подъёмом по `BaseType` от реального типа `hull[0]` до типа с
  именем "Collider"; метод `Raycast(Ray, out RaycastHit, float)` — по форме параметров (3 параметра, средний —
  by-ref, последний — float), а типы `Ray`/`RaycastHit` берутся прямо из параметров этого метода, а не угадываются
  по имени отдельно.
* Баг (по следующему trace/LogOutput): предыдущий фикс действительно нашёл `Collider` и метод `Raycast` (это
  предупреждение из лога исчезло), но следом сразу вылезло новое: `WARN ... Ray constructor or RaycastHit.point
  not found`. Причина: `RaycastHit.point` в Unity — это НЕ публичное поле, а C#-свойство (Unity оборачивает
  члены RaycastHit, упакованные под native-интероп, именно в свойства) — поэтому `GetField("point")` всегда
  возвращал null. Исправлено: теперь пробуются оба варианта (`GetField`, а если не найдено — `GetProperty`), и
  в `RaycastFirst` используется тот, что реально нашёлся.
* Баг (по сообщению "нет, всё также" — при этом в логе уже не было ни одного warning про Collider.Raycast/
  RaycastHit.point): настоящая причина — `Collider.Raycast` всегда работает в МИРОВЫХ координатах, а все
  x/y/z, которыми строился свип (z станций, outerX, topY), — в локальной системе координат shipT (именно в ней
  LocalPoints возвращает все 28 точек, и именно к ней каждый кадр в Tick применяется текущее положение/курс/
  крен/тангаж корабля). Из-за этого каждый луч стартовал и летел не там, где на самом деле стоит корабль в мире
  — и просто ПРОМАХИВАЛСЯ мимо всего, без единой ошибки или warning (выглядело как "fallback", хотя на самом деле
  reflection отработал нормально — просто сам рейкаст физически мимо). Исправлено: `DeckCornerAt`/`CentreHeightAt`
  теперь принимают `shipT` и переводят точку старта и направление луча в мировые координаты (`shipT.TransformPoint`/
  `TransformDirection`) непосредственно перед вызовом `Collider.Raycast`, а точку попадания — обратно в локальные
  (`shipT.InverseTransformPoint`) перед тем, как её использовать — весь остальной код (и возвращаемые из
  LocalPoints точки) остаётся в локальной системе координат, как и раньше.
* По просьбе пользователя (после скриншотов — часть точек визуально "внутри" профиля корпуса, трудно понять,
  действительно ли это ошибка или просто ракурс/проекция): добавлен wireframe поверх полупрозрачного оверлея
  ShowHullCollider — яркие жёлтые линии по РЕАЛЬНЫМ рёбрам треугольников коллайдера (`BuildWireframeMesh`:
  уникальные рёбра из `mesh.triangles`, собранные в один меш с топологией `MeshTopology.Lines`, тем же
  трансформом, что и заливка). Это даёт возможность визуально сверить, насколько грубый/низкополигональный
  коллайдер у конкретного корабля (длинные пологие треугольники, как у wp_ptg_komar) и куда именно по отношению
  к реальным рёбрам ложится каждая точка ShowDeckCorners. Чтение mesh.vertices/triangles для рёбер — на CPU,
  то есть подвержено тому же isReadable == false, что и раньше; обёрнуто в try/catch в `BuildWireframeMesh` так,
  что при ошибке просто не рисуется wireframe для конкретного куска корпуса (заливка всё равно рисуется, т.к. она
  не трогает vertices/triangles вовсе) — новая фича не ломает уже рабочую часть.
* Баг (по скриншоту с попарными стрелочками "где легла / где должна быть", wireframe уже включён): точки
  port/starboard на части станций цеплялись за внутренний уступ палубы (бак/разрыв палубы ближе к центру), а не
  за реальный борт — несмотря на то, что он заметно ближе к центру, он оказывался ВЫШЕ борта, и в скалярном
  произведении на направление 30°-наклона "вверх" перевешивало "наружу". Заменено на двухпроходный алгоритм
  (как в самой первой, mesh-based версии, но теперь через рейкасты): первый проход — ряд ГОРИЗОНТАЛЬНЫХ лучей
  от внешней границы габаритов внутрь к центру на разных высотах (от верха до низа корпуса с запасом) — раз луч
  стартует снаружи всего корпуса, первое, что он встретит, гарантированно настоящая внешняя обшивка, а не
  утопленная внутрь деталь (уступ палубы физически не может оказаться ПЕРВЫМ, что встретит такой луч, если он не
  выступает НАРУЖУ за борт, чего разрыв палубы не делает); среди всех попаданий по высотам берётся самое крайнее
  по X — это и есть истинный борт в этом z. Второй проход — среди попаданий в узкой полосе возле этого X берётся
  самое высокое — угол, где борт встречается с палубой. Внутренний уступ (если он действительно внутри, а не
  выступает за борт) просто никогда не попадёт в выборку первого прохода, поэтому не может быть выбран вторым.
  `DeckCornerAt` теперь принимает `yTop`/`yBottom` вместо одного `topY`, и `beamTol` (как в исходной версии).
* По просьбе пользователя (двухпроходный рейкаст "лучше, но недостаточно точно" — точки должны лежать именно
  на гранях полигонов, по верхнему контуру): вернул точный, аналитический способ размещения точек — он же был в
  самых первых версиях (round 7-9), но теперь это ОСНОВНОЙ путь, а не единственный. `TryHullTriangles` читает
  mesh.vertices/triangles каждого куска корпуса (как и раньше, может бросить исключение на isReadable == false);
  если чтение прошло успешно для ВСЕХ кусков — используются точные `DeckCornerAtMesh`/`HeightAtMesh` (точка строго
  на реальном ребре треугольника, через линейную интерполяцию по месту пересечения плоскости z, а не рядом с ним).
  Если чтение хотя бы одного куска провалилось (сейчас известен только civ_ms_andizhan) — для ВСЕГО корабля
  (не только этого куска) используется прежний физический свип (`DeckCornerAtRay`/`CentreHeightAtRay`) как раньше,
  чтобы не получить смесь точных и неточных точек на одном корпусе. Таким образом, подавляющее большинство
  кораблей с читаемым коллайдером снова получают математически точные точки на рёбрах, как в самых первых версиях,
  а физический рейкаст остаётся только настоящим резервом для тех редких кораблей, где mesh.vertices бросает
  исключение.
* По запросу пользователя: изменена раскладка точек ShowDeckCorners. Было 28 точек (нос/корма - порт/центр/
  старборд, мидель - порт/старборд, плюс 10 произвольных равномерно распределённых пар портов/стартбордов).
  Стало: 2 точки на продольной оси (нос, корма - только центр, без угловых порт/старборд точек там) + по одной
  паре порт/старборд на каждую секцию корабля в его модели повреждений (`Compartments._compartmentsCount` -
  обычно 5, итого 10 точек), каждая на своей собственной центральной z-координате. Игра не хранит для каждой
  секции отдельную 3D-геометрию (только их количество и порядок нос->корма — те же `_compartmentsCount`/
  `_portCompartments`/`_starboardCompartments`, которыми уже пользуются TrimFlooding/DcTeams/DcControl/
  DamageControlUi), поэтому «центр секции» взят как равная доля физической длины корпуса (zBow..zStern), поделенной
  на фактическое число секций этого корабля — не захардкожено "5", чтобы не разъехаться с кораблём, у которого
  секций окажется другое количество. Добавлен `Sections(comp)` (читает `_compartmentsCount` через рефлексию,
  по умолчанию 5, если поле не нашлось). Так как количество точек больше не константа, пул LineRenderer'ов
  (`_lines`) теперь пересобирается заново при каждом выборе корабля (`RebuildVisual`), а не строится один раз.
  Смысл: эти точки дальше будут использоваться, чтобы определять, черпает ли корабль бортами воду по каждой
  секции отдельно.
* Правка по явному указанию пользователя: число секций вернули из динамического чтения обратно в константу.
  У корабля ВСЕГДА ровно 5 секций в модели повреждений (и в каждой по 2 отсека, порт/старборд) — это фиксированный
  архитектурный факт игры, а не что-то, что нужно читать индивидуально для каждого корабля. Убраны `_fCompCount`
  (поле `Compartments._compartmentsCount`, резолвилось в `Init`) и метод `Sections(object comp)`/`DefaultSections`;
  вместо них — `const int Sections = 5;` и `const int PointCount = 2 + Sections * 2;` (= 12: нос + корма + 5 пар
  порт/старборд). `LocalPoints` больше не принимает параметр `sections` — использует константу напрямую.
  `RebuildVisual` оставлен как есть (пересобирает пул `LineRenderer` при каждом выборе корабля) — он больше не
  обязателен, так как количество точек теперь всегда ровно `PointCount`, но безвреден и работает корректно.
* Баг (сообщил пользователь): неповреждённый короткий корабль (<50 м) тонет из-за огромного угла дифферента при
  ускорении времени x100. Разобрано: встроенный в игру "жёсткий" предохранитель — `Vessel.OnFixedUpdate`:
  `if (GameTime.TimeCompression > 100f) base.transform.eulerAngles = new Vector3(0, y, 0);` (сбрасывает дифферент
  И крен в ноль выше 100x) — мы его НЕ трогаем и НЕ отключали; условие строго ">100f", поэтому ровно на 100x (и
  везде в диапазоне 10x..100x) этот сброс не срабатывает вообще. Настоящая причина — в `TrimFlooding.cs`: при
  ускорении выше `GameTime._physicsTimeScaleCap` (10x) сама игра намеренно "накручивает" `GameTime.fixedDeltaTime`
  (`GameTime.OnFixedUpdate`: `fixedDeltaTime *= TimeCompression / _physicsTimeScaleCap`), потому что физика
  (`Time.timeScale`) зажата на 10x, а остальная симуляция (в т.ч. скорость затопления `FloodingCompartment.
  OnFixedUpdate`/`AddFloodingForHeavyListing`, использующие `GameTime.fixedDeltaTime`) должна "догонять" полную
  TimeCompression — поэтому обычный, безопасный на 1x дифферент (от хода, волнения) на 10x-100x читается
  кратно больше. В оригинальной игре это не страшно: проверка "дифферент > 8° → тонет" (`Compartments.
  OnFixedUpdate`) срабатывает ТОЛЬКО если какой-то отсек уже затоплен СВЫШЕ Minor (`CurrentFloodingType > Minor`,
  то есть больше 33% от `_maxFlooding`) — у исправного корабля это всегда ложно, так что чистый дифферент от
  раскачки сам по себе никогда не создаёт затопление из ничего. `TrimFlooding.cs` (эта же механика "дифферент
  топит борт вместо мгновенной гибели") была реализована БЕЗ этого условия — она добавляла затопление
  (`AddFloodingForHeavyListing`) как только дифферент превышал `Limits.TrimFlood(length)`, НЕЗАВИСИМО от того,
  есть ли уже затопление — ошибка, не попавшая в код при портировании правила с "крен" (`AddFloodingForHeavyListing`
  в игре тоже ничем не ограничена, но 45° крена — редкий порог, не путается с обычной раскачкой на скорости x100).
  Исправлено: добавлена `AnySectionSignificantlyFlooded` (зеркало игрового "> Minor", то есть > 33% от
  `_maxFlooding`, в любом отсеке любого борта) — `TrimFlooding.Tick` теперь, как и игра, вообще не реагирует на
  дифферент у корабля без уже существенного затопления, что убирает петлю обратной связи "раскачка → затопление
  из ничего → больше дифферента → больше затопления" для здорового корабля на любом ускорении времени.
* Отменено по явному указанию пользователя: `AnySectionSignificantlyFlooded`/гейт по существующему затоплению
  выше — рабочее решение, но неправильное по мнению пользователя: то, что "в игре штатно" завышенный дифферент
  на 10x-100x — не значит, что это правильно; нужно чинить саму ошибку расчёта от ускорения времени, а не просто
  гейтить её симптом (создание затопления из ничего). `TrimFlooding.cs` возвращён к виду до этого коммита.
  Следующий шаг: в [Damage Model] настройках добавить пункт "Fix in game Trim compute error on high time
  compression rate" (по умолчанию true) — сам механизм исправления уточняется с пользователем перед реализацией.
* Профили `Advanced.cfg` / `Base Game.cfg` обновлены пользователем (значения из игры, Unit Status -> Damage
  Control -> шестерёнка) и синхронизированы в репозиторий как есть, без интерпретации: Advanced — HullIntegrity
  Warships 2.0->5.0, Systems Integrity 1.0->2.0, весь [FireAndFlooding] (кроме PumpOutSpeed/ExtinguishSpeed),
  DamageControlTeams MusterTime/HealTime, весь [Sinking] (ListLimit 31.5->33.0, TrimLimit 5.2->4.5,
  TrimReferenceLength 100->150), и новый блок [SystemTypeIntegrity] (HeavyTurret 60 / MediumTurret 50 /
  SmallTurret 30); Base Game — только UnattendedFireGrowthRate 0.25->0.1.
* Реализован настоящий фикс ошибки расчёта дифферента от ускорения времени (вместо отменённого гейта выше),
  по выбранному пользователем варианту "компенсировать на входе — скорость затопления/раскачки, не трогая сам
  угол и не замедляя обычное затопление по времени". Новая настройка `[General] FixHighTimeCompressionTrimError`
  (по умолчанию true), в игре — вкладка "Damage Model", группа "Damage model", строка "Fix trim compute error".
  Механизм (`TrimFlooding.cs`, `FixFactor()`): выше `GameTime._physicsTimeScaleCap` (10x) игра сама растягивает
  `GameTime.fixedDeltaTime` (`fixedDeltaTime *= TimeCompression / _physicsTimeScaleCap`) — `FloodingCompartment.
  AddFloodingForHeavyListing` читает это растянутое значение напрямую, поэтому один и тот же угол дифферента
  добавляет кратно больше воды выше 10x, чем на 10x и ниже. Фикс НЕ трогает сам угол и НЕ меняет темп обычного
  боевого затопления (`FloodingCompartment.OnFixedUpdate` не патчится вообще, идёт с полным ускорением как и
  раньше) — только затопление, которое добавляет САМ `TrimFlooding.Tick` через `AddFloodingForHeavyListing`:
  игровой метод вызывается как обычно (чтобы его собственная случайность и коэффициенты не менялись), а прибавка,
  которую он только что сделал (`after - before` в `_currentFlooding`), домножается на `_physicsTimeScaleCap /
  TimeCompression` (когда TimeCompression > cap) — трюм-затопление от дифферента идёт с тем же темпом, что было
  бы на 10x, независимо от того, насколько ускорение выше. Жёсткий сброс самой игры (`Vessel.OnFixedUpdate`,
  `TimeCompression > 100f` -> дифферент/крен в 0) не трогается, остаётся как есть.
  Не реализовано (возможное расширение, не входило в текущий запрос): такое же растяжение `GameTime.
  fixedDeltaTime` действует и на раскачку корпуса на циркуляции (`Compartments.setTiltAndPitch`, `SmoothStep`
  с `3f*GameTime.fixedDeltaTime`) — это тоже вклад в дифферент/крен при ускорении, но для его патча нужен
  транспайлер на ранее нетронутый метод игры; оставлено как отдельный вопрос, если пользователь решит, что
  нужно и это.
* По запросу пользователя: перестановка вкладок/настроек окна и замедление дифферент-затопления в 10 раз
  (`SettingsWindow.cs`, `TrimFlooding.cs`, `AdmPlugin.cs`). Только отображение в окне настроек и сама формула
  затопления от дифферента — ключи .cfg-профилей (секции `[FireAndFlooding]`/`[Sinking]`) не переименовывались.
  - `Tabs`: было `..., "Fire & Flooding", "DC Teams", "Sinking"`, стало `..., "Fire", "Flooding", "DC Teams"` —
    "Fire & Flooding" -> "Fire", "Sinking" -> "Flooding", и "Flooding" теперь сразу после "Fire" (была последней).
  - "Water coming in" (`FireAndFlooding/FloodingSpeed`) перенесена из вкладки "Fire" (была в группе "Flooding")
    на вкладку "Flooding" (бывшая "Sinking"), первой строкой, в новую группу "Flooding rate".
  - Новая настройка `[Sinking] DeckFloodingSpeed` (по умолчанию 1.0), в той же группе "Flooding rate" сразу
    после "Water coming in" — заголовок в окне "Water coming in due to deck flooding". Добавлена явно в оба
    встроенных профиля (`Advanced.cfg`, `Base Game.cfg`), после `TrimReferenceLength`.
  - `TrimFlooding.Tick`: раньше домножало прибавку `AddFloodingForHeavyListing` только на `FixFactor()`
    (компрессионная коррекция, 1.0 ниже 10x). Теперь всегда: `(after - before) / 10f * Speed * FixFactor()` —
    флат `/10` зашит в формулу (как попросил пользователь: "замедли... в формуле 10 раз"), `Speed` — новый
    множитель из `DeckFloodingSpeed` (по умолчанию 1.0 = тот самый уже-в-10-раз-медленнее темп), `FixFactor()` —
    прежняя коррекция по ускорению времени, применяется как и раньше, но теперь всегда вычисляется (не только
    когда < 1.0), поскольку итоговая формула в любом случае пересчитывается с нуля.
  - Проверено: содержимое `Release/Profiles/*.cfg` в зафиксированном состоянии репозитория и в последнем
    отправленном архиве совпадало (сверено распаковкой присланного zip) — жалоба "выгружаешь старые профили"
    не подтвердилась на стороне репозитория/архива в это обращение; возможная причина — более ранняя (до
    правок этого чата) копия архива на стороне пользователя.
* Ошибки Unity "Not allowed to access vertices/triangles on mesh '...coll_hull..' (isReadable is false)" в логе
  игры (пример: wp_ms_roro_b, coll_hull1/coll_hull3). Unity на нечитаемом меше НЕ бросает исключение, а пишет
  ERROR в лог игры и возвращает пустой массив — поэтому прежний try/catch ничего не ловил, и вдобавок
  `TryHullTriangles` молча считал корпус по неполному набору кусков (только читаемые). Теперь
  `DeckCorners.TryHullTriangles` и `BuildWireframeMesh` сначала проверяют `mesh.isReadable`: нечитаемый → не
  трогаем vertices/triangles вообще (в лог игры ничего не попадает), весь корабль уходит на raycast-путь,
  сообщение один раз на меш — только в лог мода (BepInEx, `Warn`).
* Настройка `DeckFloodingSpeed` в окне: "Water over the deck", пояснение "× default · from trim past the
  limit" (прежнее название не влезало в строку; упоминание о 10-кратном замедлении убрано и из UI, и из
  описания в .cfg).
* Анализ: дифферент неповреждённого корабля на ходу растёт с ускорением времени выше 10x (замер пользователя,
  wp_pt_p6, 25.5 м, 47 уз, полный ход: ≤10x 2.6°, 20x 3.3, 30x 4.1, 50x 5.5, 100x 9.1). Ряд точно линейный по
  D = TimeCompression/10: trim ≈ 1.88 + 0.72·D. Фикс `FixHighTimeCompressionTrimError` не помогает и не может:
  он трогает только затопление от TrimFlooding, а у целого корабля сам угол растёт без всякого затопления.
  Причина (по коду): `Vessel.OnFixedUpdate` → `applyMainThrust` двигает корабль не силой, а
  `transform.Translate(v · GameTime.fixedDeltaTime)` ДО `Compartments.OnFixedUpdate`; там момент считается как
  Σ Cross(точка отсека − `_centerOfGravity`, сила), где точки отсеков берутся из transform (уже сдвинутого),
  а `_centerOfGravity = _rigidbody.worldCenterOfMass` — из физического тела, которое до шага симуляции ещё в
  старой позиции (Physics.autoSyncTransforms выключен — игра везде зовёт SyncTransforms вручную). Плечо всех
  сил смещено вперёд на Δ = v·GameTime.fixedDeltaTime → лишний момент Cross(Δ, ΣF_плавучести) по тангажу.
  До 10x Δ постоянна (часть "штатного" дифферента на ходу, игра так и настроена), выше 10x
  `GameTime.fixedDeltaTime` растянут в D раз → Δ и лишний дифферент растут линейно по D. Сильнее всего у
  коротких быстрых кораблей: тот же Δ при маленьком восстанавливающем моменте. Предложение (ждёт решения
  пользователя): транспайлер в `Compartments.OnFixedUpdate`, заменить чтение `Rigidbody.worldCenterOfMass` на
  `worldCenterOfMass + _velocityVecInUnity · (GameTime.fixedDeltaTime − Time.fixedDeltaTime)` при включённой
  `FixHighTimeCompressionTrimError` — до 10x поправка ровно 0 (поведение игры не меняется), выше 10x остаётся
  та же погрешность, что на 10x → дифферент ~2.6° на любом ускорении.
* Реализовано по предложению выше (пользователь: "делай"). Новый файл `src/TimeCompressionFix.cs`: транспайлер на
  `Compartments.OnFixedUpdate` — после единственного чтения `Rigidbody.worldCenterOfMass` (в `_centerOfGravity`)
  вставлен вызов `TimeCompressionFix.CoG(com, this)`, который сдвигает центр масс вперёд на
  `_velocityVecInUnity · (GameTime.fixedDeltaTime − Time.fixedDeltaTime)`. До 10x разность ровно 0 (игра не
  меняется), выше 10x плечо сил плавучести снова ошибается ровно на шаг x10 → дифферент как на 10x.
  Только надводные (`Compartments._vessel != null`); подлодки двигаются/ныряют по своим правилам — не трогаются.
  Работает при `Enabled` (мод включён в Mods) и `[General] FixHighTimeCompressionTrimError` (по умолчанию true),
  независимо от Alternative Damage Control — это фикс ошибки самой игры. Переключение на ходу действует со
  следующего физического тика (флаг читается в `CoG` каждый вызов).
  Убрана прежняя коррекция под той же галочкой в `TrimFlooding` (`FixFactor`, домножение затопления от
  дифферента на cap/TimeCompression) — при правильном угле она не нужна. Формула TrimFlooding теперь
  `(after − before) / 10 · DeckFloodingSpeed`. Описание настройки в .cfg переписано под новый механизм.
  Проверить в игре: wp_pt_p6 на полном ходу — дифферент ~2.6° на x20/x50/x100 с галочкой, 3.3/5.5/9.1 без неё.
* Damage Control tab (по запросу пользователя):
  - Подсказка стрелки затопления: новая строка "Trim N.N° bow/stern down: water over deck", когда этот отсек сейчас
    топит TrimFlooding (`TrimFlooding.FloodsNow` — тот же выбор, что в Tick: крайний незаполненный отсек опускающейся
    оконечности, на этом борту). Строка "Min n% until welded" убрана (непонятна игроку).
  - Карточка "In reserve": больше не показывает команды, идущие в резерв (Moving с Section < 0) — они только в
    "In Transfer", как и восстанавливающиеся.
  - Процент Hull по бортам у названия секции: цвет `HullColor` — серый, когда отсек заварен до предела
    (`MaxAllowedIntegrity − _currentIntegrity` ≈ 0, `WeldedUp`), независимо от %; иначе жёлтый (≥75%, раньше
    был светло-серый CWhite), оранжевый (25–75%), красный (<25%). Разделитель " / " — серый. `Stage()` удалён.
    `RenderHeader` теперь получает fp/fs/sc и сам вызывает `InitCauses` (поля MaxAllowedIntegrity/_currentIntegrity
    раньше резолвились только при открытии подсказки).
  - README (все три): строки про цвет Hull и про подсказки обновлены.
* `[Effects] SmokeStartOpacity` по умолчанию 10 (было 0; `FireSmoke.StartOpacity` = 0.1). Уже сохранённый .cfg
  хранит своё значение — новое умолчание действует для новых установок / после сброса.
* `SaveConfig`: в AlternativeDamageModel.cfg больше нет пустых строк внутри секций (BepInEx пишет пустую строку
  после каждой записи) — только одна пустая строка перед каждой секцией.

## 2026-10-10: Hull Damage / Flooding с процентом; умолчания Advanced
* `DamageControlUi.Panels.cs`: `RenderHullFlood` (из `RenderExtras`) дописывает « (NN%)» к значениям карточки «Hull Damage / Flooding»
  (TextBlock колонки 2 в строках 0/1 внутреннего Grid; игра переписывает текст при смене свойства — мы сверяем каждый тик,
  базовое слово = текст без суффикса `StripPct`). Hull: урон = 100 − `Compartments.IntegrityPercentage`. Flooding: слово игры,
  % = `min(100, 100·FloodingPercentage/SinkAt)` — шкала LEVEL. С ADC (`ModelOn`) слова Hull по урону: 0 None, ≤15 Slight, ≤30 Moderate,
  ≤45 Heavy, выше — последнее слово игры (StructureDestroyed); берутся через `IntegrityToDamageControlConverter` (локализация игры), цвет — `StageBrush`.
  Названия не переименованы (пользователь дал «на память»). Неясно «свыше/до 60%» у critical — принято: всё выше 45%.
  `RemoveExtras` возвращает игровой текст/цвет.
* Advanced.cfg: `[HullIntegrity] Warships` 5.0→4.0 (CivilianShips 2.0 не менялся), `DCTeamEffectiveness` 0.5→3.0.
* Правка: Structure Destroyed при уроне > 75% (None <1, Slight ≤15, Moderate ≤30, Heavy ≤75); при 0% суффикс не пишется («None»).
  Flooding: слово игры, процент по LEVEL — без изменений.
* Hull Damage с ADC: None <1%, Slight ≤25, Moderate ≤50, Heavy ≤75, Structure Destroyed >75 (по урону).

## 2026-10-11: затопление от крена ×1/10; текст Flooding
* `TrimFlooding.InitList`: Harmony prefix/postfix на `FloodingCompartment.AddFloodingForHeavyListing` (игра зовёт его из
  Compartments.OnFixedUpdate для нижнего борта при крене > ListLimit): дельта `_currentFlooding` = игровая /10 × `Speed`
  (DeckFloodingSpeed) — как для дифферента. Свой вызов от дифферента (`_ownCall`) патчем не масштабируется (там своя формула). Только при ADC.
* Баг: Flooding писал только None/Slight. Причина: `TextBlock.Text = ...` вручную заменяет привязку игры — текст застывал.
  Теперь слово считаем сами через игровые конвертеры (`FloodingPercentageToDamageControlStringConverter` по `FloodingPercentage`,
  `IntegrityToDamageControlConverter` по целостности) каждый тик; то же для Hull без ADC.

## 2026-10-11: [Sinking] FloodImbalanceEffect (Flooding → «List from uneven flooding»)
* Подход (выбран rgreat): усилить только дисбаланс бортов. Плавучесть секции = F·погружение/(baseDensity + `_currentFloodingValue`)
  (Compartments.OnFixedUpdate и FloodingCompartmentsJob, оба читают свойство `_currentFloodingValue`). Harmony postfix на геттере
  `FloodingCompartment._currentFloodingValue` (`TrimFlooding.CfvPost`) отдаёт скорректированное значение: для секции i
  среднее m=(p+s)/2 сохраняется, отклонение умножается на K: p' = max(0, m+(p−m)K). Значения считаются раз в тик в `SkewTick`
  (prefix Compartments.OnFixedUpdate), «сырые» читаются под флагом `_rawRead`. Осадка/дифферент (по сумме) не меняются.
  Только ADC (`IsEnabled`), K=1 → игра без изменений. Умолчания: Base Game 1.0, Advanced 2.0 (не заказано явно — можно поменять).

## 2026-10-11: затопление от крена — в код мода; цвета карточки
* Затопление нижнего борта от крена теперь делает мод (`TrimFlooding.FloodList`, вызывается из Tick): при |крен| > Limits.ListFlood,
  не sinking/capsized — все отсеки низкого борта: игровой `AddFloodingForHeavyListing` ÷10 × Speed, как у дифферента. Игровой вызов
  отключён (`HeavyList/HeavyListNeg` при ADC возвращают ±float.MaxValue). Прежний Harmony-патч самого метода убран (мог не срабатывать
  при инлайне JIT; причина «не начинается затопление при критическом крене» не воспроизводилась офлайн — это более надёжный путь).
  Если крен > 70° — игра опрокидывает (capsize) и топит корабль своим механизмом, как раньше.
* Карточка Hull Damage / Flooding: цвета по классам — 0-2 как в игре, Heavy — оранжевый, Structure Destroyed / Flooding Uncontrolled
  — красный (игровой цвет последнего класса тёмный, плохо читался). Цвет ставится по классу и для Flooding (по значению игры 1/10/20/30),
  и для Hull без ADC (по целостности 99/75/25/1).

## 2026-10-11: FloodImbalanceEffect — баг с лишним весом воды
* Жалоба: корабль визуально тонет (и по LIST, и по LEVEL), хотя вкладка DC воды не прибавляет. Причина — мой SkewTick: при K=2 и воде только
  на одном борту (p, s=0) было p' = 2p, s' = max(0, −p) = 0 — т.е. эффективная вода в секции удваивалась (клэмп на нуле ломал сумму).
  Теперь сдвиг h = clamp((p−mean)·K, −mean, mean), p' = mean+h, s' = mean−h: сумма воды в секции сохраняется, растёт только разница.
  Других «альтернативных» механизмов затопления в игре не нашлось: при `_isSinking` (по LEVEL > FloodLimit / capsize) работает игровая логика.

## 2026-10-11: откат отключения capsize; диагностика LogHeel
* Отключение capsize (>70°) откачено по просьбе rgreat: игра при |z|>70 ставит `_capSized`+`Sink(All)` — это правильное поведение.
* Поиск «скрытого» механизма потопления при критическом крене (быстрее и без воды в отсеках): по декомпилу Sink() вызывают только
  Compartments.OnFixedUpdate (уровень >40 → SinkLevel, дифферент >8° с водой → SinkTrim, крен >70° → capsize), DestroyByExplosion
  (целостность <10%), отладочные окна и пара внешних систем (взрыв погреба/ракеты). Другого пути «воды» нет: остаётся физика
  плавучести — подъёмная сила каждого из 10 отсеков = F·доля_погружения/(baseDensity+вода), доля по высоте точки отсека (клэмп 0..1,
  `_compartmentsHeight` = полувысота bounds) — при крене высокий борт выходит из воды, низкий насыщается: суммарная сила и момент меняются
  без изменения воды. Это не механизм воды, а геометрия модели игры.
* Добавлена диагностика `[General] LogHeel` (вкладка Debug → Logging): раз в 2 с при крене ≥8° или дифференте ≥3° пишет в лог BepInEx
  крен, дифферент, высоту y, подъёмную силу/вес, vy, submerged, % воды, sinking/capsized.

## 2026-10-11: по логам LogHeel — множитель дисбаланса и скорость опрокидывания
* Лог (Kashin, крен растёт −8…−71° за ~2 мин, вода 14→29%): до 70° подъёмная сила ≈ вес, y ≈ −0.05…−0.12, vy ≈ 0 — корабль НЕ тонет от
  геометрии плавучести. После capsize (−71°, sinking+capsized) крен идёт дальше всего ~1.6°/с (до −139° и дальше), y не меняется,
  подъёмная сила 0.87–0.95 веса: корабль «лежит на боку». Значит «скрытого затопления» нет — есть медленный переворот и лежание на борту.
* FloodImbalanceEffect «ни на что не влиял»: при воде на одном борту прежняя формула (сохранение суммы воды, клэмп ≥ 0) упиралась в
  потолок — p'=p, s'=0 при любом K≥1. Теперь разница считается по ПОДЪЁМНОЙ силе стороны (1/(baseDensity+вода), формула игры), сумма сохраняется,
  сухой борт получает отрицательное «значение воды» (больше подъёма), мокрый сохраняет ≥5% суммы (`SkewTick`).
* Новый `[Sinking] CapsizeSpeed` (Flooding → «Capsize speed», Base Game 0 = как в игре, Advanced 1.0): после capsize (`_capSized && _isSinking`)
  при |крен| < 170° к корпусу прикладывается `AddTorque(forward · sign(list) · 0.05·K, ForceMode.Acceleration)` — ровное угловое ускорение
  в сторону крена (на боку момент плавучести ≈ 0, пропорциональное усиление не помогло бы). Оценка без демпфирования: ~9 с от 70° до 170° при K=1.

## 2026-10-11: откат к сборке _15 (20261010.2347)
* По просьбе rgreat (подход к крену/дисбалансу нужно менять целиком): src, профили и README возвращены к состоянию сборки _15 (коммит ec478fc:
  LogHeel есть, CapsizeSpeed и пересчёт дисбаланса по подъёмной силе — убраны). Опция «List from uneven flooding» скрыта из окна настроек, а её
  значение игнорируется (`TrimFlooding.Imbalance = 1`; ключ FloodImbalanceEffect в профилях остался = 1.0, чтобы старые профили читались).
  Идеи на потом: ускорение переворота после capsize (пост. угловое ускорение), усиление крена от дисбаланса через подъёмную силу — см. записи выше.

## 2026-10-13: раскраска статуса оружия на нижней панели (build 20261013.2039)
* Новый `src/WeaponStatusColors.cs`, опция `[UI] ColorWeaponStatus` (по умолчанию true; окно настроек: вкладка UI → «Weapon status colours»).
* Нижняя панель выбранного юнита: `ObjectBaseViewModel.ObjectEffectors` → `WeaponEntries` (`WeaponEntry`, по одной записи на тип боеприпаса;
  `WeaponSystems` — список `BaseSystem`, `BaseSystem.Inoperable.Value == true` — неисправна). Состояние: все исправны — цвет игры (ClearValue);
  часть неисправна — жёлтый; все — красный.
* Реализация: раз в секунду обход визуального дерева от `MainGameView.Instance` (бюджет 40000 узлов), ищутся `TextBlock`, у которых
  `DataContext` — `WeaponEntry`, а текст равен `Label` или `AmmoAmountText`; на них локально ставится `Foreground`. Окрашенные блоки хранятся
  в слабом списке; при выключении опции/мода все возвращаются к цвету игры.
* Вызов `Tick` перенесён в `Update()` и не зависит от `_uiReady` (вкладка Damage Control может быть выключена).
* НЕ проверено в игре: допущение, что текстовые блоки кнопок оружия имеют `DataContext = WeaponEntry` и точные строки Label/AmmoAmountText.
  Если не красит — смотреть Trace-лог (`WeaponStatusColors`).

## 2026-10-14: анализ расчёта крена — центр масс и вода (без изменений кода)
* Масса тела постоянна (`displacement/40`), вода массу НЕ добавляет. `rigidbody.centerOfMass` задаётся один раз в Init: `(0, y_среднего_отсека, z_среднего_отсека) + [Physics] CenterOfMass` из ini, и дальше трогается только при sink/capsize (`(0,0,0)` / при capsize `(0,1,0)`), у подлодок `(0,-0.5,0)`. Вес воды и свободные поверхности не смещают центр масс.
* Вода влияет ТОЛЬКО на подъёмную силу отсека: `F_i = (m·g/10)·s_i/(ρ + w_i)`, `w_i = MaxFloodingValueWhenSinking(2.0)·flooding/max`, `s_i = clamp((уровень_волны − y_i + H/2)/H, 0..1)`, `H = bounds.y·0.5`. Сила полной воды падает максимум до ρ/(ρ+2) (при ρ=0.8 — до 29%), т.е. потеря ≤71% подъёмной силы отсека, дальше эффекта нет.
* Точки приложения: 5 пар (борт/борт по z), x = ±bounds.x/2, y = trimDepth (+ z·sin(trimAngle)) — на уровне CoM по высоте (если CenterOfMass.y = 0). `AddForce(сумма)` + `AddTorque(Σ (r_i − CoG) × F_i)`, гравитация действует в CoM без момента. Следствие: момент крена даёт только разница F между бортами × плечо B, а остойчивость — только изменение s_i при крене (линейно в пределах H, затем клэмп) минус «маятниковый» член `W·c` от CenterOfMass.y (G выше точек приложения → дестабилизирует). Оценка: GM_eff ∝ B²/(ρ·H) − c.
* Оценка крена от одного полностью затопленного отсека: φ ≈ 0.07·s·H/B (рад). Одного борта целиком (5 отсеков) — в ~5 раз больше. Выше угла насыщения (бoльшой борт вышел из воды, s=0) восстанавливающий момент перестаёт расти — корабль уходит в крен без возврата. Это и есть причина «множитель дисбаланса ничего не меняет»: потеря подъёмной силы ограничена 71%, а плечо и жёсткость остаются прежними.
* На боку (≈90°) плечо x = B·cosφ → 0, момента нет, корабль «лежит» (согласуется с логами LogHeel: 0.87–0.95 веса, дрейф ~1.6°/с). После capsize CoM=(0,1,0) выше точек приложения → момент неустойчивый, медленно докатывает.
* Идея реализации: самим вести `centerOfMass.x` (и немного .y — свободная поверхность) по воде в отсеках: `dx = Σ w_i·x_i / (M + Σ w_i)`. Торк от смещения CoM = F_total·dx, не зависит от клэмпов s. Учесть двойной счёт с потерей подъёмной силы (она уже эквивалентна добавленному весу) — либо уменьшить её, либо принять усиление как множитель. Игра пишет centerOfMass только в Init/sink, т.е. конфликта нет.

## 2026-10-14: слияние с GitHub (2d94749)
* История на GitHub (rgreat/SeaPower.AlternativeDamageModel) отдельная от локальной: там после Steam Release добавлены только `configs` и «Settings window: drawn close cross, resizing by the edges» (крестик ✕ рисуется Path-ом, ресайз окна за любой край/угол). Локальные ~25 коммитов (карточки %, раскраска оружия, TimeCompressionFix, DeckCorners, LogHeel, профиль Advanced) на GitHub отсутствовали.
* Перенесено: коммит 2d94749 (исходники слились без конфликтов, dll пересобрана), `UserConfig/AlternativeDamageModel.cfg`, `.gitignore` (логи игры и cfg мода). Профили Release/Profiles оставлены локальные (в версии с GitHub Advanced.cfg имел заголовок «Custom» и другие значения FireHullDamageSpeed/DCTeamEffectiveness/FloodImbalanceEffect).
