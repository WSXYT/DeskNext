// Hosted X11 interoperability probe, not a compositor/physical-desktop visual test.
import { spawn, spawnSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, join, resolve, sep } from 'node:path';
import { createInterface } from 'node:readline';
import { setTimeout as delay } from 'node:timers/promises';

if (process.platform !== 'linux' || process.env.GITHUB_ACTIONS !== 'true' ||
    process.env.RUNNER_ENVIRONMENT !== 'github-hosted' || !process.env.DISPLAY)
  throw Error('Requires an isolated X11 session on a disposable GitHub-hosted Linux runner.');
const executable = resolve(process.argv[2] ?? 'artifacts/native-manual-ui/DeskNest.App');
const evidence = resolve('artifacts/nautilus-drag');
mkdirSync(evidence, { recursive: true });
function command(exe, args, timeout = 5000) {
  const r = spawnSync(exe, args.map(String), { encoding: 'utf8', timeout, maxBuffer: 1024 * 1024 });
  if (r.error || r.status !== 0) throw r.error ?? Error(`${exe}: ${r.stderr || r.stdout} (exit ${r.status})`);
  return r.stdout.trim();
}
const xdo = (...args) => command('xdotool', args);
function capture(name) { command('scrot', [join(evidence, name + '.png')]); }

// Native AT-SPI locates the selected Nautilus row; no guessed theme/sidebar offsets.
const locateItem = `
import sys, json, pyatspi
from collections import deque
folder, item = sys.argv[1:]
desktop = pyatspi.Registry.getDesktop(0)
for app in desktop:
    if not ('nautilus' in app.name.lower() or app.name == 'Files'):
        continue
    for window in app:
        if folder not in window.name:
            continue
        queue = deque([window])
        visited = 0
        while queue and visited < 4000:
            node = queue.popleft()
            visited += 1
            try:
                if node.name == item and node.getState().contains(pyatspi.STATE_SHOWING):
                    r = node.queryComponent().getExtents(pyatspi.DESKTOP_COORDS)
                    if r.width > 0 and r.height > 0:
                        print(json.dumps({'X': r.x + r.width // 2, 'Y': r.y + r.height // 2}))
                        sys.exit(0)
                queue.extend(node)
            except (NotImplementedError, RuntimeError):
                pass
sys.exit(2)
`;

async function windowFor(folder) {
  const until = Date.now() + 10_000;
  while (Date.now() < until) {
    try {
      const ids = xdo('search', '--onlyvisible', '--class', 'org.gnome.Nautilus').split(/\s+/);
      for (const id of ids.reverse())
        if (xdo('getwindowname', id).includes(basename(folder))) return id;
    } catch { /* Window registration is asynchronous. */ }
    await delay(100);
  }
  throw Error('No visible Nautilus window for ' + folder);
}

async function drag(from, to) {
  xdo('mousemove', from.X, from.Y);
  await delay(150);
  try {
    xdo('keydown', 'Control_L'); // Both directions negotiate Copy; only the app's confirmed import moves.
    xdo('mousedown', 1);
    await delay(150);
    xdo('mousemove', from.X + 24, from.Y + 6);
    await delay(200);
    for (let i = 1; i <= 16; i++) {
      xdo('mousemove', Math.round(from.X + (to.X - from.X) * i / 16), Math.round(from.Y + (to.Y - from.Y) * i / 16));
      await delay(35);
    }
    await delay(250);
  } finally {
    xdo('mouseup', 1);
    xdo('keyup', 'Control_L');
  }
}

