import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, mkdirSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { verifyNativeModule } from '../scripts/worker-package.mjs';
const sha=value=>createHash('sha256').update(value).digest('hex');
function fixture() {
  const directory=mkdtempSync(join(tmpdir(),'corelmcp-native-package-'));mkdirSync(join(directory,'native'));
  const module={schema_version:1,protocol:1,module_version:'0.4.0',corel_major:27,source_sha256:'a'.repeat(64),project_name:'CorelMcp_Runtime_v1_aaaaaaaaaaaa',file:'CorelMcp.Runtime.gms',gms_sha256:sha('test-only'),operations:['handshake','validate_fonts','create_batch','update_batch']};
  writeFileSync(join(directory,'native/CorelMcp.Runtime.gms'),'test-only');
  const save=()=>writeFileSync(join(directory,'native/module-manifest.json'),JSON.stringify(module));save();return {directory,module,save};
}
test('native package requires the exact fixed module, identity, version and hash',()=>{
  const f=fixture();assert.equal(verifyNativeModule(f.directory).source_sha256,'a'.repeat(64));
  for(const change of [f=>{f.module.module_version='0.3.0';f.save();},f=>{f.module.project_name='foreign';f.save();},f=>{f.module.file='../foreign.gms';f.save();},f=>{f.module.operations.push('execute_vba');f.save();},f=>writeFileSync(join(f.directory,'native/CorelMcp.Runtime.gms'),'changed')]){
    const bad=fixture();change(bad);assert.throws(()=>verifyNativeModule(bad.directory));
  }
});
