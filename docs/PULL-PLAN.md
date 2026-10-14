# PULL-PLAN — очередь переноса из Horizun (125 tools, Apache-2.0)

Источник: `horizun-ref/docs/inventory.json` (полный каталог) + README.
Правило: не всё подряд — волны по ценности для verify-цикла.
Что у нас уже есть — не портируем (помечено HAVE).

## Волна 1 — ядро сверки (самое важное)

1. `verify_changes` + `model_diff` (snapshot/compare/colorize + diff
   по элементам) — у нас capture и history раздельно. Собрать в
   `revit_verify_diff` (до/после одним кадром + список изменений).
2. assert-контракты (bbox/envelope/count/support + ID виновников,
   идея rhino `assert_geometry`) — `revit_verify_assert`. Измеримое
   ядро цикла вместо «вроде ок».
3. Фоновые jobs (длинные экспорты/clash без стены 60с). Инфра,
   средний размер.
4. `selection_exchange` (человек↔агент выборками). Дешёво, полезно.

## Волна 2 — сила стройки (у нас дыры, проверено grep)

5. `transform_elements` (move/copy/rotate/mirror) — НЕТ у нас.
6. `document_session` (save/save-as/close/new) — у нас только open!
7. `create_array` (linear/radial) — НЕТ.
8. `plan_structure` (колонны по пересечениям осей — у церкви есть оси!).
9. `renumber` дверей/окон (только sheets есть).
10. `file_info` (шапка RVT без открытия). Дешёво.

## Волна 3 — DWG-боль (наш кейс с церковью)

11. `cad_extract` + `query_cad` (слои/кривые/текст/блоки структурированно).
12. `cad_symbols` + `cad_unit_instances` (повторы + трансформа).
13. `plan_from_cad` (план BIM по версионным правилам).
14. `audit_cad_model` (чертёж против модели).
15. `read_schedule` (читает PDF/DWG-карточки!). Для наших DWG-спек.

## Волна 4 — QA и приёмка

16. `check_model_health` аналог: composite score (есть у LuDattilo;
    у Horizun это model_scan + audit). Один скор вместо трёх вызовов.
17. `audit_model` по требованиям + `code_check` (egress/высота).
18. `resolve_clash` propose/apply с перепроверкой (наш clash только ищет).
19. `coordination` ledger (findings + decisions + evidence).
20. `validate_ids` (IDS-проверка поставок).

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
- `execute_plan` на 100 действий: наш batch (20) расширить лимит — отдельно.

## Наше преимущество (у них нет — держать)

- `set_view_orientation` + FrameCalculator, `export_element_snapshot`
  (isolate+section+capture+restore), memory notes, `resolve_targets`,
  undo-журнал батчей, opencode-агенты и скрипты циклов.
