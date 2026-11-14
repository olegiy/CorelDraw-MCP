import test from 'node:test';
import assert from 'node:assert/strict';
import { batchToolDefinitions } from '../dist/batch-tools.js';

const binding={request_id:'once',document_id:'exact',page_index:1};
const rectangle={item_key:'r1',kind:'rectangle',x_mm:0,y_mm:0,width_mm:12,height_mm:8};
test('create batch supports 1000 uniquely keyed items and rejects the last invalid item',()=>{
 const schema=batchToolDefinitions.corel_create_batch.schema;
 const value={...binding,layer_id:'layer',items:Array.from({length:1000},(_,i)=>({...rectangle,item_key:'r'+i}))};
 assert(schema.safeParse(value).success);
 assert(!schema.safeParse({...value,items:[...value.items,rectangle]}).success);
 assert(!schema.safeParse({...value,items:[...value.items.slice(0,-1),{...rectangle,item_key:'last',width_mm:-1}]}).success);
 assert(!schema.safeParse({...value,items:[rectangle,rectangle]}).success);
 for(const extra of [{code:'Run Anything'},{macro:'Other.Main'},{backend:'old'}])assert(!schema.safeParse({...value,...extra}).success);
});
test('text batch enforces Unicode and aggregate text limits',()=>{
 const schema=batchToolDefinitions.corel_create_batch.schema;
 const text={kind:'artistic_text',item_key:'t',text:'Ёж',x_mm:0,y_mm:0,font_family:'Arial',font_size_pt:12};
 assert(schema.safeParse({...binding,layer_id:'layer',items:[text]}).success);
 for(const value of ['🦔','\ud800','a\0b'])assert(!schema.safeParse({...binding,layer_id:'layer',items:[{...text,text:value}]}).success);
 assert(!schema.safeParse({...binding,layer_id:'layer',items:[{...text,text:'x'.repeat(60000)},{...text,item_key:'t2',text:'y'.repeat(60000)}]}).success);
});
test('update batch requires a concrete edit and unique IDs with strict transforms',()=>{
 const schema=batchToolDefinitions.corel_update_batch.schema;
 const item={static_id:4,transform:{action:'move',dx_mm:5,dy_mm:3}};
 assert(schema.safeParse({...binding,items:[item]}).success);
 for(const items of [[{static_id:4}],[item,item],[{...item,transform:{action:'move',dx_mm:Infinity,dy_mm:3}}],[{...item,transform:{action:'run',macro:'Anything'}}]])assert(!schema.safeParse({...binding,items}).success);
});
