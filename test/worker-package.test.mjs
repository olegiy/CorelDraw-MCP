import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { copyFileSync, mkdtempSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import { assertInteropApproval, verifyWorkerPackage, sealWorkerPackage, probeWorker, probeNativeWorker } from '../scripts/worker-package.mjs';

const hash = value => createHash('sha256').update(value).digest('hex');
test('native activation probe sends only read-only status and refuses an unavailable module',async()=>{
  for(const available of [true,false]) {
    let closed=false;
    const check=probeNativeWorker('C:/fixture/CorelMcp.Worker.exe',{executorFactory:async options=>{
      assert.equal(options.executionMode,'hybrid');return {
        run:async(job,args)=>{assert.equal(job.tool,'corel_status');assert.deepEqual(args,{});return {ok:available,cleanup:{completed:true},result:{executor:{execution_mode:'hybrid',native_module:available?'0.4.0':null}}};},
        close:()=>{closed=true;}
      };
    }});
    if(available)assert.equal((await check).ok,true);else await assert.rejects(check,/native|VBA/i);
    assert(closed);
  }
});
test('installation admission loads before dist or node_modules have been built', () => {
  const directory = mkdtempSync(join(tmpdir(), 'corelmcp-clean-admission-'));
  for (const relative of ['scripts/check-preupgrade.mjs', 'scripts/worker-package.mjs', 'src/worker-protocol.ts', 'src/version.ts', 'package.json']) {
    mkdirSync(join(directory, relative, '..'), { recursive: true });
    copyFileSync(new URL('../' + relative, import.meta.url), join(directory, relative));
  }
  const result = spawnSync(process.execPath, ['--input-type=module', '-e',
    `await import(${JSON.stringify(pathToFileURL(join(directory, 'scripts/check-preupgrade.mjs')).href)}); await import(${JSON.stringify(pathToFileURL(join(directory, 'scripts/worker-package.mjs')).href)});`], { encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
});
function fixture() {
  const directory = mkdtempSync(join(tmpdir(), 'corelmcp-package-'));
  const runtime = { runtimeOptions: { tfm: 'net10.0', includedFrameworks: [
    { name: 'Microsoft.NETCore.App', version: '10.0.15' }, { name: 'Microsoft.WindowsDesktop.App', version: '10.0.15' },
  ] } };
  const names = ['CorelMcp.Worker.exe', 'CorelMcp.Worker.dll', 'CorelMcp.Worker.deps.json', 'CorelMcp.Worker.runtimeconfig.json',
    'VGCore.Interop.dll', 'PdfSharp.dll', 'coreclr.dll', 'hostfxr.dll', 'System.Private.CoreLib.dll',
    'third-party/PDFsharp.LICENSE.txt', 'third-party/dotnet-LICENSE.TXT', 'third-party/dotnet-THIRD-PARTY-NOTICES.TXT',
    'third-party/dotnet-winforms-LICENSE.TXT', 'third-party/dotnet-winforms-THIRD-PARTY-NOTICES.TXT',
    'third-party/dotnet-wpf-LICENSE.TXT', 'third-party/dotnet-wpf-THIRD-PARTY-NOTICES.TXT', 'third-party/Corel-interop-notice.txt'];
  const files = {};
  for (const name of names) {
    const value = name.endsWith('runtimeconfig.json') ? JSON.stringify(runtime) : `Test-only placeholder ${name}`;
    mkdirSync(join(directory, name, '..'), { recursive: true }); writeFileSync(join(directory, name), value); files[name] = hash(value);
  }
  const basDirectory=new URL('../worker/vba/',import.meta.url),source=createHash('sha256');
  for(const name of readdirSync(basDirectory).filter(n=>n.endsWith('.bas')).sort()) {source.update(name+'\n');source.update(readFileSync(new URL(name,basDirectory)));}
  const sourceHash=source.digest('hex'),module={schema_version:1,protocol:1,module_version:'0.4.0',corel_major:27,
    source_sha256:sourceHash,project_name:'CorelMcp_Runtime_v1_'+sourceHash.slice(0,12),file:'CorelMcp.Runtime.gms',gms_sha256:hash('Test-only GMS placeholder'),operations:['handshake','validate_fonts','create_batch','update_batch']};
  for(const [name,value] of Object.entries({'native/module-manifest.json':JSON.stringify(module),'native/CorelMcp.Runtime.gms':'Test-only GMS placeholder'})) {
    mkdirSync(join(directory,name,'..'),{recursive:true});writeFileSync(join(directory,name),value);files[name]=hash(value);
  }
  const manifest = { schema_version: 1, worker_version: '0.4.0', protocol_version: 1, rid: 'win-x64', self_contained: true,
    interop_approval: { scope: 'redistribute-generated-interop', typelib_sha256: 'b'.repeat(64), evidence_sha256: 'a'.repeat(64), source: 'https://www.corel.com/test-only', reviewed_at: '2026-11-12', reviewed_by: 'Test fixture' },
    native_module:{module_version:module.module_version,source_sha256:sourceHash,gms_sha256:module.gms_sha256}, files };
  const save = () => writeFileSync(join(directory, 'worker-manifest.json'), JSON.stringify(manifest)); save();
  return { directory, manifest, save };
}
test('package verification checks the entire self-contained manifest without starting Corel', () => {
  const f = fixture(); assert.equal(verifyWorkerPackage(f.directory).worker_version, '0.4.0');
  writeFileSync(join(f.directory, 'PdfSharp.dll'), 'changed'); assert.throws(() => verifyWorkerPackage(f.directory), /hash|измен|fingerprint/i);
});
test('missing, stale, non-self-contained and unexpected executable files fail closed', () => {
  for (const mutate of [f => { f.manifest.worker_version = '0.2.0'; f.save(); },
    f => { f.manifest.self_contained = false; f.save(); },
    f => { delete f.manifest.files['coreclr.dll']; f.save(); },
    f => writeFileSync(join(f.directory, 'unexpected.ps1'), 'test'),
    f => writeFileSync(join(f.directory, 'foreign.gms'), 'test'),
    f => {f.manifest.files['Microsoft.Vbe.Interop.dll']=hash('test');writeFileSync(join(f.directory,'Microsoft.Vbe.Interop.dll'),'test');f.save();},
    f => {delete f.manifest.files['native/CorelMcp.Runtime.gms'];f.save();}]) {
    const f = fixture(); mutate(f); assert.throws(() => verifyWorkerPackage(f.directory));
  }
});
test('Windows Desktop notices and the exact interop source binding are mandatory', () => {
  for (const mutate of [f => { delete f.manifest.interop_approval.typelib_sha256; },
    f => { delete f.manifest.files['third-party/dotnet-wpf-THIRD-PARTY-NOTICES.TXT']; }]) {
    const f = fixture(); mutate(f); f.save(); assert.throws(() => verifyWorkerPackage(f.directory));
  }
});
test('a framework-dependent runtimeconfig cannot be presented as self-contained', () => {
  const f = fixture(), value = JSON.stringify({ runtimeOptions: { tfm: 'net10.0', framework: { name: 'Microsoft.NETCore.App', version: '10.0.0' } } });
  writeFileSync(join(f.directory, 'CorelMcp.Worker.runtimeconfig.json'), value);
  f.manifest.files['CorelMcp.Worker.runtimeconfig.json'] = hash(value); f.save(); assert.throws(() => verifyWorkerPackage(f.directory));
});
test('unconfirmed interop rights block publication before invoking a build or installer', () => {
  const root = mkdtempSync(join(tmpdir(), 'corelmcp-rights-'));
  assert.throws(() => assertInteropApproval(root), /INTEROP_RIGHTS_UNCONFIRMED/);
  mkdirSync(join(root, 'worker')); writeFileSync(join(root, 'worker/interop-redistribution.json'), JSON.stringify({ status: 'pending' }));
  assert.throws(() => assertInteropApproval(root), /INTEROP_RIGHTS_UNCONFIRMED/);
});
test('sealing binds reviewed evidence, interop bytes and pinned notices in an isolated synthetic package', () => {
  const f = fixture(), repository = mkdtempSync(join(tmpdir(), 'corelmcp-seal-test-only-'));
  const save = (relative, bytes) => { mkdirSync(join(repository, relative, '..'), { recursive: true }); writeFileSync(join(repository, relative), bytes); };
  const source = 'b'.repeat(64), interop = readFileSync(join(f.directory, 'VGCore.Interop.dll'));
  for(const name of ['module-manifest.json','CorelMcp.Runtime.gms'])save('worker/native/'+name,readFileSync(join(f.directory,'native',name)));
  for(const name of readdirSync(new URL('../worker/vba/',import.meta.url)))save('worker/vba/'+name,readFileSync(new URL('../worker/vba/'+name,import.meta.url)));
  save('worker/api-contract.json', JSON.stringify({ typelib: { source_sha256: source } }));
  save('worker/generated/VGCore.Interop.dll', interop);
  save('worker/generated/fingerprint.json', JSON.stringify({ source_sha256: source, assembly_sha256: hash(interop) }));
  // Test data only: this record grants no real rights and is never written into the project.
  const evidence = 'Synthetic fixture, not a license grant.';
  save('evidence.txt', evidence); save('notice.txt', 'Test-only interop notice');
  save('worker/interop-redistribution.json', JSON.stringify({ schema_version: 1, status: 'confirmed',
    ...f.manifest.interop_approval, evidence_sha256: hash(evidence), evidence_path: 'evidence.txt', notice_path: 'notice.txt' }));
  const notices = JSON.parse(readFileSync(new URL('../worker/third-party/dotnet-sources.json', import.meta.url)));
  for (const name of ['dotnet-sources.json', 'PDFsharp.LICENSE.txt', ...notices.map(item => item.file)])
    save('worker/third-party/' + name, readFileSync(new URL('../worker/third-party/' + name, import.meta.url)));
  const sealed = sealWorkerPackage(f.directory, repository);
  assert.equal(sealed.interop_approval.typelib_sha256, source);
  assert(!JSON.stringify(sealed).includes('evidence_path'));
  save('evidence.txt', evidence + 'changed');
  assert.throws(() => sealWorkerPackage(f.directory, repository), /INTEROP_RIGHTS_UNCONFIRMED/);
});
test('the version probe sends only EOF and validates ready without dispatching any COM job', async () => {
  const child = new EventEmitter(); child.stdout = new PassThrough(); child.stderr = new PassThrough(); child.stdin = new PassThrough();
  const writes = []; child.stdin.on('data', data => writes.push(data));
  child.stdin.on('finish', () => { child.stdout.end(); child.emit('close', 0); });
  const result = probeWorker('C:/fixture/CorelMcp.Worker.exe', { launch: () => child });
  child.stdout.write(JSON.stringify({ type: 'ready', worker_version: '0.4.0', protocol_version: 1, runtime: '.NET 10.0.15', architecture: 'x64' }) + '\n');
  assert.equal((await result).worker_version, '0.4.0'); assert.equal(writes.length, 0);
});
