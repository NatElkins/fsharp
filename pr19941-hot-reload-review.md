# F# Hot Reload PR 19941 Review

Review target: `dotnet/fsharp#19941`, branch `hot-reload-v2`

Review base: `git diff origin/main...HEAD`

Fetched base refs before review:

- Merge-base: `188a8b085cd05f990b4d86e89e0232ee51f8a777`
- `origin/main`: `7d37e582fcc0a607e568eba4be56bdf1bc102c47`
- `HEAD`: `670b94f008e642c1de0552b12e09fd19116de9f8`

Diff size: `161 files changed, 61144 insertions(+), 241 deletions(-)`

Review method: generated the changed-file and hunk inventory first, bucketed the diff into docs, AbstractIL metadata readers/writers, CodeGen delta/PDB, Driver emit hook, HotReload services/state, TypedTree diff/name maps, FCS service API, tests/scripts/tools, then consolidated seven review lenses into one prioritized report.

## Top 10 Worth-It Findings

### 1. Raw sequence-point blobs are copied into the delta PDB

File: `src/Compiler/CodeGen/HotReloadPdb.fs:194`

Quote:

```fsharp
metadata.GetOrAddBlob(reader.GetBlobBytes methodInfo.SequencePointsBlob)
```

Issue: the PDB delta copies the fresh sequence-point blob byte-for-byte. That blob starts with the method local-signature token and can also contain inline document handles for multi-document methods.

Why it matters: delta IL bodies use the remapped delta local signature token computed in `src/Compiler/CodeGen/IlxDeltaEmitter.fs:1048`, but the PDB can still reference the fresh full-assembly token. Methods with locals can get PDB/IL local-signature skew, and `#line` or multi-document methods can bind breakpoints/stepping to wrong or absent document rows.

Suggested fix: pass a `methodToken -> LocalSignatureToken` map and document remap into `HotReloadPdb.emitDelta`, decode and re-encode sequence-point blobs with the emitted delta token, and remap every document-change handle through `getOrAddDocument`.

Churn: M

Worth-it verdict: worth it.

### 2. Portable PDB document names are copied as raw blobs

File: `src/Compiler/CodeGen/HotReloadPdb.fs:119`

Quote:

```fsharp
let nameBytes = reader.GetBlobBytes document.Name
```

File: `src/Compiler/CodeGen/HotReloadPdb.fs:139`

Quote:

```fsharp
let nameHandle = metadata.GetOrAddBlob nameBytes
```

Issue: Portable PDB document names are structured document-name blobs, not ordinary blobs. Roslyn writes them through `GetOrAddDocumentName`.

Why it matters: copied document-name blobs can reference path-part blobs that do not exist in the delta builder. The delta PDB may contain unreadable or wrong document names.

Suggested fix: read the document path string from the source PDB and use `MetadataBuilder.GetOrAddDocumentName`, or otherwise preserve the referenced blob graph. Add a readback test asserting the delta document path.

Churn: S/M

Worth-it verdict: worth it.

### 3. Delta PDB builder uses baseline entry point and zero row counts

File: `src/Compiler/CodeGen/HotReloadPdb.fs:233`

Quote:

```fsharp
match snapshot.EntryPointToken with
```

File: `src/Compiler/CodeGen/HotReloadPdb.fs:241`

Quote:

```fsharp
ImmutableArray.CreateRange(Array.zeroCreate<int> DeltaTokens.TableCount)
```

File: `src/Compiler/CodeGen/HotReloadPdb.fs:57`

Quote:

```fsharp
// Index 6 = StateMachineMethod (0x36), not commonly used
```

Issue: the delta PDB reuses the baseline debug entry point, passes all-zero external type-system row counts, and drops the `StateMachineMethod` table count from the baseline snapshot.

Why it matters: Roslyn-shaped delta PDBs use a nil debug entry point for deltas and pass the metadata root builder's row counts. The current header can describe a type system with no rows while referencing a baseline entry point, and async/debug table context starts from the wrong snapshot.

Suggested fix: for delta PDBs, pass `MethodDefinitionHandle()` as the debug entry point, pass the actual delta metadata row counts, and include `counts.[DeltaTokens.tableStateMachineMethod] <- pdbMeta.TableRowCounts.[6]`.

