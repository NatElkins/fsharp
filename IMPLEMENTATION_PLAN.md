# IMPLEMENTATION PLAN — FS-1023 (“Type Providers Generate Types From Types”)

This plan decomposes the FS-1023 implementation into executable tasks.  It assumes the architecture described in `ARCHITECTURE_PROPOSAL.md` and targets the current `dotnet/fsharp` code base together with the Type Provider SDK.

---

## Legend

* **[Compiler]** — work in the `dotnet/fsharp` repo under `src/`
* **[SDK]** — work in `FSharp.TypeProviders.SDK`
* **[Tests]** — unit/integration tests in the respective repositories

File paths are given relative to repository root unless stated otherwise.

---

## Current status

- Branch `fs-1023` created and baseline builds pass locally, so Phase 0 items are effectively complete.
- Phase 1 has core functionality in place: `TastReflection.fs` projects `TType`/`TyconRef` values into cached reflection proxies, and `TcImports` exposes the builder via `AssemblyLoader.GetTypeReflectionBuilder`. Remaining work focuses on parity coverage (events, indexers) and perf validation.
- Phase 2 now threads `System.Type` static arguments end-to-end, records `TyconRef` dependencies, and wires incremental build invalidation via `PopTypeProviderTypeDependencies`. Additional diagnostics and edge-case coverage (e.g., error messaging for unsupported inputs) are queued.
- `TcImports.GetProvidedAssemblyInfo` now refreshes metadata via first-party `GetManifestModuleContents` calls when upgrading an existing reference to `IsProviderGenerated`, so we consume the provider’s in-memory IL instead of a stale on-disk DLL.
- `TypeProviderDependencyInvalidationTests` continues to fail with `MethodDefNotFound: Fs1023Consumer.Provided::get_Value` (and, when the provider supplies attribute constructors, the missing `TypeProviderEditorHideMethodsAttribute`). The generated DLL snapshots contain only provider infrastructure helpers, confirming we must synthesise the consumer-facing members inside the compiler.
- Phases 3–8 have not started; TPSDK updates, provider samples, rollout activities, and documentation remain to-do.

Progress now depends on fixing the reflection fidelity regressions uncovered during recent debugging; until those issues are resolved we should not advance to SDK integration or wider test coverage.

### Latest session notes (2025-11-03)

- Generated assembly snapshots are written to `/var/folders/.../fs1023-generated-*.dll` on each test run. Inspection via `System.Reflection.Metadata` shows only `Fs1023.Fs1023Provider` plus `<StartupCode$…>` helpers.
- Because the provider does not emit `Fs1023Consumer.Provided`, `tycon.MembersOfFSharpTyconSorted` and `tcaug_adhoc_list` stay empty. Publishing concrete `Val` records (with `ProvidedExpr` bodies) is now the top priority.
- Next focus: wire `ProvidedMethodInfo`/`ProvidedPropertyInfo` into `PublishValueDefnMaybeInclCompilerGenerated`, translate provider invoker expressions into IL via `convertProvidedExpressionToExprAndWitness`, and ensure `InfoReader` surfaces those members so static linking no longer fabricates placeholder classes.

### Recent debugging findings

