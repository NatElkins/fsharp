# Hot Reload: T-Gro Feedback Closure Matrix

Last updated: 2026-02-26
Source comments: NatElkins/fsharp#1 (T-Gro top-level review comments, 2026-02-20)

## Goal

Track each major review concern with objective status and evidence so follow-up work is explicit and review risk remains scoped.

## Status legend

- Addressed: implemented and guarded by tests/scripts.
- Partially addressed: meaningful progress, but boundary/risk item still open.
- Open: design/implementation work still required.

## Matrix

### 1) Plugin boundary / layering safety-first

- Status: **Addressed**
- Evidence:
  - `fsc` emit path routes through a generic emit hook abstraction rather than direct hot reload APIs: `src/Compiler/Driver/fsc.fs`.
  - Hot reload hook bootstrap remains explicit-only (`--enable:hotreloaddeltas`) and wires hook behavior per compilation invocation: `src/Compiler/Driver/CompilerEmitHookBootstrap.fs`.
  - Ambient compiler emit-hook mutation has been removed; hook resolution is now explicit-config-only with no process-wide mutable fallback: `src/Compiler/Driver/CompilerEmitHookState.fs`.
  - Hot reload service no longer mutates compiler-wide hook state during session start/end: `src/Compiler/Service/service.fs`.
- Checker compile now injects explicit hook-only enablement (`--enable:hotreloadhook`) while a session is active, preserving synthesized-name replay without ambient mutable hooks: `src/Compiler/Service/service.fs`, `src/Compiler/Driver/CompilerOptions.fs`.
  - `fsc` still does not import hot reload implementation modules directly and resolves hooks through the bootstrap boundary adapter: `src/Compiler/Driver/fsc.fs`, `src/Compiler/Driver/CompilerEmitHookBootstrap.fs`.
  - Architecture guards enforce explicit-only/no-ambient wiring boundaries: `tests/FSharp.Compiler.Service.Tests/HotReload/ArchitectureGuardTests.fs`.
  - Output parity regression proves non-hot-reload artifacts stay unchanged when the flag is toggled: `tests/FSharp.Compiler.Service.Tests/HotReload/HotReloadCheckerTests.fs` (`Compiler outputs stay byte-identical when hot reload capture flag is toggled`).

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
  - Value-reference operation-name heuristics are now constrained to member references (`vref.MemberInfo.IsSome`) plus the explicit `MoveNext` sentinel, removing module-binding name heuristics while preserving lowered-shape detection: `src/Compiler/TypedTree/TypedTreeDiff.fs`.
  - Lowered-shape collection now also records structural trait-call fingerprints (`traitConstraintShapeDigest`) for `TraitCall`/`WitnessArg`, so new builder operations contribute non-name-only evidence without changing current rude-edit outcomes: `src/Compiler/TypedTree/TypedTreeDiff.fs`.
  - Lowered-shape digests now split structural vs heuristic signals (`formatLoweredShapeDigest`) and synthesized rude-edit classification explicitly evaluates both segments (`hasLoweredShapeDigestSegmentValues`), making fallback-to-name heuristics explicit instead of implicit: `src/Compiler/TypedTree/TypedTreeDiff.fs`.
  - Architecture guard enforces member-only value-branch gating, trait-shape collection, and explicit structural/heuristic digest helpers: `tests/FSharp.Compiler.Service.Tests/HotReload/ArchitectureGuardTests.fs`.
- Remaining gap:
  - Remaining work is to reduce or remove the final operation-name heuristic lists (`isLikelyQueryOperationName` / `isLikelyStateMachineOperationName`) once equivalent semantic signals are available for all covered constructs.

### 7) String-based symbol identity chain

- Status: **Partially addressed**
- Evidence:
  - Method token resolution is fail-closed and rejects incomplete runtime signature identity instead of permissive fallback: `src/Compiler/HotReload/DeltaBuilder.fs`.
  - Explicit `ContainingEntity` mapping now resolves through baseline type-token normalization and fails closed when the explicit entity cannot resolve, avoiding permissive candidate fallback: `src/Compiler/HotReload/DeltaBuilder.fs`.
  - Method resolution now pre-indexes baseline methods by normalized containing-type token + full runtime signature identity before applying compatibility fallback matching, reducing accidental cross-type string matches while preserving existing supported shapes: `src/Compiler/HotReload/DeltaBuilder.fs`.
  - Method fallback disambiguation is now fail-closed across both parameter and return signature stages (including single-candidate paths), preventing name-only resolution when signature identities diverge: `src/Compiler/HotReload/DeltaBuilder.fs`.
  - Added no-arg/unit signature normalization for symbol-side parameter identities so strict signature matching remains stable for generated `unit` cases without reopening permissive matching: `src/Compiler/HotReload/DeltaBuilder.fs`.
  - Regression tests now include parameter-mismatch and return-mismatch fail-closed scenarios, plus architecture guards for staged fallback disambiguation: `tests/FSharp.Compiler.Service.Tests/HotReload/DeltaBuilderTests.fs`, `tests/FSharp.Compiler.Service.Tests/HotReload/ArchitectureGuardTests.fs`.