Churn: S/M

Worth-it verdict: worth it.

### 4. Delta PDB omits local scope, local variable, local constant, import scope, and EnC CDI rows

File: `src/Compiler/CodeGen/HotReloadPdb.fs:196`

Quote:

```fsharp
metadata.AddMethodDebugInformation(targetDocument, sequencePointsHandle)
```

File: `src/Compiler/HotReload/EditAndContinueLanguageService.fs:335`

Quote:

```fsharp
// The delta PDB does not carry EnC CDI rows
```

Issue: the delta PDB emits sequence-point `MethodDebugInformation` only. It does not persist local scopes, local variables, local constants, import scopes, or EnC `CustomDebugInformation` blobs for local slots, lambdas, closures, and state-machine states.

Why it matters: Roslyn persists those debug rows in the PDB delta. F# currently relies on process-local baseline chaining for the next compile, but the debugger/PDB consumer does not receive the full EnC debug payload, so locals, closures, and state-machine mapping can be stale or missing.

Suggested fix: thread refreshed `EncMethodDebugInformation` into `HotReloadPdb.emitDelta`, add method-level CDI rows using the existing serializers, and copy/re-emit relevant local scope/variable/constant/import-scope rows for changed methods.

Churn: M/L

Worth-it verdict: worth it.

### 5. Display-string type identity can miss signature, base type, and interface changes

File: `src/Compiler/TypedTree/TypedTreeDiff.fs:424`

Quote:

```fsharp
let private tyToString (_: DisplayEnv) (ty: TType) = normalizeTypeString (ty.ToString())
```

File: `src/Compiler/TypedTree/TypedTreeDiff.fs:1981`

Quote:

```fsharp
let superText =
```

File: `src/Compiler/TypedTree/TypedTreeDiff.fs:2151`

Quote:

```fsharp
if
    baselineBinding.SignatureText <> updatedBinding.SignatureText
    && not hasEquivalentRuntimeSignature
then
```

Issue: display strings are non-injective. Same simple names in different namespaces can collapse, and structured runtime identity is only consulted after the display signature changes.

Why it matters: edits like `A.Customer -> B.Customer`, `NS1.Base -> NS2.Base`, or marker-interface swaps with the same simple name can avoid a rude edit and produce invalid or no-op deltas.

Suggested fix: make runtime type identity / shape digest authoritative for signatures, constraints, base types, and interfaces. Use display text only as fallback/diagnostic text. Add same-simple-name parameter, return, constraint, base, and interface tests.

Churn: M

Worth-it verdict: worth it.

### 6. Requested method updates can be silently dropped

File: `src/Compiler/CodeGen/IlxDeltaEmitter.fs:4062`

Quote:

```fsharp
let allUpdatedMethods =
    (request.UpdatedMethods @ triviaRecompileKeys @ addedMethodKeys)
    |> dedupeMethodKeys
```

File: `src/Compiler/CodeGen/IlxDeltaEmitter.fs:4068`

Quote:

```fsharp
|> List.choose (fun key ->
```

File: `src/Compiler/HotReload/EditAndContinueLanguageService.fs:494`

Quote:

```fsharp
elif hasUpdates then
```

Issue: if a requested updated method cannot be resolved in the fresh IL, `List.choose` silently drops it. The service can still accept an empty or partial delta and advance compiler-side implementation state.

Why it matters: the runtime can keep stale code while the compiler baseline moves forward, so later edits compare against state the runtime never applied.

Suggested fix: fail closed if any requested updated method or type cannot resolve in fresh IL, except explicit known no-op categories. Add tests where method resolution fails after an otherwise supported edit.

Churn: M

Worth-it verdict: worth it.

### 7. Hot reload emission context and session reset are process-global

File: `src/Compiler/HotReload/HotReloadState.fs:416`

Quote:

```fsharp
let mutable private currentEmissionContext: HotReloadEmissionContext option = None
```

File: `src/Compiler/Service/service.fs:794`

Quote:

```fsharp
FSharp.Compiler.HotReloadState.setCurrentEmissionContext (Some context)
```

File: `src/Compiler/Service/service.fs:509`

