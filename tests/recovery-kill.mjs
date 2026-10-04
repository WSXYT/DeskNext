// Run after: dotnet build tests/DeskNest.RecoveryProbe -c Release -m:1 --nodeReuse:false -p:UseSharedCompilation=false
import { spawn, spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const dll = resolve(dirname(fileURLToPath(import.meta.url)),
  'DeskNest.RecoveryProbe/bin/Release/net10.0/DeskNest.RecoveryProbe.dll');
const probe = process.argv[2] ? resolve(process.argv[2]) : dll;
if (!existsSync(probe)) throw new Error(`Build or install the probe first: ${probe}`);
const executable = probe.toLowerCase().endsWith('.dll') ? 'dotnet' : probe;
const prefix = executable === 'dotnet' ? [probe] : [];

for (const kind of ['file', 'directory', 'committed-file', 'committed-directory',
  'rename-file', 'rename-directory', 'delete-file', 'delete-directory', 'undo-file', 'undo-directory',
  'copy-file', 'copy-directory', 'committed-copy-file', 'committed-copy-directory',
  'unreceipted-file', 'unreceipted-directory', 'reverse-unreceipted-file', 'reverse-unreceipted-directory',
  'staged-copy-file', 'staged-copy-directory']) {
  if (process.platform !== 'win32' && kind.includes('copy-')) {
    console.log(JSON.stringify({ kind, skipped: true, reason: 'NTFS copy publication is Windows-only' }));
    continue;
  }
  const root = mkdtempSync(join(tmpdir(), 'DeskNext.RecoveryProbe-'));
  let completed = false;
  let child;
  let exit;
  let exited = false;
  try {
    child = spawn(executable, [...prefix, `prepare-${kind}`, root], { stdio: ['ignore', 'pipe', 'pipe'] });
    let output = '';
    child.stdout.on('data', data => { output += data; });
    child.stderr.on('data', data => { output += data; });
    child.once('error', error => { output += error.message; exited = true; });
    exit = new Promise(resolveExit => child.once('close', (code, signal) => {
      exited = true;
      resolveExit({ code, signal });
    }));
    const deadline = Date.now() + 30000;
    while (!existsSync(join(root, 'ready')) && !exited && Date.now() < deadline)
      await new Promise(resolveWait => setTimeout(resolveWait, 25));
    if (!existsSync(join(root, 'ready')))
      throw new Error(`${kind}: child did not reach the post-move barrier: ${output}`);
    if (!child.kill('SIGKILL')) throw new Error(`${kind}: failed to kill the probe process`);
    const killed = await waitForExit(exit, kind);
    if (killed.code === 0) throw new Error(`${kind}: child exited cleanly instead of being killed`);

    const recovered = spawnSync(executable, [...prefix, 'recover', root], { encoding: 'utf8', timeout: 60000 });
    if (recovered.status !== 0)
      throw new Error(`${kind}: restart recovery failed: ${recovered.stdout}\n${recovered.stderr}`);
    const result = JSON.parse(recovered.stdout.trim().split(/\r?\n/).at(-1));
    // A killed uncommitted undo must roll back to the previously committed forward move.
    const committed = kind.startsWith('committed-') || kind.startsWith('undo-');
    const valid = kind.startsWith('staged-copy-')
      ? result.Success === true && result.ManualReconciliationRequired === true && result.JournalCleared === false &&
        result.StagingPreserved === true && result.SourcePreserved === true && result.IntentPreserved === true &&
        result.PublicationAbsent === true && result.MetadataUnchanged === true && result.SubsequentMoveBlocked === true
      : kind.startsWith('reverse-unreceipted-')
      ? result.Success === true && result.ManualReconciliationRequired === true && result.JournalCleared === false &&
        result.JournalEvidencePreserved === true && result.FencePreserved === true && result.SourcePreserved === true &&
        result.DestinationAbsent === true && result.MetadataUnchanged === true
      : kind.startsWith('unreceipted-')
      ? result.Success === true && result.ManualReconciliationRequired === true && result.JournalCleared === false &&
        result.JournalPreserved === true && result.DestinationPreserved === true &&
        result.SourceAbsent === true && result.MetadataUnchanged === true
      : kind.startsWith('committed-copy-')
      ? result.Success === true && result.CopyRetained === true && result.SourcePreserved === true &&
        result.CommittedPreserved === true && result.ReceiptPreserved === true && result.JournalCleared === true &&
        result.ManualReconciliationRequired === false
      : kind.startsWith('copy-')
      ? result.Success === true && result.CopyRetained === true && result.SourcePreserved === true &&
        result.IntentPreserved === true && result.PublicationNotAdopted === true &&
        result.ManualReconciliationRequired === true && result.JournalCleared === false
      : result.Success === true && result.JournalCleared === true && result.CommittedPreserved === committed &&
        result.SourceRestored === !committed && result.DestinationPresent === committed &&
        result.PendingMarkedRecoveryRequired === !committed &&
        (!kind.endsWith('directory') || result.EmptyDirectoryRestored === true);
    if (!valid) throw new Error(`${kind}: incomplete restart evidence: ${JSON.stringify(result)}`);
    console.log(JSON.stringify({ kind, forciblyTerminated: true, ...result }));
    completed = true;
  } finally {
    if (child && !exited) {
      child.kill('SIGKILL');
      await waitForExit(exit, kind);
    }
    if (completed) rmSync(root, { recursive: true, force: true });
    else console.error(`Failure evidence preserved: ${root}`);
  }
}

async function waitForExit(exit, kind) {
  let timer;
  try {
    return await Promise.race([exit, new Promise((_, reject) => {
      timer = setTimeout(() => reject(new Error(`${kind}: process did not terminate`)), 5000);
    })]);
  } finally {
    clearTimeout(timer);
  }
}
