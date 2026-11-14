import * as z from 'zod';
import { win32 } from 'node:path';
import type { Definition } from './tools.js';

const id = z.string().min(1).max(100);
const coord = z.number().min(-1000000).max(1000000);
const dimension = z.number().positive().max(100000);
const ids = z.array(z.number().int().positive().max(2147483647)).min(1).max(100)
  .refine(values => new Set(values).size === values.length, 'StaticID не должны повторяться.');
const common = {
  request_id: z.string().min(1).max(200), document_id: id,
  page_index: z.number().int().positive(),
  deadline_seconds: z.number().int().min(1).max(3600).default(600),
};
const readBinding = { document_id: common.document_id, page_index: common.page_index, deadline_seconds: common.deadline_seconds };
const singleId = z.number().int().positive().max(2147483647);
const safeText = z.string().max(100000).refine(value => !/[\u0000\uD800-\uDFFF]/u.test(value) && !/[^\u0000-\uFFFF]/u.test(value),
  'CorelDRAW 27 не поддерживает NUL, суррогаты и символы вне BMP; документ не изменён.');
const name = safeText.min(1).max(200);
const rect = { x_mm: coord, y_mm: coord, width_mm: dimension, height_mm: dimension };
const item = z.discriminatedUnion('kind', [
  z.strictObject({ kind: z.literal('rectangle'), ...rect }),
  z.strictObject({ kind: z.literal('ellipse'), ...rect }),
  z.strictObject({ kind: z.literal('line'), x1_mm: coord, y1_mm: coord, x2_mm: coord, y2_mm: coord }),
]);
const color = z.discriminatedUnion('model', [
  z.strictObject({ model: z.literal('rgb'), r: z.number().int().min(0).max(255), g: z.number().int().min(0).max(255), b: z.number().int().min(0).max(255) }),
  z.strictObject({ model: z.literal('cmyk'), c: z.number().int().min(0).max(100), m: z.number().int().min(0).max(100), y: z.number().int().min(0).max(100), k: z.number().int().min(0).max(100) }),
]);
const fill = z.discriminatedUnion('kind', [z.strictObject({ kind: z.literal('none') }), z.strictObject({ kind: z.literal('uniform'), color })]);
const outline = z.discriminatedUnion('kind', [z.strictObject({ kind: z.literal('none') }), z.strictObject({ kind: z.literal('uniform'), color, width_mm: dimension })]);
export const drawingFields = { id, coord, dimension, fill, outline, safeText };
const anchor = { anchor_x_mm: coord, anchor_y_mm: coord };
const transform = z.discriminatedUnion('action', [
  z.strictObject({ ...common, static_ids: ids, action: z.literal('move'), dx_mm: coord, dy_mm: coord }),
  z.strictObject({ ...common, static_ids: ids, action: z.literal('scale'), sx: z.number().positive().max(100000), sy: z.number().positive().max(100000), ...anchor }),
  z.strictObject({ ...common, static_ids: ids, action: z.literal('rotate'), angle_deg: z.number().min(-360000).max(360000), ...anchor }),
  z.strictObject({ ...common, static_ids: ids, action: z.literal('flip'), axis: z.enum(['horizontal', 'vertical']), ...anchor }),
]).meta({ type: 'object' });
const layers = z.discriminatedUnion('action', [
  z.strictObject({ ...common, action: z.literal('create'), page_id: id, name }),
  z.strictObject({ ...common, action: z.literal('rename'), layer_id: id, name }),
  z.strictObject({ ...common, action: z.literal('set_flags'), layer_id: id, visible: z.boolean().optional(), editable: z.boolean().optional(), printable: z.boolean().optional() })
    .refine(value => value.visible !== undefined || value.editable !== undefined || value.printable !== undefined, 'Укажите хотя бы один флаг.'),
  z.strictObject({ ...common, action: z.literal('move_shapes'), layer_id: id, static_ids: ids }),
]).meta({ type: 'object' });
const textCommon = { ...common, layer_id: id, text: safeText, font_family: name, font_size_pt: z.number().positive().max(10000) };
const text = z.discriminatedUnion('kind', [
  z.strictObject({ ...textCommon, kind: z.literal('artistic'), x_mm: coord, y_mm: coord }),
  z.strictObject({ ...textCommon, kind: z.literal('paragraph'), ...rect }),
]).meta({ type: 'object' });
function tool(schema: z.ZodType, description: string): Definition {
  return { schema: schema as Definition['schema'], readOnly: false, destructive: true, backend: 'csharp', description };
}
const point = z.strictObject({ x_mm: coord, y_mm: coord });
const curveSegment = z.discriminatedUnion('kind', [
  z.strictObject({ kind: z.literal('line'), to: point }),
  z.strictObject({ kind: z.literal('cubic'), to: point, control1: point, control2: point }),
]);
const subpath = z.strictObject({ start: point, segments: z.array(curveSegment).min(1).max(9999), closed: z.boolean() })
  .refine(path => !path.closed || path.segments.length >= 2, 'Замкнутый контур требует не менее трёх узлов.');
