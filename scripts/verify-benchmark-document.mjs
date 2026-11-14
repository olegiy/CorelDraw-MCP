import assert from 'node:assert/strict';
import {realpathSync} from 'node:fs';
import {relative,isAbsolute,extname} from 'node:path';
import {fileURLToPath} from 'node:url';
import {connect,operation} from '../test/helpers.mjs';

// Developer presentation only: read-only typed MCP, no new worker capability.
const [path]=process.argv.slice(2);
const root=realpathSync(fileURLToPath(new URL('../artifacts/',import.meta.url)));
const fixture=realpathSync(path),child=relative(root,fixture);
assert(child&&!child.startsWith('..')&&!isAbsolute(child)&&extname(fixture).toLowerCase()==='.cdr','Нужен собственный CDR внутри artifacts.');
const session=await connect();
try {
  const job=await operation(session,'corel_status');
  assert.equal(job.status,'succeeded');assert.equal(job.cleanup.completed,true);
  const matches=job.result.documents.filter(d=>d.path&&realpathSync(d.path).toLowerCase()===fixture.toLowerCase());
  assert.equal(matches.length,1,'Нужен ровно один открытый документ с точным путём макета.');
  const doc=matches[0],id=doc.document_id;
  assert.equal(realpathSync(doc.path).toLowerCase(),fixture.toLowerCase());
  assert.equal(job.result.active_document_id,id,'Сначала выберите вкладку собственного макета в CorelDRAW.');
  const inspection=await operation(session,'corel_inspect_document',{document_id:id,page_index:1});
  assert.equal(inspection.status,'succeeded');assert.equal(inspection.cleanup.completed,true);
  console.log(JSON.stringify({document_id:id,path:fixture,active:true}));
} finally {await session.close();}
