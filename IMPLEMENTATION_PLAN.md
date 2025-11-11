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
- Phase 2 now threads `System.Type` static arguments end-to-end, records `TyconRef` dependencies, and wires incremental build invalidation via `PopTypeProviderTypeDependencies`. Additional diagnostics and edge-case coverage (e.g., error messaging for unsupported inputs) are queued, and `TcStaticConstantParameter` now writes `FS1023_TRACE` entries that capture each `System.Type` static argument (plus recursive `ensureTypeSupported` visits) so we can see exactly which type shapes trigger the remaining hangs.
- `TcImports.GetProvidedAssemblyInfo` now refreshes metadata via first-party `GetManifestModuleContents` calls when upgrading an existing reference to `IsProviderGenerated`, so we consume the provider’s in-memory IL instead of a stale on-disk DLL.
- `TypeProviderDependencyInvalidationTests`.`provided type publishes members into the TAST` now runs (the skip flag is gone). The regression was in the test harness: it only scanned top-level `ImplementationFileDeclaration`s, so it never descended into the `Fs1023Consumer` namespace entity where the generated type lives. The helper now recursively walks nested declarations, finds `Fs1023Consumer.Provided`, and asserts on its published members.
- `TypeProviders.ProvidedMemberBindingHelpers.createFor{Method,Property,Constructor}` now centralise binding creation and register bindings for each `ProvidedMemberInfo`. The helpers capture provider/member handles, definition locations, result type, `ProvidedParameterInfo` arrays (empty and non-empty), method return parameters, and eagerly cache invoker expressions for methods/constructors while properties continue to opt out.
- `CheckDeclarations` invokes those helpers for root and nested generative types so we record bindings during `TcTyconDefnCore_Phase1C`, giving future consumers a single lookup point.
- `ProvidedMemberBindingTests` exercise the helpers to confirm bindings are registered, definition locations round-trip via `withDefinitionLocation`, and now assert parameter metadata, method return-parameter wiring, and invoker-expression capture (including constructors) alongside the empty-signature baseline.
- `TypeProviderDependencyInvalidationTests.record input compiles generated summaries` is stable again after realigning the fixture with the provider contract: the `RecordInput` source type once more exposes a `Value` field (so the generated `RecordProvided.Value` accessor exists) and the helper method binds a concrete identifier instead of `_`. Both the normal and `/standalone` consumer builds now pass, giving us confidence that the emitted IL survives relocation.
- `ProvidedMethodCalls.TranslateInvokerExpressionForProvidedMethodCall` now mirrors the `methodCallToExpr` rewrite: when a `ProvidedMemberBinding` carries an associated `ValRef`, consumer invocations are rewritten to `mkApps` against that member and we skip the provider’s `GetInvokerExpression` entirely. `FS1023_TRACE` shows `[tp-invoker-call] rerouted via ValRef get_Value` during `TypeProviderDependencyInvalidationTests`, proving call sites no longer inline provider IL. The regression still fails with `Undefined value 'get_Value'`, so `NameResolution.fs` now logs unresolved `get_` lookups under the same flag; next step is to feed the published tycon members into the ambient name environment before property resolution.
- `TastReflection.TxMethodDef`/`TxConstructorDef` source parameter and return metadata directly from `ProvidedMemberBinding`, so reflection-based tooling sees the provider surface without re-querying the provider.
- `publishProvidedMembers` in `CheckDeclarations.fs` now builds the self type via `TType_app`, materialises `ValMemberInfo`/`ValReprInfo` correctly, and emits method/constructor stubs while the checker walks generative types. Each published `Val` now carries the invoker-derived body plus a `ProvidedMemberBinding`, and `registerGeneratedTycon` writes both the provider path (`Fs1023/Provided`) and the relocated consumer path (`Fs1023Consumer/Provided`) into `ProvidedGeneratedTypeRegistry` under the provider assembly. Non-local dereferences no longer fault even when consumers resolve the relocated path; the remaining compiler work is to teach IlxGen/static-link to emit IL for those cached bodies.
- A fresh comparison against the `main` worktree confirms the upstream compiler still omits `Val` stubs for generative members; the IlxGen/InfoReader paths all query `ProvidedMemberBinding`. Our branch must finish the new publication logic before any consumer can pivot.
- `FS1023_TRACE` instrumentation still appends to `/tmp/fs1023_trace.log`, and the latest runs show the generated tycon registering plus the five synthetic getters lighting up both `MembersOfFSharpTyconSorted` and `tcaug_adhoc_list`. The checker publishes bindings before we recurse into nested types, so the typed tree now faithfully contains the cached `Val`s that IlxGen/InfoReader must ingest next.
- Additional tracing inside `ModuleOrNamespaceType.AddProvidedTypeEntity` and `ProvidedGeneratedTypeRegistry.register/tryGet` shows both keys lighting up: every `Fs1023Consumer.Provided` definition now records `registry-register assembly=Fs1023Provider path=Fs1023/Provided` and the mirror entry for `Fs1023Consumer/Provided`. That dual registration keeps name resolution stable regardless of whether a given `NonLocalEntityRef` was built with the provider’s namespace or the relocated consumer path, and `/tmp/fs1023_trace.log` confirms subsequent lookups hit the cache.
- `ServiceAssemblyContent` no longer filters out generated provided types. As a result `TypeProviderDependencyInvalidationTests`.`provided type publishes members into the TAST` is now re-enabled and green locally (ran via `FS1023_TRACE=1 dotnet test tests/FSharp.Compiler.Service.Tests/FSharp.Compiler.Service.Tests.fsproj -c Release --filter "provided type publishes members into the TAST"`). The test still only guards for type/members being discoverable through FCS; we haven’t yet translated the members into `tcaug_adhoc_list`.
- The Type Provider SDK now surfaces `ProvidedMethod.GetInvokeCode`/`ProvidedConstructor.GetInvokeCode` for generative types. `ProvidedMemberBinding` therefore captures the original quotations, `publishProvidedMembers` stores them as `ReflectedDefinition`s, and IlxGen compiles those bodies directly into the consumer assembly. The Fs1023 regression now executes the emitted getters instead of falling back to `[Fs1023Provider]Fs1023.Provided::*`, so the TypeLoadException is gone.
- `TastReflection.computeParameterMetadata` mirrors CLR optional/default semantics. F# `?value` parameters no longer synthesize `HasDefaultValue = true`, `[<Optional>]` members still expose `ParameterAttributes.Optional`, and `ParameterInfo.IsDefined` only answers for the attribute that actually exists. The Fs1023 reflection assertions now read `OptionalParameter = "value:false:false"` while `OptionalLiteralParameter` remains `value:true:true:42`.
- Phases 3–8 have not started; TPSDK updates, provider samples, rollout activities, and documentation remain to-do.

