import { spawnSync } from 'node:child_process';
import { copyFileSync, existsSync, lstatSync, mkdirSync, realpathSync, symlinkSync, unlinkSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

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
  if (result.status !== 0) throw new Error(`Codex ${action} failed: ${result.stderr || result.stdout}`);
  return result.stdout.trim().replace(/^\uFEFF/, '');
}
function backupConfig() {
  if (!existsSync(configFile)) return;
  const backup = `${configFile}.corelmcp-${new Date().toISOString().replace(/[:.]/g, '-')}.bak`;
  copyFileSync(configFile, backup); console.log(`Config backup: ${backup}`);
}
try {
  if (process.platform !== 'win32') throw new Error('This installer requires Windows.');
  if (Number(process.versions.node.split('.')[0]) !== 24) throw new Error('Use Node.js 24.');
  if (!existsSync(serverPath) || !existsSync(join(sourceSkill, 'SKILL.md'))) throw new Error('Run npm ci --ignore-scripts and npm run build before installing.');
  const configured = JSON.parse(codex('List')).find(server => server.name === 'coreldraw');
  const ownsConfig = configured?.transport?.type === 'stdio' && samePath(configured.transport.command, process.execPath) && configured.transport.args?.length === 1 && samePath(configured.transport.args[0], serverPath);
  let link;
  try { link = lstatSync(targetSkill); } catch (error) { if (error.code !== 'ENOENT') throw error; }
  const ownsSkill = link?.isSymbolicLink() && samePath(realpathSync(targetSkill), realpathSync(sourceSkill));
  if (configured && !ownsConfig) throw new Error('An unrelated coreldraw MCP entry already exists. It has been preserved; choose or remove it explicitly before installing this project.');
  if (link && !ownsSkill) throw new Error(`An unrelated skill already exists at ${targetSkill}. It has been preserved.`);
  if (uninstall) {
    if (ownsConfig) { backupConfig(); codex('Remove'); }
    if (ownsSkill) unlinkSync(targetSkill); // Only remove the verified junction itself.
    console.log('Removed this project MCP registration and skill junction. Source and run artifacts remain available.');
  } else {
    let createdLink = false;
    if (!link) { mkdirSync(join(homedir(), '.agents', 'skills'), { recursive: true }); symlinkSync(sourceSkill, targetSkill, 'junction'); createdLink = true; }
    try { if (!configured) { backupConfig(); codex('Add', ['-NodePath', process.execPath, '-ServerPath', serverPath]); } }
    catch (error) { if (createdLink) unlinkSync(targetSkill); throw error; }
    const verified = JSON.parse(codex('List')).find(server => server.name === 'coreldraw');
    if (!verified || !samePath(verified.transport.args[0], serverPath)) throw new Error('The MCP registration could not be verified. Inspect codex mcp get coreldraw.');
    console.log(`MCP: coreldraw -> ${serverPath}`);
    console.log(`Global skill: ${targetSkill} -> ${realpathSync(targetSkill)}`);
    console.log('Open a new Codex session to load the MCP server and use $coreldraw-automation.');
  }
} catch (error) { console.error(error.message); process.exitCode = 1; }
