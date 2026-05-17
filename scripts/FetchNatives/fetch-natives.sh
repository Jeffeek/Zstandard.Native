#!/usr/bin/env bash
# Download (or build) libzstd native binaries into runtimes/<rid>/native/.
#
# Default: fetch the binary for the *current* host (RID inferred from `uname`).
# Pass --all to attempt every supported RID from this host.
#
# Acquisition strategy per RID:
#   win-x64    : download prebuilt DLL from facebook/zstd v$VERSION release.
#   win-arm64  : build libzstd_shared from source (CMake + MSVC). facebook/zstd
#                does not publish a prebuilt ARM64 Windows DLL. Requires a
#                Windows host (MSYS/Git Bash) with cmake + MSVC on PATH; cannot
#                cross-build from Linux/macOS.
#   linux-*    : copy from the system package (apt/dnf/apk).
#   osx-*      : copy from Homebrew.
#
# The script asserts (post-build) that the *host* RID's native directory is
# non-empty. Cross-RID failures in --all mode are logged but non-fatal.
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

fetch_windows_prebuilt() {
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
    if ! command -v unzip >/dev/null 2>&1; then
        echo "[${rid}] unzip not found. Install unzip or run scripts/FetchNatives/fetch-natives.ps1." >&2
        return 1
    fi
    unzip -q "${tmp}/zstd.zip" -d "${tmp}"

    local dll
    dll="$(find "${tmp}" -type f -name 'libzstd.dll' | head -n1)"
    if [[ -z "${dll}" ]]; then
        echo "[${rid}] libzstd.dll not found inside ${zip_url}" >&2
        return 1
    fi
    cp -f "${dll}" "${dest}/libzstd.dll"
    echo "[${rid}] -> ${dest}/libzstd.dll"
}

fetch_windows_from_source() {
    local rid="$1" ver="$2" cmake_arch="$3"

    case "$(uname -s)" in
        MINGW*|MSYS*|CYGWIN*) ;;
        *)
            echo "[${rid}] building libzstd for Windows ARM64 requires a Windows host with MSVC + CMake; current host is $(uname -s). Run fetch-natives.ps1 on a Windows ARM64 host instead." >&2
            return 1
            ;;
    esac

    command -v cmake >/dev/null 2>&1 || { echo "[${rid}] 'cmake' is required but not on PATH." >&2; return 1; }
    # Prefer Windows built-in bsdtar (System32\tar.exe). MSYS GNU tar mangles
    # native Windows paths ('C:\...').
    local tar_exe="${SYSTEMROOT:-/c/Windows}/System32/tar.exe"
    tar_exe="${tar_exe//\\//}"
    if [[ ! -x "${tar_exe}" ]]; then
        echo "[${rid}] Windows bsdtar not found at ${tar_exe}." >&2
        return 1
    fi

    local src_url="https://github.com/facebook/zstd/releases/download/v${ver}/zstd-${ver}.tar.gz"
    local dest="${runtimes}/${rid}/native"
    mkdir -p "${dest}"

    local tmp
    tmp="$(mktemp -d)"
    trap 'rm -rf "${tmp}"' RETURN

    echo "[${rid}] downloading source ${src_url}"
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL "${src_url}" -o "${tmp}/zstd.tar.gz"
    else
        wget -q -O "${tmp}/zstd.tar.gz" "${src_url}"
    fi

    echo "[${rid}] extracting source (via ${tar_exe})"
    "${tar_exe}" -xzf "${tmp}/zstd.tar.gz" -C "${tmp}"

    local src_root="${tmp}/zstd-${ver}"
    local build_dir="${tmp}/build"

    echo "[${rid}] cmake configure (-A ${cmake_arch})"
    cmake -S "${src_root}/build/cmake" -B "${build_dir}" -A "${cmake_arch}" \
        -DZSTD_BUILD_SHARED=ON \
        -DZSTD_BUILD_STATIC=OFF \
        -DZSTD_BUILD_PROGRAMS=OFF \
        -DZSTD_BUILD_TESTS=OFF

    echo "[${rid}] cmake build Release (libzstd_shared)"
    cmake --build "${build_dir}" --config Release --target libzstd_shared

    local dll
    dll="$(find "${build_dir}" -type f \( -name 'zstd.dll' -o -name 'libzstd.dll' \) | head -n1)"
    if [[ -z "${dll}" ]]; then
        echo "[${rid}] built DLL not found under ${build_dir}." >&2
        return 1
    fi
    cp -f "${dll}" "${dest}/libzstd.dll"
    echo "[${rid}] -> ${dest}/libzstd.dll (built from v${ver} source)"
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

dispatch() {
    local rid="$1"
    case "${rid}" in
        win-x64)    fetch_windows_prebuilt    "${rid}" "${VERSION}" ;;
        win-arm64)  fetch_windows_from_source "${rid}" "${VERSION}" 'ARM64' ;;
        *)          fetch_unix                "${rid}" ;;
    esac
}

host="$(host_rid)"
targets=()
if [[ "${ALL}" == "true" ]]; then
    targets=(win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64)
else
    targets=("${host}")
fi

for rid in "${targets[@]}"; do
    if [[ "${rid}" == "${host}" ]]; then
        # Host RID: strict -- any failure aborts the script.
        dispatch "${rid}"
    else
        # Cross-RID in --all mode: best-effort, log and continue.
        if ! dispatch "${rid}"; then
            echo "[${rid}] skipped (cross-RID, non-fatal)." >&2
        fi
    fi
done

# Post-build assertion: the host RID *must* have produced at least one file.
host_dir="${runtimes}/${host}/native"
if ! compgen -G "${host_dir}/*" > /dev/null; then
    echo "[${host}] no binary produced under ${host_dir} (post-build assertion failed)." >&2
    exit 1
fi

echo
echo "Done. Binaries land under: ${runtimes}"