function requireFixturePath(path) {
  const full = resolve(path), prefix = join(tmpdir(), 'DeskNest.NativeSmoke.');
  const relative = full.slice(prefix.length);
  if (!full.startsWith(prefix) || !/^[a-f0-9]{32}\//.test(relative) || !full.includes(sep))
    throw Error('The app announced a path outside its isolated native-smoke fixture.');
}

command('gsettings', ['set', 'org.gnome.desktop.interface', 'toolkit-accessibility', 'true']);
command('gsettings', ['set', 'org.gnome.nautilus.preferences', 'default-folder-viewer', 'list-view']);
command('xdg-mime', ['default', 'org.gnome.Nautilus.desktop', 'inode/directory']);
const wm = spawn('openbox', ['--sm-disable'], { stdio: 'ignore' });
await delay(400);
const app = spawn(executable, ['--native-window-smoke', '--explorer-drag'], { stdio: ['ignore', 'pipe', 'pipe'] });
const exited = new Promise(resolveExit => {
  app.once('error', error => resolveExit({ error }));
  app.once('close', (code, signal) => resolveExit({ code, signal }));
});
const timer = setTimeout(() => app.kill('SIGKILL'), 140_000);
let stdout = '', stderr = '', stages = 0;
const managers = [];
app.stderr.on('data', chunk => { stderr += chunk; });
const expected = ['inbound:false', 'outbound:false', 'inbound:true', 'outbound:true'];
try {
  for await (const line of createInterface({ input: app.stdout })) {
    stdout += line + '\n';
    process.stdout.write(line + '\n');
    if (stdout.length + stderr.length > 2 * 1024 * 1024) throw Error('Probe output exceeded its bound.');
    if (!line.startsWith('EXPLORER_DRAG_READY:')) continue;
    const s = JSON.parse(line.slice('EXPLORER_DRAG_READY:'.length));
    if (`${s.Stage}:${s.Directory}` !== expected[stages]) throw Error('Unexpected drag stage/order.');
    requireFixturePath(s.Folder);
    if (s.Item !== (s.Directory ? 'Explorer project' : 'Explorer document.txt')) throw Error('Unexpected fixture item.');
    if (s.Stage === 'outbound') {
      requireFixturePath(s.LocatedFolder);
      await windowFor(s.LocatedFolder); // Independently observe the production Reveal action.
    }
    const manager = spawn('nautilus', ['--new-window', s.Folder], { stdio: 'ignore' });
    manager.once('error', error => { stderr += `Nautilus launch: ${error.message}\n`; });
    managers.push(manager);
    const id = await windowFor(s.Folder), r = s.ExplorerRect;
    xdo('windowmove', id, r.X, r.Y);
    xdo('windowsize', id, r.Width, r.Height);
    xdo('windowactivate', '--sync', id);
    await delay(250);
    let point;
    if (s.Stage === 'inbound') {
      xdo('key', '--clearmodifiers', 'ctrl+a');
      const until = Date.now() + 7000;
      while (Date.now() < until) {
        try { point = JSON.parse(command('/usr/bin/python3', ['-c', locateItem, basename(s.Folder), s.Item], 2000)); break; }
        catch { await delay(150); }
      }
      if (!point) throw Error('AT-SPI did not expose the visible Nautilus fixture row.');
    } else point = { X: r.X + Math.round(r.Width * 0.7), Y: r.Y + Math.round(r.Height * 0.6) };
    capture(`stage-${stages}-before`);
    await drag(s.Stage === 'inbound' ? point : s.Point, s.Stage === 'inbound' ? s.Point : point);
    stages++;
  }
  const result = await exited;
  if (result.error || result.code !== 0 || result.signal) throw result.error ?? Error('App failed: ' + JSON.stringify(result));
  const sections = stdout.split('NATIVE_WINDOW_RESULT_JSON:');
  if (sections.length !== 2) throw Error('Missing or duplicate terminal native result.');
  const terminal = JSON.parse(sections[1].trim());
  const cases = stdout.split('\n').filter(line => line.startsWith('EXPLORER_DRAG_CASE:')).map(line => JSON.parse(line.slice('EXPLORER_DRAG_CASE:'.length)));
  if (stages !== 4 || cases.length !== 2 || terminal.Success !== true || terminal.ExplorerDragVerified !== true ||
      cases.some((c, i) => c.Directory !== Boolean(i) || c.SpaceCreated !== true || c.ImportConfirmed !== true || c.ExternalCopyPreservedSource !== true || c.UndoRestored !== true))
    throw Error('Incomplete native Nautilus/production import/undo evidence.');
  console.log(JSON.stringify({ nautilusDragVerified: true, stages, filesAndDirectories: true, session: 'X11/Xvfb' }));
} catch (error) {
  try { capture('failure'); } catch { }
  throw error;
} finally {
  clearTimeout(timer);
  if (app.exitCode === null && app.signalCode === null) app.kill('SIGKILL');
  await exited;
  wm.kill('SIGTERM');
  for (const manager of managers)
    if (manager.exitCode === null && manager.signalCode === null) manager.kill('SIGTERM');
  writeFileSync(join(evidence, 'app-stdout.log'), stdout);
  writeFileSync(join(evidence, 'app-stderr.log'), stderr);
}