1. **Parameter metadata gap** — `ParameterInfo` returned by the proxy does not surface `[<ParamArray>]` or `[<OptionalArgument>]`, so provider logic treats the variadic `rest` argument as normal and optional parameters lose default values. Root cause: `ParameterInfo.IsDefined` relies on `attributeType.IsAssignableFrom(proxyType)` which fails for proxy instances; we must compare by the underlying `TyconRef` identity or fully qualified name instead.
2. **Custom attribute projection** — metadata-defined attributes (`System.ParamArrayAttribute`, `TypeProviderEditorHideMethodsAttribute`) trigger `ReflectTypeSymbol: GetConstructorImpl` because attribute constructors for IL types aren’t implemented in the proxy wrappers. The existing TastReflect prototype handled this via explicit constructor/method overrides that we need to port.
3. **Indexer/property binding** — `GetPropertyImpl` fails to match the `Item` indexer (even though the property list shows `Item[]:Int32`) because parameter-type comparison uses proxy equality; the provider’s `GetProperty("Item", ..., returnType = typeof<int>, types = [| typeof<int> |])` returns `null`.
4. **Generated IL absence** — the provider-produced assembly (`Fs1023Provider`) currently exposes only the provider infrastructure types (e.g., `Fs1023.Fs1023Provider`, `<StartupCode$...>`). The generated surface `Fs1023.Provided` never materialises, so static linking fabricates an empty placeholder and the compiler never publishes val specs for `Value`/`Map`/`Optional`. This manifests as `tycon.MembersOfFSharpTyconSorted = []`, `tcaug_adhoc_list = []`, and the downstream “MethodDefNotFound: Fs1023Consumer.Provided::get_Value” emit failure.

The failing `TypeProviderDependencyInvalidationTests.reflection proxy surfaces parameter metadata` test captures all three regressions. These should be addressed before expanding Phase 1 work items.

### Immediate remediation plan

1. **Parameter metadata parity**
   - Update `ReflectParameterInfo.IsDefined` and related helpers to compare attribute identities using `TyconRef` full names instead of proxy reference equality.
   - Ensure `HasDefaultValue`/`RawDefaultValue` round-trip by threading constant values through `ParameterData`.
   - Verify via `dotnet test tests/FSharp.Compiler.ComponentTests/FSharp.Compiler.ComponentTests.fsproj --filter "TypeProviderDependencyInvalidationTests"`.
2. **IL attribute constructor projection**
   - Port TastReflect overrides for `GetConstructorImpl`, `GetMethodImpl`, and `InvokeMember` into `ReflectTypeSymbol`.
   - Add targeted regression in `tests/FSharp.Compiler.Service.Tests/TypeProviderTests.fs` that reflects `System.ParamArrayAttribute` and asserts constructor availability.
3. **Indexer binder alignment**
   - Normalise proxy parameter types before equality checks in `ReflectTypeDefinition.GetPropertyImpl`/`GetMethodImpl`.
   - Extend the FS-1023 sample provider to retrieve `Item` with explicit binder flags and validate via component tests.
4. **Publish provided members into the TAST** *(in progress)*
   - In `TcTyconDefnCore_Phase1C_EstablishDeclarationForGeneratedSetOfTypes`, translate `ProvidedMethodInfo`/`ProvidedPropertyInfo` into `Val`/`MemberInfo` records and push them through `PublishValueDefnMaybeInclCompilerGenerated`.
   - Ensure the resulting `Val`s carry a `ProvidedMemberBinding` that allows `IlxGen` to reuse the provider’s generated IL (or synthesized IL when no provider definition exists). Where the SDK supplies `ProvidedExpr` invoker code, pipe it through `convertProvidedExpressionToExprAndWitness` so the expression tree becomes concrete IL.
   - Audit static-link map emission so the generated IL type surfaces in `ProviderGeneratedTypeRoots` once members are published.
5. **Instrumentation cleanup**
   - Remove temporary logging in `TastReflection.fs` once the above tests pass; keep only structured debug output behind `TRACE` symbols.
   - Re-run `dotnet build src/Compiler/FSharp.Compiler.Service.fsproj -c Release -p:TargetFramework=netstandard2.0` to confirm the compiler build completes without warnings.
6. **Perf sanity sweep**
   - Capture baseline timings with `./build.sh Test` on `main` vs. `fs-1023` for the provider test bucket.
   - Record findings in `artifacts/fs-1023/perf-notes.md` and flag regressions >3% for follow-up.
