import * as z from 'zod';
import type { Definition } from './tools.js';
import { drawingFields } from './drawing-tools.js';

const {id,coord,dimension,fill,outline,safeText}=drawingFields;
const common={request_id:z.string().min(1).max(200),document_id:id,page_index:z.number().int().positive(),deadline_seconds:z.number().int().min(1).max(3600).default(600)};
const styles={fill:fill.optional(),outline:outline.optional()};
const itemCommon={item_key:safeText.min(1).max(100),...styles,rotation_deg:z.number().min(-360000).max(360000).optional()};
const rectangle={x_mm:coord,y_mm:coord,width_mm:dimension,height_mm:dimension};
const createItem=z.discriminatedUnion('kind',[
 z.strictObject({...itemCommon,kind:z.literal('rectangle'),...rectangle}),
 z.strictObject({...itemCommon,kind:z.literal('ellipse'),...rectangle}),
 z.strictObject({...itemCommon,kind:z.literal('line'),x1_mm:coord,y1_mm:coord,x2_mm:coord,y2_mm:coord}),
 z.strictObject({...itemCommon,kind:z.literal('artistic_text'),x_mm:coord,y_mm:coord,text:safeText,font_family:safeText.min(1).max(200),font_size_pt:z.number().positive().max(10000)})
]);
const anchor={anchor_x_mm:coord,anchor_y_mm:coord};
const transform=z.discriminatedUnion('action',[
 z.strictObject({action:z.literal('move'),dx_mm:coord,dy_mm:coord}),
 z.strictObject({action:z.literal('rotate'),angle_deg:z.number().min(-360000).max(360000),...anchor}),
 z.strictObject({action:z.literal('scale'),sx:z.number().positive().max(100000),sy:z.number().positive().max(100000),...anchor}),
 z.strictObject({action:z.literal('flip'),axis:z.enum(['horizontal','vertical']),...anchor})
]);
const updateItem=z.strictObject({static_id:z.number().int().positive().max(2147483647),...styles,transform:transform.optional()})
 .refine(v=>v.fill!==undefined||v.outline!==undefined||v.transform!==undefined,'Укажите заливку, обводку или трансформацию.');
const create=z.strictObject({...common,layer_id:id,items:z.array(createItem).min(1).max(1000)})
 .refine(v=>new Set(v.items.map(i=>i.item_key)).size===v.items.length,'item_key не должны повторяться.')
 .refine(v=>v.items.reduce((n,i)=>n+(i.kind==='artistic_text'?i.text.length:0),0)<=100000,'Общий текст пакета превышает 100000 UTF-16.')
 .refine(v=>Buffer.byteLength(JSON.stringify(v),'utf8')<=1048576,'Пакет превышает 1 МиБ.');
const update=z.strictObject({...common,items:z.array(updateItem).min(1).max(1000)})
 .refine(v=>new Set(v.items.map(i=>i.static_id)).size===v.items.length,'StaticID не должны повторяться.')
 .refine(v=>Buffer.byteLength(JSON.stringify(v),'utf8')<=1048576,'Пакет превышает 1 МиБ.');
// Registered only when the native dispatcher, host catalog and documentation are ready together.
export const batchToolDefinitions:Record<string,Definition>={
 corel_create_batch:{schema:create as Definition['schema'],backend:'csharp',readOnly:false,destructive:true,description:'Создать до 1000 фигур и надписей готовой пакетной операцией. Точные document/page/layer IDs, координаты в мм. Возвращает job_id.'},
 corel_update_batch:{schema:update as Definition['schema'],backend:'csharp',readOnly:false,destructive:true,description:'Применить индивидуальный стиль и одну трансформацию к каждому из до 1000 точных StaticID. Сначала стиль, затем трансформация. Возвращает job_id.'}
};
