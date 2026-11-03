import * as z from 'zod';
import { win32 } from 'node:path';
import type { Arguments } from './types.js';

z.config(z.locales.ru());

const request = {
  request_id: z
    .string()
    .min(1)
    .max(200)
    .describe('Уникальный идентификатор операции; повторять только с теми же параметрами.'),
};
const deadline = {
  deadline_seconds: z
    .number()
    .int()
    .min(1)
    .max(3600)
    .default(600)
    .describe('Срок наблюдения, секунды. Истечение не отменяет COM-вызов и не разрешает повтор.'),
};
const document = {
  document_id: z
    .string()
    .min(1)
    .max(100)
    .describe('Точный идентификатор документа текущей сессии исполнителя.'),
};
export const absolutePath = z
  .string()
  .min(1)
  .max(1024)
  .refine((value) => win32.isAbsolute(value) && !value.includes('\0'), 'Нужен абсолютный путь Windows.');
const pageIndex = z.number().int().min(1).describe('Номер страницы с единицы; по умолчанию текущая.');
const shapeIds = z
  .array(z.number().int().positive().max(2147483647))
  .min(1)
  .max(100)
  .refine((ids) => new Set(ids).size === ids.length, 'StaticID не должны повторяться.');
const offset = z.number().int().min(0).max(10000000).default(0);
export const jobIdSchema = z.string().regex(/^(?:[a-f0-9-]{36}|req-[a-f0-9]{64})$/, 'Нужен точный job_id.');
interface Definition {
  schema: z.ZodType<Arguments>;
  description: string;
  readOnly: boolean;
  destructive?: boolean;
  local?: boolean;
}
export const toolDefinitions: Record<string, Definition> = {
  corel_status: {
    schema: z.strictObject({ ...deadline }),
    readOnly: true,
    description:
      'Подключиться к CorelDRAW 27: версия, VBA, состояние приложения и точные ID документов. Возвращает job_id.',
  },
  corel_inspect_document: {
    schema: z.strictObject({
      ...document,
      page_index: pageIndex.optional(),
      offset,
      limit: z.number().int().min(1).max(500).default(100),
      recursive: z.boolean().default(false),
      max_depth: z.number().int().min(1).max(32).default(8),
      max_visited: z.number().int().min(100).max(50000).default(10000),
      include_text: z.boolean().default(false),
      text_limit: z.number().int().min(1).max(10000).default(1000),
      name_filter: z.string().max(200).optional(),
      shape_types: z.array(z.number().int().min(0).max(1000)).min(1).max(100).optional(),
      static_ids: shapeIds.optional(),
      ...deadline,
    }),
    readOnly: true,
    description:
      'Ограниченная инспекция страницы и групп: StaticID, родитель, габариты, оформление, выделение. Текст только include_text=true. Возвращает job_id.',
  },
  corel_create_document: {
    schema: z.strictObject({
      ...request,
      width_mm: z.number().positive().max(100000).default(210),
      height_mm: z.number().positive().max(100000).default(297),
      ...deadline,
    }),
    readOnly: false,
    description: 'Создать несохранённый документ в миллиметрах и вернуть document_id через задание.',
  },
  corel_open_document: {
    schema: z.strictObject({ ...request, path: absolutePath, ...deadline }),
    readOnly: false,
    description: 'Открыть CDR и получить document_id. Возвращает job_id.',
  },
  corel_save_document: {
    schema: z.strictObject({
      ...request,
      ...document,
      path: absolutePath.optional(),
      overwrite: z.boolean().default(false),
      ...deadline,
    }),
    readOnly: false,
    destructive: true,
    description: 'Явно сохранить CDR; замена другого файла требует overwrite=true. Возвращает job_id.',
  },
  corel_close_document: {
    schema: z.strictObject({
      ...request,
      ...document,
      discard_changes: z.boolean().default(false),
      ...deadline,
    }),
    readOnly: false,
    destructive: true,
    description: 'Закрыть точный документ; CorelDRAW не закрывается. Возвращает job_id.',
  },
  corel_export: {
    schema: z
      .strictObject({
        ...request,
        ...document,
        path: absolutePath,
        format: z.enum(['png', 'pdf', 'svg', 'jpg', 'jpeg', 'tiff', 'tif', 'ai', 'eps', 'dxf', 'plt']),
        range: z.enum(['page', 'selection', 'document']).default('page'),
        page_index: pageIndex.optional(),
        overwrite: z.boolean().default(false),
        dpi: z.number().int().min(1).max(1200).default(150),
        color_mode: z.enum(['rgb', 'cmyk']).optional(),
        jpeg_compression: z.number().int().min(0).max(100).optional(),
        tiff_compression: z.enum(['lzw', 'none']).optional(),
        use_document_profile: z.boolean().optional(),
        transparent: z.boolean().optional(),
        text_mode: z.enum(['preserve', 'curves']).optional(),
        ...deadline,
      })
      .superRefine((args, ctx) => {
        const jpg = ['jpg', 'jpeg'].includes(args.format),
          tiff = ['tiff', 'tif'].includes(args.format),
        bitmap = jpg || tiff;
        if (args.range === 'document' && args.format !== 'pdf')
          ctx.addIssue({
            code: 'custom',
            path: ['range'],
            message: 'Весь документ поддерживает только PDF.',
          });
        if (args.range === 'selection' && args.format === 'pdf')
          ctx.addIssue({
            code: 'custom',
            path: ['range'],
            message: 'PDF поддерживает страницу или документ.',
          });
        if (args.jpeg_compression !== undefined && !jpg)
          ctx.addIssue({
            code: 'custom',
            path: ['jpeg_compression'],
            message: 'Сжатие JPEG задаётся только для jpg/jpeg.',
          });
        if (args.tiff_compression !== undefined && !tiff)
          ctx.addIssue({
            code: 'custom',
            path: ['tiff_compression'],
            message: 'Сжатие TIFF задаётся только для tiff/tif.',
          });
        if (args.transparent !== undefined && !tiff)
          ctx.addIssue({
            code: 'custom',
            path: ['transparent'],
            message: 'Прозрачность доступна только TIFF.',
          });
        if (args.color_mode !== undefined && !bitmap)
          ctx.addIssue({
            code: 'custom',
            path: ['color_mode'],
          message: 'Режим цвета доступен только JPG и TIFF.',
          });
        if (args.use_document_profile !== undefined && !bitmap)
          ctx.addIssue({
            code: 'custom',
            path: ['use_document_profile'],
          message: 'Профиль документа доступен только JPG и TIFF.',
          });
        if (args.text_mode !== undefined && !['ai', 'eps'].includes(args.format))
          ctx.addIssue({
            code: 'custom',
            path: ['text_mode'],
            message: 'Режим текста задаётся только для AI/EPS.',
          });
      }),
    readOnly: false,
    destructive: true,
    description: 'Экспорт PNG/PDF/SVG/JPEG/TIFF/AI/EPS/DXF/PLT через промежуточный файл. Возвращает job_id.',
  },
  corel_preview: {
    schema: z.strictObject({
      ...document,
      page_index: pageIndex.optional(),
      range: z.enum(['page', 'selection']).default('page'),
      max_size: z.number().int().min(64).max(2400).default(1200),
      ...deadline,
    }),
    readOnly: true,
    description: 'PNG-превью страницы или выделения. Получить изображение через corel_get_job.',
  },
  corel_build_vba: {
    schema: z.strictObject({
      ...request,
      source_root: absolutePath,
      module_paths: z.array(z.string().min(1).max(1024)).min(1).max(100).optional(),
      ...deadline,
    }),
    readOnly: false,
    description: 'Полностью скомпилировать UTF-8 .bas в отдельном GMS без выполнения. Возвращает job_id.',
  },
  corel_execute_vba: {
    schema: z.strictObject({ ...request, ...document, code: z.string().min(1).max(262144), ...deadline }),
    readOnly: false,
    destructive: true,
    description:
      'Выполнить Main() As String с JSON-результатом и восстановлением настроек. VBA имеет полный доступ к системе, это не sandbox. Возвращает job_id.',
  },
  corel_describe_api: {
    schema: z
      .strictObject({
        kind: z.enum(['types', 'members', 'enum']),
        name: z
          .string()
          .regex(/^[A-Za-z_][A-Za-z0-9_]*$/, 'Допустимо только имя типа или перечисления.')
          .max(200)
          .optional(),
        filter: z.string().max(200).default(''),
        offset,
        limit: z.number().int().min(1).max(200).default(100),
        ...deadline,
      })
      .refine((v) => v.kind === 'types' || !!v.name, {
        message: 'Для members/enum нужно имя.',
        path: ['name'],
      }),
    readOnly: true,
    description: 'Метаданные установленного API 27; выражения и вызовы запрещены. Возвращает job_id.',
  },
  corel_measure_shapes: {
    schema: z.strictObject({
      ...document,
      page_index: pageIndex.optional(),
      static_ids: shapeIds.optional(),
      tolerance_mm: z.number().min(0.001).max(1).default(0.01),
      ...deadline,
    }),
    readOnly: true,
    description:
      'Измерить векторные фигуры или выделение: минимальная ширина и прямоугольник по объектам и набору. Возвращает job_id.',
  },
  corel_add_note: {
    schema: z.strictObject({
      ...request,
      category: z.enum(['lesson', 'limitation', 'idea']),
      text: z.string().trim().min(1).max(4000),
      job_id: jobIdSchema.optional(),
    }),
    readOnly: false,
    local: true,
    description: 'Локально сохранить непроверенную находку. Текст не исполняется и не меняет скилл.',
  },
  corel_export_diagnostics: {
    schema: z.strictObject({
      ...request,
      job_id: jobIdSchema,
      path: absolutePath,
      overwrite: z.boolean().default(false),
    }),
    readOnly: false,
    destructive: true,
    local: true,
    description:
      'Создать локальный краткий технический отчёт без кода, путей и пользовательского содержимого.',
  },
  corel_list_artifacts: {
    schema: z.strictObject({ offset, limit: z.number().int().min(1).max(200).default(50) }),
    readOnly: true,
    local: true,
    description: 'Показать размеры, возраст и защищённые артефакты заданий.',
  },
  corel_prune_artifacts: {
    schema: z.strictObject({
      ...request,
      candidates: z
        .array(z.strictObject({ job_id: jobIdSchema, prune_token: z.string().regex(/^[a-f0-9]{64}$/) }))
        .min(1)
        .max(100)
        .refine(
          (items) => new Set(items.map((item) => item.job_id)).size === items.length,
          'Задания в списке очистки не должны повторяться.',
        ),
    }),
    readOnly: false,
    destructive: true,
    local: true,
    description:
      'Удалить выбранные внутренние артефакты после проверки prune_token; компактный отчёт и идемпотентность сохраняются.',
  },
};
export const getJobSchema = z.strictObject({
  job_id: z.string().min(1).max(100),
  wait_seconds: z.number().min(0).max(20).default(0),
  log_lines: z.number().int().min(0).max(200).default(50),
  include_preview: z.boolean().default(false),
});
export const guideSchema = z.strictObject({
  topic: z.enum(['skill', 'tools', 'vba', 'api', 'recipes', 'pitfalls', 'install']).optional(),
  offset,
  limit: z.number().int().min(1).max(20000).default(8000),
});
export const listArtifactsSchema = z.strictObject({
  offset,
  limit: z.number().int().min(1).max(200).default(50),
});