7. **Regression guard rails**
   - Extend `TypeProviderDependencyInvalidationTests` with:
     - A reflection sanity check against `TcImports.GetTypeReflectionBuilder` (`Type.GetProperty("Value")`) before code emission.
     - A typed-tree validation that `TyconRef` for `Fs1023Consumer.Provided` includes `Vals` named `get_Value`/`Map` after publishing.
     - A follow-up IL smoke test that disassembles the generated assembly and asserts `get_Value` is emitted once IlxGen wiring lands.
   - Keep the existing end-to-end test (loading the compiled assembly) green; add structured logging of `tcaug_adhoc_list` on failure to accelerate debugging.

---

## Phase 0 — Pre-flight (Status: Complete)

1. **Confirm branch baseline**
   - Ensure we are on branch `fs-1023`.
   - Rebase against latest `dotnet/fsharp` `main` if necessary.

2. **Verify existing type-provider tests run**
   - Run `./build.sh Test` (or equivalent PS script) to make sure the baseline is passing before modifications.

---

## Phase 1 — Projection infrastructure (`TypeReflectionBuilder`) (Status: In progress)

> Current state: Reflection proxies are implemented with per-`TyconRef` caching, and `TcImports` exposes a shared builder; follow-up tasks cover broader surface implementation (events/indexers), debugger ergonomics, and performance measurements.

**Recent progress update:** `ImportMap` now exposes `GetTypeReflectionBuilder`/`ReflectType`, and `TcStaticConstantParameter` consumes the helper. Remaining Phase 1 work:
- Finish method/constructor parameter projection so `MethodInfo.GetParameters` etc. are accurate.
- Implement TODOs for event/indexer parity and clean up `notRequired` fallbacks that providers hit during reflection.
- Add targeted profiling/DebuggerDisplay as originally scoped.

### 1.1 Create projection module

- **[Compiler]** Add new file `src/Compiler/TypedTree/TastReflection.fs`.
  - Base implementation on the historical `colinbull/visualfsharp` `TastReflect.fs`, updated for the modern code layout.
  - Define:
    - `TypeReflectionBuilder` class encapsulating projection logic.
    - Proxy types (`ReflectTypeDefinition`, `ReflectMethodInfo`, `ReflectPropertyInfo`, etc.) inheriting from `System.Type`/`MethodInfo`/`PropertyInfo`.
    - Helper caches (`ConcurrentDictionary<Stamp, ProvidedSymbol>` equivalent) keyed by `TyconRef` and instantiation.
  - Implement override members for reflection queries (`GetMethods`, `GetProperties`, `GetCustomAttributesData`, `DeclaredConstructors`, etc.). Ensure the implementation matches CLR semantics for F# constructs (records, unions, modules, abstract slots).
  - **Remaining gaps (blocking):**
    - Bridge parameter metadata so `[<ParamArray>]`, `[<OptionalArgument>]`, and other FSharp.Core attributes round-trip through `ParameterInfo` and `IsDefined`.
    - Port the historical TastReflect constructor/method overrides so metadata-defined attributes and `ReflectTypeSymbol` wrappers implement `GetConstructorImpl`, `GetMethodImpl`, etc., removing the `notRequired` fallbacks.
    - Align `GetPropertyImpl`/`GetMethodImpl` filtering with CLR binder semantics for indexers and optional arguments.
    - Afterwards, resume event exposure, `Module`/`GetMember` overrides, debugger-friendly displays, and profiling.

### 1.2 Wire projection builder into `TcImports`

- **[Compiler]** Modify `src/Compiler/Driver/CompilerImports.fs`.
  - Extend `TcImportsData` with a new field `typeReflectionBuilder: TypeReflectionBuilder option`.
  - Initialise it lazily when type providers are enabled (see creation around function `CreateTcImports`).
  - Expose a function `member TcImports.GetOrCreateTypeReflectionBuilder : CompilationThreadToken -> TypeReflectionBuilder`.
  - Ensure accesses are protected by existing `tciLock`; no new lock needed.

### 1.3 Update `ImportMap`

