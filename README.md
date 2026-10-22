<!-- mcp-name: io.github.Daii1414/revit-verify -->

<h1 align="center">RvtVerify</h1>

<p align="center">
  <b>Мост между ИИ-агентом и вашим Revit — с проверками результата.</b>
</p>

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-Apache%202.0-blue.svg" alt="license" /></a>
  <a href="#инструменты"><img src="https://img.shields.io/badge/MCP-259%20tools-6C47FF" alt="MCP tools" /></a>
  <a href="#поддерживаемые-версии-revit"><img src="https://img.shields.io/badge/Revit-2025%20--%202026-186BFF" alt="Revit 2025-2026" /></a>
  <a href="https://github.com/Daii1414/revit-verify"><img src="https://img.shields.io/badge/GitHub-Daii1414%2Frevit--verify-black" alt="repo" /></a>
  <a href="CHANGELOG.md"><img src="https://img.shields.io/badge/CHANGELOG-history-informational" alt="changelog" /></a>
</p>

---

<details>
<summary>Содержание</summary>

- [Быстрый старт (5 минут)](#быстрый-старт-5-минут)
- [Что это такое](#что-это-такое-простыми-словами)
- [Как это работает](#как-это-работает-архитектура)
- [Что появилось именно в RvtVerify](#что-появилось-именно-в-rvtverify)
- [Инструменты](#инструменты)
- [Установка](#установка)
- [Настройка и права доступа](#настройка-и-права-доступа)
- [Безопасность и приватность](#безопасность-и-приватность)
- [Поддерживаемые версии Revit](#поддерживаемые-версии-revit)
- [Если не работает](#если-не-работает)
- [Состояние проекта](#состояние-проекта)
- [Лицензия и авторы](#лицензия-и-авторы)
- [Ссылки](#ссылки)

</details>

---

## Быстрый старт (5 минут)

1. **Соберите сервер** (или возьмите готовую сборку):

   ```
   git clone https://github.com/Daii1414/revit-verify.git
   cd revit-verify
   dotnet build RvtVerify.sln
   ```

2. **Зарегистрируйте сервер в MCP-клиенте** — шесть строк конфига, см. [Подключение к MCP-клиенту](#подключение-к-mcp-клиенту).

3. **Откройте Revit** → вкладка **Add-Ins** → панель **RvtVerify** → нажмите **«Старт»**.

4. **Спросите ассистента**: «покажи текущий вид» — он вызовет `revit_get_current_view_info`.

Не получилось? Запустите `dotnet "…\RvtVerify.Server.dll" --doctor` и загляните в раздел [Если не работает](#если-не-работает).

---

### Словарик

| Слово | Что значит |
|---|---|
| **инструмент** | Одна команда, которую вызывает ассистент. Имена начинаются с `revit_` |
| **тулсет** | Группа инструментов по теме: `query`, `create`, `view`, `meta` и ещё двадцать |
| **промпт** | Готовая инструкция для ассистента: «внеси изменения», «проверь модель» |
| **запечённый инструмент** | Ваш личный сценарий, сохранённый через ToolBaker |
| **слепок модели** | Снимок состояния элементов, чтобы потом сравнить, что изменилось |
| **оценка** | Число 0–100 по YAML-правилам: насколько модель соответствует требованиям |
| **read-only** | Режим «только чтение»: инструменты, которые ничего не меняют |
| **spill** | Папка, куда сервер выгружает слишком большие ответы |

---

## Что это такое (простыми словами)

**RvtVerify** — это программа-помощник, которая связывает ассистента (Claude, OpenCode, Cursor и других) с вашим **Revit**. Вы говорите ассистенту «построй здесь стены размером столько-то» — а он через RvtVerify отдаёт команду прямо в открытый Revit на вашем компьютере.

Из чего состоит:

- **Сервер** — программа на .NET 8 без интерфейса: она понимает протокол MCP и переводит команды ассистента в вызовы Revit.
- **Плагин (add-in)** — устанавливается в Revit (версии 2025 и 2026). Он и есть те «руки», которые реально создают стены, окна, виды и делают снимки внутри Revit.

Сервер и плагин общаются между собой через **защищённый канал прямо внутри вашего компьютера** (named pipe).

**Ничего не уходит в интернет** — ни ваши чертежи, ни данные проекта, ни пути к файлам. Всё остаётся у вас.

Главное отличие от обычных «мостов» к Revit — **встроенные проверки**. Ассистент не просто «сделал стены», а сам убеждается, что они на месте, нужного размера и на правильном этаже, и показывает вам отклонения. Поэтому проект и называется **RvtVerify** (verify = проверять).

> Основа — открытый проект bimwright/rvt-mcp (Apache-2.0). RvtVerify — его развитие (форк): дополнительные проверочные инструменты, автоответ на блокирующие окна Revit, локализация на 15 языков и собственный интерфейс плагина.

---

## Как это работает (архитектура)

<p align="center">
  <img src="docs/img/architecture.svg" alt="Схема: ИИ-ассистент → RvtVerify.Server → RvtVerify.Plugin внутри Revit → модель .rvt" width="900" />
</p>

Почему этим удобно пользоваться:

1. **Всё локально.** Никакого облака, никакой телеметрии. Мост живёт между двумя программами на одном компьютере.
2. **Защищённый канал.** Канал между сервером и плагином закрыт персональным токеном доступа и лимитом частоты запросов — посторонняя программа на вашем ПК не сможет командовать Revit.
3. **Единицы — миллиметры.** Все инструменты принимают и возвращают длины в миллиметрах, а не во внутренних футах Revit.
4. **Проверка вместо веры.** После любой команды ассистент может спросить «а действительно ли получилось?» и получить честный ответ со списком нарушителей.
5. **Откат одной кнопкой.** Деструктивные операции идут через схему «сначала показать, потом подтвердить», а пакеты можно отменить целиком (`revit_undo_batch`).

---

## Что появилось именно в RvtVerify

Это то, что добавлено поверх базового проекта rvt-mcp.

**Проверки и качество (QA):**

1. **Проверка построенного** (`revit_verify_assert`) — ассистент спрашивает: «стен >= 4?», «габарит в пределах X±5 мм?», «объём плиты больше нуля?», «ребра замкнуты?», «окна реально сидят на стене и не оторваны от этажа?». В ответ — «да/нет» и список элементов-нарушителей. Условия задаются структурированно: вид проверки (`count`, `bbox`, `solid_volume`, `edge_closure`, `support`), а не свободным текстом.
2. **Правила проверки из файла** (`revit_verify_run_rules`) — вы один раз описываете требования YAML, программа прогоняет всё и выдаёт оценку 0–100 с разбором по пунктам. Правила умеют привязываться к слепку модели и сверяться с ним.

```yaml
- name: каждый зал — два окна
  description: в залах должно быть не меньше двух окон
  assertions:
    - kind: count
      category: Windows
      expect: ">= 2"
```
3. **История оценок** (`revit_qa_trend`) — локальный журнал оценок: динамика, лучшее/худшее, разница с прошлым прогоном. Хранится только на вашем диске.
4. **Проверка опоры** — виджет `support` в assert и флаг `support_check` в пакетном вызове: элемент реально держится на хосте и не висит в воздухе (эвристика, не расчёт прочности).
5. **Показать нарушителей** (`revit_highlight_offenders`) — одним вызовом выделяет, приближает и красит «плохие» элементы; отменяется обычным Undo Revit.

**Снимки и сравнение:**

6. **Сравнение «до/после»** (`revit_capture_before_after`) — сняли кадр, поменяли, сняли снова — видно, что визуально изменилось (пиксельный diff, та же камера и размер). Свежесть эталонного кадра проверяется (`max_age_hours`) — если «до» старше порога, сравнение не выводится.
7. **Слепок модели** (`revit_capture_model_snapshot` / `revit_compare_model_snapshot`) — «фотография» состояния элементов: что добавилось, исчезло, поменялось. Версионируется и защищается SHA-256.
8. **Наборы кадров** (`revit_capture_review_set`) — до восьми именованных обзорных кадров за один вызов, с восстановлением вида.

<p align="center">
  <img src="docs/img/cad-pipeline.svg" alt="Схема: конвейер работы с чертежом DWG — извлечение, план, сверка, применение" width="900" />
</p>

**Работа с чертежом DWG (полный конвейер):**

9. **Извлечение** (`revit_cad_extract`) — читает слои и векторную геометрию уже загруженного в Revit чертежа. Ничего не меняет.
10. **План по чертежу** (`revit_cad_plan`) — из точных слоёв DWG собирает упорядоченный план: оси, стены, двери и окна (дуги — двери, парные линии — окна). С привязкой к источнику (слой/экземпляр). Ничего не меняет.
11. **Сверка плана с моделью** (`revit_cad_audit`) — сравнивает хешированный план с реальными осями Revit по метке и ориентации. Ничего не меняет.
12. **Применение плана** (`revit_cad_apply`) — если план понравился, ставит стены в модель: двойная проверка «сначала показать → потом подтвердить», партиями до 20 штук (один шаг Undo) и с локальным журналом происхождения каждой стены.

**Удобство и автоматизация:**

13. **Автонумерация** дверей, окон и комнат (`revit_renumber_elements`) — номера по порядку, сверху вниз по этажам и по плану; по умолчанию только показывает, что будет.
14. **Зеркальное копирование** (`revit_transform_elements` с `op=mirror`) — отразить элементы относительно оси, с вариантом «копировать».
15. **Одно свойство по запросу** (`revit_reflect_get`) — достать ровно одно значение: имя, категорию, этаж, габарит или параметр — без `send_code`.
16. **Память проекта** (`revit_create_memory_note` / `revit_read_memory_notes`) — заметки о договорённостях проекта (единицы, именование, допуски, предположения), которые ассистент прочтёт в следующий раз. Хранится локально, максимум 200 заметок, никуда не отправляется.
17. **История изменений** (`revit_record_change`, `revit_get_change_records`, `revit_survey_change_impact`) — журнал: что, кем и когда менялось в модели, плюс оценка влияния изменения. Подробнее — [docs/change-tracking.md](docs/change-tracking.md) и [docs/change-survey.md](docs/change-survey.md).
18. **Файлы моделей** (`revit_list_recent_models`, `revit_open_model`, `revit_save_document`) — список последних файлов, открытие и сохранение модели прямо из ассистента.
19. **Точная камера** (`revit_set_view_orientation`) — точная постановка вида: точка глаза, точка цели и наклон (roll) в миллиметрах, а не только «направление + дистанция».
20. **Доктор** (`RvtVerify.Server --doctor`) — проверка установки без запуска Revit.
21. **Фоновые задания** (`revit_job`) — долгие экспорты и коллизии не блокируют ассистента: submit / status / cancel / list, ID сохраняются между перезапусками.
22. **Отчёт по работе** (`revit_distill_session`) — анализ журнала команд: что чаще всего делаете, где ошибки, длительность (медиана и 95-й перцентиль).
23. **Умное поведение с окнами Revit** — при массовом построении Revit иногда спрашивает «стены пересекаются, соединить?». Программа **сама отвечает** «да», пока идёт команда, поэтому пакетная автоматизация не прерывается (вне команд это окно остаётся для ручной работы). Плюс новые стены заранее соединяются геометрией (pre-join) — таких вопросов примерно вдвое меньше.

---

## Установка

**Установщик пока не готов.** Релизный пакет и скрипты установки ещё не перенесены в этот форк — пока ставьте вручную или собирайте из исходников (команда ниже). Следите за [CHANGELOG.md](CHANGELOG.md).

Что понадобится:

- Windows, Autodesk Revit **2025 или 2026**;
- .NET 8 Desktop Runtime (если его ещё нет — [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/8.0));
- любой MCP-клиент (Claude Code, OpenCode, Cursor и т.п.).

Сборка из исходников:

```
git clone https://github.com/Daii1414/revit-verify.git
cd revit-verify
dotnet build RvtVerify.sln
```

Собранный сервер — `src/server/bin/Debug/net8.0/RvtVerify.Server.exe`. Проекты плагинов — `src/plugin-r25` и `src/plugin-r26`, собранные DLL — в их `bin/Debug/net8.0-windows7.0/`. Сборка сама копирует плагины в `%APPDATA%\Autodesk\Revit\Addins\<год>\RvtVerify\` — закройте Revit перед сборкой, иначе DLL будет занят.

Проверить, что всё встало правильно, можно **без запуска Revit**:

```
dotnet "<путь-к-репозиторию>/src/server/bin/Debug/net8.0/RvtVerify.Server.dll" --doctor
```

Доктор проверит установку манифестов плагина, наличие и совпадение версий DLL, конфигурацию и доступность папки данных. Код возврата **0** — всё в порядке, **1** — есть проблемы (подробности в тексте отчёта).

### Подключение к MCP-клиенту

Пример конфигурации (файл `.mcp.json` / `mcpServers`):

```json
{
  "mcpServers": {
    "revit-verify": {
      "command": "dotnet",
      "args": ["C:\\Users\\<ваше_имя>\\Documents\\revit-verify\\src\\server\\bin\\Debug\\net8.0\\RvtVerify.Server.dll"]
    }
  }
}
```

Замените путь на свой. Готовые шаблоны — в [docs/mcp-client-wiring.md](docs/mcp-client-wiring.md) (общая процедура), [docs/mcp-config-claude-clients.md](docs/mcp-config-claude-clients.md) (Claude Code и Claude Desktop), [docs/mcp-config-codex.md](docs/mcp-config-codex.md) (Codex), [docs/mcp-config-opencode-kilo.md](docs/mcp-config-opencode-kilo.md) (OpenCode и Kilo). В корне репозитория лежат [.mcp.json.example](.mcp.json.example) и [opencode.json](opencode.json).

---

## Инструменты

| Режим | Инструментов | Что значит |
|-------|-------------:|------------|
| Базовый запуск | **66** | Только осмотр и создание (`query`, `create`, `view`, `meta`) |
| Все возможности (`--toolsets all`) | **259** | Полный набор |
| Все + автоподбор (`--enable-adaptive-bake`) | **262** | Добавляет 3 инструмента «предложи повторяющееся». Флаг выключен по умолчанию |
| Только чтение (`--read-only`) | **36** / **107** | На базовых тулсетах — 36, со всеми (`--toolsets all`) — 107. Ничего не меняет в проекте — можно пробовать спокойно |

Личные инструменты (ToolBaker) в эти числа не входят — их создаёт пользователь, а не сборка.

В списке разрешённых инструментов ниже — **106** имён. 107-й — `revit_switch_target`: он переключает, к какому Revit идёт команда, поэтому помечен как изменяющий, но в режиме «только чтение» остаётся доступным — иначе ассистент не смог бы переключиться на нужное окно.

<p align="center">
  <img src="docs/img/read-only-modes.svg" alt="Схема: базовый режим, режим только чтение и полный доступ" width="900" />
</p>

### Тулсеты по темам

| Тема | Что внутри (коротко) |
|------|----------------------|
| `query` | Осмотр: вид, выделение, фильтры, статистика, параметры, связи элементов, рабочие наборы |
| `create` | Создание: оси, уровни, комнаты, линейные и точечные элементы, группы |
| `view` | Виды, листы, снимки экрана, камера, обрезка и масштаб |
| `meta` | Пакетное выполнение (`revit_batch_execute`, до 20 команд), несколько Revit, инфо проекта, очистка неиспользуемого, `send_code` |
| `lint` | Проверка именованных видов, профиль фирмы, сводка предупреждений модели |
| `schedule` | Спецификации: создание, поля, формулы, данные |
| `families` | Семейства: загрузка, типы, экземпляры, аудит |
| `modify` | Изменение: цвет, параметры, тип, копирование, поворот, **зеркало** |
| `delete` | Удаление по номеру (с предпросмотром) |
| `annotation` | Пометки, текст, размеры, ключевые заметки |
| `export` | Экспорт в PDF / DWG / IFC / NWC / DGN / DWF / FBX / gbXML, изображения, CSV спецификаций |
| `mep` | Инженерные сети: воздуховоды, трубы, лотки, короба, связность, разрывы |
| `graphics` | Фильтры вида, переопределения, видимость и фазы |
| `toolbaker` | Ваши личные «кнопки» повторяющихся действий |
| `sheets` | Листы, штампы, ревизии, раскладка видов, печать |
| `materials` | Материалы, внешний вид, назначение элементам |
| `geometry` | Габариты, замеры, коллизии, объёмы и площади, сложность геометрии |
| `rooms` | Комнаты, площади, отделка, разделители помещений |
| `links` | Связи с моделями и DWG, координаты, **чтение чертежа и план по нему** |
| `parameters` | Параметры проекта и общие параметры, привязки |
| `organization` | Сохранённые наборы выделения, шаблоны видов, **перенумерация** |
| `workflows` | Сложные сценарии «в одном вызове»: аудит модели, обзор коллизий, выгрузка данных, комплект листов |
| `structural` | Колонны, балки, фундаменты, арматура, нагрузки |

<p align="center">
  <img src="docs/img/verify-loop.svg" alt="Схема: цикл «задача → действие → проверка → вердикт»" width="900" />
</p>

### Готовые сценарии (промпты)

Сервер отдаёт не только инструменты, но и **промпты-сценарии** — готовые инструкции для ассистента:

- `revit_getting_started` — знакомство и порядок работы;
- `revit_change` — внесение изменений в модель;
- `revit_model_audit` — аудит модели;
- `revit_pre_issue_check` — проверка перед выпуском документации;
- `revit_drawing_layout` — раскладка чертежа;
- `revit_stairs` — рабочий процесс по лестницам.

Если промпту не хватает инструментов текущей конфигурации, он честно об этом скажет, а не заставит вас угадывать.

### Свой код, личные инструменты и локализация

- **Свой код** (`revit_send_code_to_revit`) — «аварийный люк»: если готового инструмента нет, можно выполнить свой код на C# прямо внутри Revit. По умолчанию включено, отключается флагом `--disable-send-code`. Подробнее — [docs/send-code.md](docs/send-code.md).
- **Личные инструменты (ToolBaker)** — повторяющееся действие сохраняется как ваша собственная кнопка-инструмент; есть адаптивные подсказки. Подробнее — [docs/bake.md](docs/bake.md).
- **Локализация** — интерфейс плагина на **15 языках**, включая русский; язык берётся из языка Revit, но меняется кнопкой на ленте ([docs/localization.md](docs/localization.md)).
- **Лента Revit:** кнопки «старт/стоп», «История» (поиск и повтор прошлых команд), «Уведомления», «Настройки».

---

## Настройка и права доступа

Настройки живут в файле `%LOCALAPPDATA%\RvtVerify\rvtmcp.config.json`, часть задаётся флагами при запуске сервера.

| Флаг / ключ | Что делает | Когда включать |
|---|---|---|
| `--toolsets all` | Открывает все 259 инструментов | Постоянная работа со всей моделью |
| `--read-only` | Оставляет только инструменты, которые ничего не меняют | Первое знакомство, аудит чужой модели |
| `--enable-adaptive-bake` | Подсказки «повторить действие» (по умолчанию выключено) | Когда часто повторяете одни и те же операции |
| `--disable-send-code` | Запрещает выполнение произвольного C# | Когда запуск своего кода не нужен |
| `--target` / `revit_switch_target` | Выбор конкретного Revit, если запущено несколько | Всегда, когда открыто больше одного Revit |
| `--http <порт>` | Запуск через HTTP SSE вместо stdio | Для Ollama, LM Studio и других клиентов без stdio |
| `--spill-retention-hours <n>` | Сколько часов хранить выгруженные большие ответы (по умолчанию 36) | Если нужна длинная история выгрузок |
| `autoJoinOverlapDialog` (по умолчанию — включено) | Автоответ на окно «соединить стены» | Оставьте: иначе пакетное построение встанет на этом окне |
| `--disable-change-history` | Не вести локальный журнал изменений модели | Если журнал не нужен |
| `--disable-response-guard` | Отключить ограничение размера ответа | Почти никогда не нужен |

Подробнее — в [docs/](docs/), внутреннее устройство — в [ARCHITECTURE.md](ARCHITECTURE.md).

### Permissions & auto mode (права доступа и авторежим)

В описании каждого инструмента есть отметка: он только читает или меняет документ и файлы. Временное выделение, активный вид и приближение считаются чтением. `send_code` не имеет отметки вообще: не включайте его в автоматические разрешения и подтверждайте каждый вызов произвольного кода.

Для Claude Code копируйте **только** список read-only ниже. Не используйте широкий wildcard `mcp__revit-verify__*` — он разрешит всё, включая удаление. Список соответствует `--toolsets all`; при выбранных тулсетах инструментов будет меньше. Режим `--read-only` применяет этот же список на стороне сервера: заблокированный инструмент вообще не появляется в списке — вызвать его нельзя.

<details>
<summary>Список разрешённых для чтения инструментов (генерируется из аннотаций)</summary>

<!-- BEGIN READ_ONLY_ALLOWLIST -->
```json
{
  "permissions": {
    "allow": [
      "mcp__revit-verify__revit_activate_view",
      "mcp__revit-verify__revit_ai_element_filter",
      "mcp__revit-verify__revit_analyze_geometry_complexity",
      "mcp__revit-verify__revit_analyze_mep_network",
      "mcp__revit-verify__revit_analyze_model_statistics",
      "mcp__revit-verify__revit_analyze_sheet_layout",
      "mcp__revit-verify__revit_analyze_structural_connections",
      "mcp__revit-verify__revit_analyze_usage_patterns",
      "mcp__revit-verify__revit_analyze_view_naming_patterns",
      "mcp__revit-verify__revit_audit_families",
      "mcp__revit-verify__revit_cad_audit",
      "mcp__revit-verify__revit_cad_extract",
      "mcp__revit-verify__revit_cad_plan",
      "mcp__revit-verify__revit_capture_model_snapshot",
      "mcp__revit-verify__revit_check_model_health",
      "mcp__revit-verify__revit_clash_detection",
      "mcp__revit-verify__revit_compare_model_snapshot",
      "mcp__revit-verify__revit_compute_element_area",
      "mcp__revit-verify__revit_compute_element_volume",
      "mcp__revit-verify__revit_detect_firm_profile",
      "mcp__revit-verify__revit_detect_system_elements",
      "mcp__revit-verify__revit_distill_session",
      "mcp__revit-verify__revit_find_elements_in_volume",
      "mcp__revit-verify__revit_find_mep_disconnects",
      "mcp__revit-verify__revit_find_overlapping_elements",
      "mcp__revit-verify__revit_find_schedule_elements",
      "mcp__revit-verify__revit_find_undimensioned_elements",
      "mcp__revit-verify__revit_find_untagged_elements",
      "mcp__revit-verify__revit_get_assembly_members",
      "mcp__revit-verify__revit_get_available_family_types",
      "mcp__revit-verify__revit_get_change_records",
      "mcp__revit-verify__revit_get_current_target",
      "mcp__revit-verify__revit_get_current_view_info",
      "mcp__revit-verify__revit_get_element_bounding_box",
      "mcp__revit-verify__revit_get_element_centroid",
      "mcp__revit-verify__revit_get_element_details",
      "mcp__revit-verify__revit_get_element_geometry",
      "mcp__revit-verify__revit_get_element_parameters",
      "mcp__revit-verify__revit_get_element_relationships",
      "mcp__revit-verify__revit_get_family_instances",
      "mcp__revit-verify__revit_get_group_members",
      "mcp__revit-verify__revit_get_link_coordinate_system",
      "mcp__revit-verify__revit_get_link_elements",
      "mcp__revit-verify__revit_get_material_properties",
      "mcp__revit-verify__revit_get_material_quantities",
      "mcp__revit-verify__revit_get_mep_element_connectors",
      "mcp__revit-verify__revit_get_model_warnings_summary",
      "mcp__revit-verify__revit_get_panel_schedule",
      "mcp__revit-verify__revit_get_print_settings",
      "mcp__revit-verify__revit_get_project_coordinate_system",
      "mcp__revit-verify__revit_get_room_boundaries",
      "mcp__revit-verify__revit_get_room_openings",
      "mcp__revit-verify__revit_get_schedulable_fields",
      "mcp__revit-verify__revit_get_schedule_data",
      "mcp__revit-verify__revit_get_schedule_definition",
      "mcp__revit-verify__revit_get_schedule_formulas",
      "mcp__revit-verify__revit_get_selected_elements",
      "mcp__revit-verify__revit_get_structural_loads",
      "mcp__revit-verify__revit_get_system_inventory",
      "mcp__revit-verify__revit_get_titleblock_parameters",
      "mcp__revit-verify__revit_get_type_parameters",
      "mcp__revit-verify__revit_get_view_visibility",
      "mcp__revit-verify__revit_get_viewport_geometry",
      "mcp__revit-verify__revit_list_areas",
      "mcp__revit-verify__revit_list_assemblies",
      "mcp__revit-verify__revit_list_available_targets",
      "mcp__revit-verify__revit_list_baked_tools",
      "mcp__revit-verify__revit_list_export_settings",
      "mcp__revit-verify__revit_list_family_types_in_family",
      "mcp__revit-verify__revit_list_groups",
      "mcp__revit-verify__revit_list_keynotes",
      "mcp__revit-verify__revit_list_linked_cad",
      "mcp__revit-verify__revit_list_linked_models",
      "mcp__revit-verify__revit_list_loaded_families",
      "mcp__revit-verify__revit_list_materials",
      "mcp__revit-verify__revit_list_mep_systems",
      "mcp__revit-verify__revit_list_phases",
      "mcp__revit-verify__revit_list_project_parameter_bindings",
      "mcp__revit-verify__revit_list_project_parameters",
      "mcp__revit-verify__revit_list_rebar",
      "mcp__revit-verify__revit_list_recent_models",
      "mcp__revit-verify__revit_list_revisions",
      "mcp__revit-verify__revit_list_rooms",
      "mcp__revit-verify__revit_list_saved_selections",
      "mcp__revit-verify__revit_list_schedules",
      "mcp__revit-verify__revit_list_shared_parameters",
      "mcp__revit-verify__revit_list_sheets",
      "mcp__revit-verify__revit_list_titleblocks",
      "mcp__revit-verify__revit_list_view_filters",
      "mcp__revit-verify__revit_list_view_templates",
      "mcp__revit-verify__revit_list_worksets",
      "mcp__revit-verify__revit_load_selection",
      "mcp__revit-verify__revit_measure_distance_between_elements",
      "mcp__revit-verify__revit_project_point_onto_face",
      "mcp__revit-verify__revit_raycast_from_point",
      "mcp__revit-verify__revit_read_memory_notes",
      "mcp__revit-verify__revit_reflect_get",
      "mcp__revit-verify__revit_resolve_targets",
      "mcp__revit-verify__revit_select_elements",
      "mcp__revit-verify__revit_show_element_in_view",
      "mcp__revit-verify__revit_show_message",
      "mcp__revit-verify__revit_suggest_view_name_corrections",
      "mcp__revit-verify__revit_survey_change_impact",
      "mcp__revit-verify__revit_verify_assert",
      "mcp__revit-verify__revit_verify_run_rules",
      "mcp__revit-verify__revit_workflow_model_audit"
    ]
  }
}
```
<!-- END READ_ONLY_ALLOWLIST -->

</details>

---

## Безопасность и приватность

- Всё работает **локально**. Ни чертежи, ни данные проекта, ни пути к файлам не уходят в интернет.
- Канал между сервером и плагином закрыт **токеном доступа** (создаётся при запуске, хранится только у вас) и лимитом частоты запросов.
- Инструмент `send_code_to_revit` умеет выполнять произвольный код — если он вам не нужен, отключите (`--disable-send-code`).
- Деструктивные операции идут через схему «сначала показать, потом подтвердить».
- Крупные ответы не рвут сессию: они выгружаются в локальные файлы (папка `spill` в `%LOCALAPPDATA%\RvtVerify\`) и удаляются через 36 часов.
- Журнал работы ведётся локально и может быть выключен.

<p align="center">
  <img src="docs/img/data-layout.svg" alt="Схема: каталог %LOCALAPPDATA%\RvtVerify и что в нём лежит" width="900" />
</p>

Подробности — в [SECURITY.md](SECURITY.md).

---

## Поддерживаемые версии Revit

| Версия Revit | Плагин |
|--------------|--------|
| 2025 | ✅ `RvtVerify.Plugin` (2025) |
| 2026 | ✅ `RvtVerify.Plugin` (2026) |
| 2022–2024, 2027 | Пока нет: 2022–2024 требуют .NET Framework 4.8, 2027 — .NET 10. Вернёмся позже. |

Если запущено **несколько Revit сразу** — программа сама разберётся, какой из них брать: `revit_list_available_targets` покажет все экземпляры, `revit_switch_target` переключит. Команды никогда не уйдут в «чужой» Revit.

---

## Если не работает

| Симптом | Что делать |
|---|---|
| Ассистент не видит инструментов | Проверьте, что в конфиге указан верный путь к `RvtVerify.Server.dll`, и перезапустите клиент |
| «Revit не найден» | Откройте Revit и нажмите **Старт** на ленте: Add-Ins → RvtVerify. Несколько Revit? Ассистент видит их через `revit_list_available_targets` |
| Расхождение версий сервера и плагина | `dotnet "…/RvtVerify.Server.dll" --doctor` — покажет, какая DLL не совпадает |
| Не открывается история или нет журнала | Журнал: `%LOCALAPPDATA%\RvtVerify\debug.log` |
| Нужен «чистый» запуск | Закройте Revit и клиента, удалите `%LOCALAPPDATA%\RvtVerify\revit-*.json` и запустите снова |
| Пакетное построение встало | Проверьте, что `autoJoinOverlapDialog` не выключен и что Revit не ждёт подтверждения в другом окне |

---

## Состояние проекта

Проект в активной разработке. Что уже работает (проверено на реальных моделях):

- 259 инструментов; в режиме `--read-only` остаётся 107 (36 — на базовых тулсетах);
- проверки результата: assert, YAML-правила, история оценок, слепки модели, сравнение «до/после»;
- полный конвейер работы с чертежом DWG: извлечение → план → сверка → применение с подтверждением;
- автоответ на блокирующие окна Revit и предварительное соединение стен;
- фоновые задания, доктор установки, отчёты по сессии;
- установщик — в работе.

Что нового — в [CHANGELOG.md](CHANGELOG.md).

**Развитие:** [CONTRIBUTING.md](CONTRIBUTING.md) — как вносить изменения, [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) — правила сообщества. Тесты: `dotnet test RvtVerify.sln` (каталог `tests/`).

---

## Лицензия и авторы

- Основа: **bimwright/rvt-mcp**, автор Khoa Le, лицензия **Apache-2.0**.
- Развитие и проверочный слой: **Daii1414** (RvtVerify), тоже **Apache-2.0**.
- Берёте куски кода — сохраняйте уведомление об авторах и лицензию.

---

## Ссылки

- [Репозиторий](https://github.com/Daii1414/revit-verify) — исходники на GitHub.
- [CHANGELOG.md](CHANGELOG.md) — что нового.
- [CLAUDE.md](CLAUDE.md) — как устроено внутри (для разработчиков и ассистентов).
- [ARCHITECTURE.md](ARCHITECTURE.md) — архитектура подробно.
- [docs/](docs/) — инструкции: [установка](docs/install.md), [подключение клиентов](docs/mcp-client-wiring.md), [чертежи DWG](docs/cad-planning.md), [лестницы](docs/stairs-workflow.md), [send_code](docs/send-code.md), [ToolBaker](docs/bake.md), [локализация](docs/localization.md), [отслеживание изменений](docs/change-tracking.md), [анализ влияния изменений](docs/change-survey.md), [профили фирмы](docs/firm-profiles/README.md), [контракты размещения и MEP](docs/placement-and-mep-contracts.md).
