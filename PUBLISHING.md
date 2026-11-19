# Alternative Damage Model: сборка, проверка и выкладка в Steam Workshop

> Preparing a release (version, change lists, README, translations, build, commit rules)? Read the Sea Power project doc `claude/release-howto.md` first (also: `claude/steam-workshop.md` for the Workshop ids and how to check what is published).

## 1. Структура проекта

```
AlternativeDamageModel/
├─ AlternativeDamageModel.csproj   <- сборка; после Build копирует dll в Release
├─ nuget.config                    <- пустой список источников NuGet (сборка без сети)
├─ README.BBCode.txt               <- описание для страницы Steam Workshop (BBCode)
├─ README.txt                      <- то же простым текстом (форумы)
├─ README.md                       <- то же в Markdown (GitHub)
├─ src/
│  ├─ AdmPlugin.cs                 <- сам плагин (BepInEx BaseUnityPlugin, Harmony-хуки)
│  ├─ AdmPlugin.Profiles.cs        <- профили настроек (Base Game / Advanced / свои)
│  ├─ SettingsWindow.cs / ShipNames.cs / ModuleNames.cs <- окно настроек в игре; списки кораблей и типов систем
│  ├─ DamageControlUi*.cs          <- расширенная вкладка Damage Control (Noesis): панели, подсказки, схема затопления
│  ├─ DcTeams.cs / DcControl.cs / DcAuto.cs <- команды DC, приоритеты, Auto DC
│  ├─ Limits.cs / TrimFlooding.cs  <- пороги (предел дифферента по длине); за пределом дифферента — заливает оконечность
│  └─ AnchorChainEntry.cs          <- точка входа Anchor Chain + проверка галочки в меню Mods
└─ Release/                        <- ЭТО и есть мод: содержимое папки выкладываете
   ├─ _info.ini
   ├─ AlternativeDamageModel.dll
   ├─ Profiles/                    <- системные профили *.cfg: «Base Game.cfg», «Advanced.cfg» и любые другие (только чтение)
   ├─ thumbnail.jpg                <- картинка для Workshop (JPEG 512x512, < 1 МБ)
   └─ README.md                    <- пишете сами
```

В StreamingAssets папка мода должна называться **`AlternativeDamageModel`** (не `Release`): по имени папки игра хранит
галочку мода в `usersettings.ini`, и по нему же мод проверяет, включён ли он в меню **Mods**.
Чужие dll (AnchorChain, BepInEx, 0Harmony) в папку мода не кладите. Папку `Profiles` выкладывайте вместе с dll: мод читает её
рядом со своей dll (без неё встроенные профили берутся из кода).

## 2. Сборка

```
dotnet build -c Release -p:GameDir="D:\Steam\steamapps\common\Sea Power"
```

AnchorChain.dll берётся из `<Steam>\steamapps\workshop\content\1286220\3380210757\AnchorChain.dll`
(иначе `-p:AnchorChainDll="..."`). Результат копируется в `Release\AlternativeDamageModel.dll`.

## 3. Локальная проверка

1. Скопируйте содержимое `Release` в `Sea Power\Sea Power_Data\StreamingAssets\AlternativeDamageModel\`.
2. Включите мод в меню **Mods**. Уберите IntegrityX2 ini-патчи, если они установлены (иначе удвоение применится дважды).
3. Для проверки поставьте `LogShipIntegrity = true` в `Sea Power_Data\StreamingAssets\user\AlternativeDamageControl\AlternativeDamageModel.cfg` (окно настроек → General).
4. В `BepInEx\LogOutput.log` должно быть:
   * `Alternative Damage Model 2.1.2 (build ...) loaded; follows its tick in the game's Mods menu.`
   * `Hooks installed: 29.` (без `failed`);
   * `Enabled in the game's Mods menu (AlternativeDamageModel).`
   * в миссии — строки `Hull: <ini> (Warships), <т> t, armor <...>: integrity X -> Y (x2), sinking limit ...`.
5. Снимите галочку в Mods и зайдите в миссию: `Disabled in the game's Mods menu ...`, строк `Hull:` больше нет.
6. Откройте Unit Status → Damage Control у повреждённого корабля: проценты у названий секций, значки команд DC,
   проценты и стрелки у Flooding / Fire. Если вкладка не распознана — в логе предупреждение со счётчиками найденного,
   а разметка вкладки записывается в `BepInEx/AlternativeDamageModel.dctab.txt` (один раз за запуск).
7. Шестерёнка рядом с (i) на кнопке вкладки Damage Control открывает окно настроек: выбор профиля, правка, Apply —
   в логе `Settings profile "..." in use.`; свои профили — в `Sea Power_Data\StreamingAssets\user\AlternativeDamageControl\Profiles\`.
8. Предупреждения вида `... patched, 30 expected` / `insertion point not found` означают, что игра обновилась и мод надо поправить.

## 4. Выкладка

Как у Smart Windows: Workshop-аплоадер игры, папка мода = содержимое `Release` (_info.ini, AlternativeDamageModel.dll,
thumbnail.jpg, Profiles/). Короткое описание (en/ru) — в `Release/_info.ini`; описание страницы Workshop — `README.BBCode.txt`
(вставить в поле описания на странице мода).