- Remaining gap:
  - End-to-end symbol identity still relies on string identities (`SymbolId`, `MethodDefinitionKey`) rather than semantic symbol objects.

### 8) Manual metadata serialization evolution risk

- Status: **Partially addressed**
- Evidence:
  - Delta metadata serialization remains hand-rolled in hot reload writer path (`DeltaMetadataSerializer`, `DeltaMetadataTables`, `ILBaselineReader`).
  - Automated parity gate now validates SRM table/heap parity plus mdv component scenarios across generations: `tests/FSharp.Compiler.Service.Tests/HotReload/SrmParityTests.fs`, `tests/FSharp.Compiler.ComponentTests/HotReload/MdvValidationTests.fs`, `tests/scripts/check-hotreload-metadata-parity.sh`.
  - Table serialization now fail-fast validates string/blob heap offset indices before dereferencing mirrored heap arrays, preventing silent corruption or delayed index exceptions in malformed delta construction paths: `src/Compiler/CodeGen/DeltaMetadataSerializer.fs`.
  - Regression tests exercise both invalid string-heap and invalid blob-heap index paths directly through `buildTableStream`: `tests/FSharp.Compiler.Service.Tests/HotReload/FSharpDeltaMetadataWriterTests.fs`.
- Remaining gap:
  - Keep parity coverage current as runtime/metadata shapes evolve; this is still not a direct `System.Reflection.Metadata` writer reuse path.

### 9) Large `IlxDeltaEmitter` single-function blast radius

- Status: **Partially addressed**
- Evidence:
  - `emitDelta` now routes metadata row assembly through explicit helper phases (`buildMethodAndParameterRows`, `buildPropertyEventAndSemanticsRows`, `buildCustomAttributeRows`).
  - Final payload assembly (`added/changed method projection`, `PDB delta`, `baseline apply`) now runs through dedicated `finalizeDeltaArtifacts` helpers (`buildAddedOrChangedMethods`, `buildDeltaToUpdatedMethodTokenMap`) instead of inline logic.
  - Metadata reference remapping (`TypeRef`, `MemberRef`, `MethodSpec`, `AssemblyRef`, entity-token dispatch) is now extracted into `createMetadataReferenceRemapper`, reducing direct token-remap state mutation inside `emitDelta`: `src/Compiler/CodeGen/IlxDeltaEmitter.fs`.
  - Architecture guard enforces that phase extraction remains explicit: `tests/FSharp.Compiler.Service.Tests/HotReload/ArchitectureGuardTests.fs`.
- Remaining gap:
  - Additional extraction is still needed to fully separate remap and metadata-reference remapping responsibilities.

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
  - Reduced one main-relative signature drift by localizing hot-reload activity tag literals in `EditAndContinueLanguageService` and removing `Activity.fsi` from the allowlisted drift set (`10 -> 9` files).
  - Removed hot-reload-specific `FSharpCheckProjectResults` signature exposure (`TypedImplementationFiles`, `HotReloadOptimizationData`) and switched service retrieval to non-public reflection so this branch no longer grows explicit hot-reload API surface in `FSharpCheckerResults.fsi`.
  - Removed stale `FSharpCheckerResults.fsi` entries from the main-relative `.fsi` drift allowlist/hash lock once the file returned to parity with `origin/main`, reducing tracked drift surface to 8 files.
- Remaining gap:
  - The allowlisted drift set is still non-trivial and should be reduced through targeted refactors.

## Validation performed for this update

- `./.dotnet/dotnet build FSharp.sln -c Debug -v minimal`
- `./.dotnet/dotnet test tests/FSharp.Compiler.Service.Tests/FSharp.Compiler.Service.Tests.fsproj -c Debug --no-build --filter FullyQualifiedName~HotReload -v minimal` (`322` passed)
- `./.dotnet/dotnet test tests/FSharp.Compiler.ComponentTests/FSharp.Compiler.ComponentTests.fsproj -c Debug --no-build --filter FullyQualifiedName~HotReload -v minimal` (`110` passed)