Quote:

```fsharp
do FSharp.Compiler.HotReloadState.clearSessionState ()
```

Issue: the scoped emission context is a process-wide mutable slot, and constructing an `FSharpChecker` clears process-global hot reload session state.

Why it matters: overlapping in-process compiles can overwrite or clear each other's context before the emit hook reads it. A second checker can also discard another owner's capture/session state.

Suggested fix: carry session store/project key through per-compile config or a per-compile emit hook. Move session clearing to an explicit ambient capture lifecycle or host boundary. Add concurrent/two-checker regression coverage.

Churn: M/L

Worth-it verdict: worth it.

### 8. Removed or renamed-away files are not examined

File: `src/Compiler/HotReload/DeltaBuilder.fs:46`

Quote:

```fsharp
let definitionMap =
    (emptyDefinitionMap, updatedFiles)
    ||> Seq.fold (fun acc updatedFile ->
```

Issue: `computeSymbolChanges` folds only over updated files. New files get a rude edit, but baseline-only files are never examined.

Why it matters: removing a source file can delete modules, types, or methods while hot reload reports no deleted symbols or rude edit for that file. The running process can keep stale code.

Suggested fix: build both baseline and updated file-key sets. After the updated-file fold, add an unsupported/declaration-removed rude edit for every baseline key missing from the updated lookup.

Churn: S

Worth-it verdict: worth it.

### 9. User-string delta tokens do not enforce the 24-bit offset limit

File: `src/Compiler/CodeGen/IlxDeltaStreams.fs:70`

Quote:

```fsharp
let absoluteOffset = heapStartOffset + currentOffset
```

File: `src/Compiler/CodeGen/IlxDeltaStreams.fs:71`

Quote:

```fsharp
let token = 0x70000000 ||| absoluteOffset
```

Issue: user-string delta token calculation uses the accumulated baseline `#US` heap size directly and does not cap/check the 24-bit token offset capacity.

Why it matters: `0x70xxxxxx` user-string tokens only have 24 offset bits. If the baseline heap crosses the capacity boundary, OR-ing the full offset can corrupt the token table tag/offset.

Suggested fix: cap the delta user-string start offset at the user-string heap capacity and reject or rude-edit newly added user strings whose start offset would exceed the 24-bit limit.

Churn: S

Worth-it verdict: worth it.

### 10. Active statement line-shift merging can shrink covered ranges

File: `src/Compiler/HotReload/ActiveStatements.fs:470`

Quote:

```fsharp
segment.OldStartLine <= previousOldEndLine
```

File: `src/Compiler/HotReload/ActiveStatements.fs:498`

Quote:

```fsharp
previousOldEndLine <- segment.OldEndLine
```

Issue: overlap detection compares only with the immediately previous segment, and same-delta overlapping segments can shrink the tracked covered range.

Why it matters: if an outer method/lambda range shifts and an inner generated method with the same delta is processed next, a following segment can receive a bogus zero-delta reset or line-shift update instead of recompile treatment.

Suggested fix: preserve the max covered end with `previousOldEndLine <- max previousOldEndLine segment.OldEndLine`, or track active overlapping intervals per document. Add nested same-delta and different-delta merge tests.

Churn: S

Worth-it verdict: worth it.

## Other Findings By Lens

### Logic and correctness

File: `src/Compiler/Service/service.fs:255`

Quote:

```fsharp
Array.tryFindIndex (fun opt -> String.Equals(opt, "-o", StringComparison.OrdinalIgnoreCase))
```

Issue: service output path parsing handles inline `--out:` and split `-o`, but not split `--out`, while `TrackedInputs.fs:88` recognizes both `-o` and `--out`.

Why it matters: valid compiler-supported `--out path` projects can fail `AddProject` with a missing output path.

Suggested fix: centralize output option parsing or include split `--out` here.

Churn: S

Worth-it verdict: worth it.

File: `src/Compiler/Service/service.fs:558`

Quote:

```fsharp
String.Equals(registeredPath, target, StringComparison.OrdinalIgnoreCase)
```

Issue: live session lookup compares output paths case-insensitively on every platform.

