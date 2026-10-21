#!/usr/bin/env bash
# Run inside the prepared Linux sandbox, from a disposable project checkout.
set -uo pipefail

usage() {
    cat <<'EOF'
Usage: bash tools/research/filament-linux-build/run-case.sh baseline|fixed \
  --source /inputs/filament --output-root /work/build/CASE \
  --evidence /work/evidence/CASE [--parallel 8] [--runtime-identifier linux-x64]

Existing output/evidence directories must be empty. Cache environment variables
are supplied by the caller. Baseline returns the actual failed build exit code;
an unexpectedly successful baseline returns 1. Fixed returns 0 only after all
build, no-GPU ABI, manifest and portable checks succeed.
EOF
}

die() { printf 'run-case: %s\n' "$*" >&2; exit 2; }
[[ $# -gt 0 ]] || { usage >&2; exit 2; }
case_name=$1
shift
case "$case_name" in baseline|fixed) ;; -h|--help) usage; exit 0 ;; *) die 'case must be baseline or fixed' ;; esac
source_root= output_root= evidence_root=
parallel=8
rid=linux-x64
while [[ $# -gt 0 ]]; do
    case "$1" in
        --source|--output-root|--evidence|--parallel|--runtime-identifier)
            [[ $# -ge 2 ]] || die "missing value for $1"
            case "$1" in
                --source) source_root=$2 ;;
                --output-root) output_root=$2 ;;
                --evidence) evidence_root=$2 ;;
                --parallel) parallel=$2 ;;
                --runtime-identifier) rid=$2 ;;
            esac
            shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) die "unknown argument: $1" ;;
    esac
