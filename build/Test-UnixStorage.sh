#!/usr/bin/env bash
set -euo pipefail

# Privileged mounts are confined to disposable GitHub-hosted runner fixtures.
if [[ "${GITHUB_ACTIONS:-}" != true || "${RUNNER_ENVIRONMENT:-}" != github-hosted ]]; then
  echo 'Native mount probes require a disposable GitHub-hosted runner.' >&2
  exit 64
fi
platform="$(uname -s)"
[[ "$platform" == Linux || "$platform" == Darwin ]] || exit 64
root="$(mktemp -d "${TMPDIR:-/tmp}/DeskNext.MountProbe-XXXXXX")"
chmod 700 "$root"
linux_mounts=()
mac_attached=false
cleanup() {
  local result=$? failed=false i
  set +e
  if [[ "$platform" == Linux ]]; then
    for ((i=${#linux_mounts[@]}-1; i>=0; i--)); do
      sudo -n umount "${linux_mounts[$i]}" || failed=true
    done
  elif [[ "$mac_attached" == true ]]; then
    hdiutil detach "$root/volume" || failed=true
  fi
  if [[ "$failed" == true ]]; then
    echo "Unmount failed; refusing recursive cleanup: $root" >&2
    exit 1
  fi
  if [[ "$result" == 0 ]]; then rm -rf -- "$root";
  else echo "Failed fixture retained (unmounted): $root" >&2; fi
  exit "$result"
}
trap cleanup EXIT
mkdir "$root/volume" "$root/source" "$root/bind" "$root/stack" "$root/lower" "$root/upper"
printf '%s\n' 'DeskNext native storage fixture' > "$root/owner"

if [[ "$platform" == Linux ]]; then
  truncate -s 64M "$root/volume.img"
  mkfs.ext4 -q -F -m 0 "$root/volume.img"
  sudo -n mount -o loop,nosuid,nodev "$root/volume.img" "$root/volume"
  linux_mounts+=("$root/volume")
  sudo -n chown "$(id -u):$(id -g)" "$root/volume"
  sudo -n mount --bind "$root/source" "$root/bind"
  linux_mounts+=("$root/bind")
  sudo -n mount --bind "$root/lower" "$root/stack"
  linux_mounts+=("$root/stack")
  sudo -n mount --bind "$root/upper" "$root/stack"
  linux_mounts+=("$root/stack")
else
  hdiutil create -size 128m -fs APFS -volname DeskNextProbe \
    -format UDRW "$root/volume.dmg"
  hdiutil attach -nobrowse -mountpoint "$root/volume" "$root/volume.dmg"
  mac_attached=true
fi

results="artifacts/native-mount-results/$(basename "$root")"
DESKNEXT_MOUNT_PROBE_ROOT="$root" dotnet test \
  tests/DeskNest.Core.Tests/DeskNest.Core.Tests.csproj -c Release --no-build \
  --filter FullyQualifiedName~NativeMountTests \
  --logger 'trx;LogFileName=native-mounts.trx' \
  --results-directory "$results"
node - "$results/native-mounts.trx" <<'JS'
const xml = require('node:fs').readFileSync(process.argv[2], 'utf8');
const counters = xml.match(/<Counters\b([^>]*)>/)?.[1] ?? '';
const counts = Object.fromEntries([...counters.matchAll(/(\w+)="(\d+)"/g)].map(m => [m[1], Number(m[2])]));
if (counts.total !== 2 || counts.executed !== 2 || counts.passed !== 2 || counts.failed !== 0 || counts.notExecuted !== 0)
  throw Error('Missing, skipped or failed native mount tests: ' + counters);
JS
printf '{"platform":"%s","nativeMountAndFullDiskChecks":true}\n' "$platform"
