#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

APP_DIR="${ROOT}/tests/projects/HotReloadDemo/HotReloadDemoApp"

if [[ ! -d "${APP_DIR}" ]]; then
  echo "error: HotReloadDemoApp directory not found at ${APP_DIR}" >&2
  exit 1
fi

export DOTNET_MODIFIABLE_ASSEMBLIES=debug

pushd "${APP_DIR}" >/dev/null

echo "Running HotReloadDemoApp in scripted mode..." >&2

output="$(../../../../.dotnet/dotnet run -- --scripted --multi-delta)"
exit_code=$?

popd >/dev/null

echo "${output}"

if [[ ${exit_code} -ne 0 ]]; then
  echo "error: HotReloadDemoApp scripted run failed" >&2
  exit ${exit_code}
fi

if ! grep -q "Scripted run succeeded: emitted" <<<"${output}"; then
  echo "error: scripted run did not report success" >&2
  exit 10
fi

echo "Hot reload demo smoke test completed successfully." >&2
