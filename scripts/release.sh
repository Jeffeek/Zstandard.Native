#!/usr/bin/env bash
# Compute the release version and tag for Zstandard.Native.
#
# Branch policy mirrors scripts/release.ps1:
#   release/v<semver>  -> STABLE release.   Version: <semver>     Tag: v<semver>
#   master             -> PRE-RELEASE.      Version: <prefix>-preview.<run>+<sha7>
#                                            Tag:     v<prefix>-preview.<run>
#
# Environment / args:
#   BRANCH         (or $GITHUB_REF_NAME)
#   RUN_NUMBER     (or $GITHUB_RUN_NUMBER)
#   SHA            (or $GITHUB_SHA)
#   VERSION_PREFIX (optional, read from Directory.Build.props if absent)
#
# Writes GITHUB_OUTPUT lines when $GITHUB_OUTPUT is set.

set -euo pipefail

BRANCH="${BRANCH:-${GITHUB_REF_NAME:-}}"
RUN_NUMBER="${RUN_NUMBER:-${GITHUB_RUN_NUMBER:-0}}"
SHA="${SHA:-${GITHUB_SHA:-}}"
VERSION_PREFIX="${VERSION_PREFIX:-}"

if [[ -z "${BRANCH}" ]]; then
    echo "ERROR: BRANCH is required (set BRANCH or GITHUB_REF_NAME)." >&2
    exit 1
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
props="${script_dir}/../Directory.Build.props"

if [[ -z "${VERSION_PREFIX}" ]]; then
    if [[ ! -f "${props}" ]]; then
        echo "ERROR: Directory.Build.props not found at ${props}" >&2
        exit 1
    fi
    VERSION_PREFIX="$(grep -oE '<VersionPrefix>[^<]+</VersionPrefix>' "${props}" \
        | head -n1 \
        | sed -E 's|</?VersionPrefix>||g')"
    if [[ -z "${VERSION_PREFIX}" ]]; then
        echo "ERROR: VersionPrefix not found in Directory.Build.props" >&2
        exit 1
    fi
fi

kind=""
prefix=""
suffix=""
tag=""

if [[ "${BRANCH}" =~ ^release/v([0-9]+\.[0-9]+\.[0-9]+(-[A-Za-z0-9.-]+)?)$ ]]; then
    kind="stable"
    prefix="${BASH_REMATCH[1]}"
    suffix=""
    tag="v${prefix}"
elif [[ "${BRANCH}" == "master" ]]; then
    kind="prerelease"
    prefix="${VERSION_PREFIX}"
    short_sha="${SHA:0:7}"
    [[ -z "${short_sha}" ]] && short_sha="local"
    suffix="preview.${RUN_NUMBER}+${short_sha}"
    tag="v${prefix}-preview.${RUN_NUMBER}"
else
    echo "ERROR: Refusing to release from branch '${BRANCH}'. Allowed: master, release/v<semver>." >&2
    exit 1
fi

if [[ -n "${suffix}" ]]; then
    full_version="${prefix}-${suffix}"
else
    full_version="${prefix}"
fi

is_stable="false"
[[ "${kind}" == "stable" ]] && is_stable="true"

echo "Release kind  : ${kind}"
echo "Version       : ${full_version}"
echo "VersionPrefix : ${prefix}"
echo "VersionSuffix : ${suffix}"
echo "Tag           : ${tag}"

if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    {
        echo "kind=${kind}"
        echo "version=${full_version}"
        echo "version_prefix=${prefix}"
        echo "version_suffix=${suffix}"
        echo "tag=${tag}"
        echo "is_stable=${is_stable}"
    } >> "${GITHUB_OUTPUT}"
fi
