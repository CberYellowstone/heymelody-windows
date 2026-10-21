#!/usr/bin/env bash
# Prepare the approved, task-local Ubuntu 24.04 Filament reproduction environment.
set -euo pipefail
umask 022

task_root=${1:?usage: prepare-agentbox.sh /var/tmp/hm-filament-repro-<unique-id>}
marker_name=.hm-filament-repro
filament_commit=ea31afeaf66339e7414aa648aa76c7e61adbde28
rootfs_sha=e77b6f10c2590cef872b33ee9f635a0e3fd1f57fb074c0e52b5c7f56147a0c86
pwsh_sha=ddbc4a2d113bbd46d283cfedcbcd117a70caefd7673f41f2b4e0000badf103bc

[[ $EUID == 0 ]] || { echo 'Run as root in WSL agentbox.' >&2; exit 1; }
[[ $task_root =~ ^/var/tmp/hm-filament-repro-[[:alnum:]][[:alnum:]._-]*$ && ! -L $task_root ]] || {
    echo 'Use a non-symlinked /var/tmp/hm-filament-repro-<unique-id> directory.' >&2; exit 1;
}
[[ $(realpath -m -- "$task_root") == "$task_root" ]] || exit 1
marker_value=${task_root##*/}
if [[ -d $task_root ]]; then
    if [[ -e $task_root/$marker_name ]]; then
        [[ ! -L $task_root/$marker_name && $(cat "$task_root/$marker_name") == "$marker_value" ]] || exit 1
    elif [[ -n $(find "$task_root" -mindepth 1 -maxdepth 1 -print -quit) ]]; then
        echo 'Refusing an existing nonempty directory without the task marker.' >&2
        exit 1
    fi
fi
install -d "$task_root"
printf '%s\n' "$marker_value" > "$task_root/$marker_name"
for directory in rootfs inputs sources cases cache evidence home tmp; do
    [[ ! -L $task_root/$directory ]] || { echo 'Refusing a symlinked task directory.' >&2; exit 1; }
    install -d "$task_root/$directory"
done
exec >> "$task_root/evidence/setup.log" 2>&1
rm -f -- "$task_root/evidence/setup.ready"
trap 'result=$?; printf "setup.exit_code=%s at %s\n" "$result" "$(date -u +%FT%TZ)"; printf "%s\n" "$result" > "$task_root/evidence/setup.exit-code"; exit "$result"' EXIT
printf '\nSetup started at %s\n' "$(date -u +%FT%TZ)"

rootfs=$task_root/rootfs
inputs=$task_root/inputs
for tool in bwrap curl tar sha256sum sha512sum install; do command -v "$tool" >/dev/null; done

download() {
    local url=$1 destination=$2
    if [[ ! -s $destination ]]; then
        printf 'Downloading %s\n' "$url"
        curl --fail --location --silent --show-error --retry 3 --retry-delay 2 \
            --connect-timeout 30 --max-time 1800 --output "$destination.partial" "$url"
        mv -- "$destination.partial" "$destination"
    fi
}
verify() {
    local algorithm=$1 expected=$2 file=$3
    printf '%s  %s\n' "$expected" "$file" | "${algorithm}sum" --check --status
    printf '%s verified: %s\n' "$algorithm" "${file##*/}"
}

rootfs_archive=$inputs/ubuntu-base-24.04.5-base-amd64.tar.gz
download https://cdimage.ubuntu.com/ubuntu-base/releases/24.04/release/ubuntu-base-24.04.5-base-amd64.tar.gz "$rootfs_archive"
verify sha256 "$rootfs_sha" "$rootfs_archive"
if [[ ! -e $task_root/.rootfs-extracted ]]; then
    tar --extract --gzip --numeric-owner --file "$rootfs_archive" --directory "$rootfs"
    printf '%s\n' "$rootfs_sha" > "$task_root/.rootfs-extracted"
else
    [[ $(cat "$task_root/.rootfs-extracted") == "$rootfs_sha" ]] || exit 1
fi
# Copy DNS configuration; never bind the host's /etc, /usr, or home into the guest.
cp -L --remove-destination /etc/resolv.conf "$rootfs/etc/resolv.conf"
install -d "$rootfs/work/cases" "$rootfs/work/cache" "$rootfs/work/evidence" \
    "$rootfs/work/sources" "$rootfs/inputs/downloads" "$rootfs/inputs/filament" \
    "$rootfs/home/agent" "$rootfs/tmp" "$rootfs/run" "$rootfs/opt/dotnet" "$rootfs/opt/powershell/7"
# Package maintainer scripts must not start services during this isolated setup.
printf '#!/bin/sh\nexit 101\n' > "$rootfs/usr/sbin/policy-rc.d"
chmod 755 "$rootfs/usr/sbin/policy-rc.d"
install -d "$task_root/cache/apt" "$task_root/cache/nuget" "$task_root/cache/xdg"
chmod 1777 "$task_root/tmp"

# All writable mounts and package installations remain under this task root.
sandbox_root() {
    bwrap --bind "$rootfs" / --proc /proc --dev /dev --tmpfs /run \
        --bind "$task_root/cases" /work/cases --bind "$task_root/cache" /work/cache \
        --bind "$task_root/evidence" /work/evidence --bind "$task_root/sources" /work/sources \
        --bind "$task_root/home" /home/agent --bind "$task_root/tmp" /tmp \
        --bind "$task_root/cache/apt" /var/cache/apt --ro-bind "$inputs" /inputs/downloads \
        --unshare-pid --die-with-parent --new-session --clearenv \
        --setenv PATH /opt/dotnet:/opt/powershell/7:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
        --setenv HOME /root --setenv TMPDIR /tmp --setenv TMP /tmp --setenv TEMP /tmp \
        --setenv XDG_CACHE_HOME /work/cache/xdg-root --setenv XDG_CONFIG_HOME /root/.config \
        --setenv XDG_DATA_HOME /root/.local/share --setenv XDG_STATE_HOME /root/.local/state \
        --setenv DOTNET_ROOT /opt/dotnet --setenv DOTNET_CLI_HOME /root/.dotnet \
        --setenv NUGET_PACKAGES /work/cache/nuget --setenv DOTNET_CLI_TELEMETRY_OPTOUT 1 \
        --setenv DOTNET_SKIP_FIRST_TIME_EXPERIENCE 1 --setenv DEBIAN_FRONTEND noninteractive \
        --setenv LC_ALL C.UTF-8 --chdir / -- "$@"
}

printf 'Installing packages inside the isolated rootfs.\n'
sandbox_root /usr/bin/bash -s <<'GUEST_PACKAGES'
set -euo pipefail
apt-get -o Dpkg::Use-Pty=0 update
apt-get -o Dpkg::Use-Pty=0 install --yes --no-install-recommends \
    ca-certificates curl gzip tar clang-18 cmake ninja-build python3 git \
    libegl-dev libgl-dev libgles-dev libvulkan1 libc6 libgcc-s1 libgssapi-krb5-2 libicu74 \
    liblttng-ust1t64 libssl3t64 libstdc++6 libunwind8 zlib1g tzdata passwd util-linux
if ! getent group 1000 >/dev/null; then groupadd --gid 1000 agent; fi
if ! getent passwd 1000 >/dev/null; then
    useradd --uid 1000 --gid 1000 --home-dir /home/agent --shell /bin/bash agent
fi
[[ $(id -u agent) == 1000 && $(id -g agent) == 1000 ]]
GUEST_PACKAGES

pwsh_archive=$inputs/powershell-7.6.6-linux-x64.tar.gz
download https://github.com/PowerShell/PowerShell/releases/download/v7.6.6/powershell-7.6.6-linux-x64.tar.gz "$pwsh_archive"
verify sha256 "$pwsh_sha" "$pwsh_archive"
tar --extract --gzip --file "$pwsh_archive" --directory "$rootfs/opt/powershell/7"
chmod 755 "$rootfs/opt/powershell/7/pwsh"

metadata=$inputs/dotnet-10.0-releases.json
download https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json "$metadata"
sdk_info=$(sandbox_root /usr/bin/python3 - /inputs/downloads/dotnet-10.0-releases.json <<'SDK_METADATA'
import hashlib, json, pathlib, re, sys
from urllib.parse import urlsplit
p = pathlib.Path(sys.argv[1])
raw = p.read_bytes()
text = raw.decode('utf-8-sig')
print(json.dumps({'metadata_bytes': len(raw), 'characters': len(text),
    'lines': len(text.splitlines()), 'longest_line': max(map(len, text.splitlines()), default=0),
    'sha256': hashlib.sha256(raw).hexdigest()}), file=sys.stderr)
found = set()
for release in json.loads(text)['releases']:
    for sdk in release.get('sdks', []) + ([release['sdk']] if 'sdk' in release else []):
        if sdk.get('version') != '10.0.402':
            continue
        for item in sdk.get('files', []):
            if item.get('rid') == 'linux-x64' and item.get('name', '').endswith('.tar.gz'):
                url, checksum = item['url'], item['hash'].lower()
                assert urlsplit(url).scheme == 'https'
                assert urlsplit(url).hostname in ('builds.dotnet.microsoft.com', 'download.visualstudio.microsoft.com')
                assert re.fullmatch('[0-9a-f]{128}', checksum)
                found.add((url, checksum))
assert len(found) == 1, f'Expected one SDK 10.0.402 linux-x64 archive, found {len(found)}'
print('\t'.join(found.pop()))
SDK_METADATA
)
IFS=$'\t' read -r sdk_url sdk_sha <<< "$sdk_info"
sdk_archive=$inputs/dotnet-sdk-10.0.402-linux-x64.tar.gz
download "$sdk_url" "$sdk_archive"
verify sha512 "$sdk_sha" "$sdk_archive"
printf '%s  %s\n' "$sdk_sha" "${sdk_archive##*/}" > "$task_root/evidence/dotnet-sdk.sha512"
tar --extract --gzip --file "$sdk_archive" --directory "$rootfs/opt/dotnet"

chown 1000:1000 "$task_root/cache"
chown -R 1000:1000 "$task_root/home" "$task_root/cases" "$task_root/evidence" \
    "$task_root/cache/nuget" "$task_root/cache/xdg" "$task_root/sources"
sandbox_user() {
    bwrap --ro-bind "$rootfs" / --proc /proc --dev /dev --tmpfs /run \
        --bind "$task_root/cases" /work/cases --bind "$task_root/cache" /work/cache \
        --bind "$task_root/evidence" /work/evidence --bind "$task_root/sources" /work/sources \
        --bind "$task_root/home" /home/agent --bind "$task_root/tmp" /tmp \
        --ro-bind "$inputs" /inputs/downloads \
        --unshare-pid --die-with-parent --new-session --clearenv \
        --setenv PATH /opt/dotnet:/opt/powershell/7:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
        --setenv HOME /home/agent --setenv TMPDIR /tmp --setenv TMP /tmp --setenv TEMP /tmp \
        --setenv XDG_CACHE_HOME /work/cache/xdg --setenv XDG_CONFIG_HOME /home/agent/.config \
        --setenv XDG_DATA_HOME /home/agent/.local/share --setenv XDG_STATE_HOME /home/agent/.local/state \
        --setenv DOTNET_ROOT /opt/dotnet --setenv DOTNET_CLI_HOME /home/agent/.dotnet \
        --setenv NUGET_PACKAGES /work/cache/nuget --setenv DOTNET_CLI_TELEMETRY_OPTOUT 1 \
        --setenv DOTNET_SKIP_FIRST_TIME_EXPERIENCE 1 --setenv LC_ALL C.UTF-8 \
        --setenv GIT_CONFIG_GLOBAL /dev/null --setenv GIT_CONFIG_SYSTEM /dev/null \
        --setenv GIT_TERMINAL_PROMPT 0 --chdir /work \
        -- /usr/bin/setpriv --reuid=1000 --regid=1000 --clear-groups --no-new-privs -- "$@"
}

printf 'Fetching the pinned public Filament source with the host Git as uid 1000.\n'
install -d -o 1000 -g 1000 "$task_root/cache/git-template"
env -i PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
    HOME="$task_root/home" XDG_CONFIG_HOME="$task_root/home/.config" \
    XDG_CACHE_HOME="$task_root/cache/xdg" XDG_DATA_HOME="$task_root/home/.local/share" \
    XDG_STATE_HOME="$task_root/home/.local/state" TMPDIR="$task_root/tmp" \
    GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_SYSTEM=/dev/null GIT_CONFIG_NOSYSTEM=1 \
    GIT_TERMINAL_PROMPT=0 GIT_TEMPLATE_DIR="$task_root/cache/git-template" LC_ALL=C.UTF-8 \
    /usr/bin/setpriv --reuid=1000 --regid=1000 --clear-groups --no-new-privs \
    /usr/bin/bash -s -- "$task_root" "$filament_commit" <<'FILAMENT_SOURCE'
set -euo pipefail
source_dir=$1/sources/filament
commit=$2
run_git() {
    printf 'git'
    printf ' %q' -c core.autocrlf=false -c core.eol=lf -c credential.helper= -c core.askPass= "$@"
    printf '\n'
    git -c core.autocrlf=false -c core.eol=lf -c credential.helper= -c core.askPass= "$@"
}
run_git --version
if [[ ! -d $source_dir/.git ]]; then
    run_git init --quiet "$source_dir"
    run_git -C "$source_dir" remote add origin https://github.com/google/filament.git
fi
[[ $(git -C "$source_dir" remote get-url origin) == https://github.com/google/filament.git ]]
[[ -z $(git -C "$source_dir" status --porcelain) ]]
run_git -C "$source_dir" fetch --quiet --depth 1 origin "$commit"
run_git -C "$source_dir" checkout --quiet --detach "$commit"
[[ $(git -C "$source_dir" rev-parse HEAD) == "$commit" ]]
[[ -z $(git -C "$source_dir" status --porcelain) ]]
FILAMENT_SOURCE
sandbox_user /usr/bin/bash -s -- "$filament_commit" <<'VERIFY_FILAMENT_SOURCE'
set -euo pipefail
[[ $(git -C /work/sources/filament rev-parse HEAD) == "$1" ]]
[[ -z $(git -C /work/sources/filament status --porcelain) ]]
printf 'Filament HEAD=%s; worktree clean\n' "$(git -C /work/sources/filament rev-parse HEAD)" | tee /work/evidence/filament-source.txt
VERIFY_FILAMENT_SOURCE

sandbox_user /usr/bin/bash -s <<'VERIFY_ENVIRONMENT'
set -euo pipefail
id
awk '/^(Uid|Gid|CapInh|CapPrm|CapEff|CapAmb|NoNewPrivs):/ { print }' /proc/self/status
cat /etc/os-release
clang-18 --version
cmake --version
ninja --version
python3 --version
git --version
dotnet --info
[[ $(dotnet --version) == 10.0.402 ]]
pwsh -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion.ToString(); if ($PSVersionTable.PSVersion.ToString() -ne "7.6.6") { exit 1 }'
VERIFY_ENVIRONMENT
printf 'Ready at %s\n' "$(date -u +%FT%TZ)" > "$task_root/evidence/setup.ready"
printf 'Environment ready: %s\n' "$task_root"
