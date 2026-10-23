# AGENTS.md

Инструкции для ИИ-агентов (Claude Code, OpenCode, Cursor и другие), которые подключаются к **RvtVerify** — локальному MCP-шлюзу между агентом и Autodesk Revit.

Этот файл дополняет [`README.md`](README.md) и [`CLAUDE.md`](CLAUDE.md). Он посвящён конкретно подключению клиентов, настройке и правилам взаимодействия с агентом.

---

## 1. Что делает RvtVerify

RvtVerify — это два процесса на одном компьютере:

```
ИИ-агент  ──MCP (stdio)──►  RvtVerify.Server (.NET 8)
                                     │
                                     │ named pipe + токен доступа
                                     ▼
                              RvtVerify.Plugin  ◄─── внутри Revit
                                     │
                                     ▼
                                 ваша модель .rvt
```

- **Всё локально.** Никаких данных модели, чертежей или путей к файлам не уходит в интернет.
- **Защищённый канал.** Между сервером и плагином — named pipe с персональным токеном доступа и лимитом частоты запросов (20 req / 10 с на соединение).
- **Миллиметры на границе.** Все длины инструментами принимаются и возвращаются в миллиметрах.

---

## 2. Подключение MCP-клиента

Сервер — консольное приложение .NET 8. Он запускается через `dotnet` и общается с MCP-клиентом по stdio (NDJSON).

### Готовые шаблоны конфигурации

В корне репозитория:
- [`.mcp.json.example`](.mcp.json.example) — базовый шаблон
- [`opencode.json`](opencode.json) — шаблон для OpenCode CLI

Подробные инструкции по клиентам:
- [docs/mcp-client-wiring.md](docs/mcp-client-wiring.md) — общая процедура подключения
- [docs/mcp-config-claude-clients.md](docs/mcp-config-claude-clients.md) — Claude Code и Claude Desktop
- [docs/mcp-config-codex.md](docs/mcp-config-codex.md) — OpenAI Codex
- [docs/mcp-config-opencode-kilo.md](docs/mcp-config-opencode-kilo.md) — OpenCode и Kilo

### Быстрый старт

1. Соберите сервер (или возьмите готовую сборку):

```bash
git clone https://github.com/Daii1414/revit-verify.git
cd revit-verify
dotnet build RvtVerify.sln
```

2. Запустите сервер вручную (для проверки) или зарегистрируйте в MCP-клиенте:

```bash
dotnet "src/server/bin/Debug/net8.0/RvtVerify.Server.dll"
```

3. В MCP-клиенте добавьте сервер:

```json
{
  "mcpServers": {
    "revit-verify": {
      "command": "dotnet",
      "args": ["<ПУТЬ_К_РЕПО>/src/server/bin/Debug/net8.0/RvtVerify.Server.dll"]
    }
  }
}
```

4. Откройте **Revit 2025 или 2026**, перейдите на вкладку **Add-Ins → RvtVerify** и нажмите **«Старт»**.

5. Спросите ассистента: «покажи текущий вид» — он вызовет `revit_get_current_view_info`.

### Проверка установки (без Revit)

```bash
dotnet "src/server/bin/Debug/net8.0/RvtVerify.Server.dll" --doctor
```

Код возврата **0** — всё в порядке, **1** — есть проблемы (подробности в тексте отчёта).

---

## 3. Поддерживаемые версии

| Компонент | Версия |
|-----------|--------|
| Revit | 2025, 2026 (только эти версии shipped плагины) |
| .NET | 8 (сервер), .NET 8 / .NET 4.8 (плагины, см. CLAUDE.md) |
| Сервер | `RvtVerify.Server` (.NET 8) |

Внимание: Revit 2022–2024 и 2027 **не поддерживаются** в этом форке (базовый проект сломан для них / требует другого .NET). Не открывайте issues с просьбами добавить эти версии без патча.

---

## 4. Правила для агентов

### Инструменты

