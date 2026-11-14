import { spawnSync } from 'node:child_process';
import { copyFileSync, existsSync, lstatSync, mkdirSync, realpathSync, symlinkSync, unlinkSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { checkUpgrade, defaultUpgradeOptions } from './check-preupgrade.mjs';
import { verifyWorkerPackage, probeWorker, probeNativeWorker } from './worker-package.mjs';

const root = fileURLToPath(new URL('..', import.meta.url));
const serverPath = join(root, 'dist', 'index.js');
const sourceSkill = join(root, 'skills', 'coreldraw-automation');
const targetSkill = join(homedir(), '.agents', 'skills', 'coreldraw-automation');
const configFile = join(process.env.CODEX_HOME ?? join(homedir(), '.codex'), 'config.toml');
const helper = join(root, 'scripts', 'Codex-Config.ps1');
const uninstall = process.argv.includes('--uninstall');
function samePath(a, b) { return resolve(a).toLowerCase() === resolve(b).toLowerCase(); }
function codex(action, additional = []) {
  const result = spawnSync('powershell.exe', ['-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', helper, '-Action', action, ...additional], { windowsHide: true, encoding: 'utf8', shell: false, timeout: 30000 });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`Ошибка Codex ${action}: ${result.stderr || result.stdout}`);
  return result.stdout.trim().replace(/^\uFEFF/, '');
}
function backupConfig() {
  if (!existsSync(configFile)) return;
  const backup = `${configFile}.corelmcp-${new Date().toISOString().replace(/[:.]/g, '-')}.bak`;
  copyFileSync(configFile, backup); console.log(`Резервная копия конфигурации: ${backup}`);
}
try {
  if (process.platform !== 'win32') throw new Error('Установщик требует Windows.');
  if (Number(process.versions.node.split('.')[0]) !== 24) throw new Error('Требуется Node.js 24.');
  if (!existsSync(serverPath) || !existsSync(join(sourceSkill, 'SKILL.md'))) throw new Error('Перед установкой выполните npm ci --ignore-scripts и npm run build.');
  const configured = JSON.parse(codex('List')).find(server => server.name === 'coreldraw');
  const ownsConfig = configured?.transport?.type === 'stdio' && samePath(configured.transport.command, process.execPath) && configured.transport.args?.length === 1 && samePath(configured.transport.args[0], serverPath);
  let link;
  try { link = lstatSync(targetSkill); } catch (error) { if (error.code !== 'ENOENT') throw error; }
  const ownsSkill = link?.isSymbolicLink() && samePath(realpathSync(targetSkill), realpathSync(sourceSkill));
  if (configured && !ownsConfig) throw new Error('Уже существует сторонняя регистрация MCP coreldraw. Она сохранена; перед установкой этого проекта явно выберите другую регистрацию или удалите прежнюю.');
  if (link && !ownsSkill) throw new Error(`По пути ${targetSkill} уже существует сторонний навык. Он сохранён.`);
  const admission = checkUpgrade(defaultUpgradeOptions());
  if (!admission.ok) throw new Error(admission.message + ' ' + admission.reason);
  if (!uninstall) {
    verifyWorkerPackage();
    await probeWorker(join(root, 'worker/publish/win-x64/CorelMcp.Worker.exe'));
    await probeNativeWorker(join(root, 'worker/publish/win-x64/CorelMcp.Worker.exe'));
  }
  if (uninstall) {
    if (ownsConfig) { backupConfig(); codex('Remove'); }
    if (ownsSkill) unlinkSync(targetSkill); // Only remove the verified junction itself.
    console.log('Регистрация MCP и ссылка на навык этого проекта удалены. Исходники и отчёты заданий сохранены.');
  } else {
    let createdLink = false;
    if (!link) { mkdirSync(join(homedir(), '.agents', 'skills'), { recursive: true }); symlinkSync(sourceSkill, targetSkill, 'junction'); createdLink = true; }
    try { if (!configured) { backupConfig(); codex('Add', ['-NodePath', process.execPath, '-ServerPath', serverPath]); } }
    catch (error) { if (createdLink) unlinkSync(targetSkill); throw error; }
    const verified = JSON.parse(codex('List')).find(server => server.name === 'coreldraw');
    if (!verified || !samePath(verified.transport.args[0], serverPath)) throw new Error('Регистрация MCP не подтверждена. Проверьте codex mcp get coreldraw.');
    console.log(`MCP: coreldraw -> ${serverPath}`);
    console.log(`Глобальный навык: ${targetSkill} -> ${realpathSync(targetSkill)}`);
    console.log('Откройте новый сеанс Codex для загрузки MCP и использования $coreldraw-automation.');
  }
} catch (error) { console.error(error.message); process.exitCode = 1; }