Progress now depends on tackling the provider infrastructure debt described in the architecture proposal. Rather than layering more System.Type special cases, the recommended next step is to refactor the pipeline around a dedicated `ProvidedMemberBinding` before continuing with FS‑1023.

### Hybrid refactor (foundation)

1. **Stage the scaffolding now**
   - Add a `ProvidedMemberBinding` field (and helpers) to `ValOptionalData` while leaving existing behaviour untouched. Commit this groundwork so it can be re-used regardless of future direction.

2. **Get buy-in (in progress)**
   - Share the architectural note (hybrid option) with core compiler maintainers. Pending feedback, either continue fleshing out the binding or revert the scaffolding if they prefer the minimal spike.

3. **Introduce `ProvidedMemberBinding` fully**
   - Extend `ValOptionalData` (or add an accessor) with an optional binding payload capturing provider metadata (provider handle, `ProvidedMethodInfo`/`ProvidedPropertyInfo`, parameter shapes, invoker expression/IL, definition location).
   - Provide helper APIs for storing and querying `ProvidedMemberBinding` data (current implementation uses a conditional weak table keyed by `ProvidedMemberInfo`).

4. **Populate bindings in the checker**
 - In `TcTyconDefnCore_Phase1C_EstablishDeclarationForGeneratedSetOfTypes`, when synthesising members for generative types, construct the binding and attach it to each `Val` before publishing.
  - Preserve existing behaviour for static parameter application and experimental logging, but route metadata through the binding.
  - *Status:* helpers now create bindings for each `ProvidedMemberInfo` encountered (root and nested), capturing provider/member handles, result type, definition location, and parameter metadata. `publishProvidedMembers` synthesises the corresponding `Val`s (using `TType_app` for the self type and the correct `ValReprInfo` shapes), and we now attach invoker-based bodies so downstream stages can compile them once IlxGen pivots.