- Все MCP-имена инструментов начинаются с `revit_` (например, `revit_create_grid`, `revit_verify_assert`).
- Инструменты разбиты на **тулсеты**: `query`, `create`, `view`, `meta`, `modify`, `delete`, `export`, `annotation`, `mep`, `schedule`, `families`, `graphics`, `toolbaker`, `lint`, `sheets`, `materials`, `geometry`, `rooms`, `links`, `parameters`, `organization`, `workflows`, `structural`.
- По умолчанию открыты только `query`, `create`, `view`, `meta` (66 инструментов). Чтобы открыть всё, запустите сервер с `--toolsets all` (259 инструментов).
- Режим `--read-only` открывает только инструменты, которые ничего не меняют (107 с `--toolsets all`, 36 — с базовыми тулсетами).

### Отправка команд

- Агент вызывает инструмент через MCP `tools/call`.
- Сервер пересылает команду в плагин Revit через **named pipe**. Плагин выполняет её в UI-потоке Revit через `ExternalEvent`.
- Команда привязана к поколению целевого Revit-экземпляра. Если между вызовами переключился целевой Revit (`revit_switch_target`), команда не уйдёт в «чужой» экземпляр — вернётся ошибка.

### Когда команду нельзя вызвать

- Инструмент помечен как изменяющий документ, а сервер запущен с `--read-only`.
- `send_code_to_revit` отключён флагом `--disable-send-code`.
- Целевой Revit не запущен, или плагин не нажал «Старт» на ленте.

### Проверка результата (основная价值 RvtVerify)

Агент **не должен верить на слово** после изменения модели. Используйте инструменты проверки:

- `revit_verify_assert` — проверка одного условия (количество стен, габариты, объём, замкнутость ребер, поддержка элемента).
- `revit_verify_run_rules` — проверка из YAML-файла с оценкой 0–100.
- `revit_capture_before_after` / `revit_compare_model_snapshot` — снимки «до/после».
- `revit_highlight_offenders` — подсветка нарушителей в виде Revit (отменяется Undo).

### Псевдонимы имён не поддерживаются

Если вы мигрируете с другой реализации Revit-MCP и имя инструмента не совпадает — не добавляйте алиас сами. Откройте issue; алиасы рассматриваются, когда три или более пользователей запросят один и тот же.

---

## 5. Безопасность

- **Не открывайте публичный issue** с привилегированной информацией (уязвимость, обход аутентификации, RCE через `send_code_to_revit` / ToolBaker). Свяжитесь с maintains приватно — см. [`SECURITY.md`](SECURITY.md).
- `send_code_to_revit` выполняет произвольный C# в контексте Revit. Отключайте через `--disable-send-code`, если не нужен.
- Никогда не присылайте логи, содержащие токены доступа или личные пути, в публичные issues. Логи лежат в `%LOCALAPPDATA%\RvtVerify\debug.log`.

---

## 6. Работа с кодом

- Сборка: `dotnet build RvtVerify.sln` (закройте Revit перед сборкой — плагины копируются в `%APPDATA%\Autodesk\Revit\Addins\<год>\RvtVerify\` и DLL будет залочена).
- Тесты: `dotnet test RvtVerify.sln` (2104 теста, должны быть все зелёные).
- Документация разработчика: [`CLAUDE.md`](CLAUDE.md) (архитектура, ключевые паттерны), [`ARCHITECTURE.md`](ARCHITECTURE.md).
- Конвенция коммитов: Conventional Commits (`feat:`, `fix:`, `docs:`, `chore:`).
- Имена инструментов и параметров — `snake_case`.

---

## 7. Частые проблемы

| Симптом | Что делать |
|---------|------------|
| Агент не видит инструментов | Проверьте путь к `RvtVerify.Server.dll` в конфиге клиента и перезапустите клиент |
| «Revit не найден» | Откройте Revit → Add-Ins → RvtVerify → **Старт**. Несколько Revit? Используйте `revit_list_available_targets` |
| Расхождение версий сервера и плагина | Запустите `...RvtVerify.Server.dll --doctor` |
| Пакетное построение встало | Проверьте, что `autoJoinOverlapDialog` не выключен в конфиге |