const curveEditBinding = { ...common, static_id: singleId, expected_revision: z.string().regex(/^[a-f0-9]{64}$/) };
const index = z.number().int().positive().max(10000);
const curveEdit = z.discriminatedUnion('action', [
  z.strictObject({ ...curveEditBinding, action: z.literal('move_node'), subpath_index: index, node_index: index, x_mm: coord, y_mm: coord }),
  z.strictObject({ ...curveEditBinding, action: z.literal('insert_node'), subpath_index: index, segment_index: index, t: z.number().gt(0).lt(1) }),
  z.strictObject({ ...curveEditBinding, action: z.literal('delete_node'), subpath_index: index, node_index: index }),
  z.strictObject({ ...curveEditBinding, action: z.literal('set_node_type'), subpath_index: index, node_index: index, node_type: z.enum(['cusp', 'smooth', 'symmetrical']) }),
  z.strictObject({ ...curveEditBinding, action: z.literal('close_subpath'), subpath_index: index }),
  z.strictObject({ ...curveEditBinding, action: z.literal('join_endpoints'), first_subpath_index: index, first_end: z.enum(['start', 'end']), second_subpath_index: index, second_end: z.enum(['start', 'end']), tolerance_mm: z.number().min(0).max(100000) })
    .refine(value => value.first_subpath_index !== value.second_subpath_index || value.first_end !== value.second_end, 'Нельзя соединять конец с самим собой.'),
]).meta({ type: 'object' });
const editText = z.discriminatedUnion('action', [
  z.strictObject({ ...common, static_ids: ids, action: z.literal('replace_literal'), old: safeText.min(1), new: safeText, case_sensitive: z.boolean() }),
  z.strictObject({ ...common, static_ids: ids, action: z.literal('set_text'), text: safeText }),
  z.strictObject({ ...common, static_ids: ids, action: z.literal('format'), font_family: name.optional(), font_size_pt: z.number().positive().max(10000).optional(), bold: z.boolean().optional(), italic: z.boolean().optional(), alignment: z.enum(['left', 'center', 'right', 'justify', 'full_justify']).optional(), line_spacing_percent: z.number().positive().max(1000).optional() })
    .refine(value => ['font_family', 'font_size_pt', 'bold', 'italic', 'alignment', 'line_spacing_percent'].some(key => value[key as keyof typeof value] !== undefined), 'Укажите хотя бы одно свойство форматирования.'),
]).meta({ type: 'object' });
const nonnegativeDimension = z.number().min(0).max(100000);
const preflightCheck = z.discriminatedUnion('kind', [
  z.strictObject({ kind: z.literal('open_contours') }),
  z.strictObject({ kind: z.literal('text_present') }),
  z.strictObject({ kind: z.literal('dimensions'), min_width_mm: nonnegativeDimension, max_width_mm: nonnegativeDimension, min_height_mm: nonnegativeDimension, max_height_mm: nonnegativeDimension, tolerance_mm: nonnegativeDimension })
    .refine(value => value.min_width_mm <= value.max_width_mm && value.min_height_mm <= value.max_height_mm, 'Минимальный размер не должен превышать максимальный.'),
  z.strictObject({ kind: z.literal('outline_width'), expected_mm: nonnegativeDimension, tolerance_mm: nonnegativeDimension }),
  z.strictObject({ kind: z.literal('outline_color'), color, tolerance: z.number().int().min(0).max(255) })
    .refine(value => value.color.model !== 'cmyk' || value.tolerance <= 100, 'Допуск CMYK не должен превышать 100.'),
]);
function references(extra: z.ZodRawShape) {
  const fields = { ...common, static_ids: ids, axis: z.enum(['x', 'y']), ...extra };
  return [z.strictObject({ ...fields, reference: z.literal('page') }), z.strictObject({ ...fields, reference: z.literal('selection') }),
    z.strictObject({ ...fields, reference: z.literal('shape'), reference_id: singleId })] as const;
}
const align = z.union([
  ...references({ action: z.literal('align'), align: z.enum(['min', 'center', 'max']) }),
  ...references({ action: z.literal('distribute'), distribution: z.literal('equal_gaps'), static_ids: ids.min(2) }),
]).meta({ type: 'object' });
const layoutFields = { ...common, static_ids: ids, margin_left_mm: nonnegativeDimension.default(0), margin_right_mm: nonnegativeDimension.default(0),
  margin_top_mm: nonnegativeDimension.default(0), margin_bottom_mm: nonnegativeDimension.default(0), gap_x_mm: nonnegativeDimension.default(0), gap_y_mm: nonnegativeDimension.default(0) };
