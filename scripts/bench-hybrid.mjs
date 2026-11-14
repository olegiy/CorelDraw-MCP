import assert from 'node:assert/strict';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { randomUUID, createHash } from 'node:crypto';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { performance } from 'node:perf_hooks';
import { connect, operation } from '../test/helpers.mjs';
import {decodePng} from './benchmark-artifacts.mjs';
import {verifyCopies} from './benchmark-correctness.mjs';

const allowed=['tiles20','tiles100','tiles1000','text20','text100','style100','move100','duplicate100','geometry100','png100','inspect100'];
const near=(actual,expected)=>assert(Math.abs(actual-expected)<=.001,`${actual} != ${expected}`);
export function rgb(index) {
  const stops=[[255,0,0],[255,255,0],[0,255,0],[0,255,255],[0,0,255],[255,0,255]];
  return stops[index/200|0].map((value,c)=>Math.floor((value*200+(stops[(index/200|0)+1][c]-value)*(index%200)+100)/200));
}
function rectangle(i) {return {kind:'rectangle',x_mm:20+(i%40)*18,y_mm:20+Math.floor(i/40)*18,width_mm:12,height_mm:8};}
function fill(i) {const [r,g,b]=rgb(i);return {kind:'uniform',color:{model:'rgb',r,g,b}};}
function checkTiles(shapes,ids) {
  const map=new Map(shapes.map(s=>[s.static_id,s]));assert.equal(map.size,ids.length);
  ids.forEach((id,i)=>{
    const s=map.get(id),r=rectangle(i),b=s.bounds_mm;near(b.left+b.width/2,r.x_mm+6);near(b.bottom+b.height/2,r.y_mm+4);near(s.rotation,i%360);
    const [red,g,bv]=rgb(i);assert.deepEqual(s.appearance.fill_color,{model:'rgb',r:red,g,b:bv});assert.equal(s.appearance.outline_type,0);
  });
}
class BudgetStop extends Error {}
export async function benchmark({cases=allowed,samples=3,budgetMs=300000}={}) {
  if(process.env.CORELDRAW_WORKER_LIVE!=='1')throw new Error('Нужен CORELDRAW_WORKER_LIVE=1.');
  assert(cases.every(c=>allowed.includes(c)));assert(Number.isInteger(samples)&&samples>=1&&samples<=5);assert(budgetMs>0&&budgetMs<=300000);
  const directory=fileURLToPath(new URL(`../artifacts/hybrid-benchmark/${randomUUID()}/`,import.meta.url));mkdirSync(directory,{recursive:true});
  const manifest=readFileSync(new URL('../worker/native/module-manifest.json',import.meta.url));
  const assembly=process.env.CORELDRAW_MCP_CSHARP_WORKER?.replace(/\.exe$/i,'.dll')??fileURLToPath(new URL('../worker/publish/win-x64/CorelMcp.Worker.dll',import.meta.url));
  const report={completed:false,full_pairs_completed:false,scope:'Полные последовательности публичных MCP-команд; подготовка fixture исключена только для geometry/style/move/duplicate/PNG/inspection.',worker_assembly_sha256:createHash('sha256').update(readFileSync(assembly)).digest('hex'),manifest_sha256:createHash('sha256').update(manifest).digest('hex'),native:JSON.parse(manifest),samples:[],stopped:[],cleanup:[]};
  const save=()=>writeFileSync(join(directory,'benchmark.json'),JSON.stringify(report,null,2));
  const sessions={};
  try {
    for(const mode of ['csharp','hybrid'])sessions[mode]=await connect({executionMode:mode,runs:join(directory,mode,'runs')});
    for(const name of cases) {
      const stoppedModes=new Set();
      for(let iteration=-1;iteration<samples;iteration++)for(const mode of iteration%2===0?['hybrid','csharp']:['csharp','hybrid']) {
        if(stoppedModes.has(mode))continue;
        const session=sessions[mode],phases={},row={case:name,mode,warmup:iteration===-1,iteration,phases_ms:phases,operations:[],worker_stages_ms:{}};let doc,started=0;
        async function ok(tool,args={}) {
          const job=await operation(session,tool,args,360000);
          assert.equal(job.status,'succeeded',JSON.stringify(job.error));assert.equal(job.cleanup?.completed,true);
          row.versions=job.versions;row.operations.push({tool,job_id:job.job_id,versions:job.versions});
          const events=readFileSync(job.log_path,'utf8').trim().split('\n').map(line=>{const split=line.indexOf(' ');return {time:Date.parse(line.slice(0,split)),stage:line.slice(split+1)};});
          for(let i=0;i+1<events.length;i++)if(Number.isFinite(events[i].time)&&Number.isFinite(events[i+1].time))
            row.worker_stages_ms[events[i].stage]=(row.worker_stages_ms[events[i].stage]??0)+events[i+1].time-events[i].time;
          return job.result;
        }
        async function phase(key,action) {const t=performance.now();try{return await action();}finally{phases[key]=(phases[key]??0)+performance.now()-t;}}
        const guard=()=>{if(started&&performance.now()-started>budgetMs)throw new BudgetStop('Остановлено между завершёнными командами по бюджету сценария.');};
        const draw=(tool,args)=>{guard();return ok(tool,{request_id:randomUUID(),document_id:doc,page_index:1,...args});};
        async function inspect() {
          const first=await ok('corel_inspect_document',{document_id:doc,page_index:1,recursive:true,include_text:true,limit:500});
          let next=first.next_offset;const shapes=[...first.shapes];
          while(next!==null) {const page=await ok('corel_inspect_document',{document_id:doc,page_index:1,recursive:true,include_text:true,limit:500,offset:next});shapes.push(...page.shapes);next=page.next_offset;}
          return {...first,shapes};
        }
        try {
          const setup=performance.now();doc=(await ok('corel_create_document',{request_id:randomUUID(),width_mm:760,height_mm:490})).document_id;
          const layer_id=(await inspect()).layers.find(l=>!l.is_special_layer).layer_id;
          const count=Number(name.match(/\d+$/)[0]);let ids=[],geometry,copyIds=[];
          if(!name.startsWith('tiles')&&!name.startsWith('text')) {
            ids=(await draw('corel_create_shapes',{layer_id,items:Array.from({length:count},(_,i)=>({...rectangle(i),...(name==='geometry100'&&i%2?{kind:'ellipse'}:{})}))})).created_static_ids;
            await draw('corel_style_shapes',{static_ids:ids,fill:fill(0),outline:{kind:'uniform',width_mm:.2,color:{model:'rgb',r:0,g:0,b:0}}});
          }
          const before=await inspect();row.setup_ms=performance.now()-setup;started=performance.now();
          if(name.startsWith('tiles')) {
            if(mode==='hybrid')ids=(await phase('create_style_rotate',()=>draw('corel_create_batch',{layer_id,items:Array.from({length:count},(_,i)=>({...rectangle(i),item_key:'c'+i,fill:fill(i),outline:{kind:'none'},rotation_deg:i%360}))}))).created_static_ids;
            else {
              for(let i=0;i<count;i+=100)ids.push(...(await phase('create',()=>draw('corel_create_shapes',{layer_id,items:Array.from({length:Math.min(100,count-i)},(_,j)=>rectangle(i+j))}))).created_static_ids);
              for(let i=0;i<count;i++) {
                await phase('style',()=>draw('corel_style_shapes',{static_ids:[ids[i]],fill:fill(i),outline:{kind:'none'}}));
                await phase('rotate',()=>draw('corel_transform_shapes',{static_ids:[ids[i]],action:'rotate',angle_deg:i%360,anchor_x_mm:rectangle(i).x_mm+6,anchor_y_mm:rectangle(i).y_mm+4}));
              }
            }
          } else if(name.startsWith('text')) {
            const text=i=>({text:'Ёж — этикетка '+i,x_mm:10+(i%10)*65,y_mm:10+Math.floor(i/10)*30,font_family:'Arial',font_size_pt:12});
            if(mode==='hybrid')ids=(await phase('create',()=>draw('corel_create_batch',{layer_id,items:Array.from({length:count},(_,i)=>({kind:'artistic_text',item_key:'t'+i,...text(i)}))}))).created_static_ids;
            else for(let i=0;i<count;i++)ids.push(...(await phase('create',()=>draw('corel_create_text',{kind:'artistic',layer_id,...text(i)}))).created_static_ids);
          } else if(name==='geometry100')geometry=await phase('measure_including_snapshot_and_math',()=>ok('corel_measure_shapes',{document_id:doc,page_index:1,static_ids:ids,tolerance_mm:.001}));
          else if(name==='style100')await phase('style',()=>draw('corel_style_shapes',{static_ids:ids,fill:fill(100),outline:{kind:'none'}}));
          else if(name==='move100')await phase('move',()=>draw('corel_transform_shapes',{static_ids:ids,action:'move',dx_mm:5,dy_mm:3}));
          else if(name==='duplicate100')copyIds=(await phase('duplicate',()=>draw('corel_duplicate_shapes',{static_ids:ids,copies:1,step_x_mm:0,step_y_mm:50}))).items.map(item=>item.static_id);
          else if(name==='png100')await phase('png',()=>ok('corel_export',{request_id:randomUUID(),document_id:doc,format:'png',path:join(directory,`${name}-${mode}-${iteration}.png`),dpi:96}));
          else if(name==='inspect100')await phase('inspection',inspect);
          if(name.startsWith('tiles')||name.startsWith('text'))await phase('save',()=>ok('corel_save_document',{request_id:randomUUID(),document_id:doc,path:join(directory,`${name}-${mode}-${iteration}.cdr`)}));
          row.total_ms=performance.now()-started;started=0;
          await phase('postcheck',async()=>{
            const after=await inspect();
            if(name.startsWith('tiles'))checkTiles(after.shapes,ids);
            else if(name.startsWith('text')) {const map=new Map(after.shapes.map(s=>[s.static_id,s]));ids.forEach((id,i)=>assert.equal(map.get(id).text,'Ёж — этикетка '+i));}
            else if(name==='geometry100') {
              assert.equal(geometry.original_state_preserved,true);assert.equal(geometry.objects.length,100);
              geometry.objects.forEach(o=>{assert(ids.includes(o.static_id));assert(Number.isFinite(o.rectangle.area_mm2)&&o.rectangle.area_mm2>0);});
              row.geometry=geometry.objects;assert.deepEqual(after,before);
            } else if(name==='move100'||name==='style100') {
              assert.equal(after.shapes.length,ids.length);
              const original=new Map(before.shapes.map(s=>[s.static_id,s]));
              assert.equal(new Set(after.shapes.map(s=>s.static_id)).size,ids.length);
              after.shapes.forEach(s=>{
                const source=original.get(s.static_id);assert(source,'Потеря или подмена ID цели');
                near(s.bounds_mm.left,source.bounds_mm.left+(name==='move100'?5:0));near(s.bounds_mm.bottom,source.bounds_mm.bottom+(name==='move100'?3:0));
                near(s.bounds_mm.width,source.bounds_mm.width);near(s.bounds_mm.height,source.bounds_mm.height);near(s.rotation,source.rotation);
                assert.equal(s.type,source.type);assert.equal(s.layer,source.layer);
                if(name==='style100'){assert.deepEqual(s.appearance.fill_color,fill(100).color);assert.equal(s.appearance.outline_type,0);}
                else assert.deepEqual(s.appearance,source.appearance);
              });
            }
            else if(name==='duplicate100')verifyCopies(ids.map(id=>before.shapes.find(s=>s.static_id===id)),after.shapes,copyIds,0,50);
            else {
              assert.deepEqual(after,before);
              if(name==='png100') {
                const image=decodePng(readFileSync(join(directory,`${name}-${mode}-${iteration}.png`)));assert(image.painted>1000);
                row.png={width:image.width,height:image.height,painted:image.painted,rgba_sha256:createHash('sha256').update(image.rgba).digest('hex')};
              }
            }
          });
          row.correctness=name.startsWith('text')?'Для всех целей проверен текст; шрифт и геометрия этим повтором независимо не проверены':'Все цели проверены';report.samples.push(row);
          console.log(`${name} ${mode} ${iteration===-1?'прогрев':iteration+1}: ${row.total_ms.toFixed(1)} мс`);
        } catch(error) {
          if(error instanceof BudgetStop) {row.elapsed_ms=performance.now()-started;row.reason=error.message;report.stopped.push(row);stoppedModes.add(mode);
            await ok('corel_save_document',{request_id:randomUUID(),document_id:doc,path:join(directory,`${name}-${mode}-partial.cdr`)});
          } else throw error;
        } finally {
          if(doc&&session.pending.size===0) {await ok('corel_close_document',{request_id:randomUUID(),document_id:doc,discard_changes:true});report.cleanup.push({case:name,mode,iteration,closed:true});}
          else if(doc)report.cleanup.push({case:name,mode,iteration,closed:false,reason:'native_pending_or_unknown',document_id:doc,pending_job_ids:[...session.pending]});
          save();
        }
      }
      if(name==='geometry100') {
        const rows=report.samples.filter(r=>r.case===name),reference=rows[0]?.geometry;
        for(const row of rows)row.geometry.forEach((o,i)=>{near(o.min_width_mm,reference[i].min_width_mm);near(o.rectangle.width_mm,reference[i].rectangle.width_mm);near(o.rectangle.height_mm,reference[i].rectangle.height_mm);});
      }
      if(name==='png100') {
        const rows=report.samples.filter(r=>r.case===name);for(const row of rows)assert.deepEqual(row.png,rows[0].png,'Декодированные пиксели PNG должны совпадать между режимами и повторами');
      }
    }
    report.completed=true;
    report.full_pairs_completed=cases.every(name=>['csharp','hybrid'].every(mode=>report.samples.filter(row=>row.case===name&&row.mode===mode&&!row.warmup).length===samples));
  } catch(error) {report.error=String(error);throw error;}
  finally {save();for(const session of Object.values(sessions))await session.close();}
  console.log(`Отчёт: ${directory}`);return {directory,report};
}
if(process.argv[1]&&fileURLToPath(import.meta.url)===process.argv[1]) {
  const selected=process.argv[2]?.split(',')??allowed;await benchmark({cases:selected,samples:process.argv[3]?Number(process.argv[3]):3});
}
