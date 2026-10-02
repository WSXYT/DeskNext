import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
const driver = fileURLToPath(new URL('./run-app-probe.mjs', import.meta.url));

function run(body, ...args) {
    const root = mkdtempSync(join(tmpdir(), 'DeskNext-driver-'));
    try {
        const fixture = join(root, 'fixture.mjs');
        writeFileSync(fixture, body);
        return spawnSync(process.execPath, [driver, process.execPath, fixture, ...args],
            { encoding: 'utf8', timeout: 10_000 });
    } finally { rmSync(root, { recursive: true, force: true }); }
}
const report = (value, marker = 'PROBE_RESULT_JSON:') =>
    `console.log(${JSON.stringify(marker)}); console.log(${JSON.stringify(JSON.stringify(value))});`;

test('zero exit without terminal evidence is a failure', () => {
    const result = run('console.log("started only");');
    assert.equal(result.status, 1);
    assert.match(result.stderr, /Expected exactly one terminal/);
});
test('successful report cannot mask a failing process', () => {
    assert.equal(run(report({ Success: true }) + 'process.exit(7);').status, 1);
});
test('strict success and valid JSON are required', () => {
    for (const body of [report({ Success: false }), report({ Success: 'true' }),
        'console.log("PROBE_RESULT_JSON: {broken");'])
        assert.equal(run(body).status, 1);
    assert.equal(run(report({ Success: true })).status, 0);
});
test('requested native load needs its own proof', () => {
    assert.equal(run(report({ Success: true }), '--native-library', 'fixture.dll').status, 1);
    assert.equal(run(report({ Success: true, NativeLoadRequested: true, NativeLoadSuccess: true }),
        '--native-library', 'fixture.dll').status, 0);
});
test('independent space window needs its own proof', () => {
    const args = ['--native-window-smoke', '--space-window'];
    assert.equal(run(report({ Success: true }, 'NATIVE_WINDOW_RESULT_JSON:'), ...args).status, 1);
    assert.equal(run(report({ Success: true, SpaceWindowVerified: true }, 'NATIVE_WINDOW_RESULT_JSON:'), ...args).status, 0);
});
test('native manual workflow requires both workflow and clipboard evidence', () => {
    const args = ['--native-window-smoke', '--manual-workflow'];
    assert.equal(run(report({ Success: true, ManualWorkflowVerified: true }, 'NATIVE_WINDOW_RESULT_JSON:'), ...args).status, 1);
    assert.equal(run(report({ Success: true, ManualWorkflowVerified: true, NativeClipboardRoundTripVerified: true },
        'NATIVE_WINDOW_RESULT_JSON:'), ...args).status, 0);
});
test('native recovery inspection needs its own proof', () => {
    const args = ['--native-window-smoke', '--inspect-recovery'];
    assert.equal(run(report({ Success: true }, 'NATIVE_WINDOW_RESULT_JSON:'), ...args).status, 1);
    assert.equal(run(report({ Success: true, RecoveryInspectionVerified: true },
        'NATIVE_WINDOW_RESULT_JSON:'), ...args).status, 0);
});