5. **Update consumers**
   - **InfoReader / reflection builder:** replace ad hoc `TProvidedTypeRepr` checks with `match vref.ProvidedBinding` to surface metadata. Ensure existing reflection tests stay green.
   - **IlxGen:** wire code generation to the cached bodies so we emit IL without re-querying the provider; also ensure static linking knows the generated tycons live in the provider module.
   - **Static linking / dependencies:** record provider-generated roots and dependencies via the binding instead of scanning the entire typed tree.
   - **FCS symbol layers / tooling:** expose richer metadata (e.g., XML docs, definition locations) via the binding; remove duplicate provider checks.

5. **Regression tests**
   - Port existing provider tests to exercise the new path; add assertions for the new typed-tree test (`provided type publishes members into the TAST`).
   - Ensure the reflection sanity and IL smoke tests stay skipped until FS‑1023 wiring lands, but keep them in the suite for when code generation returns.

6. **Follow-up: FS‑1023 feature work**
   - Once the binding refactor is in place, resume the FS‑1023 implementation (publish members, translate `ProvidedExpr`, update IlxGen) using the binding instead of scattered special cases.

### Status (2025-11-04)

- Reflection proxy gaps (param metadata, constructor/method resolution, indexer comparison) have been patched.
- A TAST regression guard (`provided type publishes members into the TAST`) is in place (currently skipped until publication is implemented).
- Architecture and plan documents updated to capture the hybrid refactor.

### Open work after refactor

Assuming the binding abstraction lands, revisit these FS‑1023 tasks:

1. Publish provided members (with bindings) into `tcaug_adhoc_list`.
2. Emit IL for generated members via IlxGen using the binding.
3. Enable the IL smoke and end-to-end tests; ensure Map/Optional/indexer parity.
4. Clean up debug instrumentation and capture perf baselines.

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

- ✅ `TcStaticConstantParameter` now validates `System.Type` arguments (rejecting provided and anonymous types), reflects them via `ImportMap.ReflectTypeWithDependencies`, and records every referenced `TyconRef`.
- **[Compiler]** Follow-up: tighten diagnostics/messages once we add the negative tests in §2.3.

### 2.2 Record dependency stamps

- ✅ `TypeReflectionBuilder` maintains per-projection dependency scopes; callers can capture the visited `TyconRef`s via `CaptureTypeDependencies`, and `TcStaticConstantParameter` forwards them to `RecordTypeDependency`.
- **[Tests]** Still to do: add a regression where mutating an input type triggers provider re-execution to guard the new plumbing.

### 2.3 Diagnostics

- ✅ Enhanced the static-argument validation to surface specific reasons (anonymous record, provided type, type parameter) through `etInvalidStaticArgument`, and added targeted regression tests covering each rejection path.
- **[Compiler]** Follow-up (optional): introduce a dedicated diagnostic code if we decide a specialised error identifier is warranted beyond the enriched messaging.

---

## Phase 3 — TPSDK integration (Status: Completed)

### 3.1 Recognise new proxies

- **Status:** Done — `FSharp.TypeProviders.SDK/src/ProvidedTypes.fs` now flags both `FSharp.Compiler.TastReflect` and `Microsoft.FSharp.Compiler.TastReflect` proxy classes, returning the proxy instance instead of wrapping it in `ProvidedTypeSymbol`, so `ConvertTargetTypeToSource` preserves reflection metadata.

### 3.2 Update Provided API surface

- **Status:** Done — No further API changes required beyond the new proxy handling; existing builders (`ProvidedTypeBuilder.MakeGenericType`, etc.) work unchanged with the proxied `Type` values.

### 3.3 Tests

- **Status:** Done — Added `ProxyTypeTests` in the TPSDK test suite to assert that proxy types round-trip through `ConvertTargetTypeToSource` and expose their reflection surface (properties plus custom attributes). Test execution currently fails on this machine because .NET x64 runtime is unavailable (`Could not find 'dotnet' host for the 'X64' architecture`).

