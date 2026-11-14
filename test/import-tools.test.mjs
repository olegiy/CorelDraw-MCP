import test from 'node:test';
import assert from 'node:assert/strict';
import { toolDefinitions } from '../dist/tools.js';
const bind={request_id:'once',document_id:'exact',page_index:1};
const examples={
  corel_import:{...bind,path:'C:\\input.svg',format:'svg',sha256:'a'.repeat(64),size_bytes:123,layer_id:'layer',x_mm:10,y_mm:20},
  corel_manage_pages:{...bind,action:'add',count:2,width_mm:210,height_mm:297},
  corel_powerclip:{...bind,action:'place',container_id:1,static_ids:[2,3]},
};
for(const [name,args] of Object.entries(examples))test(`${name} is strict and requires exact mutation binding`,()=>{
  const d=toolDefinitions[name];assert.ok(d,`Missing typed tool ${name}`);assert.equal(d.schema.safeParse(args).success,true);assert.equal(d.readOnly,false);
  for(const key of ['request_id','document_id','page_index']){const bad={...args};delete bad[key];assert.equal(d.schema.safeParse(bad).success,false);}
  assert.equal(d.schema.safeParse({...args,source_code:'anything'}).success,false);
});
test('import fingerprint, PDF page, fit frame and PowerClip variants reject ambiguity',()=>{
  for(const name of Object.keys(examples))assert.ok(toolDefinitions[name],name);
  const parse=(name,changes)=>toolDefinitions[name].schema.safeParse({...examples[name],...changes}).success;
  assert.equal(parse('corel_import',{format:'pdf'}),false);
  assert.equal(parse('corel_import',{format:'pdf',pdf_page:2}),true);
  assert.equal(parse('corel_import',{pdf_page:2}),false);
  assert.equal(parse('corel_import',{path:'relative.svg'}),false);
  assert.equal(parse('corel_import',{path:'\\\\server\\share\\input.svg'}),false);
  assert.equal(parse('corel_import',{size_bytes:256*1024*1024+1}),false);
  assert.equal(parse('corel_import',{sha256:'wrong'}),false);
  assert.equal(parse('corel_import',{fit:{width_mm:10}}),false);
  assert.equal(parse('corel_manage_pages',{action:'delete'}),false);
  assert.equal(parse('corel_manage_pages',{count:101}),false);
  assert.equal(parse('corel_powerclip',{static_ids:[1]}),false);
  assert.equal(parse('corel_powerclip',{action:'extract'}),false);
  assert.equal(toolDefinitions.corel_powerclip.schema.safeParse({...bind,action:'extract',container_id:1}).success,true);
  assert.equal(toolDefinitions.corel_powerclip.schema.parse(examples.corel_powerclip).center,false);
});
