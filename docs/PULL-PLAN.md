# PULL-PLAN — очередь переноса из Horizun (125 tools, Apache-2.0)

Источник: `horizun-ref/docs/inventory.json` (полный каталог) + README.
Правило: не всё подряд — волны по ценности для verify-цикла.
Что у нас уже есть — не портируем (помечено HAVE).

## Волна 1 — ядро сверки (актуальный аудит)

1. **Частично есть `revit_verify_assert`**: bbox, точное/диапазонное
   количество и диагностика отсутствующих ID. Следом: измеряемые solid-контракты,
   ограничения scope и более точные ID нарушителей. Не называть это
   watertight/support-проверкой без корректного Revit-измерения.
2. **Delivery diff пока нет**: локальная история изменений, одиночный
   `revit_export_element_snapshot`, точная камера и `revit_capture_review_set`
   существуют раздельно. Нужен baseline/after compare с доказуемой
   идентичностью модели и честной обработкой неполных снимков.
3. **Правил QA пока нет**: версионированный контракт pass/fail/score с
   offending element IDs; начать с read-only проверок и тестируемой схемы.
4. **CAD-пайплайн частичный**: `revit_cad_extract` читает ограниченную
   геометрию уже загруженных CAD import/link. Следом — versioned rules,
   интерпретационный plan с provenance и отдельный audit CAD против модели;
   автоматическое применение не включать.
5. **Долгие фоновые jobs отсутствуют**. У истории есть фоновое восстановление
   очереди, но это не persistent job API для инструментов и не отмена/прогресс.
6. `selection_exchange` отдельно не найден; перед добавлением сверить с
   текущими инструментами выборки/сохранённых наборов и ограничениями.

## Волна 2 — строительство и модель (актуальный аудит)

7. **Уже есть** `revit_transform_elements` (move/copy/rotate),
   `revit_save_document` (Save/SaveAs с ограничениями workshared) и
   `revit_create_array` (linear/radial). Mirror не входит.
8. **Частично есть** `revit_undo_batch`: только для журнала batch-created
   элементов, не универсальный откат.
9. `plan_structure` (колонны по пересечениям осей) — не найден.
10. Renumber дверей/окон/комнат — есть renumber sheets, вариант для этих
    категорий не найден.
11. `file_info` без открытия — не найден.

## Волна 3 — DWG-боль (наш кейс с церковью)

12. **Частично есть** `revit_cad_extract`: ограниченные слои/кривые
    загруженных импортов/ссылок; это не независимый DWG-парсер и может не
    содержать исходный текст/атрибуты блоков.
13. `query_cad`, повторы/единицы, `plan_from_cad` с версионированными
    правилами и provenance, `audit_cad_model` и чтение внешних карточек —
    не найдены.

## Волна 4 — QA и приёмка

14. **Уже есть** `revit_check_model_health` (score/grade/recommendations) и
    `revit_model_audit` с предупреждениями, семействами и purge dry-run.
15. Требования/code-check, persistent findings/coordination ledger и IDS
    поставок — пробелы; приоритет после ядра verify-цикла.
16. Clash есть как поиск; propose/apply с повторной readback-проверкой
    требует отдельного безопасного дизайна.

## Волна 5 — обмен и учёт (позже)

21. `exchange` manifests + `information_container` (WIP/Shared/Published).
22. `excel_read/write_rows` без COM (ведомости!), `budget_compare` vs Excel.
23. `catalog_lookup` incl. bSDD-словари (классификаторы для verify).
24. `copy_between_documents`, `manage_styles`, `manage_units` (base points).
25. `link_schedule` (MS Project 4D) — если понадобится 4D.

## Не берём (сознательно, зафиксировано)

- MEP-routing/арматура/электрика глубоко, ACC/CDE-облака, PowerBI,
  ISO-контейнеры, phases/design-options полнота, curtain/assemblies/toPo —
  не наш этап. Куртины/фазы/группы advanced — по требованию модели.
- Python-процедуры с шеллом, BCF-топики — покрыто ToolBaker/send_code.
- In-Revit чат, MSI-установщик их образца — свой путь (dotnet + opencode).
- `execute_plan` на 100 действий: текущий batch ограничен 20; увеличение
  лимита отдельно оценить по транзакциям, откату и размеру ответа.

## Наше преимущество (у них нет — держать)

- `revit_set_view_orientation` + FrameCalculator (включая exact eye/target/up),
  `revit_export_element_snapshot`, `revit_capture_review_set` (до 8 кадров),
  локальная memory/history, `revit_resolve_targets`, undo-журнал batch,
  opencode-агенты и скрипты циклов.
