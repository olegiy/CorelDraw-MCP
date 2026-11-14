import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { copyFileSync, existsSync, lstatSync, mkdtempSync, readFileSync, readdirSync, renameSync, writeFileSync } from 'node:fs';
import {tmpdir} from 'node:os';
import {randomUUID} from 'node:crypto';
import { dirname, join, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { VERSION } from '../src/version.ts';
import { parseProtocolJson } from '../src/worker-protocol.ts';

const root = fileURLToPath(new URL('..', import.meta.url));
const sha = data => createHash('sha256').update(data).digest('hex');
const digest = value => typeof value === 'string' && /^[a-f0-9]{64}$/.test(value);
const decoder = new TextDecoder('utf-8', { fatal: true });
const required = ['CorelMcp.Worker.exe', 'CorelMcp.Worker.dll', 'CorelMcp.Worker.deps.json', 'CorelMcp.Worker.runtimeconfig.json',
  'VGCore.Interop.dll', 'PdfSharp.dll', 'coreclr.dll', 'hostfxr.dll', 'System.Private.CoreLib.dll',
  'third-party/PDFsharp.LICENSE.txt', 'third-party/dotnet-LICENSE.TXT', 'third-party/dotnet-THIRD-PARTY-NOTICES.TXT',
  'third-party/dotnet-winforms-LICENSE.TXT', 'third-party/dotnet-winforms-THIRD-PARTY-NOTICES.TXT',
  'third-party/dotnet-wpf-LICENSE.TXT', 'third-party/dotnet-wpf-THIRD-PARTY-NOTICES.TXT', 'third-party/Corel-interop-notice.txt',
  'native/CorelMcp.Runtime.gms','native/module-manifest.json'];
function noLinks(path) {
  for (let current = resolve(path);; current = dirname(current)) {
    if (lstatSync(current).isSymbolicLink()) throw new Error('Ссылка/junction в комплекте запрещена.');
    if (dirname(current) === current) break;
  }
}
function localPath(base, name) {
  if (typeof name !== 'string' || !name || name.includes('\\') || name.includes(':') || /[\x00-\x1f]/.test(name) || name.split('/').some(part => !part || part === '.' || part === '..'))
    throw new Error('Неподтверждённый относительный путь комплекта.');
  const path = resolve(base, name);
  if (!path.startsWith(resolve(base) + sep)) throw new Error('Путь вне комплекта.');
  noLinks(path); return path;
}
function json(path) {
  noLinks(path); const stat = lstatSync(path);
  if (!stat.isFile() || stat.size < 1 || stat.size > 4 * 1024**2) throw new Error('Некорректный файл метаданных комплекта.');
  return parseProtocolJson(decoder.decode(readFileSync(path)));
}
function approval(value) {
  return value?.scope === 'redistribute-generated-interop' && digest(value.evidence_sha256) && digest(value.typelib_sha256)
    && typeof value.source === 'string' && value.source.length > 0 && value.source.length <= 2000
    && typeof value.reviewed_by === 'string' && value.reviewed_by.length > 0
    && typeof value.reviewed_at === 'string' && /^\d{4}-\d{2}-\d{2}/.test(value.reviewed_at);
}

export function assertInteropApproval(repository = root) {
  try {
    const value = json(join(repository, 'worker/interop-redistribution.json'));
    const contract = json(join(repository, 'worker/api-contract.json'));
    if (value.schema_version !== 1 || value.status !== 'confirmed' || !approval(value)
        || value.typelib_sha256 !== contract.typelib.source_sha256) throw new Error('Неполная привязка прав.');
    const fingerprintPath = join(repository, 'worker/generated/fingerprint.json');
    if (existsSync(fingerprintPath)) {
      const fingerprint = json(fingerprintPath);
      if (fingerprint.source_sha256 !== value.typelib_sha256
          || fingerprint.assembly_sha256 !== sha(readFileSync(join(repository, 'worker/generated/VGCore.Interop.dll')))) throw new Error('Interop не совпадает с проверенным источником.');
    }
    const evidencePath = localPath(repository, value.evidence_path), noticePath = localPath(repository, value.notice_path);
    for (const path of [evidencePath, noticePath]) if (!lstatSync(path).isFile() || lstatSync(path).size > 1024**2 || lstatSync(path).size < 1) throw new Error('Неполное подтверждение.');
    if (sha(readFileSync(evidencePath)) !== value.evidence_sha256) throw new Error('Подтверждение изменено.');
    return { ...value, noticePath };
  } catch {
    throw new Error('INTEROP_RIGHTS_UNCONFIRMED: до упаковки требуется проверенное основание распространения VGCore interop; см. worker/INTEROP-DISTRIBUTION.md.');
  }
}

export async function probeNativeWorker(path,{executorFactory}={}) {
  const directory=mkdtempSync(join(tmpdir(),'CorelMcp-native-readiness-'));
  const factory=executorFactory??(async options=>{const {CSharpExecutor}=await import('../dist/executor.js');return new CSharpExecutor(options);});
  const executor=await factory({workerPath:path,executionMode:'hybrid'});
  try {
    const result=await executor.run({job_id:randomUUID(),tool:'corel_status',run_directory:directory},{},()=>{});
    writeFileSync(join(directory,'readiness.json'),JSON.stringify(result,null,2));
    if(!result.ok||result.cleanup?.completed!==true||result.result?.executor?.execution_mode!=='hybrid'||result.result?.executor?.native_module!==VERSION)
      throw new Error('Native VBA runtime не подтверждён; активация не выполнена. Диагностика: '+directory);
    return result;
  } finally {executor.close();}
}

export function verifyWorkerPackage(directory = join(root, 'worker/publish/win-x64')) {
  const manifest = json(join(directory, 'worker-manifest.json'));
  if (manifest.schema_version !== 1 || manifest.worker_version !== VERSION || manifest.protocol_version !== 1
      || manifest.rid !== 'win-x64' || manifest.self_contained !== true || !approval(manifest.interop_approval)
      || !manifest.files || Array.isArray(manifest.files) || typeof manifest.files !== 'object') throw new Error('Несовместимый manifest исполнителя.');
  if (!required.every(name => Object.hasOwn(manifest.files, name))) throw new Error('Комплект исполнителя неполон.');
  let count = 0, bytes = 0; const actual = [];
  function walk(directoryPath, prefix = '') {
    noLinks(directoryPath);
    for (const name of readdirSync(directoryPath)) {
      const relative = prefix + name, path = localPath(directory, relative), stat = lstatSync(path);
      if (++count > 4000 || relative.split('/').length > 16) throw new Error('Превышен бюджет проверки комплекта.');
      if (stat.isDirectory()) walk(path, relative + '/');
      else {
        if (!stat.isFile() || (bytes += stat.size) > 2 * 1024**3 || stat.size > 512 * 1024**2) throw new Error('Недопустимый файл комплекта.');
        if (relative === 'worker-manifest.json') continue;
        if ((/\.(?:bas|gms|ps1|psm1|cs|cmd|bat)$/i.test(relative)&&relative!=='native/CorelMcp.Runtime.gms')
            || /(?:vbide|vbe\.interop|vbeext).*\.dll$/i.test(relative)) throw new Error('Неподтверждённый исполняемый код или VBE dependency в runtime-комплекте.');
        if (!digest(manifest.files[relative]) || sha(readFileSync(path)) !== manifest.files[relative]) throw new Error('Hash/fingerprint файла отсутствует или изменён: ' + relative);
        actual.push(relative);
      }
    }
  }
  walk(directory);
  const native=verifyNativeModule(directory);
  if(manifest.native_module?.source_sha256!==native.source_sha256||manifest.native_module?.gms_sha256!==native.gms_sha256
      ||manifest.native_module?.module_version!==native.module_version)throw new Error('Manifest комплекта не привязан к native module.');
  if (actual.length !== Object.keys(manifest.files).length) throw new Error('Manifest ссылается на отсутствующие файлы.');
  const runtime = json(join(directory, 'CorelMcp.Worker.runtimeconfig.json')).runtimeOptions;
  if (runtime?.tfm !== 'net10.0' || runtime.framework || runtime.frameworks
      || !['Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App'].every(name => runtime.includedFrameworks?.some(item => item.name === name && /^10\./.test(item.version))))
    throw new Error('Комплект не подтверждает self-contained .NET 10 Windows Desktop.');
  return manifest;
}

export function verifyNativeModule(directory) {
  const path=localPath(directory,'native/module-manifest.json'),value=json(path);
  const fields=['schema_version','protocol','module_version','corel_major','source_sha256','project_name','file','gms_sha256','operations'];
  const allowed=new Set(['handshake','validate_fonts','create_batch','update_batch','create_shapes','style','move','duplicate','replace_literal','set_text','format_text','convert_text','extract_geometry']);
  if(Object.keys(value).length!==fields.length||fields.some(key=>!Object.hasOwn(value,key))
      ||value.schema_version!==1||value.protocol!==1||value.module_version!==VERSION||value.corel_major!==27
      ||!digest(value.source_sha256)||!digest(value.gms_sha256)||value.project_name!=='CorelMcp_Runtime_v1_'+value.source_sha256.slice(0,12)
      ||value.file!=='CorelMcp.Runtime.gms'||!Array.isArray(value.operations)||value.operations.length>13
      ||new Set(value.operations).size!==value.operations.length||value.operations.some(op=>!allowed.has(op))
      ||!['handshake','validate_fonts','create_batch','update_batch'].every(op=>value.operations.includes(op)))throw new Error('Неподтверждённый native module manifest.');
  const module=localPath(directory,'native/'+value.file),stat=lstatSync(module);
  if(!stat.isFile()||stat.size<1||stat.size>16*1024**2||sha(readFileSync(module))!==value.gms_sha256)throw new Error('Native GMS hash не совпадает с manifest.');
  return value;
}

export function sealWorkerPackage(directory = join(root, 'worker/publish/win-x64'), repository = root) {
  const permission = assertInteropApproval(repository);
  noLinks(directory);
  const native=verifyNativeModule(directory),sourceNative=verifyNativeModule(join(repository,'worker'));
  if(JSON.stringify(native)!==JSON.stringify(sourceNative))throw new Error('В пакет попал другой native module.');
  const basDirectory=join(repository,'worker/vba'),digestSource=createHash('sha256');
  for(const name of readdirSync(basDirectory).filter(name=>name.endsWith('.bas')).sort()) {
    const path=localPath(basDirectory,name);digestSource.update(name+'\n');digestSource.update(readFileSync(path));
  }
  if(digestSource.digest('hex')!==native.source_sha256)throw new Error('GMS не соответствует текущим фиксированным BAS исходникам.');
  const fingerprint = json(join(repository, 'worker/generated/fingerprint.json'));
  if (sha(readFileSync(join(directory, 'VGCore.Interop.dll'))) !== fingerprint.assembly_sha256) throw new Error('В пакет попала другая interop-сборка.');
  const sources = json(join(repository, 'worker/third-party/dotnet-sources.json'));
  if (!Array.isArray(sources) || sources.length !== 6) throw new Error('Неполный набор .NET license notices.');
  for (const item of sources) {
    const path = localPath(join(repository, 'worker/third-party'), item.file);
    if (!String(item.source).includes('/v10.0.15/') || sha(readFileSync(path)) !== item.sha256) throw new Error('Изменены закреплённые .NET license notices.');
  }
  for (const name of readdirSync(join(repository, 'worker/third-party'))) {
    const path = localPath(join(repository, 'worker/third-party'), name);
    if (!lstatSync(path).isFile()) throw new Error('Неожиданный каталог лицензий.');
    copyFileSync(path, join(directory, 'third-party', name));
  }
  copyFileSync(permission.noticePath, join(directory, 'third-party/Corel-interop-notice.txt'));
  const files = {};
  function walk(path, prefix = '') {
    noLinks(path);
    for (const name of readdirSync(path)) {
      const relative = prefix + name, full = localPath(directory, relative);
      if (lstatSync(full).isDirectory()) walk(full, relative + '/');
      else if (relative !== 'worker-manifest.json') files[relative] = sha(readFileSync(full));
    }
  }
  walk(directory);
  const interop_approval = Object.fromEntries(['scope', 'typelib_sha256', 'evidence_sha256', 'source', 'reviewed_at', 'reviewed_by'].map(key => [key, permission[key]]));
  const manifest = { schema_version: 1, worker_version: VERSION, protocol_version: 1, rid: 'win-x64', self_contained: true, interop_approval,
    native_module:{module_version:native.module_version,source_sha256:native.source_sha256,gms_sha256:native.gms_sha256},files };
  const temporary = join(directory, 'worker-manifest.json.part');
  writeFileSync(temporary, JSON.stringify(manifest, null, 2) + '\n', { flag: 'wx' });
  renameSync(temporary, join(directory, 'worker-manifest.json'));
  return verifyWorkerPackage(directory);
}

export function probeWorker(path, { launch, environment, timeoutMs = 10000 } = {}) {
  return new Promise((resolveProbe, reject) => {
    const env = environment ?? Object.fromEntries(Object.entries(process.env).filter(([key]) => !/^DOTNET_/i.test(key)));
    const child = launch?.() ?? spawn(path, [], { env, windowsHide: true, shell: false, stdio: ['pipe', 'pipe', 'pipe'] });
    let buffer = Buffer.alloc(0), ready, failed = false;
    const finish = error => { if (failed) return; failed = true; clearTimeout(timer); child.stdin.end(); reject(error); };
    const timer = setTimeout(() => finish(new Error('Не подтверждён ready исполнителя; COM-задания не отправлялись.')), timeoutMs);
    child.stdout.on('data', chunk => {
      if (failed) return;
      buffer = Buffer.concat([buffer, chunk]);
      if (buffer.length > 65536) return finish(new Error('Слишком большая ready-запись.'));
      const newline = buffer.indexOf(10);
      if (newline < 0) return;
      try {
        if (ready) throw new Error('Лишняя запись ready.');
        const value = parseProtocolJson(decoder.decode(buffer.subarray(0, newline)).replace(/\r$/, ''));
        if (Object.keys(value).sort().join() !== 'architecture,protocol_version,runtime,type,worker_version'
            || value.type !== 'ready' || value.protocol_version !== 1 || value.worker_version !== VERSION
            || value.architecture !== 'x64' || !/^\.NET 10\./.test(value.runtime)) throw new Error('Несовместимый ready исполнителя.');
        ready = value; buffer = buffer.subarray(newline + 1); child.stdin.end();
      } catch { finish(new Error('Не подтверждены версия, архитектура и протокол исполнителя.')); }
    });
    child.stderr.on('data', () => {}); // A probe never forwards uncontrolled output to MCP stdout.
    child.stdin.on('error', () => finish(new Error('Канал version probe недоступен.')));
    child.once('error', () => finish(new Error('Не удалось запустить проверку исполнителя.')));
    child.once('close', code => {
      clearTimeout(timer);
      if (failed) return;
      if (code !== 0 || !ready || buffer.length) return finish(new Error('Исполнитель не завершил version probe чисто.'));
      failed = true; resolveProbe(ready);
    });
  });
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    if (process.argv.includes('--check-rights')) { assertInteropApproval(); console.log('Основание распространения interop проверено.'); }
    else if (process.argv.includes('--seal')) { sealWorkerPackage(); console.log('Manifest комплекта создан и проверен.'); }
    else {
      verifyWorkerPackage();await probeWorker(join(root,'worker/publish/win-x64/CorelMcp.Worker.exe'));
      if(process.argv.includes('--verify-native')) {await probeNativeWorker(join(root,'worker/publish/win-x64/CorelMcp.Worker.exe'));console.log('Готовый VBA runtime проверен до активации; документы не изменялись.');}
      else console.log('Комплект и ready исполнителя проверены; COM не вызывался.');
    }
  } catch (error) { console.error(error.code === 'ENOENT' ? 'Готовый комплект исполнителя отсутствует; установка не активирована.' : error.message); process.exitCode = 1; }
}
