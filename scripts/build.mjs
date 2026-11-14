import { spawnSync } from 'node:child_process';
import { existsSync, lstatSync, rmSync } from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(fileURLToPath(new URL('..', import.meta.url)));
const output = resolve(root, 'dist');
if (dirname(output) !== root || basename(output) !== 'dist' || (existsSync(output) && lstatSync(output).isSymbolicLink()))
  throw new Error('Каталог сборки не подтверждён; удаление запрещено.');
// Remove only this project's generated output, including stale modules from an earlier release.
rmSync(output, { recursive: true, force: true });
const result = spawnSync(process.execPath, [join(root, 'node_modules/typescript/bin/tsc'), '-p', join(root, 'tsconfig.json')],
  { cwd: root, windowsHide: true, shell: false, stdio: 'inherit' });
if (result.error) throw result.error;
process.exitCode = result.status ?? 1;
