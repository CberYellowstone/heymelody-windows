#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 2 || ! $2 =~ ^(baseline|fixed)$ ]]; then
    echo "Usage: sudo bash run-agentbox.sh /var/tmp/hm-filament-repro-<id> baseline|fixed" >&2
    exit 64
fi
task=$(realpath -e -- "$1")
case_name=$2
if [[ $task != /var/tmp/hm-filament-repro-* || $task == /var/tmp/hm-filament-repro- ||
      ! -f $task/.hm-filament-repro || $(cat "$task/.hm-filament-repro") != "${task##*/}" ]]; then
    echo "Refusing an unmarked or unexpected task directory: $task" >&2
    exit 64
fi
if [[ $EUID != 0 ]]; then
    echo 'Run through wsl -d agentbox -u root; the case itself uses uid/gid 1000.' >&2
    exit 77
fi
test -d "$task/cases/$case_name/project"
test -f "$task/inputs/run-case.sh"
test -d "$task/sources/filament/.git"
mkdir -p "$task/evidence/$case_name"
chown 1000:1000 "$task/evidence/$case_name"

exec bwrap --clearenv \
    --ro-bind "$task/rootfs" / \
    --proc /proc --dev /dev --tmpfs /run \
    --bind "$task/cases" /work/cases \
    --bind "$task/cache" /work/cache \
    --bind "$task/evidence" /work/evidence \
    --bind "$task/home" /home/agent \
    --bind "$task/tmp" /tmp \
    --ro-bind "$task/inputs" /inputs/downloads \
    --ro-bind "$task/sources/filament" /inputs/filament \
    --unshare-pid --unshare-ipc --unshare-uts \
    --die-with-parent --new-session \
    --setenv PATH /opt/dotnet:/opt/powershell/7:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
    --setenv HOME /home/agent --setenv LANG C.UTF-8 --setenv LC_ALL C.UTF-8 \
    --setenv DOTNET_ROOT /opt/dotnet --setenv DOTNET_CLI_HOME /home/agent/.dotnet \
    --setenv DOTNET_CLI_TELEMETRY_OPTOUT 1 --setenv DOTNET_NOLOGO 1 \
    --setenv NUGET_PACKAGES /work/cache/nuget \
    --setenv XDG_CACHE_HOME /work/cache/xdg --setenv XDG_CONFIG_HOME /home/agent/.config \
    --setenv XDG_DATA_HOME /home/agent/.local/share --setenv XDG_STATE_HOME /home/agent/.local/state \
    --setenv TMPDIR /tmp --setenv TMP /tmp --setenv TEMP /tmp \
    --chdir "/work/cases/$case_name/project" \
    /usr/bin/setpriv --reuid=1000 --regid=1000 --clear-groups --no-new-privs \
    /bin/bash /inputs/downloads/run-case.sh "$case_name" \
        --source /inputs/filament --output-root "/work/cases/$case_name/output" \
        --evidence "/work/evidence/$case_name" --parallel 8
