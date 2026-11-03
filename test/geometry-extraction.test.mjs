import test from 'node:test';
import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
test('извлечение геометрии отказывает до копирования и сохраняет исходный документ',{skip:process.platform!=='win32'},()=>{
 const run=spawnSync('powershell.exe',['-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',fileURLToPath(new URL('./geometry-extraction.ps1',import.meta.url))],{encoding:'utf8'});
 assert.equal(run.status,0,run.stdout+run.stderr);assert.match(run.stdout,/geometry extraction fixtures passed/);
});