---

## Phase 4 — Compiler pipeline integration (Status: Completed)

### 4.1 Apply static arguments to providers

- **Status:** Done — `PrettyNaming.ComputeMangledNameWithoutDefaultArgValues` now preserves full type identity (assembly-qualified) for `System.Type` static arguments, and `TryLinkProvidedType` rehydrates them via `Type.GetType`, so providers see the same `System.Type` payload when metadata is reloaded.
- **[Compiler]** In `src/Compiler/TypedTree/TypeProviders.fs`, ensure `ApplyStaticArguments` passes the projected `System.Type` to the provider.
  - Verify `ValidateProvidedTypeDefinition` and related code handle the new proxies (no additional changes expected).

### 4.2 Static-linking adjustments

- **Status:** Done — audited `ProvidedAssemblyStaticLinkingMap` usage (CheckDeclarations/IlxGen) and confirmed the remapping tables operate purely on `ILTypeRef`; the richer static-argument mangling does not require additional linking changes.

### 4.3 Script/IDE pipeline

- **Status:** Done — verified `FSharpChecker`/`Fsi.fs` consume the common `ImportMap.ReflectTypeWithDependencies` flow, so the new `System.Type` preservation automatically applies to IDE/script scenarios without extra code.

### 4.4 Provided-type IL emission (Status: In progress)

- **Goal:** stop depending on provider-generated IL for `[<Generate>]` types. Instead, let the compiler emit the IL from the typed tree and the cached `ProvidedMemberBinding` invoker metadata.
- **Incremental steps:**
  1. **Typed-tree bodies:** we now attach concrete `Expr` lambdas to each published provided member by reusing `ProvidedMethodCalls.TryMakeProvidedMemberBodyFromBinding` (`src/Compiler/Checking/CheckDeclarations.fs`). The next helper work lives in `ProvidedMethodCalls` (keep it the single authority for re-hydrating provider invokers) so IlxGen can reuse it without duplicating the quotation conversion.
  1b. **Invoker retargeting (Status: Done):** `ProvidedMemberBindingHelpers.withAssociatedMember` now stamps each binding with the `ValRef` we synthesize in `publishProvidedMembers`, and `MethodCalls.methodCallToExpr` checks that metadata before translating a `ProvidedCallExpr`. When a generated member is invoked, we now emit `mkApps` against the relocated `ValRef` rather than calling back into the provider’s Reflection.Emit type, so the cached lambdas (`TryMakeProvidedMemberBodyFromBinding`) stay self-contained and IlxGen can compile them without pulling IL from the provider assembly. Follow-up: keep the instrumentation in place until IlxGen consumes the new bodies so we can compare emitted IL before/after.
  1c. **Zero-arg routing (Status: Done):** `ProvidedMethodCalls.tryCallAssociatedMemberWithArgs` only rewrites calls when the invocation supplies at least one receiver/argument. Static getters with no parameters now stay on the provider-invoker path until IlxGen emits their IL bodies, which eliminates the previous `GenLambda` crash (`expr=Val` with `storeSequel=None`) and lets the failing regression reach the current “missing Fs1023Consumer.Provided” error.
  2. **Member catalog helper:** add an IlxGen-side collector (`src/Compiler/CodeGen/IlxGen.fs`) that walks `tycon.TypeContents.tcaug_adhoc_list` and gathers the generated `ValRef`s alongside their stored bodies (`Val.ReflectedDefinition`) and `ProvidedMemberBinding`s. This helper should also normalize property names (`get_`/`set_`) so we can emit `ILPropertyDef`s after the methods exist. Data structures to touch: `Val`, `ValMemberInfo`, `SynMemberKind`.
  3. **Emit provided methods/ctors:** re-enter the existing `GenMethodForBinding` pipeline with the synthesized bindings. For each `ValRef` representing a provided method or getter, call `GenMethodForBinding` with the body we stored in step 1. Constructors need a thin wrapper that flips `SynMemberKind.Constructor` into a `.ctor` IL method (use `MemberFlags.IsInstance` + argument info from `GetValReprTypeInCompiledForm`). File: `src/Compiler/CodeGen/IlxGen.fs` (new helpers next to the other `GenMethodForBinding` cases).
  4. **Type-def skeleton:** extend `GenTypeDef` to handle `TProvidedTypeRepr`. Use `TProvidedTypeInfo` (see `Construct.NewProvidedTyconRepr`) to compute the IL kind (class/interface/struct), `LazyBaseType`, `GetInterfaces`, `IsSealed`, etc., then call a new `EmitProvidedTypeDef` that plugs the generated methods/properties/fields into an `ILTypeDef`. Touchpoints: `GenTypeDef`, `ILTypeDefAdditionalFlags`, `ProvidedTypeInfo` accessors.
     - ✅ Current change-set wires up the base type/implements/kind bits so the generated IL type now inherits the correct parent (`System.ValueType`, `MulticastDelegate`, etc.) and lists the same interfaces as the typed tree.
  5. **Static-link integration:** the generated IL type must be added to the main module just like `TFSharpTyconRepr` types. Reuse the existing `AddProvidedTypeEntity` registration plus `tcImports.ProviderGeneratedTypeRoots` so static linking (`src/Compiler/Driver/StaticLinking.fs`) sees the IlxGen-emitted definition instead of falling back to an empty placeholder.
     - ✅ When no provider assemblies need relocation (`SkipProviderStaticLinking = true`), the static-link phase now skips the relocation pipeline altogether, leaving the IlxGen-emitted type definitions intact (no more placeholder classes during `/standalone`).
  6. **Regression coverage:** extend `tests/FSharp.Compiler.Service.Tests/TypeProviderDependencyInvalidationTests.fs` with a reflection-based assertion that the compiled `Fs1023Consumer.Provided` type exposes the summary properties (e.g., call `typeof<Provided>.GetProperty("MapParameters")`). This ensures IlxGen emits real IL for the summaries before we reintroduce additional static members.
     - ✅ The `provided type publishes members into the TAST` regression now compiles both the normal and `/standalone` binaries and reflects over `Fs1023Consumer.Provided` to assert the summary getters return the expected strings, proving the emitted IL works even after static linking.

