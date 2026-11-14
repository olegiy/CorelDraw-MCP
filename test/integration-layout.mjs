import assert from 'node:assert/strict';
import { mkdirSync, writeFileSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import { connect, operation } from './helpers.mjs';
if(process.env.CORELDRAW_WORKER_LIVE!=='1'||process.env.CORELDRAW_MCP_BACKEND!=='csharp')throw new Error('Нужны CORELDRAW_WORKER_LIVE=1 и CORELDRAW_MCP_BACKEND=csharp.');
const directory=fileURLToPath(new URL(`../artifacts/integration-layout/${randomUUID()}/`,import.meta.url));
mkdirSync(directory,{recursive:true});const session=await connect({runs:join(directory,'runs')});
const owned=[],evidence=[],cleanup=[];let primary;
async function ok(tool,args={}){const job=await operation(session,tool,args);evidence.push({tool,job_id:job.job_id,status:job.status,result:job.result,error:job.error,cleanup:job.cleanup});assert.equal(job.status,'succeeded',JSON.stringify(job.error));assert.equal(job.cleanup?.completed,true);return job.result;}
async function create(){const doc=await ok('corel_create_document',{request_id:randomUUID(),width_mm:200,height_mm:150});owned.push(doc.document_id);return doc.document_id;}
try{
  const doc=await create(),binding={document_id:doc,page_index:1};
  const layer_id=(await ok('corel_inspect_document',{document_id:doc})).layers.find(v=>!v.is_special_layer).layer_id;
  const control=await create(),controlBefore=await ok('corel_inspect_document',{document_id:control});
  const draw=(tool,args)=>ok(tool,{request_id:randomUUID(),...binding,...args});
  const made=await draw('corel_create_shapes',{layer_id,items:[{kind:'rectangle',x_mm:60,y_mm:30,width_mm:10,height_mm:10},{kind:'rectangle',x_mm:0,y_mm:0,width_mm:20,height_mm:10},{kind:'rectangle',x_mm:20,y_mm:20,width_mm:10,height_mm:10}]});
  const ids=[...made.created_static_ids];
  const aligned=await draw('corel_align_shapes',{static_ids:ids,axis:'x',action:'distribute',distribution:'equal_gaps',reference:'page'});assert.deepEqual(aligned.placements.map(p=>p.x_mm),[0,100,190]);
  await draw('corel_style_shapes',{static_ids:ids,outline:{kind:'uniform',width_mm:1,color:{model:'rgb',r:0,g:0,b:0}}});
  const copied=await draw('corel_grid_duplicate',{static_ids:[ids[0]],rows:2,columns:2,gap_x_mm:3,gap_y_mm:4,anchor_x_mm:10,anchor_y_mm:10});assert.equal(copied.created_static_ids.length,3);ids.push(...copied.created_static_ids);
  const text=await draw('corel_create_text',{kind:'artistic',layer_id,text:'Раскладка',x_mm:100,y_mm:50,font_family:'Arial',font_size_pt:12});ids.push(...text.created_static_ids);
  const args={static_ids:ids,margin_left_mm:5,margin_right_mm:5,margin_bottom_mm:5,margin_top_mm:5,gap_x_mm:2,gap_y_mm:2};
  const before=await ok('corel_inspect_document',{document_id:doc,recursive:true,include_text:true});
  const plan=await draw('corel_layout_shapes',args);assert.equal(plan.dry_run,true);assert.equal(plan.unplaced.length,0);assert.deepEqual(await ok('corel_inspect_document',{document_id:doc,recursive:true,include_text:true}),before);
  const applied=await draw('corel_layout_shapes',{...args,dry_run:false,expected_revision:plan.layout_revision});assert.equal(applied.actual_bounds.length,7);
  const stale=await operation(session,'corel_layout_shapes',{request_id:randomUUID(),...binding,...args,dry_run:false,expected_revision:plan.layout_revision});assert.equal(stale.status,'failed');assert.equal(stale.error.code,'STALE_LAYOUT');assert.equal(stale.error.applied,false);
  await ok('corel_preview',{document_id:doc,max_size:600});assert.deepEqual(await ok('corel_inspect_document',{document_id:control}),controlBefore);assert.equal((await ok('corel_status')).active_document_id,control);
}catch(error){primary=error;}
finally{for(const document_id of owned.reverse())try{await ok('corel_close_document',{request_id:randomUUID(),document_id,discard_changes:true});cleanup.push({document_id,closed:true});}catch(error){cleanup.push({document_id,closed:false,error:String(error)});primary??=error;}writeFileSync(join(directory,'verification.json'),JSON.stringify({passed:!primary,error:primary&&String(primary),cleanup,evidence},null,2));await session.close();}
if(primary)throw primary;console.log(`Раскладка проверена: ${directory}`);