done
[[ -n "$source_root" && -n "$output_root" && -n "$evidence_root" ]] || die 'source, output-root and evidence are required'
[[ "$parallel" =~ ^[0-9]+$ && ${#parallel} -le 2 ]] || die 'parallel must be 1..64'
parallel=$((10#$parallel))
(( parallel >= 1 && parallel <= 64 )) || die 'parallel must be 1..64'
[[ "$rid" == linux-x64 || "$rid" == linux-arm64 ]] || die 'runtime-identifier must be linux-x64 or linux-arm64'
[[ $(uname -s) == Linux ]] || die 'run this script inside the prepared Linux sandbox'
project_root=$(pwd -P)
[[ -f tools/filament/Build-FilamentNative.ps1 && -f tools/Verify-FilamentRuntime.ps1 && -f native/HeyMelody.Filament.Native/CMakeLists.txt ]] || die 'working directory must be the disposable project root'
for tool in realpath python3 pwsh clang-18 clang++-18 cmake ninja git dotnet; do
    command -v "$tool" >/dev/null 2>&1 || die "required tool not found: $tool"
done
source_root=$(realpath -e -- "$source_root") || die 'source path does not exist'
output_root=$(realpath -m -- "$output_root") || die 'invalid output-root'
evidence_root=$(realpath -m -- "$evidence_root") || die 'invalid evidence path'
[[ -f "$source_root/filament/include/filament/Engine.h" ]] || die 'source must be the pinned Filament checkout'
[[ "$output_root" != / && "$evidence_root" != / && "$output_root" != "$evidence_root" ]] || die 'output and evidence must be distinct non-root directories'
case "$output_root/" in "$evidence_root/"*) die 'output-root cannot be inside evidence' ;; esac
case "$evidence_root/" in "$output_root/"*) die 'evidence cannot be inside output-root' ;; esac
prepare_empty_directory() {
    local path=$1
    [[ ! -L "$path" ]] || die "directory cannot be a symbolic link: $path"
    if [[ -e "$path" ]]; then
        [[ -d "$path" && -z $(find "$path" -mindepth 1 -maxdepth 1 -print -quit) ]] || die "refusing to reuse nonempty directory: $path"
    else
        mkdir -p -- "$path" || die "cannot create directory: $path"
    fi
}
prepare_empty_directory "$output_root"
prepare_empty_directory "$evidence_root"
export CC=clang-18 CXX=clang++-18
started=$(date -u +%Y-%m-%dT%H:%M:%SZ)
run_id=$(date -u +%Y%m%dT%H%M%SZ)-$$
model_output="$project_root/artifacts/filament-linux-build-$case_name-$run_id"
runtime_root="$output_root/runtimes/$rid/native"
steps_file="$evidence_root/steps.tsv"
: > "$steps_file"

finish() {
    local status=$1 exit_code=$2
    trap - INT TERM
    python3 - "$evidence_root" "$case_name" "$status" "$exit_code" "$started" "$project_root" "$source_root" "$output_root" "$rid" "$parallel" "$model_output" <<'PY'
import datetime, hashlib, json, pathlib, re, sys
evidence, case, status, code, started, project, source, output, rid, parallel, model_output = sys.argv[1:]
root = pathlib.Path(evidence)
steps = []
for row in (root / "steps.tsv").read_text().splitlines():
    name, step_code, began, ended = row.split("\t")
    steps.append(dict(name=name, exitCode=int(step_code), startedUtc=began, finishedUtc=ended,
                      command=f"{name}.command.txt", log=f"{name}.log"))
first_error = None
build_log = root / "build.log"
if build_log.exists():
    ansi = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]")
    with build_log.open(errors="replace") as stream:
        for line_number, line in enumerate(stream, 1):
            clean = ansi.sub("", line).rstrip("\n")
            if re.search(r"(?:fatal )?error:|undefined reference|undefined symbol:|CMake Error|Exception:|ninja: build stopped", clean):
                first_error = dict(log="build.log", line=line_number, text=clean)
                break
summary = dict(schemaVersion=1, case=case, status=status, exitCode=int(code), startedUtc=started,
               finishedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
               projectRoot=project, filamentSource=source, outputRoot=output,
               runtimeIdentifier=rid, parallel=int(parallel), cc="clang-18", cxx="clang++-18",
               modelPreviewOutput=model_output, firstBuildError=first_error, steps=steps,
               scope="Linux compilation, no-GPU C ABI smoke, static runtime verification and portable checks; no GPU or Bluetooth validation.")
(root / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
if first_error:
    (root / "first-build-error.json").write_text(json.dumps(first_error, indent=2) + "\n")
hashes = []
for path in sorted(root.rglob("*")):
    if path.is_file() and path.name != "SHA256SUMS":
        hashes.append(f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.relative_to(root).as_posix()}")
(root / "SHA256SUMS").write_text("\n".join(hashes) + "\n")
PY
    local metadata_code=$?
    if (( metadata_code != 0 )); then
        status=metadata-failed
        if (( exit_code == 0 )); then exit_code=$metadata_code; fi
    fi
    printf 'case=%s status=%s exit=%s summary=%s/summary.json\n' "$case_name" "$status" "$exit_code" "$evidence_root"
    exit "$exit_code"
}
trap 'finish interrupted 130' INT
trap 'finish interrupted 143' TERM

run_step() {
    local step_name=$1 step_started step_finished step_exit_code
    shift
    step_started=$(date -u +%Y-%m-%dT%H:%M:%SZ)
    { printf 'cwd=%q\n' "$project_root"; printf '%q ' "$@"; printf '\n'; } > "$evidence_root/$step_name.command.txt"
    "$@" > "$evidence_root/$step_name.log" 2>&1
    step_exit_code=$?
    step_finished=$(date -u +%Y-%m-%dT%H:%M:%SZ)
    printf '%s\n' "$step_exit_code" > "$evidence_root/$step_name.exit-code"
    printf '%s\t%s\t%s\t%s\n' "$step_name" "$step_exit_code" "$step_started" "$step_finished" >> "$steps_file"
    printf 'case=%s step=%s exit=%s log=%s/%s.log\n' "$case_name" "$step_name" "$step_exit_code" "$evidence_root" "$step_name"
    return "$step_exit_code"
}
record_environment() {
    local environment_name
    uname -a
    cat /etc/os-release
    printf '\nCompiler and build tools\n'
    clang-18 --version && clang++-18 --version && cmake --version && ninja --version && git --version && python3 --version || return $?
    pwsh -NoProfile -NonInteractive -Command '$PSVersionTable | ConvertTo-Json' || return $?
    dotnet --info || return $?
    printf '\nFilament source commit\n'
    git -c "safe.directory=$source_root" -C "$source_root" rev-parse HEAD || return $?
    printf '\nProject commit (may be an exported source tree)\n'
    git -c "safe.directory=$project_root" -C "$project_root" rev-parse HEAD 2>/dev/null || printf 'unavailable: exported source tree\n'
    printf '\nSelected environment (no complete environment dump)\n'
    for environment_name in CC CXX HOME XDG_CACHE_HOME DOTNET_CLI_HOME NUGET_PACKAGES TMPDIR; do
        printf '%s=%s\n' "$environment_name" "${!environment_name-}"
    done
    printf '\nInput file hashes\n'
    sha256sum "${BASH_SOURCE[0]}" tools/filament/Build-FilamentNative.ps1 tools/Verify-FilamentRuntime.ps1 native/HeyMelody.Filament.Native/CMakeLists.txt native/HeyMelody.Filament.Native/tools/smoke.cpp
}
run_step environment record_environment || finish environment-failed $?
if command -v dpkg-query >/dev/null 2>&1; then
    run_step packages dpkg-query -W '-f=${binary:Package}\t${Version}\n' || finish packages-failed $?
fi
run_step build pwsh -NoProfile -NonInteractive -File ./tools/filament/Build-FilamentNative.ps1 \
    -FilamentSource "$source_root" -RuntimeIdentifier "$rid" -OutputRoot "$output_root" \
    -FetchBuildTools -SkipSmoke -Parallel "$parallel"
build_code=$?
if [[ "$case_name" == baseline ]]; then
    if (( build_code == 0 )); then finish baseline-unexpectedly-passed 1; fi
    finish baseline-build-failed "$build_code"
fi
(( build_code == 0 )) || finish build-failed "$build_code"
run_step abi-smoke env -u LD_LIBRARY_PATH \
    "$output_root/b/$rid/hm_filament_smoke" || finish abi-smoke-failed $?
run_step verify-runtime env "HM_REPRO_VERIFY=$project_root/tools/Verify-FilamentRuntime.ps1" \
    "HM_REPRO_NATIVE=$runtime_root" "HM_REPRO_RID=$rid" \
    pwsh -NoProfile -NonInteractive -Command '$ErrorActionPreference = "Stop"; & $env:HM_REPRO_VERIFY -Directory $env:HM_REPRO_NATIVE -RuntimeIdentifier $env:HM_REPRO_RID -RequireCurrent | ConvertTo-Json -Depth 8' \
    || finish verify-runtime-failed $?
run_step portable-rendering dotnet run --project tests/HeyMelody.Rendering.Filament.Tests -c Release \
    || finish portable-rendering-failed $?
run_step portable-model dotnet run --project tests/HeyMelody.ModelPreview.Tests -c Release -- --output-dir "$model_output" \
    || finish portable-model-failed $?
archive_reports() {
    python3 - "$evidence_root" "$project_root" "$model_output" <<'PY'
import json, pathlib, shutil, sys
evidence, project, model = map(pathlib.Path, sys.argv[1:])
artifacts = (project / "artifacts").resolve()
render = None
with (evidence / "portable-rendering.log").open() as stream:
    for line in stream:
        try:
            value = json.loads(line)
        except json.JSONDecodeError:
            continue
        if isinstance(value, dict) and value.get("success") is True and "output" in value:
            render = pathlib.Path(value["output"])
if render is None:
    raise RuntimeError("Portable rendering check did not report its artifact directory")
for name, directory in (("portable-rendering", render), ("portable-model", model)):
    report = (directory / "report.json").resolve(strict=True)
    if not report.is_relative_to(artifacts):
        raise RuntimeError("Portable report escaped the disposable project artifacts")
    if json.loads(report.read_text()).get("success") is not True:
        raise RuntimeError(f"{name} report does not declare success")
    shutil.copyfile(report, evidence / f"{name}-report.json")
verified = json.loads((evidence / "verify-runtime.log").read_text())
if verified.get("success") is not True:
    raise RuntimeError("Runtime verification did not declare success")
(evidence / "verify-runtime-result.json").write_text(json.dumps(verified, indent=2) + "\n")
PY
}
run_step archive-reports archive_reports || finish archive-reports-failed $?
finish passed 0
