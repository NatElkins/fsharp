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

FSHARP_HOTRELOAD_COMPARE_SRM_METADATA=1 "${DOTNET}" test tests/FSharp.Compiler.Service.Tests/FSharp.Compiler.Service.Tests.fsproj \
  -c Debug --no-build --filter FullyQualifiedName~SrmParityTests -v minimal

FSHARP_HOTRELOAD_COMPARE_SRM_METADATA=1 "${DOTNET}" test tests/FSharp.Compiler.ComponentTests/FSharp.Compiler.ComponentTests.fsproj \
  -c Debug --no-build --filter FullyQualifiedName~HotReload.MdvValidationTests -v minimal

echo "hotreload-metadata-parity-check: SRM + mdv parity slices passed."
