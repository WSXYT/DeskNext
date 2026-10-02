// Developer/CI driver: GUI-subsystem executables must finish AND report a valid result.
import { spawnSync } from 'node:child_process';
const [exe, ...args] = process.argv.slice(2);
if (!exe) throw new Error('Usage: node tests/run-app-probe.mjs <executable> <probe arguments>');
const child = spawnSync(exe, args, { encoding: 'utf8', timeout: 90_000, maxBuffer: 2 * 1024 * 1024, windowsHide: true });
process.stdout.write(child.stdout ?? '');
process.stderr.write(child.stderr ?? '');
if (child.error || child.status !== 0 || child.signal)
    throw child.error ?? new Error(`Probe process failed: status=${child.status}, signal=${child.signal}`);
const native = args.includes('--native-window-smoke');
const marker = native ? 'NATIVE_WINDOW_RESULT_JSON:' : 'PROBE_RESULT_JSON:';
const sections = (child.stdout ?? '').split(marker);
if (sections.length !== 2) throw new Error(`Expected exactly one terminal ${marker}`);
const result = JSON.parse(sections[1].trim());
if (result.Success !== true) throw new Error('Probe did not report Success=true.');
if (args.some(a => a === '--native-library' || a.startsWith('--native-library=')) &&
    (result.NativeLoadRequested !== true || result.NativeLoadSuccess !== true))
    throw new Error('Requested native library interop was not verified.');
if (args.includes('--inspect-recovery') && result.RecoveryInspectionVerified !== true)
    throw new Error('Requested recovery inspection was not verified.');
if (args.includes('--space-window') && result.SpaceWindowVerified !== true)
    throw new Error('Requested independent space window was not verified.');
if (args.includes('--manual-workflow') &&
    (result.ManualWorkflowVerified !== true || result.NativeClipboardRoundTripVerified !== true))
    throw new Error('Requested native manual workflow and clipboard round-trip were not verified.');
