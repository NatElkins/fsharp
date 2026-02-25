# Hot Reload: T-Gro Feedback Closure Matrix

Last updated: 2026-02-25
Source comments: NatElkins/fsharp#1 (T-Gro top-level review comments, 2026-02-20)

## Goal

Track each major review concern with objective status and evidence so follow-up work is explicit and review risk remains scoped.

## Status legend

- Addressed: implemented and guarded by tests/scripts.
- Partially addressed: meaningful progress, but boundary/risk item still open.
- Open: design/implementation work still required.

## Matrix

### 1) Plugin boundary / layering safety-first

- Status: **Partially addressed**
- Evidence:
  - `fsc` emit path now routes through generic emit hook abstraction rather than direct hot reload APIs: `src/Compiler/Driver/fsc.fs`.
  - Hot reload hook bootstrap is explicit-only (`--enable:hotreloaddeltas`), with ambient lifecycle owned by hot reload service session start/end: `src/Compiler/Driver/CompilerEmitHookBootstrap.fs`, `src/Compiler/Service/service.fs`.
  - Architecture guards enforce these boundaries: `tests/FSharp.Compiler.Service.Tests/HotReload/ArchitectureGuardTests.fs`.
- Remaining gap:
  - Compiler-wide hook/global state boundaries are still inside core compiler assemblies (not a separate plugin assembly boundary).

### 2) Remove IlxGen-specific hot reload naming hook drift

- Status: **Addressed**
- Evidence:
  - `hotReloadIlxName` removed; centralized naming wrappers now enforce one path in `IlxGen`.
  - Naming-path guard script enforces wrapper-only direct generator access: `tests/scripts/check-ilxgen-name-path.sh`.

### 3) Extract checker-owned hot reload state

- Status: **Addressed**
- Evidence:
  - `FSharpHotReloadService` owns session orchestration and state transitions; checker delegates through thin APIs: `src/Compiler/Service/service.fs`.

### 4) Keep normal compilation naming semantics upstream-equivalent when hot reload is off

- Status: **Addressed**
- Evidence:
  - `CompilerGlobalState` non-map path uses file-index + start-line + 1-based increment semantics: `src/Compiler/TypedTree/CompilerGlobalState.fs`.

### 5) opDigest wildcard catch-all silent-risk

- Status: **Addressed**
- Evidence:
  - `opDigest` is wildcard-free.
  - Guard test enforces no `| _ ->` in `opDigest`: `tests/FSharp.Compiler.Service.Tests/HotReload/ArchitectureGuardTests.fs`.

### 6) State-machine/query string heuristics

- Status: **Partially addressed**
- Evidence:
  - Declaring-type string heuristic removed.
  - Value-reference operation-name heuristics are now gated to member/module references (plus `MoveNext` sentinel) to avoid broad local-name matches while preserving lowered-shape detection: `src/Compiler/TypedTree/TypedTreeDiff.fs`.
  - Architecture guard enforces the value-branch gating pattern: `tests/FSharp.Compiler.Service.Tests/HotReload/ArchitectureGuardTests.fs`.
- Remaining gap:
  - Lowered-shape classification still uses operation-name heuristics; move to stronger semantic signals where feasible.

### 7) String-based symbol identity chain

- Status: **Partially addressed**
- Evidence:
  - Method token resolution is fail-closed and rejects incomplete runtime signature identity instead of permissive fallback: `src/Compiler/HotReload/DeltaBuilder.fs`.
  - Explicit `ContainingEntity` mapping now resolves through baseline type-token normalization and fails closed when the explicit entity cannot resolve, avoiding permissive candidate fallback: `src/Compiler/HotReload/DeltaBuilder.fs`.
  - Regression tests cover incomplete identity, ambiguous mapping, explicit-entity normalization, and explicit-entity mismatch fail-closed behavior: `tests/FSharp.Compiler.Service.Tests/HotReload/DeltaBuilderTests.fs`.
- Remaining gap:
  - End-to-end symbol identity still relies on string identities (`SymbolId`, `MethodDefinitionKey`) rather than semantic symbol objects.

### 8) Manual metadata serialization evolution risk

- Status: **Partially addressed**
- Evidence:
  - Delta metadata serialization remains hand-rolled in hot reload writer path (`DeltaMetadataSerializer`, `DeltaMetadataTables`, `ILBaselineReader`).
  - Automated SRM parity gate now validates table/heap parity across scenarios and multi-generation chains: `tests/FSharp.Compiler.Service.Tests/HotReload/SrmParityTests.fs`, `tests/scripts/check-hotreload-metadata-parity.sh`.
- Remaining gap:
  - Keep parity coverage current as runtime/metadata shapes evolve; this is still not a direct `System.Reflection.Metadata` writer reuse path.

### 9) Large `IlxDeltaEmitter` single-function blast radius

- Status: **Partially addressed**
- Evidence:
  - `emitDelta` now routes metadata row assembly through explicit helper phases (`buildPropertyEventAndSemanticsRows`, `buildCustomAttributeRows`): `src/Compiler/CodeGen/IlxDeltaEmitter.fs`.
  - Architecture guard enforces that phase extraction remains explicit: `tests/FSharp.Compiler.Service.Tests/HotReload/ArchitectureGuardTests.fs`.
- Remaining gap:
  - Additional extraction is still needed to fully separate remap/metadata/PDB/baseline-update responsibilities.

### 10) HR files in core directories

- Status: **Addressed**
- Evidence:
  - Hot reload namespaced modules live under `src/Compiler/HotReload/` (e.g., `DefinitionMap.fs`, `FSharpSymbolChanges.fs`).

### 11) `isEnvVarTruthy` duplication

- Status: **Addressed**
- Evidence:
  - Shared helper used from `Utilities/EnvironmentHelpers.fs`.

### 12) ApplyUpdate setup duplication

- Status: **Addressed**
- Evidence:
  - Shared test helper extracted in `tests/FSharp.Compiler.ComponentTests/HotReload/ApplyUpdateShared.fs`.

### 13) Construct coverage breadth (Tier1/Tier2)

- Status: **Addressed (baseline matrix added)**
- Evidence:
  - Runtime integration construct matrix tests cover Tier1 and Tier2 edit/apply scenarios: `tests/FSharp.Compiler.ComponentTests/HotReload/RuntimeIntegrationTests.fs`.

### 14) Maintain `.fsi` stability relative to `main`

- Status: **Partially addressed**
- Evidence:
  - Guard now enforces allowlist + mandatory hash-locking for every drifted `.fsi`: `tests/scripts/check-main-fsi-drift.sh`.
  - Refresh helper added: `tests/scripts/refresh-main-fsi-drift-hashes.sh`.
- Remaining gap:
  - The allowlisted drift set is still non-trivial and should be reduced through targeted refactors.

## Validation performed for this update

- `./.dotnet/dotnet build FSharp.sln -c Debug -v minimal`
- `./.dotnet/dotnet test tests/FSharp.Compiler.Service.Tests/FSharp.Compiler.Service.Tests.fsproj -c Debug --no-build --filter FullyQualifiedName~HotReload -v minimal` (`315` passed)
- `./.dotnet/dotnet test tests/FSharp.Compiler.ComponentTests/FSharp.Compiler.ComponentTests.fsproj -c Debug --no-build --filter FullyQualifiedName~HotReload -v minimal` (`110` passed)
- `bash tests/scripts/check-hotreload-metadata-parity.sh` (`9` passed)
- `bash tests/scripts/check-main-fsi-drift.sh origin/main`
- `bash tests/scripts/check-ilxgen-name-path.sh`
