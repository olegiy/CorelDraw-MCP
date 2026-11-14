import assert from 'node:assert/strict';
import {mkdirSync,readFileSync,writeFileSync} from 'node:fs';
import {randomUUID,createHash} from 'node:crypto';
import {join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {call,connect,operation} from './helpers.mjs';
import {rgb} from '../scripts/bench-hybrid.mjs';
import {decodePng} from '../scripts/benchmark-artifacts.mjs';
if(process.env.CORELDRAW_WORKER_LIVE!=='1')throw new Error('Нужен CORELDRAW_WORKER_LIVE=1.');
const directory=fileURLToPath(new URL(`../artifacts/integration-hybrid/${randomUUID()}/`,import.meta.url));mkdirSync(directory,{recursive:true});
const session=await connect({executionMode:'hybrid',runs:join(directory,'runs')}),owned=new Set(),evidence=[],cleanup=[];
let primary,smokeOperations=0;
async function ok(tool,args={}) {
  const job=await operation(session,tool,args,300000);evidence.push({tool,job_id:job.job_id,status:job.status,versions:job.versions,result:job.result,error:job.error,cleanup:job.cleanup});
  assert.equal(job.status,'succeeded',JSON.stringify(job.error));assert.equal(job.cleanup?.completed,true);return job;
}
async function create() {const doc=(await ok('corel_create_document',{request_id:randomUUID(),width_mm:760,height_mm:490})).result.document_id;owned.add(doc);return doc;}
async function inspect(doc) {
  const first=(await ok('corel_inspect_document',{document_id:doc,recursive:true,include_text:true,limit:500})).result,shapes=[...first.shapes];let offset=first.next_offset;
  while(offset!==null){const page=(await ok('corel_inspect_document',{document_id:doc,recursive:true,include_text:true,limit:500,offset})).result;shapes.push(...page.shapes);offset=page.next_offset;}
  return {...first,shapes};
}
const near=(actual,expected)=>assert(Math.abs(actual-expected)<=.001,`${actual} != ${expected}`);
try {
  const baseline=(await ok('corel_status')).result;
  const doc=await create(),initial=await inspect(doc),layer_id=initial.layers.find(l=>!l.is_special_layer).layer_id;
  const request_id=randomUUID(),args={request_id,document_id:doc,page_index:1,layer_id,items:Array.from({length:20},(_,i)=>({item_key:'r'+i,kind:'rectangle',x_mm:10+i*20,y_mm:10,width_mm:12,height_mm:8,fill:{kind:'uniform',color:{model:'rgb',r:80,g:120,b:180}},outline:{kind:'none'}}))};
  const first=await ok('corel_create_batch',args),same=await call(session,'corel_create_batch',args);assert.equal(same.job_id,first.job_id);
  smokeOperations++;
  const ids=first.result.created_static_ids;assert.equal(ids.length,20);
  for(let i=0;i<99;i++) {
    const result=await ok('corel_update_batch',{request_id:randomUUID(),document_id:doc,page_index:1,items:ids.map(static_id=>({static_id,transform:{action:'move',dx_mm:i%2===0?1:-1,dy_mm:0}}))});
    assert.equal(result.versions.execution_engine,'vba');assert.equal(result.result.items.length,20);
    smokeOperations++;
    result.result.items.forEach((item,index)=>near(item.bounds_mm.left,10+index*20+(i%2===0?1:0)));
  }
  const smoke=await inspect(doc);assert.equal(smoke.total_shapes,20);smoke.shapes.forEach(s=>near(s.bounds_mm.left,11+ids.indexOf(s.static_id)*20));
  await ok('corel_save_document',{request_id:randomUUID(),document_id:doc,path:join(directory,'100-native-operations.cdr')});
  const atlas=await create(),atlasInitial=await inspect(atlas),atlasLayer=atlasInitial.layers.find(l=>!l.is_special_layer).layer_id;
  const result=await ok('corel_create_batch',{request_id:randomUUID(),document_id:atlas,page_index:1,layer_id:atlasLayer,items:Array.from({length:1000},(_,i)=>{
    const [r,g,b]=rgb(i);return {item_key:'c'+i,kind:'rectangle',x_mm:20+(i%40)*18,y_mm:20+Math.floor(i/40)*18,width_mm:12,height_mm:8,rotation_deg:i%360,fill:{kind:'uniform',color:{model:'rgb',r,g,b}},outline:{kind:'none'}};
  })});
  assert.equal(result.result.created_static_ids.length,1000);const actual=await inspect(atlas),map=new Map(actual.shapes.map(s=>[s.static_id,s]));assert.equal(map.size,1000);
  result.result.items.forEach((item,i)=>{
    const s=map.get(item.static_id),[r,g,b]=rgb(i);assert.deepEqual(s.appearance.fill_color,{model:'rgb',r,g,b});assert.equal(s.appearance.outline_type,0);near(s.rotation,i%360);
    near(s.bounds_mm.left+s.bounds_mm.width/2,26+(i%40)*18);near(s.bounds_mm.bottom+s.bounds_mm.height/2,24+Math.floor(i/40)*18);
  });
  const path=join(directory,'Атлас 1000 плашек.cdr');await ok('corel_save_document',{request_id:randomUUID(),document_id:atlas,path});
  await ok('corel_export',{request_id:randomUUID(),document_id:atlas,format:'png',path:join(directory,'atlas.png'),dpi:96});
  const png=readFileSync(join(directory,'atlas.png')),pixels=decodePng(png);assert(pixels.painted>10000);
  writeFileSync(join(directory,'atlas-artifacts.json'),JSON.stringify({cdr:path,png_sha256:createHash('sha256').update(png).digest('hex'),painted_pixels:pixels.painted,rgba_sha256:createHash('sha256').update(pixels.rgba).digest('hex')},null,2));
  const after=(await ok('corel_status')).result;
  for(const source of baseline.documents)assert.deepEqual(after.documents.find(d=>d.document_id===source.document_id),source);
} catch(error){primary=error;}
finally {
  for(const document_id of owned)try {await ok('corel_close_document',{request_id:randomUUID(),document_id,discard_changes:true});cleanup.push({document_id,closed:true});}
  catch(error){cleanup.push({document_id,closed:false,error:String(error)});primary??=error;}
  writeFileSync(join(directory,'verification.json'),JSON.stringify({passed:!primary,error:primary&&String(primary),native_operations_in_smoke:smokeOperations,cleanup,evidence},null,2));await session.close();
}
if(primary)throw primary;
console.log(`Гибрид: 100 native-операций и 1000 проверенных плашек: ${directory}`);
