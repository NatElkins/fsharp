#!/usr/bin/env bash
set -euo pipefail

BASE_REF="${1:-origin/main}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
ALLOWLIST_FILE="${SCRIPT_DIR}/main-fsi-allowlist.txt"

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

echo
if [[ ${#changed[@]} -eq 0 ]]; then
  echo "No src/Compiler .fsi drift detected."
else
  echo "Allowed src/Compiler .fsi drift (${#changed[@]} files):"
  printf '  %s\n' "${changed[@]}"
fi
