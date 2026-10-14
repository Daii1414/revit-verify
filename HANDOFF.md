# HANDOFF — продолжи работу с этого места (2026-10-14, вечер)

## 0. Пользователь и стиль — ОБНОВЛЕНО юзером
- Русский язык. Новичок — объясняй просто. Токены НЕ ограничены: режим экономии
  ВЫКЛЮЧЕН юзером («делай по максимуму, чтобы круто»). Отвечай подробно.
- Остальное как было: автономно, спрашивать только перед деструктивным,
  коммитить и пушить сам (main в revit-verify).

## 0. Пользователь и стиль
- Язык: русский. Daii1414, eshirotanaka@gmail.com. Новичок в программировании:
  объясняй просто, без жаргона. Ответы короткие, если не просят подробно.
- Токены ограничены — экономь вывод, не лей воду.
- Работай автономно, решения принимай сам. Спрашивай ТОЛЬКО перед
  деструктивным/необратимым (удаление с GitHub, закрытие Revit с работой,
  правки его моделей). Коммить и пушь сам (main в revit-verify).
- НЕ создавай файлы без нужды; правь существующие.

## 1. Машина
- Windows. Папки: `C:\Users\daii\Documents\デフォルトプロジェクト\` (корень работ).
- .NET 8 SDK 8.0.425, Node 24 LTS (ставили через winget), Python 3.14,
  OpenCode 1.18.35, Revit 2025. VS НЕТ (сборка через dotnet CLI).
- Консоль cp1252: русский в `print()` падает — в скриптах делать
  `sys.stdout.reconfigure(encoding='utf-8')`. PowerShell: нет `tail`, `grep`,
  `&&`, `||`; бинарный поиск строк — ТОЛЬКО через Python
  (PS `.Contains()` врёт на больших blob). `git push | Select` даёт exit 1
  даже при успехе — не пугаться.
- Временные скрипты: `C:\Users\daii\AppData\Local\Temp\opencode\` (pipetest.py —
  сырой клиент нашей pipe!).

## 2. Репозитории и папки
- `fork/` — СТАРЫЙ форк mcp-servers-for-revit. Архив, только локально
  (ветка dev, НЕ пушить — пользователь запретил; remote dev удалён).
- `rvt-fork/` — клон Daii1414/rvt-mcp, ветка dev, ТОЛЬКО локально.
- `revit-verify/` — АКТИВНЫЙ приватный https://github.com/Daii1414/revit-verify,
  ветка main, пушить можно и нужно. База: bimwright/rvt-mcp v1.1.0 (Apache-2.0).
- `upstream-ref/`, `rvt-upstream-ref/`, `horizun-ref/` — чужие клоны
  только для чтения.
- Лицензии: bimwright + Horizun = Apache-2.0 (код можно с уведомлениями).
  Sam-AEC = GPL (только идеи!). cad2data-бинарники = proprietary.

## 3. Что сделано (трек 1, код) — всё в main revit-verify
- Полный порт 234+ тулзов: server + plugin-r25/r26 (r22-24 сломаны в апстриме,
  r27 нужен .NET 10 — выкинуты осознанно). Тесты 1860+ зелёные.
- Своя identity: сборки/ exe `RvtVerify.*`, AddInId новые GUID, каталог
  `%LOCALAPPDATA%\RvtVerify\`, pipe `RvtVerify-<pid>`, панель ленты своя,
  кнопки с префиксом (фикс краша coexist с релизным!).
- Новые инструменты (наши, сверх апстрима):
  1. `set_view_orientation` — камера (FrameCalculator-ядро с юнит-тестами,
     bbox-фикс: исключены виды/камеры/датумы/импорты/связи; zoom_to_fit;
     кадрирование по element_ids). Проверен вживую.
  2. `export_element_snapshot` — isolate + section-box + capture + restore.
     Проверен вживую (восстановление подтверждено).
  3. `create/read_memory_notes` — память проекта (проверены через stdio).
  4. `delete_element`: dry_run + max_count.
  5. `resolve_targets` (уровни/категории/семейства/типы, read-only).
  6. `verify_assert` (bbox/count пост-условия, read-only).
  7. `undo_batch` (журнал батчей + drift-guard + перепроверка).
  8. `transform_elements` (move/copy/rotate).
- Рефакторы: Program.cs 285KB → 27 файлов; FrameCalculator; CI (build+tests);
  update-check/about/bake-issues переведены на наш репо (был тост про v1.1.0!).
- Агенты/скрипты: `.opencode/agents/revit-builder.md`, `revit-observer.md`,
  `.opencode/skills/revit-verify/SKILL.md`, `scripts/orchestrator-stage1.py`,
  `scripts/verify-loop.py` (лежат в rvt-fork И продублированы? — НЕТ, только
  в rvt-fork/dev! в revit-verify их НЕТ. Если нужны — перенести).
- Доки: docs/ (TEARDOWN-orientation, PORT-ANALYSIS, STEAL-LIST,
  STEAL-LIST-v2 на ~22 проектах, PULL-PLAN с волнами 1-5).

## 4. Механика «1 инструмент = N файлов» (выучено болью)
Новый тулза = handler + dispatcher Register + server-метод + shell-тест-список
(View/Meta/Query pinned!) + счётчики (RuntimeControls/ToolsList/default) +
снапшоты (UPDATE_SNAPSHOTS=1) + доки (4 README + CLAUDE + CHANGELOG + allowlist)
+ policy (если read-only) + classifier (если write-имя новое) +
contract-пробы (параметр JSON-текста называть *_json, иначе проба "probe"
уронит тест). После edit-инструмента — нормализовать LF (он возвращает CRLF,
тест на 2048 байт падает!). Проверять: build r25+r26, full suite, коммит, пуш.

## 5. Revit live (текущее)
- Сессия MCP в ЭТОМ чате идёт через РЕЛИЗНЫЙ сервер: новых тулзов в нём нет —
  вызывать через batch по command-имени или сырым pipe-скриптом.
- Оба плагина стоят рядом без конфликтов (проверено вживую).
- НЕ ТРОГАТЬ здания в ChurchOfLight.rvt (работа другой модели!) и TunnelHouse
  (пользователя). View-операции и новые 3D-виды — можно (откатываемо).
- Модели: `D:\Архитектура\Ревит проекты\` (TunnelHouse.rvt,
  ChurchOfLight.rvt, ChurchOfLight-v1.rvt). DWG церкви:
  `D:\зарузки\Проект церкв света в Тадао.dwg` (бинарный, читается только
  импортом в Revit; эталон: оси 1–8/A–D, 11.62×4.82м).
- ChurchOfLight-v1.rvt: мои сетки/стены/двери ПРОПАЛИ при закрытии Revit
  (сохранял! причина не найдена — бэкапы .0001/.0002 рядом). Оси (12) выжили.

## 6. Трек 2 (церковь) — пауза
Построено было: 12 осей, 8 стен, плита, 5 дверей. Допущения: высота 3600
(разреза нет!), толщины стоковые, без проёмов→потом с проёмами, без крыши,
второе здание не начато. Белые пятна в виде 1F — не разгадано (на цифры
не влияет).

## 7. Бэклог по приоритету
1. ~~Живая проверка новых тулзов~~ СДЕЛАНО 2026-10-14 вечером (PID 26204, свежий
   DLL 21:30): песочница = копия ChurchOfLight-v1.rvt в Temp\opencode\verify-sandbox.rvt
   (твои модели НЕ тронуты). Прошло вживую: open_model, resolve_targets (壁→7
   хитов, уровни с отметками), verify_assert (bbox стенки после move +2000мм,
   count), check_model_health (score=100 grade=A), set_view_orientation
   distance_mm=25000 (ответ distance_mm точный), create wall → transform move →
   create_array linear x2 → delete dry_run+real → batch_execute 2 walls →
   undo_batch list/dry_run/undo_last-real (deleted 2) → create_array radial x4
   (угол 90°) → delete 4 → save_document x2, export_element_snapshot (png на
   диске), memory create+read через stdio (зонд-нота удалена, store пуст).
   Discovery-баг НЕ воспроизвёлся на свежем старте (revit-2025-26204.json на
   месте). Revit ОСТАВЛЕН ОТКРЫТЫМ с verify-sandbox (со EMT1 стенкой 140740).
   Скрипты: Temp\opencode\verify_full.py, verify_round2.py, verify_fix.py,
   radial_debug.py, cleanup.py, mem_stdio2.py, open_sandbox.py.
2. ~~save_document, health-score, create_array~~ СДЕЛАНО 2026-10-14, коммит 35a314c
   (revit_save_document + revit_check_model_health/HealthScorer + revit_create_array
   + distance_mm; тесты 1883 зелёных + 1 флаки-тост; counts 56/244/247/96).
3. ~~FrameCalculator: distance_mm~~ СДЕЛАНО (ResolveDistance + тесты, там же).
4. Установщик для публикации — СЛЕДУЮЩИЙ: install.ps1 в репо ОТСУТСТВУЕТ (только
   tests/installer/* + docs/install.md под апстрим Bimwright-пути). Нужен свой
   install.ps1 (r25+r26, %LOCALAPPDATA%\RvtVerify, наши URL) + упаковка релиза.
   Single-registration — НА ПАУЗЕ (решил юзер).
5. Церковь: крыша/окна/второе здание (нужен юзер: разрезы, высоты).
6. DEPLOY-ПРАВИЛО: сборка при открытом Revit падает на копировании DLL в Addins
   (MSB3021, файл залочен) — собирать с /p:RvtVerifySkipDeploy=true; деплой
   происходит сам при сборке с закрытым Revit (Deploy-таргет в csproj).