- **Current failure signal:** with static linking suppressed we now crash later in codegen: the regression emits `internal error: One of your modules expects the type 'Fs1023Consumer.Provided' to be defined...`. That confirms the GenLambda issue is gone and the remaining work is to make `GenTypeDef` emit `TProvidedTypeRepr` bodies.
- **Known gap:** compiling a consumer that passes a generic type (e.g., `Generic<'T>`) as the static argument currently hangs inside `checker.Compile`. The new `GenericInput_multipleInstantiations` regression is skipped until we diagnose the hang (suspect: TastReflection/ProvidedMemberBinding handling of optional generic parameters). Latest `FS1023_TRACE` runs show that `ImportMap.ReflectTypeWithDependencies` and `TypeReflectionBuilder.CaptureTypeDependencies` both return almost immediately for `Generic<int>` (deps reported as `Generic\`1,Int32` with <10 ms total), so the stall happens downstream—likely while reusing the reflected `System.Type` inside static-parameter application. We have now instrumented both `TryApplyProvidedType` and `TryApplyProvidedMethod` (wrapped around `provider.ApplyStaticArguments{,ForMethod}`) so future traces will tell us whether the provider call returns or hangs; next step is to re-run the generic regression under `FS1023_TRACE=1` once the baseline build is fixed (current tree fails earlier because `ProvidedGeneratedTypeRegistry` is missing from the working copy).

---

## Phase 5 — Tests (Status: In progress)

