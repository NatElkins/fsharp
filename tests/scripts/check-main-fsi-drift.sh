#!/usr/bin/env bash
set -euo pipefail

BASE_REF="${1:-origin/main}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
ALLOWLIST_FILE="${SCRIPT_DIR}/main-fsi-allowlist.txt"
LOCKED_HASH_FILE="${SCRIPT_DIR}/main-fsi-drift-hashes.txt"

if ! git -C "${REPO_ROOT}" rev-parse --verify "${BASE_REF}" >/dev/null 2>&1; then
  echo "error: baseline ref '${BASE_REF}' not found" >&2
  exit 2
fi

if [[ ! -f "${ALLOWLIST_FILE}" ]]; then
  echo "error: allowlist file not found: ${ALLOWLIST_FILE}" >&2
  exit 2
fi

mapfile -t changed < <(
  git -C "${REPO_ROOT}" diff --name-only "${BASE_REF}...HEAD" |
    rg '^src/Compiler/.*\.fsi$' |
    LC_ALL=C sort
)

mapfile -t allowed < <(
  rg -v '^\s*(#|$)' "${ALLOWLIST_FILE}" |
    LC_ALL=C sort
)

unexpected="$(comm -23 <(printf '%s\n' "${changed[@]}") <(printf '%s\n' "${allowed[@]}"))"

echo "baseline: ${BASE_REF}"
echo "allowlist: ${ALLOWLIST_FILE}"

if [[ -n "${unexpected}" ]]; then
  echo
  echo "Unexpected .fsi drift relative to ${BASE_REF}:" >&2
  echo "${unexpected}" >&2
  exit 1
fi

if [[ -f "${LOCKED_HASH_FILE}" ]]; then
  mapfile -t locked_entries < <(
    rg -v '^\s*(#|$)' "${LOCKED_HASH_FILE}" || true
  )

  if [[ ${#locked_entries[@]} -gt 0 ]]; then
    echo "locked-fingerprints: ${LOCKED_HASH_FILE}"
  fi

  for entry in "${locked_entries[@]}"; do
    locked_path="${entry%% *}"
    expected_hash="${entry#* }"

    if [[ "${locked_path}" == "${expected_hash}" ]]; then
      echo "error: invalid locked fingerprint entry '${entry}'" >&2
      exit 2
    fi

    if [[ ! -f "${REPO_ROOT}/${locked_path}" ]]; then
      echo "error: locked fingerprint path not found: ${locked_path}" >&2
      exit 2
    fi

    if git -C "${REPO_ROOT}" diff --quiet "${BASE_REF}...HEAD" -- "${locked_path}"; then
      echo "error: locked fingerprint path '${locked_path}' no longer differs from ${BASE_REF}; remove it from ${LOCKED_HASH_FILE}" >&2
      exit 1
    fi

    actual_hash="$(git -C "${REPO_ROOT}" diff --no-color "${BASE_REF}...HEAD" -- "${locked_path}" | shasum -a 256 | awk '{print $1}')"

    if [[ "${actual_hash}" != "${expected_hash}" ]]; then
      echo "error: locked fingerprint mismatch for '${locked_path}'" >&2
      echo "  expected: ${expected_hash}" >&2
      echo "  actual:   ${actual_hash}" >&2
      echo "  update ${LOCKED_HASH_FILE} only when intentionally changing the mainline .fsi drift." >&2
      exit 1
    fi
  done
fi

echo
if [[ ${#changed[@]} -eq 0 ]]; then
  echo "No src/Compiler .fsi drift detected."
else
  echo "Allowed src/Compiler .fsi drift (${#changed[@]} files):"
  printf '  %s\n' "${changed[@]}"
fi
