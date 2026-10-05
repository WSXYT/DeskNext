// Native hosted macOS interoperability, not physical-input or installed-app acceptance.
import { spawn, spawnSync } from 'node:child_process';
import { mkdirSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, join, resolve } from 'node:path';
import { createInterface } from 'node:readline';
import { setTimeout as delay } from 'node:timers/promises';

if (process.platform !== 'darwin' || process.env.GITHUB_ACTIONS !== 'true' ||
    process.env.RUNNER_ENVIRONMENT !== 'github-hosted')
  throw Error('Requires a disposable GitHub-hosted macOS desktop.');
const executable = resolve(process.argv[2] ?? 'artifacts/native-manual-ui/DeskNest.App');
const helper = resolve('artifacts/finder-input');
const evidence = resolve('artifacts/finder-drag');
mkdirSync(evidence, { recursive: true });
function command(exe, args, timeout = 8000) {
  const r = spawnSync(exe, args.map(String), { encoding: 'utf8', timeout, maxBuffer: 1024 * 1024 });
  if (r.error || r.status !== 0) throw r.error ?? Error(`${exe}: ${r.stderr || r.stdout} (exit ${r.status})`);
  return r.stdout.trim();
}
function capture(name) { command('/usr/sbin/screencapture', ['-x', join(evidence, name + '.png')]); }
function requireFixture(path) {
  const full = resolve(path), prefix = join(tmpdir(), 'DeskNest.NativeSmoke.');
  if (!full.startsWith(prefix) || !/^[a-f0-9]{32}\//.test(full.slice(prefix.length)))
    throw Error('Path escaped the isolated native-smoke fixture.');
}
const prepare = `on run argv
  set folderPath to item 1 of argv
  set subjectName to item 2 of argv
  set leftEdge to (item 3 of argv) as integer
  set topEdge to (item 4 of argv) as integer
  set rightEdge to (item 5 of argv) as integer
  set bottomEdge to (item 6 of argv) as integer
  set folderAlias to POSIX file folderPath as alias
  tell application "Finder"
    set w to make new Finder window to folderAlias
    set current view of w to list view
    set bounds of w to {leftEdge, topEdge, rightEdge, bottomEdge}
    set sidebar width of w to 150
    activate
    if subjectName is not "" then
      set subjectAlias to POSIX file (folderPath & "/" & subjectName) as alias
      set selection to {subjectAlias}
    end if
    return id of w
  end tell
end run`;
const visible = `on run argv
  set paths to {}
  tell application "Finder"
    repeat with w in Finder windows
      try
        set end of paths to POSIX path of (target of w as alias)
      on error messageText number errorNumber
        set end of paths to "ERROR " & errorNumber & ": " & messageText
      end try
    end repeat
  end tell
  set AppleScript's text item delimiters to linefeed
  return paths as text
end run`;
const closeOwned = `on run argv
  tell application "Finder"
    repeat with identifier in argv
      try
        close Finder window id (identifier as integer)
      end try
    end repeat
  end tell
end run`;
console.log(command(helper, ['--preflight']));
const app = spawn(executable, ['--native-window-smoke', '--explorer-drag'], { stdio: ['ignore', 'pipe', 'pipe'] });
const exited = new Promise(resolveExit => {
  app.once('error', error => resolveExit({ error }));
  app.once('close', (code, signal) => resolveExit({ code, signal }));
});
const timer = setTimeout(() => app.kill('SIGKILL'), 140_000);
const expected = ['inbound:false', 'outbound:false', 'inbound:true', 'outbound:true'];
let stdout = '', stderr = '', stages = 0;
const windows = [];
app.stderr.on('data', chunk => { stderr += chunk; });
try {
  for await (const line of createInterface({ input: app.stdout })) {
    stdout += line + '\n';
    process.stdout.write(line + '\n');
    if (stdout.length + stderr.length > 2 * 1024 * 1024) throw Error('Probe output exceeded its bound.');
    if (!line.startsWith('EXPLORER_DRAG_READY:')) continue;
    const s = JSON.parse(line.slice('EXPLORER_DRAG_READY:'.length));
    if (`${s.Stage}:${s.Directory}` !== expected[stages] ||
        s.Item !== (s.Directory ? 'Explorer project' : 'Explorer document.txt')) throw Error('Unexpected stage/subject.');
    requireFixture(s.Folder);
    if (!Number.isFinite(s.Scale) || s.Scale <= 0) throw Error('Missing native pixel-to-point scale.');
    if (s.Stage === 'outbound') {
      requireFixture(s.LocatedFolder);
      const wanted = statSync(s.LocatedFolder, { bigint: true });
      let revealed = false;
      for (let attempt = 0; attempt < 10 && !revealed; attempt++) {
        const targets = command('/usr/bin/osascript', ['-e', visible]).split('\n');
        console.log(JSON.stringify({ finderWindowTargets: targets, expectedFolder: s.LocatedFolder }));
        // Finder aliases can spell the same physical directory differently. Read-only observation,
        // not transaction authorization: match the actual window target's device/inode.
        revealed = targets.some(path => {
          if (!path.startsWith('/')) return false;
          try { const actual = statSync(path, { bigint: true }); return actual.dev === wanted.dev && actual.ino === wanted.ino; }
          catch { return false; }
        });
        if (!revealed) await delay(150);
      }
      if (!revealed) throw Error('Production Reveal did not open the containing folder in Finder.');
    }
    const r = Object.fromEntries(Object.entries(s.ExplorerRect).map(([key, value]) => [key, Math.round(value / s.Scale)]));
    if (![r.X, r.Y, r.Width, r.Height].every(Number.isFinite)) throw Error('Invalid Finder rectangle.');
    const id = command('/usr/bin/osascript', ['-e', prepare, s.Folder, s.Stage === 'inbound' ? s.Item : '',
      r.X, r.Y, r.X + r.Width, r.Y + r.Height]);
    if (!/^\d+$/.test(id)) throw Error('Finder did not return an owned window ID.');
    windows.push(id);
    await delay(300);
    let point = { X: r.X + r.Width * 0.7, Y: r.Y + r.Height * 0.65 };
    if (s.Stage === 'inbound') {
      point = null;
      for (let attempt = 0; attempt < 12 && !point; attempt++) {
        try {
          const output = command(helper, ['--locate', basename(s.Folder), s.Item], 3000);
          point = JSON.parse(output.split('FINDER_POINT_JSON:')[1]);
        } catch { await delay(150); }
      }
      if (!point) throw Error('Finder AX did not expose the visible fixture row.');
    }
    const appPoint = { X: s.Point.X / s.Scale, Y: s.Point.Y / s.Scale };
    const from = s.Stage === 'inbound' ? point : appPoint, to = s.Stage === 'inbound' ? appPoint : point;
    console.log(JSON.stringify({ dragStage: stages, from, to, scale: s.Scale }));
    capture(`stage-${stages}-before`);
    console.log(command(helper, ['--drag', from.X, from.Y, to.X, to.Y, join(evidence, `stage-${stages}-drag.png`)]));
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
    throw Error('Incomplete native Finder/production import/undo evidence.');
  console.log(JSON.stringify({ finderDragVerified: true, stages, filesAndDirectories: true, session: 'native macOS' }));
} catch (error) {
  try { capture('failure'); } catch { }
  throw error;
} finally {
  clearTimeout(timer);
  if (app.exitCode === null && app.signalCode === null) app.kill('SIGKILL');
  await exited;
  try { if (windows.length) command('/usr/bin/osascript', ['-e', closeOwned, ...windows]); } catch { }
  writeFileSync(join(evidence, 'app-stdout.log'), stdout);
  writeFileSync(join(evidence, 'app-stderr.log'), stderr);
}