const layout = z.union([
  z.strictObject({ ...layoutFields, dry_run: z.literal(true).default(true) }),
  z.strictObject({ ...layoutFields, dry_run: z.literal(false), expected_revision: z.string().regex(/^[a-f0-9]{64}$/) }),
]).meta({ type: 'object' });
const importFields={...common,path:z.string().min(1).max(1024).refine(value=>win32.isAbsolute(value)&&/^[A-Za-z]:[\\/]/.test(value)&&!value.includes('\0'),'Нужен абсолютный путь на локальном диске Windows.'),
  sha256:z.string().regex(/^[a-f0-9]{64}$/),size_bytes:z.number().int().min(1).max(256*1024*1024),layer_id:id,x_mm:coord,y_mm:coord,
  fit:z.strictObject({width_mm:dimension,height_mm:dimension}).optional()};
const importSchema=z.discriminatedUnion('format',[
  z.strictObject({...importFields,format:z.literal('svg')}),z.strictObject({...importFields,format:z.literal('png')}),
  z.strictObject({...importFields,format:z.literal('jpeg')}),z.strictObject({...importFields,format:z.literal('pdf'),pdf_page:z.number().int().min(1).max(100000)}),
]).meta({type:'object'});
const pages=z.discriminatedUnion('action',[
  z.strictObject({...common,action:z.literal('add'),count:z.number().int().min(1).max(100),width_mm:dimension,height_mm:dimension}),
  z.strictObject({...common,action:z.literal('resize'),page_id:id,width_mm:dimension,height_mm:dimension}),
]).meta({type:'object'});
const powerclip=z.discriminatedUnion('action',[
  z.strictObject({...common,action:z.literal('place'),container_id:singleId,static_ids:ids,center:z.boolean().default(false)})
    .refine(value=>!value.static_ids.includes(value.container_id),'Контейнер нельзя поместить в самого себя.'),
  z.strictObject({...common,action:z.literal('extract'),container_id:singleId}),
]).meta({type:'object'});
export const drawingToolDefinitions: Record<string, Definition> = {
  corel_import:tool(importSchema,'Импортировать один локальный SVG/PDF/PNG/JPEG до 256 МиБ по SHA-256 и размеру, на точный слой. PDF требует одну явную страницу; fit сохраняет пропорции. Без clipboard и диалогового fallback. Возвращает job_id.'),
  corel_manage_pages:tool(pages,'Добавить до 100 страниц заданного размера либо изменить размер точной page_id. Удаление и перестановка не поддерживаются. Возвращает job_id.'),
  corel_powerclip:tool(powerclip,'Поместить точные фигуры в контейнер PowerClip без изменения размеров или извлечь содержимое, сохраняя контейнер. Center=false по умолчанию; циклы запрещены. Возвращает job_id.'),
  corel_align_shapes: tool(align, 'Выровнять geometry bounds без обводки либо распределить equal gaps по оси x/y. Reference page/selection/shape; shape требует reference_id. Возвращает job_id.'),
  corel_grid_duplicate: tool(z.strictObject({ ...common, static_ids: ids, rows: z.number().int().min(1).max(1000), columns: z.number().int().min(1).max(1000),
    gap_x_mm: nonnegativeDimension.default(0), gap_y_mm: nonnegativeDimension.default(0), anchor_x_mm: coord, anchor_y_mm: coord })
    .refine(value => (value.rows * value.columns - 1) * value.static_ids.length <= 1000, 'Допустимо до 1000 новых фигур с учётом групп.'), 'Сетка по общему bounding box с обводкой; исходники образуют первую ячейку. Anchor — нижний левый угол первой ячейки в мм, до 1000 новых фигур. Возвращает job_id.'),
  corel_layout_shapes: tool(layout, 'Детерминированная shelf-раскладка bounding boxes с обводкой, без поворотов/масштабирования/копий. Dry_run по умолчанию; apply требует revision и отсутствие unplaced. Не является фигурным nesting. Возвращает job_id.'),
  corel_inspect_curve: { ...tool(z.strictObject({ ...readBinding, static_id: singleId, offset: z.number().int().min(0).max(10000).default(0), limit: z.number().int().min(1).max(500).default(100) }), 'Прочитать узлы/сегменты точной кривой и полный geometry_revision. Пагинация ограничивает выдачу, а не revision. Возвращает job_id.'), readOnly: true },
  corel_create_curve: tool(z.strictObject({ ...common, layer_id: id, subpaths: z.array(subpath).min(1).max(5000) })
    .refine(value => value.subpaths.reduce((total, path) => total + path.segments.length + 1, 0) <= 10000, 'Допустимо не более 10000 узлов.'), 'Создать кривую из явных line/cubic сегментов и нескольких subpaths. Координаты в мм; до 10000 узлов. Возвращает job_id.'),
  corel_edit_curve: tool(curveEdit, 'Выполнить одну правку кривой при совпадении expected_revision. Индексы с единицы; join только концов в пределах tolerance_mm. Возвращает новый revision через job_id.'),
  corel_edit_text: tool(editText, 'Буквально заменить, задать текст или формат всей истории точных текстовых фигур. Replace сохраняет форматирование за пределами совпадений; regex не поддерживается. Возвращает job_id.'),
  corel_find_text: { ...tool(z.strictObject({ ...readBinding, query: safeText.min(1), case_sensitive: z.boolean(), offset: z.number().int().min(0).max(50000).default(0), limit: z.number().int().min(1).max(500).default(100), include_text: z.boolean().default(false), snippet_limit: z.number().int().min(1).max(1000).default(200), max_visited: z.number().int().min(100).max(50000).default(10000) }), 'Буквальный поиск текста на точной странице без изменений. Фрагменты только include_text=true; regex запрещён. Возвращает job_id.'), readOnly: true },
  corel_preflight: { ...tool(z.strictObject({ ...readBinding, static_ids: ids, checks: z.array(preflightCheck).min(1).max(20) }), 'Проверить контуры, размеры, обводку и наличие текста без исправлений. passed/failed/unknown по каждому правилу; не гарантирует пригодность станку. Возвращает job_id.'), readOnly: true },
  corel_create_shapes: tool(z.strictObject({ ...common, layer_id: id, items: z.array(item).min(1).max(100) }), 'Создать до 100 прямоугольников, эллипсов или линий на точном слое. Координаты в мм от нижнего левого угла страницы. Возвращает job_id.'),
  corel_transform_shapes: tool(transform, 'Переместить, масштабировать, повернуть или отразить точные StaticID. Явный anchor в мм; scale только положительный. Возвращает job_id.'),
  corel_duplicate_shapes: tool(z.strictObject({ ...common, static_ids: ids, copies: z.number().int().min(1).max(1000), step_x_mm: coord, step_y_mm: coord })
    .refine(value => value.static_ids.length * value.copies <= 1000, 'Допустимо до 1000 новых фигур, включая содержимое групп.'), 'Дублировать точные StaticID с заданным шагом в мм, сохраняя исходники. До 1000 новых фигур с учётом групп. Возвращает job_id.'),
  corel_style_shapes: tool(z.strictObject({ ...common, static_ids: ids, fill: fill.optional(), outline: outline.optional() })
    .refine(value => value.fill !== undefined || value.outline !== undefined, 'Укажите fill или outline.'), 'Задать явную заливку и/или обводку точных StaticID. Цвет RGB/CMYK, ширина в мм; отсутствующие свойства сохраняются. Возвращает job_id.'),
  corel_manage_layers: tool(layers, 'Создать, переименовать слой, изменить его флаги или перенести точные фигуры. Существующий слой задаётся opaque layer_id. Возвращает job_id.'),
  corel_group_shapes: tool(z.discriminatedUnion('action', [
    z.strictObject({ ...common, action: z.literal('group'), static_ids: ids.min(2) }),
    z.strictObject({ ...common, action: z.literal('ungroup'), static_ids: ids.length(1) }),
  ]).meta({ type: 'object' }), 'Сгруппировать фигуры одного слоя или разгруппировать ровно одну группу на один уровень. Возвращает job_id.'),
  corel_boolean_shapes: tool(z.strictObject({ ...common, action: z.enum(['weld', 'trim', 'intersect']), left_id: z.number().int().positive().max(2147483647), right_id: z.number().int().positive().max(2147483647), keep_left: z.boolean(), keep_right: z.boolean() })
    .refine(value => value.left_id !== value.right_id, 'Левый и правый StaticID должны различаться.'), 'Weld/trim/intersect двух фигур с явными keep_left/keep_right. Trim: LEFT вырезает RIGHT. Возвращает фактические ID через job_id.'),
  corel_create_text: tool(text, 'Создать artistic или paragraph текст с явным установленным шрифтом. BMP Unicode; неподдерживаемые символы отклоняются до мутации. Возвращает job_id.'),
  corel_convert_to_curves: tool(z.strictObject({ ...common, static_ids: ids }), 'Преобразовать точные поддержанные фигуры или текст в кривые после проверки всех целей. Bitmap и эффекты не поддерживаются. Возвращает job_id.'),
};