### 5.1 Compiler unit tests

  - ✅ `record input compiles generated summaries` — after restoring the source record’s `Value` field and fixing the summary helper to use a named identifier, this regression now passes for both the default and `/standalone` consumer builds. The test reflects over the emitted IL to ensure `RecordProvided.Value` and `MapParameters` survive relocation.
  - ✅ `union input compiles generated summaries` — now mirrors the record coverage: we compile/reflect both binaries and assert `ShapeProvided.MapParameters`/`Value` produce the expected strings, confirming union metadata also survives relocation.
    - ⏳ `GenericInput_multipleInstantiations` — still skipped, but the 10 minute rerun on **11 Nov 2025 @ 03:01 UTC** with the new `compileWithLogging` instrumentation shows the provider build finishing and the consumer run stalling inside `checker.ParseAndCheckProject`. `generic.log` in `/var/folders/m_/v89b4sb95fz2lbt83sy2yjyh0000gn/T/fs1023-bebc21923f8d4674b77729c84a40c488/` now records `generic-consumer parse+check begin` with no matching `end`, and neither `FS1023_TRACE` nor the log ever see `fsc begin`, so the deadlock happens before emit. The helper also prints the MSBuild scratch paths (`fs1023-msbuild/332b5c72…` and `fs1023-msbuild/7311a82…`), though the `.binlog` files never materialise because MSBuild never completes.
    - **Timeout repro (done):** running `FS1023_TRACE=1 FS1023_KEEP_TEMP=1 timeout 600s dotnet test … --filter "GenericInput_multipleInstantiations"` now leaves behind `/tmp/fs1023_generic_timeout.log`, `/tmp/fs1023_generic_trace.nettrace`, `/tmp/fs1023_generic_dump.dmp`, and `/tmp/fs1023_generic_sample.txt` alongside the preserved temp tree above. The log confirms the harness submits both provider/consumer compiles, and the trace/dump capture the exact instant the consumer parse begins and then freezes.
    - **MSBuild thread dump (new):** `dotnet-dump analyze /tmp/fs1023_generic_dump.dmp` shows `Microsoft.Build.BackEnd.InProcNode.Run` (thread 16) waiting on `WaitHandle.WaitMultiple`, four `NodeProviderOutOfProcBase+NodeContext.DrainPacketQueue` threads (IDs 22–25) parked on `WaitHandle.WaitOne`, and six `Microsoft.Build.Tasks.Copy.ParallelCopyTask` workers (IDs 27–32) blocked inside `WaitHandle.WaitOne`. The rest of the pool sits inside `BlockingCollection.TryTake`, so MSBuild’s request queue never drains and the original `BuildSubmission.Execute` in thread 1 stays stuck in `WaitHandle.WaitOne`.
    - **BuildRequest logging (11 Nov @ 12:20 UTC):** `compileWithLogging` now stamps every provider/consumer invocation with a submission ID, the computed target list, and begin/parse/compile/end timestamps. The run that timed out at **12:20:11Z** produced `/var/folders/.../fs1023-70d433ad62fe4deda136937bc564b2ad/generic.log` showing that the consumer never emitted `build-request compile-end` even though it logged `parse+check-begin`. MSBuild `.binlog` directories for that run live under `/var/folders/.../fs1023-msbuild/e880f6104e274d2db38f3e52360be2e9` (provider) and `/var/folders/.../fs1023-msbuild/378e041332cb40e3b15304ef580772af` (consumer).
    - **BackgroundCompiler instrumentation (11 Nov):** the new `FS1023_TRACE` hooks inside `src/Compiler/Service/BackgroundCompiler.fs` emit `[builder-cache]` and `[project-check]` entries into `/tmp/fs1023_trace.log`. The latest timed run (`fs1023-e62dd12d595f4ff48712f2659857bb8e` temp dir, log timestamp **03:36:39Z**) shows `generic-consumer.fsproj` hitting a cache miss, successfully building an incremental builder, logging `full-check begin`, and then never producing the matching `full-check end`. The provider project completes the same path in ~700 ms, so the hang is now conclusively narrowed to `builder.GetFullCheckResultsAndImplementationsForProject` on the consumer.
    - **IncrementalBuilder tracing (11 Nov @ 04:40 UTC):** added `[incrementalbuilder]` hooks inside `src/Compiler/Service/IncrementalBuild.fs` so each `GetCheckResults*` call and the `FinalizeTypeCheckTask` boundary emits begin/end markers. The latest timeout run (`fs1023-c4477d9c73e248a3b07dce38096d9075` temp dir) shows the provider project logging both `[bound-model] finalize begin` and `end`, followed by `[project-check] get-full end`. The consumer logs `get-full begin` → `get-results begin` → `[bound-model] finalize begin` for `GenericConsumer.dll` **but never emits the matching finalize/end entries**, confirming the deadlock sits inside `FinalizeTypeCheckTask` while producing the final bound model for the consumer.
    - **FinalizeTypeCheckTask deep trace (11 Nov @ 07:40 UTC):** expanded the instrumentation to log each `boundModel` evaluation and `tc-info` computation (including the syntax tree file name). The latest timeout (`fs1023-1d42c559b513407088fabc9c4d480a4b`) shows `[tc-info] typecheck begin file=…/Generic.fs` followed by `[tc-info] node begin file=startup` with no matching end, so `BoundModel.GetOrComputeTcInfo()` hangs while `CheckOneInput` type-checks `startup.fs` long before `tcInfoExtras` or IL emission.
    - **Manual `fsc` replay (done):** running the recorded provider command from `generic.log` via `dotnet artifacts/bin/fsc/Release/net10.0/fsc.dll …` inside the preserved temp directory completes immediately. The consumer command fails fast with a missing `FSharp.TypeProviders.SDK` reference rather than hanging. That narrows the hang to the FCS/MSBuild hosting layer (incremental builder → type-provider reload) instead of the command-line compiler itself.
    - **Opt-out experiment (11 Nov @ 12:51 UTC):** new env toggles `FS1023_FORCE_FRESH_CHECKER=1` (instantiate a per-build checker) and `FS1023_SKIP_PARSE_AND_CHECK=1` let us bypass the cached incremental builder and skip the `ParseAndCheckProject` hop entirely. Even with both toggles enabled the consumer still logged `build-request compile-begin` with no matching `compile-end` before the watchdog killed the test (`/var/folders/.../fs1023-3214be90b5df4d6692771032325f1433/generic.log`). MSBuild dumped its partial logs into `/var/folders/.../fs1023-msbuild/488c263c837e4db4a89f9d86ca35300c`, so the remaining deadlock now definitively happens during `checker.Compile` rather than during type-checking.
    - **CompileOps instrumentation (11 Nov @ 13:27 UTC):** `compileFromArgs` plus `main4`/`main5`/`main6` now emit `[fs1023][compileops] …` markers. The provider compile shows `main4/main5/main6 begin/end` entries within 200 ms, but **the consumer never logs `compileFromArgs begin` or any `main4` entry**, even when `FS1023_SKIP_PARSE_AND_CHECK=1`. Combined with the `build-request compile-begin` log, this proves we stall before the driver enters the TAST→IL phase—likely while evaluating provider static arguments or preparing the bound model.
    - **Manual CLI replay (11 Nov @ 13:30 UTC):** running the captured consumer command via `dotnet artifacts/bin/fsc/.../fsc.dll @consumer.rsp` now fails immediately (missing `ParamArray` + `TxTType` support) instead of hanging, which reinforces that the pathological behavior is specific to the FSharpChecker/MSBuild hosting layer.
    - **Provider-call traces (11 Nov @ 13:55 UTC):** `ProvidedType.ApplyStaticArguments` and `ProvidedMethodBase.ApplyStaticArgumentsForMethod` now emit `[fs1023][typeproviders]` begin/end/fail markers. The newest timeout shows the provider-side callbacks firing for `Fs1023Provider` but we still never log a matching `compile-call-end` for the consumer, so the deadlock sits after the provider returns but before IlxGen starts.
    - **Provider instrumentation (11 Nov @ 14:05 UTC):** the inline `Fs1023Provider` spike now logs `define-start/end`, every `GetMethods`/`GetProperties` enumeration, each member synthesis (`addMember-*`, `addSummary-*`), and the detailed parameter walks. These `[fs1023][provider] …` breadcrumbs flow into STDOUT during the hung run, so the next timeout should tell us exactly which reflection call or provided-member addition never completes.
    - **Timeout sample (11 Nov @ 16:50 UTC):** running `timeout 300s env FS1023_TRACE=1 FS1023_KEEP_TEMP=1 dotnet test … --filter "GenericInput_multipleInstantiations"` now leaves the provider trace under `/tmp/fs1023_provider.log`. The latest run captured `define-start` and `methods-begin` for `ProvidedGenericInt` but never reached `methods-end`, proving we hang while enumerating `sourceType.GetMethods` for the consumer `Generic<'T>` type (before any member synthesis starts). The preserved workspace for that run is `/var/folders/.../fs1023-ad9dde56f73a41c981784d0ada048c7f`.
    - **Dump/trace capture (11 Nov @ 17:05 UTC):** replaced the shell `timeout` with a Python watchdog that (a) streams stdout to `/tmp/fs1023_generic_timeout.log`, (b) collects `/tmp/fs1023_generic_dump_latest.dmp` (~7.2 GB) and `/tmp/fs1023_generic_trace.nettrace` (11 MB Speedscope) after 270 s, (c) sends `SIGQUIT`, then terminates the test before the 300 s limit. `dotnet-dump analyze … clrthreads/setthread 29` shows multiple MSBuild copy workers blocked inside `Microsoft.Build.Tasks.Copy.ParallelCopyTask` (`System.Threading.WaitHandle.WaitOne`), matching the earlier suspicion that the FCS-hosted build deadlocks in the MSBuild driver once the provider compile spins up its copy tasks.
    - **CLI repro (11 Nov @ 17:20 UTC):** rebuilt the recorded `consumer.rsp` (183 args harvested from `generic.log`) and ran `timeout 300s dotnet …/fsc.dll @consumer.rsp` inside `/var/folders/.../fs1023-ad9dde56f73a41c981784d0ada048c7f`. After copying `FSharp.TypeProviders.SDK.dll` beside the provider, the standalone compiler also timed out, printing the same provider log sequence (`define-start` → `methods-begin` with no `methods-end`). This proves the hang is not MSBuild-specific—the TastReflection `System.Type` proxies deadlock when `GetMethods(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance ||| BindingFlags.Static)` is invoked for `Generic<'T>`.
    - **Next debugging hop:**
        1. Mine the captured dump/trace to identify which MSBuild request (target + path) owns the stuck `ParallelCopyTask`, then compare against the provider log to see whether we’re copying the provider DLL, the temporary consumer, or another dependency.
        2. Instrument the TastReflection proxy (`src/Compiler/TypedTree/TastReflection.fs` → `TxTypeDefinition.GetMethods`/`TxType.GetMethods`) so every `BindingFlags` combination logs before/after we materialise the method table. If `GetMethods` never reaches the “end” log, we’ll know the proxy itself is recursing (e.g., enumerating provided members that call back into the provider).
        3. Once TastReflection logging is in place, rerun both the MSBuild-hosted test and the CLI `@consumer.rsp` reproduce under the watchdog to determine whether the deadlock is in our `ProvidedType` proxy or in user code. If it’s proxy-side, focus on breaking the recursion (perhaps by short-circuiting `BindingFlags.NonPublic` for project-defined types); if not, return to the MSBuild investigation above.
  - Negative coverage (anonymous records, type parameters, provided types as static arguments) stays enabled and green.
  - ✅ Component sweep: `dotnet test tests/FSharp.Compiler.ComponentTests/FSharp.Compiler.ComponentTests.fsproj -c Release` now runs cleanly, so the Fs1023 plumbing no longer regresses the broader suite.
  - **Next:** re-enable the generic regression once the hang is resolved, then expand coverage toward IDE scenarios (`tests/fsharp/typeProviders/**`) and TPSDK samples.

### 5.2 SDK + C# consumer tests

- **Goal:** add a minimal C# consumer that references the generated F# assembly, executes the relocation-safe members, and validates the IL does not reference the provider binary.
- **Status:** ✅ `csharp consumer executes generated member` now targets `net10.0`, references the freshly built Fs1023 consumer DLL plus `FSharp.Core`, and the reflection asserts prove both `Value` and `MapParameters` flow across the language boundary. `/standalone` coverage remains a follow-up once IlxGen stop relying on provider IL entirely.

### 5.3 Negative tests

- ✅ Static-argument rejections: `anonymous record static argument is rejected`, `type parameter static argument is rejected`, and `provided type static argument is rejected` all assert that `TcStaticConstantParameter` surfaces the expected diagnostics for anonymous records, typars, and provided types respectively.
- ✅ Provider-surface guard: `non-generated provider types are rejected` compiles a provider that attempts to return an erased/non-generated type and verifies the compiler surfaces the relocation failure (“type could not be found in that assembly”) rather than emitting IL for it, proving we block providers from projecting project-defined types.

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