Why it matters: on case-sensitive filesystems, distinct outputs such as `Lib.dll` and `lib.dll` can coexist and be tracked by different sessions. The later registration can win incorrectly.

Suggested fix: use an OS/filesystem-appropriate comparer, at minimum `Ordinal` on Unix-like systems and `OrdinalIgnoreCase` only where appropriate. Prefer including project identity in the lookup instead of path alone.

Churn: S

Worth-it verdict: worth it.

### Roslyn parity

File: `src/Compiler/CodeGen/HotReloadPdb.fs:196`

Quote:

```fsharp
metadata.AddMethodDebugInformation(targetDocument, sequencePointsHandle)
```

Issue: the PDB delta is not yet Roslyn-shaped for debug info. Roslyn persists EnC local-slot, lambda-map, and state-machine-state CDI rows.

Why it matters: external PDB consumers expect the debug payload in the delta, not only in process-local baseline chaining.

Suggested fix: align `HotReloadPdb.emitDelta` with Roslyn's `SerializeEncMethodDebugInformation` flow.

Churn: M

Worth-it verdict: worth it.

### Duplication and reuse

File: `src/Compiler/CodeGen/DeltaMetadataEncoding.fs:7`

Quote:

```fsharp
module RowElementTags =
```

File: `src/Compiler/AbstractIL/ilwrite.fs:157`

Quote:

```fsharp
module RowElementTags =
```

Issue: delta metadata encoding duplicates `RowElementTags` from the existing IL writer.

Why it matters: the duplicate tag universe can drift as more metadata row encoding is added.

Suggested fix: expose or move the shared row-element tag definitions rather than maintaining a hot-reload copy.

Churn: M

Worth-it verdict: worth it before more delta metadata code lands.

File: `src/Compiler/AbstractIL/ILBaselineReader.fs:274`

Quote:

```fsharp
module private TableIndices =
```

Issue: baseline metadata reading introduces another private table-index universe.

Why it matters: metadata table numbers are central correctness constants. Multiple copies make off-by-one and missing-table bugs more likely.

Suggested fix: reuse the existing `TableName`/`TableNames` constants or add a shared table-index helper.

Churn: M

Worth-it verdict: worth it.

File: `src/Compiler/TypedTree/TypedTreeDiff.fs:294`

Quote:

```fsharp
let private stableHash (text: string) =
```

File: `src/Compiler/Utilities/TypeHashing.fs:23`

Quote:

```fsharp
let hashStableString (s: string) : Hash =
```

Issue: `TypedTreeDiff` reimplements stable FNV hashing already available in `TypeHashing`.

Why it matters: low severity, but this file already carries a lot of custom digest logic.

Suggested fix: reuse `TypeHashing.hashStableString` or put hot reload hashing behind one helper.

Churn: S

Worth-it verdict: opportunistic.

File: `src/Compiler/TypedTree/TypedTreeDiff.fs:316`

Quote:

```fsharp
match Environment.GetEnvironmentVariable("FSHARP_HOTRELOAD_TRACE_METHODS") with
```

Issue: manual truthy environment parsing duplicates `EnvironmentHelpers.isEnvVarTruthy`.

Why it matters: small consistency issue; other hot reload files already use `isEnvVarTruthy`.

Suggested fix: use `EnvironmentHelpers.isEnvVarTruthy`.

Churn: S

Worth-it verdict: opportunistic.

### Organization and churn

File: `src/Compiler/CodeGen/IlxDeltaEmitter.fs:3271`

Quote:

```fsharp
let emitDeltaWithDebugData (freshDebugPdb: byte[] option) (request: IlxDeltaRequest) : IlxDelta =
```

Issue: `IlxDeltaEmitter.fs` is 5,337 lines and now owns metadata rewriting, PDB coordination, active-statement plumbing, symbol matching, and delta body emission.

Why it matters: it is difficult to audit hot reload correctness when unrelated concerns live in one large module.

Suggested fix: split PDB/debug-data coordination, metadata row planning, and method-body rewriting after the current correctness issues are fixed.

Churn: M/L

Worth-it verdict: worth it as follow-up, not as a blocker to the fixes above.

File: `src/Compiler/TypedTree/TypedTreeDiff.fs:424`

Quote:

