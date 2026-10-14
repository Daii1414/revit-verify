# PORT-ANALYSIS — что хорошо, что плохо (по фактам)

Scope: `src/` (234 tools) + `tests/` нового репозитория.
Правило: хвалю/ругаю только проверенное. Вывод после каждого пункта:
KEEP (не трогать), FIX (чинить), HOLD (решить позже).

## KEEP — реализовано хорошо, не переписывать

1. **Транспорт + авторизация.** Pipe (2025–27) / TCP (2022–24),
   discovery-файлы, токен на сессию, проверка живого PID, привязка
   session→instance с generation-pin. Файлы: `shared/Transport/*`,
   `server/TargetSelection.cs`, `shared/Security/AuthToken.cs`.
2. **Батчи.** `batch_execute`: до 20 команд, один TransactionGroup,
   `Assimilate`/`RollBack`, unit-testable `BatchExecutor.Run`.
3. **Гейты ответов.** Лимит 1 MiB, предупреждение 100 KB, spill в файл,
   санитизация путей в ошибках (`Response*`, `ErrorSanitizer`,
   `SecretMasker`). Журнал calls с redact send_code.
4. **Конфиг в 3 слоя** (файл < env < CLI), `RvtMcpConfig`, nullable-поля.
5. **Гейт send_code.** Произвольный C# только при opt-in
   (adaptive-bake), redact в логах. Проверено на себе: свои транзакции
   внутри откатываются, чтение — нет.
6. **Golden-тесты.** Снапшоты тул-листа + счётчики + shell-тесты классов.
   Жёстко, но ловят рассинхрон (поймали мой инструмент 4 раза).
7. **Пакеты свежие.** MCP SDK 1.1.0, Newtonsoft 13.0.3, Sqlite 8.0.11.
   Устаревшего не найдено.

## FIX — подтверждённые проблемы

- **F1. God-file.** `src/server/Program.cs` — 285 КБ, 26 классов тулсетов
  + бутстрап в одном файле. Риск: merge-конфликты на каждом инструменте.
  Фикс: разрезать по классам (механика, поведения ноль).
- **F2. Цена инструмента — 11+ файлов.** Хендлер + Register + метод сервера
  + shell-тест + счётчики + 3 снапшота + 6 доков. Доказано на
  `set_view_orientation`. Фикс: single-registration (REENGINEERING.md).
- **F3. Потолок 2048 байт.** `ServerInstructionsText` упирается в лимит
  Anthropic. Доказано: CRLF-концы дали 2068 и красный тест; спас LF.
  Запаса 7 байт. Фикс: резать текст / выносить каталог из инструкций.
- **F4. Двойной JSON-стек.** Newtonsoft везде + System.Text.Json в
  `RuntimeToolFilter` (строки 56, 163). Проверено: оба сохраняют имена,
  бага нет — но поддерживать два стека дорого. Фикс: один стек (позже).
- **F5. set_view_orientation P1–P8** — см. `docs/teardown-orientation.md`.
  Главный: кадр математикой вместо fit, bbox по 50k элементов.
- **F6. r22–r24 не собираются** (`string.Contains(x, StringComparison)` на
  net48, `ListSheets`/`ListTitleblocks`). Чужой код, наши оболочки r25/r26
  чистые. HOLD: чинить только если понадобятся старые Revit.

## HOLD — проверить позже

- Глубина безопасности pipe (ACL, scope токена между пользователями).
- Перф bbox-сканов на больших моделях (мой хендлер; лимит 50k).
- `UpdateCheck`: заявлено «без сети» — поверить кодом.
- Установщик (install.ps1) под RvtVerify: нужен к публикации, не сейчас.