- ✅ **Done** — `ImportMap` now owns `GetTypeReflectionBuilder`/`ReflectType`, and call sites use the helper instead of reaching through `AssemblyLoader`.
  - **Next up:** audit remaining compiler code for direct `GetTypeReflectionBuilder` usage and route them through `ImportMap` where appropriate to keep the abstraction consistent.

### 1.4 Utility functions

- **[Compiler]** Add helper module (e.g., `TypeReflectionUtils`) for converting attributes (`AttribInfo`) into `CustomAttributeData`. Reuse logic from existing provider infrastructure (e.g., `ProvidedTypes.fs`).

### 1.5 Parameter metadata fidelity

- **[Compiler]** Rework `ParameterInfo` projection so `IsDefined`, `GetCustomAttributesData`, `IsOptional`, and default values mirror CLR behaviour. Compare attribute identities using `TyconRef` full names and handle metadata attributes without creating proxy mismatches.
- **[Tests]** Extend the FS-1023 provider sample to assert `[<ParamArray>]`/`[<OptionalArgument>]` round-trip via reflection (currently failing test will become the regression guard).

### 1.6 Custom attribute projection for IL types

- **[Compiler]** Port the historical TastReflect implementations for `ReflectTypeSymbol.GetConstructorImpl`, `GetMethodImpl`, and related helpers so metadata-defined attributes (e.g., `System.ParamArrayAttribute`) can be materialised.
- **[Compiler]** Update `TxCustomAttributesDatum` to fall back to real constructors for IL attributes when appropriate.
- *Reference note:* `colinbull/visualfsharp` branch `rfc/fs-1023-type-providers` implements `TxCustomAttributesDatum` and friends in `src/fsharp/TastReflect.fs:520-584`; reuse that constructor discovery strategy while modernising the API surface.
- **[Tests]** Add targeted unit tests (or assertions inside existing provider tests) that inspect attribute constructors returned by the proxy.

### 1.7 Indexer and binder parity

- **[Compiler]** Align `ReflectTypeDefinition.GetPropertyImpl` and `GetMethodImpl` with the CLR binder: erase proxy wrappers when comparing parameter types, handle optional arguments and `HasDefaultValue`, and support indexer discovery (`Item` property).
- **[Tests]** Extend the FS-1023 provider to retrieve the `Item` property with explicit binder arguments and confirm it succeeds.
- **[Cleanup]** Once the above passes, remove the temporary `[fs1023]` / `[tast]` instrumentation added during investigation.

---

## Phase 2 — Static parameter evaluation updates (Status: In progress)

> Current state: `TcStaticConstantParameter` materialises `System.Type` proxies, records `TyconRef` dependencies, and invalidation now flows through incremental builds; remaining work includes richer diagnostics and additional negative/regression tests.

### 2.1 Modify `TcStaticConstantParameter`

- **[Compiler]** File `src/Compiler/Checking/Expressions/CheckExpressions.fs`.
  - In function `TcStaticConstantParameter`, add branch when `kind` equals `g.system_Type_typ`.
    - Resolve the syntactic type (`SynType.LongIdent`, `SynType.App`, etc.) to `TType`.
    - Call `cenv.amap.assemblyLoader` to ensure the type is concrete (reject provided types).
    - Use `TypeReflectionBuilder` via `ImportMap.ReflectType` to obtain `System.Type`.
    - Record dependency (see 2.2).
    - Return boxed `System.Type` as static argument.

### 2.2 Record dependency stamps

- **[Compiler]** Add functionality to record all `TyconRef` visited during projection.
  - Potential approach: have `TypeReflectionBuilder` maintain a thread-local `HashSet<TyconRef>` for the current projection, and expose it to `TcStaticConstantParameter`.
  - After projection, register stamps via existing `TypeProviderInlinedRepresentation` infrastructure and `CompilerImports.RecordGeneratedTypeRoot`.
- Ensure recorded dependencies tie into the incremental builder so that provider caches invalidate when these types change.
- **[Tests]** Add a regression demonstrating that modifying an input type triggers provider re-execution.

