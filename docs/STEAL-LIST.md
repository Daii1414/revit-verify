# STEAL-LIST — что забрать у соседей (с картой посадки)

Источники прочитаны 2026-10-14 (README + структура, код не запускался).
MIT у обоих — идеи переносим свободно, код только с сохранением
уведомлений. Переписываем на C#, не тащим Python/TS.

## Взять (по приоритету)

1. **Element snapshot: isolate + section-box + capture + restore**
   От: oakplank `export_element_snapshot`, `isolate_elements_in_view`,
   `clear_temporary_isolate`.
   Зачем: крупные планы узлов для сверки (сейчас только целый вид).
   Куда: `src/shared/Handlers/IsolateElementsHandler.cs` (новый) +
   переиспользовать `CaptureViewImageHandler`; сервер в `ViewTools`.
   Усилить: вернуть isolate как часть verify-цикла.
2. **Память проекта (conventions).**
   От: oakplank `save/get_revit_memory_note`; mcp-sfr `store/query_*`.
   Зачем: единицы, стандарты именования, допуски — точность повторных
   проходов без переспроса.
   Куда: JSON в `%LOCALAPPDATA%\RvtVerify\memory\`, 2 инструмента
   (`verify_save_note`, `verify_read_notes`), read-only по умолчанию.
   Проверить пересечение с нашим ChangeHistoryStore перед кодом.
3. **Fuzzy → exact резолвер.**
   От: oakplank `resolve_revit_targets` (+ `get_revit_schema_context`).
   Зачем: меньше угадываний имён уровней/типов/параметров агентом.
   Куда: `src/shared/Handlers/ResolveTargetsHandler.cs`; сверить с
   нашим SurveyEngine, не дублировать.
4. **Dry-run/confirm на удаление.**
   От: oakplank `delete_elements` (dry-run, max-count, unpin).
   Зачем: наше удаление деструктивно без предпросмотра.
   Куда: проверить `DeleteElementHandler` — если предпросмотра нет,
   добавить `dry_run` + `max_count`. Сначала прочитать код.
5. **Дистрибуция.** От: mcp-sfr (ZIP под год + npm, release workflow).
   Зачем: понадобится к публикации.
   Куда: backlog, не сейчас (наш install = dotnet build + opencode.json).

## Не брать

- `plan_and_execute_workflow` (oakplank) — у нас есть `batch_execute`.
- SQLite-память mcp-sfr — у нас есть journal/history; сверить при п.2.
- Web UI :8000 (oakplank) — вне цели (автономный цикл, не пульт).
- Официальный Autodesk MCP (только 2027, read-only) — вернёмся при
  переезде; наш AddInId уже не конфликтует.
