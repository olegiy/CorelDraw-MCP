# Рецепты VBA

Примеры в `examples/` — стандартные UTF-8 модули с `Public Function Main() As String`, кроме библиотечного `M_CommonHelpers.bas` без Main. Перед `corel_execute_vba` укажите точный `document_id`; сервер создаёт временный GMS и внешнюю Undo-группу.

Новые рецепты `M_TextUnicode.bas`, `M_FillOutline.bas`, `M_DuplicateOffset.bas`,
`M_LayerGroup.bas`, `M_BooleanShapes.bas` и `M_CurveNodes.bas` используют
общие `JsonNumber`, `JsonString`, `MmToDocumentUnits` и
`DocumentUnitsToMm` из `M_CommonHelpers.bas`. Для `corel_build_vba` передайте
helper и рецепт как отдельные `module_paths`. Для `corel_execute_vba`
добавьте процедуры helper в один модуль ПОСЛЕ рецепта, удалив из helper
`Option Explicit`: директива и все `Private Const` рецепта должны находиться
до первой процедуры. Приватных копий helper в этих шести рецептах нет.
Перед вызовом замените константы StaticID (по умолчанию 0, явный отказ) на
числа, полученные инспекцией нужного document_id и страницы. Не подставляйте
выражения или индексы фигур. Пример: `TARGET_STATIC_ID As Long = 123` в
`M_DuplicateOffset.bas`, затем `corel_execute_vba(request_id, document_id, code)`.

| Файл | Паттерн | Измеряемый результат |
| --- | --- | --- |
| `M_CreateGeometry.bas` | прямоугольник, RGB-заливка и обводка | фактические размеры, RGB и ширина обводки |
| `M_Text.bas` | ArtisticText, редактирование Unicode и стиль | фактический текст, имя, шрифт и размер |
| `M_Transforms.bas` | Rotate/Move/SetSize | итоговые угол, координаты и размеры |
| `M_Duplicate.bas` | Duplicate со смещением | ID источника/копий, число и фактическое смещение |
| `M_LayersGroups.bas` | собственный слой и Group | ID группы и членов |
| `M_StaticFind.bas` | рекурсивный поиск по StaticID | найденный объект и ширина |
| `M_Boolean.bas` | Intersect двух собственных фигур | StaticID результата |
| `M_Weld.bas` | Weld двух собственных фигур | ID источника, цели и результата |
| `M_Trim.bas` | Trim собственной фигуры резаком | ID источника, резака и результата |
| `M_Style.bas` | RGB-заливка и обводка | цвет и ширина |
| `M_JsonLocale.bas` | приватные функции мм и экранирования JSON | 1 дюйм = 25.4 мм, строка с кавычками |
| `M_TextUnicode.bas` | TEXT_KIND artistic/paragraph, Unicode | StaticID, текст, шрифт, размер |
| `M_FillOutline.bas` | TARGET_STATIC_ID, COLOR_MODE rgb/cmyk | StaticID, color_mode, ширина контура в мм |
| `M_DuplicateOffset.bas` | Duplicate с метрическим смещением | IDs источника/копии, фактическое смещение |
| `M_LayerGroup.bas` | FIRST/SECOND_STATIC_ID; найти/создать LAYER_NAME | имя слоя, ID группы и только заданных членов |
| `M_BooleanShapes.bas` | LEFT/RIGHT_STATIC_ID; BOOLEAN_OPERATION weld/trim/intersect | IDs входов/результата, ширина в мм |
| `M_CurveNodes.bas` | TARGET_STATIC_ID готовой кривой, без преобразования | количество и типы узлов/сегментов |

`M_MoveSelection` и `M_StaticFind` требуют предварительно проверенного выделения в целевом документе. Старые демонстрационные модули создают свои фигуры; новые адресные рецепты изменяют только указанные StaticID. `M_StaticFind` ищет тот же StaticID по слоям/группам активной страницы и измеряет ширину.

Перед изменением примеры проверяют наличие открытого документа и доступность активного слоя; сценарии выделения дополнительно проверяют страницу/слои объектов. Weld/Trim используют собственные фигуры с сохранением исходников. Эти проверки не заменяют предварительную инспекцию правильного document_id.

Числа сериализуйте через `Replace(Trim$(Str$(value)), ",", ".")` (включая ведущий ноль для значений меньше единицы), строки — через JSON-экранирование всех управляющих символов. Не используйте `Evaluate`, `Application.Quit`, диалоги или вложенный `BeginCommandGroup`.

Адресные рецепты проверяют слой целевого объекта, не активного слоя. Boolean
принимает только rectangle/ellipse/curve на одном слое и сохраняет оба входа;
Trim вычитает левую фигуру из правой. Группировка принимает две различные
фигуры верхнего уровня, не захватывает остальные фигуры слоя и отклоняет
неоднозначное имя/заблокированный слой. Чтение узлов не меняет исходник,
принимает только curve и ограничено 10000 узлов/сегментов.
Пустое выделение не подменяет входные StaticID; `RequireSingleSelection`
отказывает при количестве не равном одному, preview selection даёт EMPTY_SELECTION.

Helpers ограничивают поиск глубиной 8 и 10000 посещений; ограниченная группа
не прерывает поиск в соседних ветках. Если цель не найдена и обход усечён,
возвращается SEARCH_LIMIT, а не ложное отсутствие. STALE/foreign document_id
отклоняется worker до запуска VBA; StaticID ищется только на целевой странице.
Одинаковое числовое StaticID другого документа не даёт прав на доступ к нему.

Рецепты создания/Boolean/дублирования/группировки сохраняют до 100 выделенных
фигур и восстанавливают их после успеха/ошибки. Группировка выбранных членов
может не позволить Corel восстановить то же выделение: тогда Main ошибочен,
а группа уже могла быть создана. Нет обещания rollback. Структурированные
`RECIPE_VALIDATION_FAILED`/`RECIPE_ACTION_FAILED` различаются по `error.applied`:
false до первого изменяющего вызова, true если действие могло примениться.
Ошибка восстановления выделения видна как `cleanup.errors[step=recipe.selection]`.
Даже при cleanup.completed=true частичное действие не откатывается.

Проверка: `node test/integration-recipes.mjs` создаёт собственные документы,
проверяет мм/дюймы, варианты рецептов, stale ID, locked layer, типы, JSON,
forced failure, выделение и отсутствие изменений второго документа. Отчёт
verification.json записывается после закрытия собственных документов.