### 2.3 Diagnostics

- **[Compiler]** Add new diagnostic in `src/Compiler/Driver/CompilerDiagnostics.fs` for unsupported static argument types.
  - Example: `FSxxxx: Static parameter must reference a non-erased type`.
- **[Tests]** Extend `TypeProviderDependencyInvalidationTests` with negative cases (anonymous records, provided types, inference variables) once diagnostics are in place.

---

## Phase 3 — TPSDK integration (Status: Not started)

### 3.1 Recognise new proxies

- **[SDK]** File `src/ProvidedTypes.fs`.
  - Update `TargetTypeDefinition`/`TypeSymbol` logic (search for `ConvertTargetTypeToSource`).
  - Detect when the target `System.Type` is a `TastReflection` proxy.
  - Convert proxies back to design-time types by wrapping them with existing adapter infrastructure.

### 3.2 Update Provided API surface

- Ensure `ProvidedTypeBuilder.MakeGenericType`, `ProvidedTypeDefinition`, `ProvidedMethod`, etc. accept the new proxy types without modification.

### 3.3 Tests

- **[SDK Tests]** Add regression tests in `tests/` verifying that a `ProvidedStaticParameter(typeof<Type>)` yields a type with accessible fields/properties/attributes.

---

## Phase 4 — Compiler pipeline integration (Status: Not started)

### 4.1 Apply static arguments to providers

- **[Compiler]** In `src/Compiler/TypedTree/TypeProviders.fs`, ensure `ApplyStaticArguments` passes the projected `System.Type` to the provider.
  - Verify `ValidateProvidedTypeDefinition` and related code handle the new proxies (no additional changes expected).

### 4.2 Static-linking adjustments

- Validate that `ProvidedAssemblyStaticLinkingMap` already handles the recorded type remappings. No explicit change expected, but add comments or assertions if necessary.

### 4.3 Script/IDE pipeline

- Confirm `Fsi.fs` and `FSharpChecker` rely on the shared compiler pipeline; no change required beyond making sure the new code paths are accessible.

---

## Phase 5 — Tests (Status: Not started)

### 5.1 Compiler unit tests

- **[Tests]** Add new tests under `tests/fsharp/typeProviders/`:
  - `GenerativeTypeFromType` sample (similar to historical `Serialiser` sample).
  - Cases covering:
    - Record input type.
    - Union input type.
    - Generic type input with various instantiations.
    - Attribute propagation.
    - Error when using provided/anonymous types as input.

### 5.2 End-to-end C# consumer

- Add integration test where provider generates a type based on an F# record and compile a small C# project consuming the generated API.

### 5.3 Negative tests

- Validate diagnostics when:
  - Static parameter references unsupported type.
  - Provider emits IL referencing non-generated project types.

---

## Phase 6 — Documentation (Status: Not started)

1. **Update architecture doc**
   - Move relevant “answers” from `ARCHITECTURE_PROPOSAL.md` into comments or design notes if needed.

2. **Create developer guidance**
   - Draft doc (e.g., `docs/upcoming/fs-1023.md`) describing how provider authors can use the new capability and restrictions (no anonymous records, no direct references to project types).

---

## Phase 7 — Clean-up and validation (Status: Not started)

1. **Run full test suite** (`./build.sh Test`).
2. **Review API surface** to ensure no public breaking changes.
3. **Prepare PR** targeting `dotnet/fsharp` with summary, risk assessment, and links to design docs.
4. **Coordinate with SDK release plan** if updates are required in `FSharp.TypeProviders.SDK` NuGet package.

---

## Phase 8 — Rollout and monitoring (Status: Not started)

1. **Feature flag & preview builds**
   - Gate compiler changes behind `--langversion:preview` and an internal MSBuild property until validation completes.
   - Publish preview SDK packages to an internal feed and circulate instructions to partner providers.

