import assert from 'node:assert/strict';
import {mkdirSync,readFileSync,writeFileSync} from 'node:fs';
import {randomUUID,createHash} from 'node:crypto';
import {join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {call,connect,operation} from './helpers.mjs';
import {rgb} from '../scripts/bench-hybrid.mjs';
import {decodePng} from '../scripts/benchmark-artifacts.mjs';
import {near,verifyTranslation,verifyCreatedBatch} from './hybrid-checks.mjs';
if(process.env.CORELDRAW_WORKER_LIVE!=='1')throw new Error('Нужен CORELDRAW_WORKER_LIVE=1.');
const directory=fileURLToPath(new URL(`../artifacts/integration-hybrid/${randomUUID()}/`,import.meta.url));mkdirSync(directory,{recursive:true});
const session=await connect({executionMode:'hybrid',runs:join(directory,'runs')}),owned=new Set(),evidence=[],cleanup=[];
const scenarios=[];
let primary,currentScenario='setup',baseline;
async function scenario(name,action) {
  currentScenario=name;
  const start=evidence.length;
  try {await action();scenarios.push({name,passed:true,operations:evidence.length-start});}
  catch(error){scenarios.push({name,passed:false,operations:evidence.length-start,error:String(error)});throw error;}
}
async function run(tool,args={}) {
  const job=await operation(session,tool,args,300000);
  evidence.push({scenario:currentScenario,tool,args,job_id:job.job_id,status:job.status,versions:job.versions,result:job.result,error:job.error,cleanup:job.cleanup});
  assert.equal(job.cleanup?.completed,true);assert.deepEqual(job.cleanup.errors,[]);return job;
}
async function ok(tool,args={}) {
  const job=await run(tool,args);assert.equal(job.status,'succeeded',JSON.stringify(job.error));return job;
}
async function create() {const doc=(await ok('corel_create_document',{request_id:randomUUID(),width_mm:760,height_mm:490})).result.document_id;owned.add(doc);return doc;}
async function inspect(doc,page_index=1,limit=500) {
  const args={document_id:doc,page_index,recursive:true,include_text:true,limit};
  const first=(await ok('corel_inspect_document',args)).result,shapes=[...first.shapes];let offset=first.next_offset;
  assert.equal(first.depth_truncated,false);assert.equal(first.visited_truncated,false);
  const offsets=new Set();
  while(offset!==null){assert(!offsets.has(offset),'Пагинация не продвигается');offsets.add(offset);const page=(await ok('corel_inspect_document',{...args,offset})).result;shapes.push(...page.shapes);offset=page.next_offset;}
  assert.equal(new Set(shapes.map(s=>s.static_id)).size,shapes.length);
  return {...first,shapes};
}
const normalLayer=state=>{const layers=state.layers.filter(l=>!l.is_special_layer&&l.editable);assert.equal(layers.length,1);return layers[0].layer_id;};
const shape=(state,id)=>{const value=state.shapes.find(s=>s.static_id===id);assert(value,`Отсутствует StaticID ${id}`);return value;};
function bounds(item,left,bottom,width,height) {
  for(const [key,value] of Object.entries({left,bottom,width,height}))near(item.bounds_mm[key],value);
}
function fingerprint(path) {const bytes=readFileSync(path);return {path,size_bytes:bytes.length,sha256:createHash('sha256').update(bytes).digest('hex')};}
try {
  baseline=(await ok('corel_status')).result;
  const doc=await create(),initial=await inspect(doc),layer_id=normalLayer(initial);
  const atlas=await create(),atlasInitial=await inspect(atlas),atlasLayer=normalLayer(atlasInitial);
  const control=await create(),controlBefore=await inspect(control);
  const binding={document_id:doc,page_index:1};
  const draw=async(tool,args)=>(await ok(tool,{request_id:randomUUID(),...binding,...args})).result;
  const read=async(tool,args)=>(await ok(tool,{...binding,...args})).result;
  async function rejected(tool,args,code) {
    const before=await inspect(doc);
    const job=await run(tool,{request_id:randomUUID(),...binding,...args});
    assert.equal(job.status,'failed');assert.equal(job.error.code,code);assert.equal(job.error.applied,false);
    assert.deepEqual(await inspect(doc),before);
  }
  const request_id=randomUUID(),args={request_id,document_id:doc,page_index:1,layer_id,items:Array.from({length:20},(_,i)=>({item_key:'r'+i,kind:'rectangle',x_mm:10+i*20,y_mm:10,width_mm:12,height_mm:8,fill:{kind:'uniform',color:{model:'rgb',r:80,g:120,b:180}},outline:{kind:'none'}}))};
  let ids,mixed,copyIds,secondaryLayer,curveId;
  await scenario('batch-idempotency',async()=>{
    const first=await ok('corel_create_batch',args);verifyCreatedBatch(args.items,first.result);
    assert.equal(first.versions.execution_engine,'vba');ids=first.result.created_static_ids;
    const before=await inspect(doc),same=await call(session,'corel_create_batch',args);
    assert.equal(before.total_shapes,20);assert.equal(before.shapes.length,20);
    assert.equal(same.job_id,first.job_id);assert.deepEqual(await inspect(doc),before);
    ids.forEach((id,i)=>bounds(shape(before,id),10+i*20,10,12,8));
  });
  const original=await inspect(doc);
  for(const {name,dx,dy} of [{name:'batch-move',dx:3,dy:-2},{name:'batch-return',dx:-3,dy:2}])await scenario(name,async()=>{
    const before=await inspect(doc);
    const result=await ok('corel_update_batch',{request_id:randomUUID(),...binding,items:ids.map(static_id=>({static_id,transform:{action:'move',dx_mm:dx,dy_mm:dy}}))});
    assert.equal(result.versions.execution_engine,'vba');assert.deepEqual(result.result.items.map(i=>i.static_id),ids);
    verifyTranslation(before.shapes,(await inspect(doc)).shapes,ids,dx,dy);
  });
  verifyTranslation(original.shapes,(await inspect(doc)).shapes,ids,0,0);
  await scenario('mixed-batch-and-transforms',async()=>{
    const items=[
      {item_key:'panel',kind:'rectangle',x_mm:10,y_mm:50,width_mm:30,height_mm:20,fill:{kind:'uniform',color:{model:'rgb',r:40,g:120,b:210}},outline:{kind:'none'}},
      {item_key:'oval',kind:'ellipse',x_mm:30,y_mm:50,width_mm:20,height_mm:20,fill:{kind:'none'},outline:{kind:'uniform',width_mm:.2,color:{model:'cmyk',c:0,m:0,y:0,k:100}}},
      {item_key:'line',kind:'line',x1_mm:10,y1_mm:100,x2_mm:40,y2_mm:100,outline:{kind:'uniform',width_mm:.2,color:{model:'rgb',r:255,g:0,b:0}}},
      {item_key:'text',kind:'artistic_text',text:'Ёж ёж — гибрид',x_mm:10,y_mm:130,font_family:'Arial',font_size_pt:12},
    ];
    const made=await draw('corel_create_batch',{layer_id,items});verifyCreatedBatch(items,made);mixed=made.created_static_ids;
    assert.equal(made.items[3].font_family,'Arial');near(made.items[3].font_size_pt,12);
    const state=await inspect(doc);bounds(shape(state,mixed[0]),10,50,30,20);bounds(shape(state,mixed[1]),30,50,20,20);
    assert.equal(shape(state,mixed[3]).text,'Ёж ёж — гибрид');assert.equal(new Set(mixed.map(id=>shape(state,id).type)).size,4);
    await draw('corel_update_batch',{items:[
      {static_id:mixed[0],fill:{kind:'uniform',color:{model:'rgb',r:255,g:180,b:0}}},
      {static_id:mixed[1],transform:{action:'scale',sx:2,sy:.5,anchor_x_mm:30,anchor_y_mm:50}},
      {static_id:mixed[2],transform:{action:'rotate',angle_deg:90,anchor_x_mm:10,anchor_y_mm:100}},
      {static_id:mixed[3],transform:{action:'move',dx_mm:5,dy_mm:3}},
    ]});
    const after=await inspect(doc);bounds(shape(after,mixed[1]),30,50,40,10);bounds(shape(after,mixed[2]),10,100,0,30);
    assert.deepEqual(shape(after,mixed[0]).appearance.fill_color,{model:'rgb',r:255,g:180,b:0});
    near(shape(after,mixed[2]).rotation,90);verifyTranslation(state.shapes.filter(s=>s.static_id===mixed[3]),after.shapes.filter(s=>s.static_id===mixed[3]),[mixed[3]],5,3);
    await draw('corel_transform_shapes',{static_ids:[mixed[2]],action:'flip',axis:'horizontal',anchor_x_mm:20,anchor_y_mm:115});
    bounds(shape(await inspect(doc),mixed[2]),30,100,0,30);
  });
  await scenario('batch-prevalidation',async()=>{
    const missing=2147483647;assert(!(await inspect(doc)).shapes.some(s=>s.static_id===missing));
    await rejected('corel_update_batch',{items:[{static_id:ids[0],transform:{action:'move',dx_mm:100,dy_mm:0}},{static_id:missing,transform:{action:'move',dx_mm:100,dy_mm:0}}]},'SHAPE_NOT_FOUND');
  });
  await scenario('copies-layers-and-groups',async()=>{
    const before=await inspect(doc),source=shape(before,mixed[0]);
    const copies=await draw('corel_duplicate_shapes',{static_ids:[mixed[0]],copies:2,step_x_mm:60,step_y_mm:0});copyIds=copies.created_static_ids;assert.equal(copyIds.length,2);
    const copied=await inspect(doc);copyIds.forEach((id,i)=>{bounds(shape(copied,id),70+i*60,50,30,20);assert.deepEqual(shape(copied,id).appearance,source.appearance);});
    assert.deepEqual(shape(copied,mixed[0]),source);
    secondaryLayer=(await draw('corel_manage_layers',{action:'create',page_id:initial.page_id,name:'Копии'})).layer.layer_id;
    await draw('corel_manage_layers',{action:'rename',layer_id:secondaryLayer,name:'Копии — проверка'});
    await draw('corel_manage_layers',{action:'move_shapes',layer_id:secondaryLayer,static_ids:copyIds});
    const moved=await inspect(doc);copyIds.forEach(id=>assert.equal(shape(moved,id).layer,'Копии — проверка'));
    const group=await draw('corel_group_shapes',{action:'group',static_ids:copyIds}),groupId=group.created_static_ids[0];
    const grouped=await inspect(doc);copyIds.forEach(id=>assert.equal(shape(grouped,id).parent_static_id,groupId));
    const ungrouped=await draw('corel_group_shapes',{action:'ungroup',static_ids:[groupId]});assert(ungrouped.deleted_static_ids.includes(groupId));
    const restored=await inspect(doc);copyIds.forEach(id=>assert.deepEqual(shape(restored,id),shape(moved,id)));
    await draw('corel_manage_layers',{action:'set_flags',layer_id:secondaryLayer,editable:false});
    await rejected('corel_update_batch',{items:[{static_id:ids[0],transform:{action:'move',dx_mm:1,dy_mm:0}},{static_id:copyIds[0],transform:{action:'move',dx_mm:1,dy_mm:0}}]},'LOCKED_TARGET');
    await draw('corel_manage_layers',{action:'set_flags',layer_id:secondaryLayer,editable:true});
  });
  await scenario('boolean-geometry',async()=>{
    for(const action of ['weld','trim','intersect']){
      const pair=await draw('corel_create_shapes',{layer_id,items:[{kind:'rectangle',x_mm:300,y_mm:50,width_mm:10,height_mm:10},{kind:'rectangle',x_mm:305,y_mm:50,width_mm:10,height_mm:10}]});
      const [left_id,right_id]=pair.created_static_ids,keep=action!=='trim';
      const result=await draw('corel_boolean_shapes',{action,left_id,right_id,keep_left:keep,keep_right:keep});assert.equal(result.items.length,1);
      const state=await inspect(doc),actual=shape(state,result.items[0].static_id);
      bounds(actual,{weld:300,trim:310,intersect:305}[action],50,action==='weld'?15:5,10);
      assert.equal(state.shapes.some(s=>s.static_id===left_id),keep);assert.equal(state.shapes.some(s=>s.static_id===right_id),keep);
      for(const id of result.deleted_static_ids)assert(!state.shapes.some(s=>s.static_id===id));
    }
    const before=await inspect(doc),measured=await read('corel_measure_shapes',{static_ids:[mixed[0]],tolerance_mm:.001});
    assert.equal(measured.original_state_preserved,true);assert.equal(measured.objects.length,1);near(measured.objects[0].rectangle.area_mm2,600);
    assert.deepEqual(await inspect(doc),before);
  });
  await scenario('text-edit-and-search',async()=>{
    const textId=mixed[3];
    await draw('corel_edit_text',{static_ids:[textId],action:'replace_literal',old:'ёж',new:'Лис',case_sensitive:false});
    const found=await read('corel_find_text',{query:'лис',case_sensitive:false,include_text:true});assert.equal(found.complete,true);
    assert.deepEqual(found.matches.map(m=>({static_id:m.static_id,count:m.count,snippet:m.snippet})),[{static_id:textId,count:2,snippet:'Лис Лис — гибрид'}]);
    assert.equal((await read('corel_find_text',{query:'лис',case_sensitive:true})).matches.length,0);
    const paragraph=await draw('corel_create_text',{kind:'paragraph',layer_id,text:'Строка 1\r\nСтрока 2',x_mm:10,y_mm:200,width_mm:80,height_mm:40,font_family:'Arial',font_size_pt:11});
    assert.equal(paragraph.items[0].font_family,'Arial');
    await draw('corel_edit_text',{static_ids:paragraph.created_static_ids,action:'set_text',text:'Абзац — проверка'});
    const formatted=await draw('corel_edit_text',{static_ids:paragraph.created_static_ids,action:'format',font_size_pt:14,bold:true,alignment:'center'});
    near(formatted.items[0].font_size_pt,14);
    assert.equal(shape(await inspect(doc),paragraph.created_static_ids[0]).text,'Абзац — проверка');
    await rejected('corel_create_text',{kind:'artistic',layer_id,text:'Текст',x_mm:0,y_mm:0,font_family:'CorelMcp_NoSuchFont_71823',font_size_pt:12},'FONT_NOT_INSTALLED');
    const converted=await draw('corel_convert_to_curves',{static_ids:[textId]});assert.equal(converted.id_mappings[0].source_static_id,textId);
    assert(converted.items.every(item=>item.type===3||item.type===7));
    assert.equal((await read('corel_find_text',{query:'Лис',case_sensitive:true})).matches.length,0);
  });
  await scenario('curve-revisions-and-preflight',async()=>{
    const made=await draw('corel_create_curve',{layer_id,subpaths:[{start:{x_mm:200,y_mm:200},closed:false,segments:[{kind:'line',to:{x_mm:230,y_mm:200}},{kind:'line',to:{x_mm:230,y_mm:220}}]}]});curveId=made.created_static_ids[0];
    const before=await read('corel_inspect_curve',{static_id:curveId});assert.equal(before.total_nodes,3);assert.equal(before.subpaths[0].closed,false);
    assert.equal((await read('corel_preflight',{static_ids:[curveId],checks:[{kind:'open_contours'}]})).objects[0].checks[0].status,'failed');
    await draw('corel_edit_curve',{static_id:curveId,expected_revision:before.geometry_revision,action:'close_subpath',subpath_index:1});
    const after=await read('corel_inspect_curve',{static_id:curveId});assert.equal(after.subpaths[0].closed,true);assert.notEqual(after.geometry_revision,before.geometry_revision);
    await rejected('corel_edit_curve',{static_id:curveId,expected_revision:before.geometry_revision,action:'move_node',subpath_index:1,node_index:1,x_mm:0,y_mm:0},'STALE_GEOMETRY');
    await draw('corel_style_shapes',{static_ids:[curveId],fill:{kind:'none'},outline:{kind:'uniform',width_mm:.2,color:{model:'rgb',r:255,g:0,b:0}}});
    const checks=await read('corel_preflight',{static_ids:[curveId],checks:[{kind:'open_contours'},{kind:'outline_width',expected_mm:.2,tolerance_mm:.001},{kind:'outline_color',color:{model:'rgb',r:255,g:0,b:0},tolerance:0}]});
    assert(checks.objects[0].checks.every(check=>check.status==='passed'));
  });
  await scenario('alignment-grid-and-layout',async()=>{
    await draw('corel_align_shapes',{static_ids:copyIds,axis:'y',action:'align',align:'min',reference:'shape',reference_id:curveId});
    const aligned=await inspect(doc);copyIds.forEach(id=>near(shape(aligned,id).bounds_mm.bottom,200));
    const grid=await draw('corel_grid_duplicate',{static_ids:[copyIds[0]],rows:2,columns:2,gap_x_mm:3,gap_y_mm:4,anchor_x_mm:400,anchor_y_mm:200});assert.equal(grid.created_static_ids.length,3);
    const state=await inspect(doc),gridIds=[copyIds[0],...grid.created_static_ids];
    assert.deepEqual(gridIds.map(id=>[shape(state,id).bounds_mm.left,shape(state,id).bounds_mm.bottom]).sort((a,b)=>a[1]-b[1]||a[0]-b[0]),[[400,200],[433,200],[400,224],[433,224]]);
    const layoutArgs={static_ids:gridIds,margin_left_mm:5,margin_right_mm:5,margin_bottom_mm:5,margin_top_mm:5,gap_x_mm:2,gap_y_mm:2};
    const before=await inspect(doc),plan=await draw('corel_layout_shapes',layoutArgs);assert.equal(plan.dry_run,true);assert.equal(plan.unplaced.length,0);assert.deepEqual(await inspect(doc),before);
    const applied=await draw('corel_layout_shapes',{...layoutArgs,dry_run:false,expected_revision:plan.layout_revision});assert.equal(applied.actual_bounds.length,4);
    assert.notDeepEqual((await inspect(doc)).shapes,before.shapes);
    await rejected('corel_layout_shapes',{...layoutArgs,dry_run:false,expected_revision:plan.layout_revision},'STALE_LAYOUT');
  });
  await scenario('pages-import-and-powerclip',async()=>{
    const added=await draw('corel_manage_pages',{action:'add',count:1,width_mm:100,height_mm:80});assert.equal(added.pages.length,1);
    await draw('corel_manage_pages',{page_index:2,action:'resize',page_id:added.pages[0].page_id,width_mm:120,height_mm:90});
    const page=await inspect(doc,2);near(page.pages[1].width,120);near(page.pages[1].height,90);assert.equal(page.total_shapes,0);
    const pageBinding={...binding,page_index:2};
    const targetLayer=(await draw('corel_manage_layers',{...pageBinding,action:'create',page_id:page.page_id,name:'Импорт'})).layer.layer_id;
    assert((await inspect(doc,2)).layers.some(layer=>layer.layer_id===targetLayer&&layer.editable&&!layer.is_special_layer));
    const svg=join(directory,'Кириллица с пробелами.svg');
    writeFileSync(svg,'<svg xmlns="http://www.w3.org/2000/svg" width="40mm" height="20mm" viewBox="0 0 40 20"><rect width="40" height="20" fill="#ff0000"/></svg>');
    const input=fingerprint(svg),imported=await draw('corel_import',{...pageBinding,format:'svg',...input,layer_id:targetLayer,x_mm:10,y_mm:10,fit:{width_mm:20,height_mm:20}});
    assert.equal(imported.source_sha256,input.sha256);bounds({bounds_mm:imported.import_bounds_mm},10,10,20,10);assert.equal(fingerprint(svg).sha256,input.sha256);
    const container=(await draw('corel_create_shapes',{...pageBinding,layer_id:targetLayer,items:[{kind:'rectangle',x_mm:5,y_mm:5,width_mm:40,height_mm:30}]})).created_static_ids[0];
    const placed=await draw('corel_powerclip',{...pageBinding,action:'place',container_id:container,static_ids:imported.imported_root_static_ids,center:false});assert(placed.contents.length>0);
    const extracted=await draw('corel_powerclip',{...pageBinding,action:'extract',container_id:container});assert.equal(extracted.container.static_id,container);assert.deepEqual(extracted.contents.map(v=>v.bounds_mm),placed.contents.map(v=>v.bounds_mm));
  });
  await scenario('save-export-preview-and-reopen',async()=>{
    const path=join(directory,'Разнообразные сценарии.cdr');await ok('corel_save_document',{request_id:randomUUID(),document_id:doc,path});assert(fingerprint(path).size_bytes>0);
    const before=[await inspect(doc),await inspect(doc,2)];
    const pdf=join(directory,'Две страницы.pdf');await ok('corel_export',{request_id:randomUUID(),document_id:doc,format:'pdf',range:'document',path:pdf});assert.equal(readFileSync(pdf).subarray(0,5).toString(),'%PDF-');
    const preview=await ok('corel_preview',{document_id:doc,page_index:2,max_size:600});
    const image=await session.client.callTool({name:'corel_get_job',arguments:{job_id:preview.job_id,include_preview:true}});assert(image.content.some(block=>block.type==='image'));
    assert.deepEqual([await inspect(doc),await inspect(doc,2)],before);
    await ok('corel_close_document',{request_id:randomUUID(),document_id:doc,discard_changes:false});owned.delete(doc);
    const reopened=(await ok('corel_open_document',{request_id:randomUUID(),path})).result.document_id;owned.add(reopened);
    for(const page_index of [1,2]){
      const actual=await inspect(reopened,page_index),old=before[page_index-1];assert.equal(actual.total_shapes,old.total_shapes);
      const ordered=state=>[...state.shapes].sort((a,b)=>a.layer.localeCompare(b.layer)||a.type-b.type||a.bounds_mm.left-b.bounds_mm.left||a.bounds_mm.bottom-b.bounds_mm.bottom);
      const oldShapes=ordered(old),newShapes=ordered(actual);assert.equal(newShapes.length,oldShapes.length);
      newShapes.forEach((item,index)=>{
        const source=oldShapes[index];
        for(const key of ['type','layer','text'])assert.deepEqual(item[key],source[key]);
        for(const key of ['left','bottom','width','height'])near(item.bounds_mm[key],source.bounds_mm[key]);near(item.rotation,source.rotation);
        assert(item.appearance&&source.appearance);
        for(const key of ['fill_type','outline_type','fill_color','outline_color'])assert.deepEqual(item.appearance[key],source.appearance[key]);
        near(item.appearance.outline_width_mm,source.appearance.outline_width_mm);
      });
    }
    await ok('corel_close_document',{request_id:randomUUID(),document_id:reopened,discard_changes:true});owned.delete(reopened);
  });
  await scenario('large-batch-and-paginated-inspection',async()=>{
    const items=Array.from({length:1000},(_,i)=>{
      const [r,g,b]=rgb(i);return {item_key:'c'+i,kind:'rectangle',x_mm:20+(i%40)*18,y_mm:20+Math.floor(i/40)*18,width_mm:12,height_mm:8,rotation_deg:i%360,fill:{kind:'uniform',color:{model:'rgb',r,g,b}},outline:{kind:'none'}};
    });
    const result=await ok('corel_create_batch',{request_id:randomUUID(),document_id:atlas,page_index:1,layer_id:atlasLayer,items});verifyCreatedBatch(items,result.result);
    const actual=await inspect(atlas),map=new Map(actual.shapes.map(s=>[s.static_id,s]));assert.equal(map.size,1000);assert.equal(actual.total_shapes,1000);
    result.result.items.forEach((item,i)=>{
      const s=map.get(item.static_id),[r,g,b]=rgb(i);assert.deepEqual(s.appearance.fill_color,{model:'rgb',r,g,b});assert.equal(s.appearance.outline_type,0);near(s.rotation,i%360);
      near(s.bounds_mm.left+s.bounds_mm.width/2,26+(i%40)*18);near(s.bounds_mm.bottom+s.bounds_mm.height/2,24+Math.floor(i/40)*18);
    });
    const path=join(directory,'Атлас 1000 плашек.cdr');await ok('corel_save_document',{request_id:randomUUID(),document_id:atlas,path});
    const before=await inspect(atlas);
    await ok('corel_export',{request_id:randomUUID(),document_id:atlas,format:'png',path:join(directory,'atlas.png'),dpi:96});
    const png=readFileSync(join(directory,'atlas.png')),pixels=decodePng(png);assert(pixels.painted>10000);
    assert.deepEqual(await inspect(atlas),before);
    writeFileSync(join(directory,'atlas-artifacts.json'),JSON.stringify({cdr:path,png_sha256:createHash('sha256').update(png).digest('hex'),painted_pixels:pixels.painted,rgba_sha256:createHash('sha256').update(pixels.rgba).digest('hex')},null,2));
  });
  assert.deepEqual(await inspect(control),controlBefore);
  const after=(await ok('corel_status')).result;
  assert.equal(after.active_document_id,control);
  assert.deepEqual(after.application_state,baseline.application_state);
  for(const source of baseline.documents)assert.deepEqual(after.documents.find(d=>d.document_id===source.document_id),source);
} catch(error){primary=error;}
finally {
  currentScenario='cleanup';
  for(const document_id of [...owned].reverse())try {
    assert.equal(session.pending.size,0,'Cleanup запрещён при pending/unknown native job');
    await ok('corel_close_document',{request_id:randomUUID(),document_id,discard_changes:true});cleanup.push({document_id,closed:true});
  }
  catch(error){cleanup.push({document_id,closed:false,error:String(error)});primary??=error;}
  if(baseline&&session.pending.size===0)try {
    const final=(await ok('corel_status')).result;
    assert.deepEqual(final.documents,baseline.documents);assert.deepEqual(final.application_state,baseline.application_state);
  }catch(error){primary??=error;}
  try {await session.close();}catch(error){primary??=error;}
  writeFileSync(join(directory,'verification.json'),JSON.stringify({passed:!primary,error:primary&&String(primary),repeated_move_checks:scenarios.filter(s=>['batch-move','batch-return'].includes(s.name)&&s.passed).length,scenarios,tools_checked:[...new Set(evidence.map(e=>e.tool))],native_operations:evidence.filter(e=>e.versions?.execution_engine==='vba'&&e.status==='succeeded').length,cleanup,evidence},null,2));
}
if(primary)throw primary;
console.log(`Гибрид: ${scenarios.length} сценариев, ${new Set(evidence.map(e=>e.tool)).size} функций, две проверки перемещения: ${directory}`);