```fsharp
let private tyToString (_: DisplayEnv) (ty: TType) = normalizeTypeString (ty.ToString())
```

Issue: `TypedTreeDiff.fs` is 2,930 lines and combines type identity, expression hashing, lambda alignment, snapshots, classification, and baseline lambda collection.

Why it matters: the fail-open type identity issues are harder to see because classification and rendering are interleaved.

Suggested fix: split type/runtime identity rendering, binding snapshots, expression/lambda digests, and diff classification into smaller modules.

Churn: M/L

Worth-it verdict: useful follow-up after correctness fixes.

File: `tests/FSharp.Compiler.ComponentTests/HotReload/RuntimeIntegrationTests.fs`

Issue: the runtime integration test module is 5,318 lines.

Why it matters: broad test files are hard to run selectively and easy to grow without clear ownership boundaries.

Suggested fix: split by scenario, for example apply-update runner, closures, active statements, metadata/PDB, and rude edits.

Churn: M

Worth-it verdict: follow-up cleanup.

### Robustness and maintainability

File: `tests/FSharp.Compiler.Service.Tests/HotReload/ArchitectureGuardTests.fs:166`

Quote:

```fsharp
for path in Directory.GetFiles(driverDir, "*.fs") do
```

File: `tests/scripts/check-hotreload-plugin-boundary.sh:20`

Quote:

```bash
git -C "${REPO_ROOT}" ls-files \
```

Issue: the F# CI-style architecture guard scans only Driver files, while the shell script scans Driver, TypedTree, Generated, and IlxGen.

Why it matters: forbidden direct hot reload references can slip into TypedTree or IlxGen unless the script is run separately.

Suggested fix: port the full candidate set into `ArchitectureGuardTests` or wire the script into CI.

Churn: S

Worth-it verdict: worth it.

File: `src/Compiler/HotReload/RudeEditDiagnostics.fs:71`

Quote:

```fsharp
| RudeEditKind.SignatureChange -> "FSHRDL001"
```

Issue: the new diagnostic channel is internally consistent, but T-Gro's review pattern points at dotnet/sdk and Roslyn convention sensitivity here.

Why it matters: once external tooling consumes these IDs, changing namespace/help-link conventions is expensive.

Suggested fix: settle the FSHRDL vs ENC namespace and help-link/story now, before public tooling depends on it.

Churn: S/M

Worth-it verdict: worth it if the API is intended to be public in this PR.

### PDB and sequence-point correctness

File: `src/Compiler/HotReload/ActiveStatements.fs:498`

Quote:

```fsharp
previousOldEndLine <- segment.OldEndLine
```

Issue: line-shift merging only remembers the previous segment, not the full covered interval.

Why it matters: stale or duplicated `SequencePointUpdates` are possible around nested generated methods or lambdas.

Suggested fix: preserve the maximum covered end and add direct tests for nested same-delta ranges followed by another segment.

Churn: S

Worth-it verdict: worth it.

## Hygiene

Command:

```bash
git diff --check origin/main...HEAD
```

Result:

```text
tests/FSharp.Compiler.ComponentTests/HotReload/MdvValidationTests.fs:2910: trailing whitespace.
tests/FSharp.Compiler.ComponentTests/HotReload/MdvValidationTests.fs:2909: new blank line at EOF.
tests/FSharp.Compiler.Service.Tests/HotReload/CodedIndexTests.fs:285: new blank line at EOF.
tests/FSharp.Compiler.Service.Tests/HotReload/PortablePdbReaderTests.fs:296: new blank line at EOF.
```

Suggested fix: remove the whitespace and blank EOF churn before merge.

Churn: S

Worth-it verdict: worth it.

## Verification Performed

- `git fetch origin main --prune`
- `git status --short --branch`
- `git diff --shortstat origin/main...HEAD`
- `git diff --dirstat=files,0 origin/main...HEAD`
- `git diff --unified=0 --no-ext-diff origin/main...HEAD` for changed-file/hunk inventory
- GitHub PR review comment/thread inspection through `gh api`
- Seven parallel review lenses
- Local line-number verification with `nl`/`sed`/`rg`
- `git diff --check origin/main...HEAD`

Full test suite was not run; this was a code review and local verification pass.