2. **Dogfood with partner providers**
   - Pair with two early adopters (e.g., `SqlClient` provider, `FSharp.Data`) to migrate sample code and collect compatibility feedback.
   - Capture blocking issues in a shared tracker and feed fixes back into Phases 1–7.

3. **Observability & telemetry**
   - Emit structured log events when type projections occur (counts, cache hits) guarded by compiler `--times` to aid perf investigations.
   - Add optional TPSDK counters (EventSource) that downstream hosts can enable to monitor provider invalidation churn.

4. **Release sign-off**
   - Produce release notes outlining new diagnostics, supported scenarios, and known limitations.
   - Coordinate with .NET release management to align compiler and SDK publishing timelines.

---

## Risk register

| Risk | Phase trigger | Mitigation |
|------|---------------|------------|
| Projection cache leaks increase memory usage | Phase 1 rollout | Add stress tests with scripted provider reloads; monitor with VS memory snapshots before GA. |
| Provider invalidation misses edits in signature files | Phase 2/5 testing | Expand integration tests to mutate `.fsi`/`.fs` pairs and assert reruns; audit dependency stamps captured from both declarations. |
| TPSDK consumers lag on updates | Phase 3/8 | Publish compatibility matrix in docs and provide a temporary shims package so older providers fail gracefully with actionable errors. |
| Performance regressions in IDE scenarios | Phase 4–5 | Run VS RPS and Ionide benchmarks; fallback plan is to keep feature behind preview flag while optimising caches. |
| Diagnostics confuse users when referencing unsupported types | Phase 2/5 | Document error codes, add IDE quick info, and include common remediation steps in docs and release notes. |

---

## Appendix — Key Files to Touch

- `src/Compiler/TypedTree/TastReflection.fs` *(new)*
- `src/Compiler/Driver/CompilerImports.fs`
- `src/Compiler/Checking/import.fs`
- `src/Compiler/Checking/Expressions/CheckExpressions.fs`
- `src/Compiler/TypedTree/TypeProviders.fs`
- `src/Compiler/Driver/CompilerDiagnostics.fs`
- `FSharp.TypeProviders.SDK/src/ProvidedTypes.fs`
- `tests/fsharp/typeProviders/**`
- `FSharp.TypeProviders.SDK/tests/**`

---

This implementation plan intentionally leaves timelines blank but enumerates the tasks in the order they should be executed, ensuring dependencies are respected and tests are accounted for at every stage.

---

## Open questions & decisions

- Confirm whether proxy `System.Type` instances should expose `Assembly` metadata reflecting the original defining assembly or a synthetic `TastReflection` container; decision needed before Phase 1 debugger work proceeds.
- Decide how to surface anonymous type restrictions to provider authors: compiler diagnostic only vs. TPSDK helper API; requires alignment during the next cross-team sync.
- Determine if `TypeReflectionBuilder` must de-duplicate `System.Reflection.Emit.CustomAttributeData` instances across projections to minimise allocations for hot reload scenarios.
- Clarify whether incremental build invalidation should treat signature files (`.fsi`) as separate dependency roots or merge them with their implementation counterparts for telemetry reporting.
- Finalise the preview flag strategy—MSBuild property name and default value—so SDK samples can target the correct condition before the feature is broadly advertised.

## Validation checklist (pre-GA)

- All compiler and TPSDK tests pass on Windows, macOS, and Linux agents via `./build.sh Test` and targeted `dotnet test` invocations.
- Feature flag defaults reviewed; preview flag disabled by default and guarded through documented MSBuild property.
- Provider partner sign-off recorded for at least two adopters, with actionable bug backlog triaged.
- Documentation updates (`docs/upcoming/fs-1023.md`, release notes) reviewed by content owners and published to the internal staging site.
- Telemetry events verified in a controlled environment, confirming counters only emit when explicitly enabled.
- Performance baselines captured comparing `main` vs. `fs-1023` for typical IDE workloads, with regression deltas within agreed thresholds.

---
