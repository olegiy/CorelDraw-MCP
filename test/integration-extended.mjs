import assert from 'node:assert/strict';
import { mkdtemp, mkdir, readFile, writeFile, readdir } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { connect, operation, call } from './helpers.mjs';

const root = await mkdtemp(join(process.env.CORELDRAW_MCP_INTEGRATION_PARENT || process.env.LOCALAPPDATA, 'CorelDrawMcp-extended-'));
const runs = join(root, 'runs'); const session = await connect({ runs });
const created = new Set(); const checks = []; let serial = 0;
const req = name => ({ request_id: `extended-${name}-${++serial}` });
async function ok(tool,args={}) { const job=await operation(session,tool,args,180000); assert.equal(job.status,'succeeded',JSON.stringify(job,null,2)); assert.notEqual(job.cleanup?.completed,false,JSON.stringify(job,null,2)); return job; }
async function scenario(name, body) { const result=await body(); checks.push(name); console.log(`PASS ${name}`); return result; }
async function execute(id, code) { return ok('corel_execute_vba',{...req('vba'),document_id:id,code}); }
const main = body => `Option Explicit\nPublic Function Main() As String\n${body}\nEnd Function`;
try {
 const status=await ok('corel_status');
 const doc=(await ok('corel_create_document',{...req('source'),width_mm:100,height_mm:80})).result; created.add(doc.document_id);
 const id=doc.document_id;
 const initial=await execute(id,main('Dim a As Shape, b As Shape\nSet a=ActiveLayer.CreateRectangle2(10,10,30,20)\na.Fill.ApplyUniformFill CreateRGBColor(20,100,220)\na.Outline.SetNoOutline\na.Name="selected-rectangle"\nSet b=ActiveLayer.CreateArtisticText(12,40,"Текст проверки")\nb.Name="private-text"\na.CreateSelection\nMain="{""rectangle_id"":" & CStr(a.StaticID) & "}"'));
 const rectangleId=initial.result.value.rectangle_id;
 await scenario('API types, members and enum use installed CorelDRAW type library',async()=>{
   for(const args of [{kind:'types',filter:'Shape'},{kind:'members',name:'Shape',filter:'Move'},{kind:'enum',name:'cdrFilter',filter:'PNG'}]) {
     const report=await ok('corel_describe_api',args); assert(report.result.items.length>0,JSON.stringify(report.result));
   }
 });
 await scenario('Preview selection uses bounded detail and preserves selection',async()=>{
   const before=(await ok('corel_inspect_document',{document_id:id})).result;
   const preview=await ok('corel_preview',{document_id:id,range:'selection',max_size:300});
   const png=await readFile(preview.result.path);assert.equal(Math.max(png.readUInt32BE(16),png.readUInt32BE(20)),300);
   assert.deepEqual((await ok('corel_inspect_document',{document_id:id})).result.selection_ids,before.selection_ids);
 });
 await scenario('Recursive inspection filters groups and includes text only explicitly',async()=>{
   const ordinary=(await ok('corel_inspect_document',{document_id:id})).result;
   assert(ordinary.shapes.every(s=>!Object.hasOwn(s,'text')));
   const text=(await ok('corel_inspect_document',{document_id:id,name_filter:'private',include_text:true,text_limit:5})).result;
   assert.equal(text.shapes.length,1);assert.equal(text.shapes[0].text.length,5);assert.equal(text.shapes[0].text_truncated,true);
 });
 await scenario('Six native export formats create nonempty files',async()=>{
   const exported={};
   for(const format of ['jpg','tiff','ai','eps','dxf','plt']) {
     const output=join(root,`export.${format}`); const report=await ok('corel_export',{...req(format),document_id:id,format,range:'selection',path:output});
     exported[format]=output;
     const bytes=await readFile(output);assert(bytes.length>10,format);
     if(format==='jpg')assert.equal(bytes.subarray(0,2).toString('hex'),'ffd8');
     if(format==='tiff')assert(['49492a00','4d4d002a'].includes(bytes.subarray(0,4).toString('hex')));
     console.log(`FORMAT ${format}: ${report.result.bytes} bytes`);
   }
   for(const format of ['dxf','plt']) {
     const imported=(await ok('corel_create_document',{...req(`import-${format}`),width_mm:100,height_mm:80})).result; created.add(imported.document_id);
     const literal=exported[format].replaceAll('"','""');
     const result=await execute(imported.document_id,main(`ActiveLayer.Import "${literal}"\nIf ActiveDocument.SelectionRange.Count=0 Then Err.Raise 5,"Main","Импорт ${format} не создал объектов."\nMain="{""count"":" & CStr(ActiveDocument.SelectionRange.Count) & ",""width"":" & Trim$(Str$(ActiveDocument.SelectionRange.SizeWidth)) & ",""height"":" & Trim$(Str$(ActiveDocument.SelectionRange.SizeHeight)) & "}"`));
     assert(result.result.value.count>0,format);
     assert(Math.abs(result.result.value.width-30)<.1,`${format}: ${JSON.stringify(result.result.value)}`);assert(Math.abs(result.result.value.height-20)<.1,format);
     await ok('corel_close_document',{...req(`close-import-${format}`),document_id:imported.document_id,discard_changes:true}); created.delete(imported.document_id);
   }
 });
 await scenario('JPEG CMYK, TIFF options, aliases and AI/EPS curves preserve original objects',async()=>{
   const before=(await ok('corel_inspect_document',{document_id:id})).result;
   for(const [format,extension,options] of [['jpg','jpeg',{color_mode:'cmyk',jpeg_compression:70}],['tiff','tif',{color_mode:'cmyk',tiff_compression:'none'}],['tiff','tiff',{transparent:true}],['ai','ai',{text_mode:'curves'}],['eps','eps',{text_mode:'curves'}]]) await ok('corel_export',{...req('options'),document_id:id,format,path:join(root,`options-${serial}.${extension}`),...options});
   const after=(await ok('corel_inspect_document',{document_id:id})).result;
   assert.equal(after.total_shapes,before.total_shapes);assert.deepEqual(after.selection_ids,before.selection_ids);assert.deepEqual(after.shapes.map(s=>[s.static_id,s.type,s.bounds]),before.shapes.map(s=>[s.static_id,s.type,s.bounds]));
 });
 const examples=fileURLToPath(new URL('../skills/coreldraw-automation/examples/',import.meta.url));
 await scenario('All packaged VBA examples compile and execute measurable JSON',async()=>{
    for(const file of (await readdir(examples)).filter(f=>f.endsWith('.bas') && f !== 'M_CommonHelpers.bas')) {
     const exampleDoc=(await ok('corel_create_document',{...req('example'),width_mm:100,height_mm:80})).result;created.add(exampleDoc.document_id);
      const prepared=await execute(exampleDoc.document_id,main('Dim s As Shape,t As Shape,c As Shape\nSet s=ActiveDocument.ActiveLayer.CreateRectangle2(10,10,20,10)\nSet t=ActiveDocument.ActiveLayer.CreateRectangle2(20,10,20,10)\nSet c=ActiveDocument.ActiveLayer.CreateRectangle2(1,1,5,5)\nc.ConvertToCurves\ns.CreateSelection\nMain="{""first"":" & CStr(s.StaticID) & ",""second"":" & CStr(t.StaticID) & ",""curve"":" & CStr(c.StaticID) & "}"'));
      let code=await readFile(join(examples,file),'utf8');
      if (['M_TextUnicode.bas','M_FillOutline.bas','M_DuplicateOffset.bas','M_LayerGroup.bas','M_BooleanShapes.bas','M_CurveNodes.bas'].includes(file)) {
        const ids=prepared.result.value;
        code=code.replace(/(Const (?:TARGET_STATIC_ID|LEFT_STATIC_ID|FIRST_STATIC_ID) As Long = )0/g,`$1${file==='M_CurveNodes.bas'?ids.curve:ids.first}`)
          .replace(/(Const (?:RIGHT_STATIC_ID|SECOND_STATIC_ID) As Long = )0/g,`$1${ids.second}`);
        code+='\n'+(await readFile(join(examples,'M_CommonHelpers.bas'),'utf8')).replace(/^Option Explicit\s*/m,'');
      }
     const result=await execute(exampleDoc.document_id,code);assert(result.result.value!==undefined,file);console.log(`RECIPE ${file}`);
     await ok('corel_close_document',{...req('close-example'),document_id:exampleDoc.document_id,discard_changes:true});created.delete(exampleDoc.document_id);
   }
 });
 await scenario('Vector measurement includes outlines and preserves source state',async()=>{
   const before=(await ok('corel_inspect_document',{document_id:id})).result;
   const measured=(await ok('corel_measure_shapes',{document_id:id,static_ids:[rectangleId]})).result;
   assert.equal(measured.units,'mm');assert.equal(measured.objects[0].static_id,rectangleId);
   assert(Math.abs(measured.objects[0].rectangle.width_mm-30)<.03);assert(Math.abs(measured.objects[0].rectangle.height_mm-20)<.03);
   const after=(await ok('corel_inspect_document',{document_id:id})).result;
   assert.deepEqual(after.shapes,before.shapes);assert.deepEqual(after.selection_ids,before.selection_ids);
 });
 await scenario('Measurement of rotated shapes, text, groups, curves and thick outlines',async()=>{
   const fixture=(await ok('corel_create_document',{...req('geometry-fixture'),width_mm:200,height_mm:160})).result;created.add(fixture.document_id);
   const setup=await execute(fixture.document_id,main('Dim a As Shape, b As Shape, c As Shape, t As Shape, g As Shape, r As ShapeRange\nSet a=ActiveLayer.CreateRectangle2(10,10,30,20)\na.Fill.ApplyUniformFill CreateRGBColor(20,100,220)\na.Outline.SetNoOutline\na.Rotate 27\nSet b=ActiveLayer.CreateRectangle2(60,10,30,20)\nb.Outline.Width=2\nSet c=ActiveLayer.CreateEllipse2(50,80,15,10)\nc.Fill.ApplyUniformFill CreateRGBColor(20,100,220)\nc.Outline.SetNoOutline\nc.ConvertToCurves\nSet t=ActiveLayer.CreateArtisticText(100,100,"Текст")\nt.Outline.SetNoOutline\nSet r=CreateShapeRange\nr.Add c\nr.Add t\nSet g=r.Group\na.CreateSelection\nMain="{""rotated"":" & CStr(a.StaticID) & ",""outlined"":" & CStr(b.StaticID) & ",""group"":" & CStr(g.StaticID) & "}"'));
   const ids=setup.result.value;
   await ok('corel_save_document',{...req('geometry-save'),document_id:fixture.document_id,path:join(root,'geometry-source.cdr')});
   const before=(await ok('corel_inspect_document',{document_id:fixture.document_id,recursive:true})).result;
   const measurement=(await ok('corel_measure_shapes',{document_id:fixture.document_id,static_ids:[ids.rotated,ids.outlined,ids.group]})).result;
   assert.equal(measurement.objects.length,3);
   assert(Math.abs(measurement.objects[0].min_width_mm-20)<.03);
   assert(Math.abs(measurement.objects[1].rectangle.width_mm-32)<.03);assert(Math.abs(measurement.objects[1].rectangle.height_mm-22)<.03);
   assert(measurement.objects[2].rectangle.width_mm>0);
   const after=(await ok('corel_inspect_document',{document_id:fixture.document_id,recursive:true})).result;
   const child=after.shapes.find(s=>s.parent_static_id===ids.group);assert(child);
   for(const static_ids of [[ids.group,child.static_id],[child.static_id,ids.group]]) {
     const overlapping=(await ok('corel_measure_shapes',{document_id:fixture.document_id,static_ids})).result;
     const alone=(await ok('corel_measure_shapes',{document_id:fixture.document_id,static_ids:[child.static_id]})).result;
     const childResult=overlapping.objects.find(s=>s.static_id===child.static_id);
     assert(Math.abs(childResult.min_width_mm-alone.objects[0].min_width_mm)<.001);
     const groupResult=overlapping.objects.find(s=>s.static_id===ids.group);
     assert(Math.abs(groupResult.min_width_mm-measurement.objects[2].min_width_mm)<.001);
   }
   assert.deepEqual(after.shapes,before.shapes);assert.deepEqual(after.selection_ids,before.selection_ids);
   const status=(await ok('corel_status')).result;const source=status.documents.find(d=>d.document_id===fixture.document_id);assert.equal(source.dirty,false,JSON.stringify(source));
   const marker=await execute(fixture.document_id,main('Dim s As Shape\nSet s=ActiveLayer.CreateRectangle2(150,10,2,2)\ns.Name="undo-marker"\nMain="{}"'));
   const withMarker=(await ok('corel_inspect_document',{document_id:fixture.document_id})).result.total_shapes;
   await ok('corel_measure_shapes',{document_id:fixture.document_id,static_ids:[ids.rotated]});
   // Undo must be observed outside corel_execute_vba's own new command group.
   // The helper uses the same STA module/mutex and exact owned temporary path.
   const undoneResult=spawnSync('powershell.exe',['-NoLogo','-NoProfile','-STA','-File',fileURLToPath(new URL('./integration-undo.ps1',import.meta.url)),'-TestDocumentPath',join(root,'geometry-source.cdr')],{encoding:'utf8',windowsHide:true,shell:false});
   assert.equal(undoneResult.status,0,undoneResult.stderr);
   const undone=(await ok('corel_inspect_document',{document_id:fixture.document_id})).result;
   assert.equal(undone.total_shapes,withMarker-1);assert(undone.shapes.every(s=>s.name!=='undo-marker'));
   const hidden=await execute(fixture.document_id,main('Dim a As Shape,b As Shape,g As Shape,r As ShapeRange\nSet a=ActiveLayer.CreateRectangle2(10,10,5,5)\na.Fill.ApplyUniformFill CreateRGBColor(0,0,0)\nSet b=ActiveLayer.CreateRectangle2(1000,1000,5,5)\nb.Fill.ApplyUniformFill CreateRGBColor(0,0,0)\nSet r=CreateShapeRange\nr.Add a\nr.Add b\nSet g=r.Group\nb.Visible=False\nMain="{""group"":" & CStr(g.StaticID) & "}"'));
   const rejected=await operation(session,'corel_measure_shapes',{document_id:fixture.document_id,static_ids:[hidden.result.value.group]});
   assert.equal(rejected.status,'failed');assert.equal(rejected.error.code,'UNSUPPORTED_GEOMETRY');assert.match(rejected.error.message,/скрыт/);
   await ok('corel_close_document',{...req('geometry-close'),document_id:fixture.document_id,discard_changes:true});created.delete(fixture.document_id);
 });
 await writeFile(join(root,'verification.json'),JSON.stringify({ok:true,checks,root,date:new Date().toISOString()},null,2));
} catch(error) {
 await writeFile(join(root,'verification.json'),JSON.stringify({ok:false,checks,root,error:String(error),date:new Date().toISOString()},null,2));throw error;
} finally {
 for(const document_id of created)try{await operation(session,'corel_close_document',{...req('cleanup'),document_id,discard_changes:true});}catch(e){console.error(e);}
 console.log(`Extended report: ${root}`);console.error(session.stderr());await session.close();
}
