#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
DOTNET="${ROOT}/.dotnet/dotnet"

if [[ ! -x "${DOTNET}" ]]; then
  echo "error: dotnet executable not found at ${DOTNET}" >&2
  exit 1
fi

cd "${ROOT}"

"${DOTNET}" test tests/FSharp.Compiler.Service.Tests/FSharp.Compiler.Service.Tests.fsproj \
  -c Debug --no-build --filter FullyQualifiedName~SrmParityTests -v minimal

echo "hotreload-metadata-parity-check: SRM parity test slice passed."
