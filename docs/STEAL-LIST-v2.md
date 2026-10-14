# STEAL-LIST v2 — широкий обзор GitHub (2026-10-14)

Surveyed ~22 repos (READMEs + structure; code not run unless noted).
Licenses: MIT/Apache-2.0 → ideas AND code (keep notices).
Sam-AEC = GPL-3.0 → IDEAS ONLY. cad2data binaries = proprietary → ideas only.
v1 (oakplank/dedupe) stays valid; this adds what v1 missed.

## P0 — прямо в цель verify-цикла

1. **assert_geometry (bbox/envelope/count/watertight/supported + offending IDs)**
   От: rhino-mcp (MIT). Частично есть `revit_verify_assert`: bbox/count и
   диагностика отсутствующих ID. Watertight/support-проверок и автоматического
   сбора ID виновников при создании нет.
2. **Delivery diffs (snapshot/compare/colorize + quality trend).**
   От: Horizun `horizun_model_diff`, naviscoord (persistent issue IDs).
   У нас: history + capture раздельно. Куда: `revit_verify_diff`
   (до/после одним кадром + diff по элементам).
3. **YAML-правила QA (pass/fail + score + GUIDs).**
   От: alesdev88/Archicad-MCP (MIT). У нас: нет. Куда: `verify_run_rules`
   с YAML-файлами (стены замкнуты? комнаты? размеры?). Сюда же
   freshness-checks от naviscoord (кадр протух — не верить).
4. **DWG→BIM pipeline (extract → plan → apply с provenance + ревизии).**
   От: Horizun `horizun_plan_from_cad` + 13 CAD-инструментов (Apache-2.0).
   Теперь есть первая read-only ступень `revit_cad_extract`: слои и ограниченная
   векторная геометрия уже загруженных CAD-import/link. Это не lossless DWG
   parser: исходный текст и атрибуты блоков могут отсутствовать. Следом нужны
   правила интерпретации, план с provenance и отдельное подтверждённое применение.

## P1 — надёжность и контекст

5. **Фоновые jobs с persistent ID** (длинные экспорты/clash).
   От: Horizun (`horizun_submit_job`), rhino (live progress).
   У нас: стена 60с timeout. Куда: job-обёртка поверх export/clash.
6. **Ленивые схемы** (abridged tools/list + exact schema on demand, −64KB).
   От: Horizun (`HORIZUN_SCHEMA_MODE=facade`). У нас: toolsets only.
   Куда: учесть в single-registration.
7. **Мульти-операционные инструменты** вместо взрыва тулзов (481 subops!).
   От: Horizun. У нас: 241 тулза и растёт. Решение: НЕ копировать слепо —
   взвесить в single-registration (компакт vs гранулярность).
8. **Рефлексивное чтение** (`get` любого свойства по пути, без компиляции).
   От: Sam-AEC `revit_reflect_get` (идея; GPL — только идея).
   У нас: только тяжёлый send_code. Куда: `revit_reflect_get(element, path)`.
9. **Мок-режим сервера** (фейковые ответы без Revit).
   От: Sam-AEC, AB MockBridge. У нас: нет. Куда: тесты/разработка.

## P2 — дыры в каталоге (проверено grep: у нас НЕТ)

10. **transform_elements** — уже есть `revit_transform_elements` (move/copy/rotate).
    Mirror в этот инструмент не входит.
11. **save_document / save-as** — уже есть `revit_save_document`; SaveAs для
    workshared-моделей запрещён.
12. **check_model_health** — уже есть `revit_check_model_health` с оценкой,
    grade и рекомендациями.
13. **create_array** — уже есть `revit_create_array` (linear/radial).
14. **renumber** дверей/окон/комнат (только sheets есть).
15. **horizun_undo** — частично есть `revit_undo_batch` для созданных элементов
    `batch_execute`, с журналом и защитой от изменения модели после батча.
    Универсальный откат любого verified-батча пока не реализован.
16. **CRUD согласований/стен/слоёв тоньше**: wall joins, curtain grids,
    phases/design options mgmt, assemblies/parts, HQ schedules (Horizun);
    conduit QA + bend totals (schauh11); panel schedules (Demolinator).

## P3 — инфраструктура и честность

17. **Doctor без Revit** (проверка манифеста/сборок по метаданным, AB).
    У нас: только живые проверки. Куда: `RvtVerify.Server.exe --doctor`.
18. **Self-contained publish + per-user install** (AB). У нас: нужен SDK.
    Куда: backlog публикации.
19. **AGENTS-инструкции в репо** (cad2data AI_AGENTS_INSTRUCTIONS/OPENCODE.md,
    revit-skills skill-паки, rhino-architect skill). У нас: один skill.
    Куда: `agents/` паки (verify-loop, revit-api, testing).
20. **Version-drift тест** (4 файла + тег, alesdev88). У нас: только counts.
    Куда: скопировать приём.
21. **Честные лимиты в доках** (UNVERIFIED-метки Sam-AEC, release evidence
    Horizun/naviscoord, crash-предупреждения alesdev88). У нас частично.
    Куда: стиль вести всегда.
22. **Память в модели** (rhino: brief/rules в .3dm едут с файлом) vs наш
    локальный JSON. Подумать: что едет с .rvt?
23. **Эскалация прав по явному согласию** (owner-gated Python, Horizun;
    confirm-интерлок AB; elicitation rhino). Наш send_code гейтится
    adaptive-bake — ок, но confirm-флаг унифицировать.
24. **IFC без Revit** (IfcOpenShell: Sam-AEC провайдер, bonsai-mcp,
    cad2data конвертеры). Направление: чтение IFC/DWG офлайн для сверки.
25. **Классификации** (bSDD-mcp словари, Uniclass/Omniclass RAG у DDC).
    Куда: обогащение справочников при verify.
26. **Сессионный distiller** (WAL → eval-отчёт, rhino). Наш journal мог бы.

## Не брать (сознательно)

- In-Revit чат-панели с API-ключами (LuDattilo/ZedMoster/schauh11/tell2jyoti):
  мы OpenCode-first, ключи не храним.
- Web UI + LAN (oakplank/Demolinator): вне цели (автономия, не пульт).
- Baked Python с шеллом (Horizun procedures): покрыто ToolBaker.
- Полный мердж ЧУЖОГО дерева (Horizun 125 тулзов): брать точечно, иначе
  утонем в чужой архитектуре. Исключение: изучить их DWG-план (идеи).
- GPL-код Sam-AEC: только идеи, ни строки.
- Piped chat/MSC Project/Navisworks/PowerBI/Excel-направления: не наш скоуп
  сейчас (записано, не делаем).
