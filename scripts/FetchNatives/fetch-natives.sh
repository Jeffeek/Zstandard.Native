#!/usr/bin/env bash
# Download libzstd native binaries into runtimes/<rid>/native/.
#
# Default: fetch the binary for the *current* host (RID inferred from `uname`).
# Pass --all to attempt every supported RID.
#
# Usage:
#   bash scripts/FetchNatives/fetch-natives.sh
#   bash scripts/FetchNatives/fetch-natives.sh --version 1.5.6 --all

set -euo pipefail

VERSION="1.5.6"
ALL="false"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --version) VERSION="$2"; shift 2;;
        --all)     ALL="true";   shift;;
        -h|--help)
            grep -E '^# ' "$0" | sed 's/^# //'
            exit 0;;
        *) echo "Unknown arg: $1" >&2; exit 1;;
    esac
done

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "${script_dir}/../.." && pwd)"
runtimes="${repo_root}/runtimes"

host_rid() {
    local os arch
    case "$(uname -s)" in
        Linux*)   os="linux" ;;
        Darwin*)  os="osx"   ;;
        MINGW*|MSYS*|CYGWIN*) os="win" ;;
        *) echo "Unsupported host OS: $(uname -s)" >&2; exit 1;;
    esac
    case "$(uname -m)" in
        x86_64|amd64) arch="x64"   ;;
        aarch64|arm64) arch="arm64" ;;
        *) echo "Unsupported host arch: $(uname -m)" >&2; exit 1;;
    esac
    echo "${os}-${arch}"
}

fetch_windows() {
    local rid="$1" ver="$2"
    local zip_url="https://github.com/facebook/zstd/releases/download/v${ver}/zstd-v${ver}-win64.zip"
    local dest="${runtimes}/${rid}/native"
    mkdir -p "${dest}"

    local tmp
    tmp="$(mktemp -d)"
    trap 'rm -rf "${tmp}"' RETURN

    echo "[${rid}] downloading ${zip_url}"
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL "${zip_url}" -o "${tmp}/zstd.zip"
    else
        wget -q -O "${tmp}/zstd.zip" "${zip_url}"
    fi

    echo "[${rid}] extracting"
    if command -v unzip >/dev/null 2>&1; then
        unzip -q "${tmp}/zstd.zip" -d "${tmp}"
    else
        echo "[${rid}] unzip not found. Install unzip or run scripts/FetchNatives/fetch-natives.ps1." >&2
        return 1
    fi

    local dll
    dll="$(find "${tmp}" -type f -name 'libzstd.dll' | head -n1)"
    if [[ -z "${dll}" ]]; then
        echo "[${rid}] libzstd.dll not found inside ${zip_url}" >&2
        return 1
    fi
    cp -f "${dll}" "${dest}/libzstd.dll"
    echo "[${rid}] -> ${dest}/libzstd.dll"
}

fetch_unix() {
    local rid="$1"
    local dest="${runtimes}/${rid}/native"
    mkdir -p "${dest}"

    case "${rid}" in
        linux-x64)
            local src=""
            for cand in /usr/lib/x86_64-linux-gnu/libzstd.so.1 /usr/lib64/libzstd.so.1 /usr/lib/libzstd.so.1; do
                [[ -f "${cand}" ]] && src="${cand}" && break
            done
            if [[ -z "${src}" ]]; then
                echo "[${rid}] system libzstd not found. Install it (Debian/Ubuntu: 'sudo apt-get install -y libzstd1'; Alpine: 'apk add zstd-libs'; Fedora: 'sudo dnf install libzstd')." >&2
                return 1
            fi
            cp -f "${src}" "${dest}/libzstd.so"
            echo "[${rid}] -> ${dest}/libzstd.so (from ${src})"
            ;;
        linux-arm64)
            local src=""
            for cand in /usr/lib/aarch64-linux-gnu/libzstd.so.1 /usr/lib64/libzstd.so.1 /usr/lib/libzstd.so.1; do
                [[ -f "${cand}" ]] && src="${cand}" && break
            done
            if [[ -z "${src}" ]]; then
                echo "[${rid}] system libzstd not found on this host; cannot cross-fetch." >&2
                return 1
            fi
            cp -f "${src}" "${dest}/libzstd.so"
            echo "[${rid}] -> ${dest}/libzstd.so (from ${src})"
            ;;
        osx-x64|osx-arm64)
            if ! command -v brew >/dev/null 2>&1; then
                echo "[${rid}] Homebrew not found. 'brew install zstd', then copy \$(brew --prefix zstd)/lib/libzstd.dylib here." >&2
                return 1
            fi
            local prefix
            prefix="$(brew --prefix zstd 2>/dev/null || true)"
            if [[ -z "${prefix}" || ! -f "${prefix}/lib/libzstd.dylib" ]]; then
                echo "[${rid}] zstd not installed via Homebrew. Run 'brew install zstd'." >&2
                return 1
            fi
            cp -f "${prefix}/lib/libzstd.dylib" "${dest}/libzstd.dylib"
            echo "[${rid}] -> ${dest}/libzstd.dylib (from ${prefix}/lib/libzstd.dylib)"
            ;;
        *)
            echo "[${rid}] no Unix recipe." >&2
            return 1
            ;;
    esac
}

targets=()
if [[ "${ALL}" == "true" ]]; then
    targets=(win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64)
else
    targets=("$(host_rid)")
fi

for rid in "${targets[@]}"; do
    case "${rid}" in
        win-*)  fetch_windows "${rid}" "${VERSION}" || true ;;
        *)      fetch_unix   "${rid}"             || true ;;
    esac
done

echo
echo "Done. Binaries land under: ${runtimes}"
