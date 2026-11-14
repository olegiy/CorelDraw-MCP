# JSON-рецепты

Сначала прочитайте [контракт](recipe-contract.md) и предусловия выбранного примера. Все ID, пути и fingerprint в примерах должны быть связаны с фактическими результатами чтения; последовательность не исполняется как программа.

| Файл | Сценарий |
| --- | --- |
| [batch.json](../examples/batch.json) | Пакет до 1000 фигур/надписей, затем индивидуальное изменение; только hybrid |
| [boolean-shapes.json](../examples/boolean-shapes.json) | Булевы операции |
| [create-geometry.json](../examples/create-geometry.json) | Создание геометрии |
| [curve-nodes.json](../examples/curve-nodes.json) | Узлы и revision |
| [duplicate-offset.json](../examples/duplicate-offset.json) | Копии со смещением |
| [fill-outline.json](../examples/fill-outline.json) | Заливка и обводка |
| [import-assembly.json](../examples/import-assembly.json) | Импорт, страницы и PowerClip |
| [layer-group.json](../examples/layer-group.json) | Слой и группировка |
| [layout.json](../examples/layout.json) | Сетка и раскладка |
| [production.json](../examples/production.json) | Явная подготовка к производству |
| [text-unicode.json](../examples/text-unicode.json) | Кириллица и буквальная замена |
| [transforms.json](../examples/transforms.json) | Перенос, поворот и масштаб |

После каждой мутации выполняйте inspect и preview; при unknown/timed_out/partial не выполняйте автоматический повтор. Готовые живые сценарии в test/integration-drawing.mjs, integration-production.mjs, integration-layout.mjs и integration-import.mjs создают и закрывают только собственные документы.
