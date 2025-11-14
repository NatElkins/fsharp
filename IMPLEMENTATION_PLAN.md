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
- Generative constructors and property setters now flow through `publishProvidedMembers`: the checker synthesises `.ctor`/`set_*` `Val`s (complete with invoker bodies) and IlxGen picks them up via `collectProvidedMembersForTycon`. The Fs1023 regression asserts `set_MutableSummary`/`.ctor` appear in the typed tree and uses reflection (normal + `/standalone`) to prove the generated setter mutates state across both build modes.
- `TastReflection.TxMethodDef`/`TxConstructorDef` source parameter and return metadata directly from `ProvidedMemberBinding`, so reflection-based tooling sees the provider surface without re-querying the provider.
- `publishProvidedMembers` in `CheckDeclarations.fs` now builds the self type via `TType_app`, materialises `ValMemberInfo`/`ValReprInfo` correctly, and emits method/constructor stubs while the checker walks generative types. Each published `Val` now carries the invoker-derived body plus a `ProvidedMemberBinding`, and `registerGeneratedTycon` writes both the provider path (`Fs1023/Provided`) and the relocated consumer path (`Fs1023Consumer/Provided`) into `ProvidedGeneratedTypeRegistry` under the provider assembly. Non-local dereferences no longer fault even when consumers resolve the relocated path; the remaining compiler work is to teach IlxGen/static-link to emit IL for those cached bodies.
- A fresh comparison against the `main` worktree confirms the upstream compiler still omits `Val` stubs for generative members; the IlxGen/InfoReader paths all query `ProvidedMemberBinding`. Our branch must finish the new publication logic before any consumer can pivot.
- `FS1023_TRACE` instrumentation still appends to `/tmp/fs1023_trace.log`, and the latest runs show the generated tycon registering plus the five synthetic getters lighting up both `MembersOfFSharpTyconSorted` and `tcaug_adhoc_list`. The checker publishes bindings before we recurse into nested types, so the typed tree now faithfully contains the cached `Val`s that IlxGen/InfoReader must ingest next.
- Additional tracing inside `ModuleOrNamespaceType.AddProvidedTypeEntity` and `ProvidedGeneratedTypeRegistry.register/tryGet` shows both keys lighting up: every `Fs1023Consumer.Provided` definition now records `registry-register assembly=Fs1023Provider path=Fs1023/Provided` and the mirror entry for `Fs1023Consumer/Provided`. That dual registration keeps name resolution stable regardless of whether a given `NonLocalEntityRef` was built with the provider’s namespace or the relocated consumer path, and `/tmp/fs1023_trace.log` confirms subsequent lookups hit the cache.
- `ServiceAssemblyContent` no longer filters out generated provided types. As a result `TypeProviderDependencyInvalidationTests`.`provided type publishes members into the TAST` is now re-enabled and green locally (ran via `FS1023_TRACE=1 dotnet test tests/FSharp.Compiler.Service.Tests/FSharp.Compiler.Service.Tests.fsproj -c Release --filter "provided type publishes members into the TAST"`). The test still only guards for type/members being discoverable through FCS; we haven’t yet translated the members into `tcaug_adhoc_list`.
- The Type Provider SDK now surfaces `ProvidedMethod.GetInvokeCode`/`ProvidedConstructor.GetInvokeCode` for generative types. `ProvidedMemberBinding` therefore captures the original quotations, `publishProvidedMembers` stores them as `ReflectedDefinition`s, and IlxGen compiles those bodies directly into the consumer assembly. The Fs1023 regression now executes the emitted getters instead of falling back to `[Fs1023Provider]Fs1023.Provided::*`, so the TypeLoadException is gone.
- `TastReflection.computeParameterMetadata` mirrors CLR optional/default semantics. F# `?value` parameters no longer synthesize `HasDefaultValue = true`, `[<Optional>]` members still expose `ParameterAttributes.Optional`, and `ParameterInfo.IsDefined` only answers for the attribute that actually exists. The Fs1023 reflection assertions now read `OptionalParameter = "value:false:false:none"` while `OptionalLiteralParameter` remains `value:true:true:42`.
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

**Recent progress update:** `ImportMap` now exposes `GetTypeReflectionBuilder`/`ReflectType`, and `TcStaticConstantParameter` consumes the helper.

**Next concrete actions before leaving Phase 1:**
1. **Parity checklist sweep**
   - Re-read `TastReflection.fs` and close the remaining TODOs called out in §1.1 (method/ctor parameter fidelity, `GetConstructorImpl`/`GetMethodImpl` fallbacks, indexer binding). Strike each item from this plan once we have code + tests.
   - Extend `TypeProviderDependencyInvalidationTests` to cover any newly fixed behaviour (e.g., extra optional-arg cases, multi-parameter indexers) so we have regression evidence.
2. **Event/indexer audit**
   - Double-check that the new provided-event path (`tcaug_provided_events`) works for nested types, static events, and non-default BindingFlags. If we find gaps, fix them and record the tests in §5.1.
   - Re-verify the indexer binder behaviour with `Type.GetProperty("Item", …)` + varied `types` arrays (null vs. explicit parameter list).
   - **2025-11-14 binding-flags note:** The `record input compiles generated summaries` regression now explicitly probes `GetMethod/GetProperty` with both public and non-public flags. The hidden members (`HiddenSummary`, `HiddenResult`) only surface when `BindingFlags.NonPublic` is specified, and the indexer is discoverable with both explicit parameter lists and a `types = null` filter, matching CLR behaviour.
   - **2025-11-14 event note:** `provided event surfaces in emitted IL` now verifies that `GetEvent` honors `BindingFlags` (public/static succeeds; non-public/instance fails) and that `GetEvents` returns the expected event sets.
   - **2025-11-14 attribute-flags note:** `ReflectTypeDefinition.GetAttributeFlagsImpl` now respects visibility (nested/public/internal), interface vs. class semantics, value-type sealing, and provided/IL metadata (`IsSealed`, `IsAbstract`). The Fs1023 regression asserts the generated types report the expected `IsPublic`/`IsNestedPublic` values.
3. **Debugger & profiling hooks**
   - Finish the ergonomics perf pass originally promised here: add profiling toggles/measurements for TastReflection projections, and ensure the debugger view for the remaining proxy types is friendly (we only handled `ReflectModule` so far).
   - **2025-11-14 profiling note:** Type projections now log `[tastreflection] type-begin/type-end` (name, nested flag, ctor/method counts, elapsed ticks) whenever `FS1023_TRACE_TAST` is enabled, and `TypeReflectionBuilder.NotifyTypeCreated` records timings even outside the profiling mode.
4. **Doc + checklist update**
   - Once the above items are green, mark Phase 1 as “Complete” in this document (not just “In progress”) and move the detailed parity checklist to an appendix for future reference.
   - **2025-11-14 parity note:** `TxMethodDef`/`TxConstructorDef` now implement real equality/hash/metadata-token logic (declaring type + parameter types + generic arity). The `record input compiles generated summaries` regression asserts that both `MapParameters` **and** the parameterless constructors from `Fs1023Consumer.{Provided,ShapeProvided}` remain distinct when added to a `HashSet`, so duplicate identities no longer collapse.

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
  - ✅ 12 Nov 2025 — Audit complete: a repo-wide search shows only `ImportMap`/`TcImports` implementations reference `GetTypeReflectionBuilder`, so no additional call sites need routing.

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
- ✅ `TxConstructorDef`, `TxMethodDef`, `TxPropertyDefinition`, `TxEventDefinition`, and the record-field projections now materialise attribute instances for `GetCustomAttributes`/`GetCustomAttributesData`, and the Fs1023 regression inspects those instances via `p.GetCustomAttributes(false)` to prove the proxy supports real attribute objects (e.g., `OptionalArgumentAttribute`).

### 1.7 Indexer and binder parity

- **[Compiler]** Align `ReflectTypeDefinition.GetPropertyImpl` and `GetMethodImpl` with the CLR binder: erase proxy wrappers when comparing parameter types, handle optional arguments and `HasDefaultValue`, and support indexer discovery (`Item` property).
- **[Tests]** Extend the FS-1023 provider to retrieve the `Item` property with explicit binder arguments and confirm it succeeds.
- **[Cleanup]** Once the above passes, remove the temporary `[fs1023]` / `[tast]` instrumentation added during investigation.
  - ✅ Removed the ad-hoc `[tast-debug]` `printfn` statements from `TastReflection.fs`; future diagnostics are handled through the FS1023 tracing infrastructure instead of unconditional console output.

---

## Phase 2 — Static parameter evaluation updates (Status: In progress)

> Current state: `TcStaticConstantParameter` materialises `System.Type` proxies, records `TyconRef` dependencies, and invalidation now flows through incremental builds; remaining work includes richer diagnostics and additional negative/regression tests.

### 2.1 Modify `TcStaticConstantParameter`

- ✅ `TcStaticConstantParameter` now validates `System.Type` arguments (rejecting provided and anonymous types), reflects them via `ImportMap.ReflectTypeWithDependencies`, and records every referenced `TyconRef`.
- **[Compiler]** Follow-up: tighten diagnostics/messages once we add the negative tests in §2.3.

### 2.2 Record dependency stamps

- ✅ `TypeReflectionBuilder` maintains per-projection dependency scopes; callers can capture the visited `TyconRef`s via `CaptureTypeDependencies`, and `TcStaticConstantParameter` forwards them to `RecordTypeDependency`.
- ✅ **[Tests]** Added `type provider re-runs when source type changes` (and expanded it to count provider log entries) to prove that editing `Fs1023Consumer.Model` forces `Fs1023Provider` to re-run and that incremental builds observe the dependency via `TypeReflectionBuilder`. The test also confirms the dependency file list includes the model source.
- ✅ **[Tests]** Added `type provider re-runs when signature file changes`, which introduces paired `.fsi`/`.fs` inputs under `Fs1023Signature` and asserts that mutating only the signature file triggers provider re-execution, increments the `[fs1023][provider] define-start` count, and records the `.fsi` path in `DependencyFiles`.

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

- **Next breakdown (Phase 4.4 remaining work):**
  1. **Finalize member harvesting (`IlxGen.fs`)** — ✅ `collectProvidedMembersForTycon` now walks `tcaug_adhoc_list`, captures the `ValRef`/`ProvidedMemberBinding` pairs, remembers declaration order, and pre-computes property shapes so later emission phases can reuse a single catalog instead of re-scanning `MembersOfFSharpTyconSorted`.
  2. **Emit methods/ctors (`GenProvidedTypeDef`)** — ✅ `genProvidedMemberBinding` now consumes the catalog entries directly, so we emit provided methods (and ctors) in declaration order, reusing the cached `ProvidedMemberBinding` bodies when present and falling back to `ReflectedDefinition` only if the binding had no invoker. The IlxGen trace still records each emission (downstream work: layer ctor-specific tracing if we need per-member diagnostics).
  3. **Emit properties/events** — ✅ `GenProvidedTypeDef` now projects the catalog’s property shapes into real `ILPropertyDef`s so getters/setters share a single definition, and we kept the existing `GenEventForProperty` path for `[<CLIEvent>]` getters. Property shells now live entirely in IlxGen (catalog → IL) rather than being stapled inside `genProvidedMemberBinding`.
  4. **Wire `skipProviderStaticLinking`** — ✅ `TcImports.RecordGeneratedTycon` now flips `SkipProviderStaticLinking` as soon as we register a generated tycon, which prevents the static-linker from trying to embed the provider-supplied IL alongside the IlxGen-emitted type. `FS1023_TRACE` still logs the `record-tycon` event so we can see when relocation is bypassed.
  5. **Validation hooks** — ✅ IlxGen now logs a per-type summary (method/property counts) under `FS1023_TRACE`, and the `provided type publishes members into the TAST` regression reflects over the compiled consumer assembly (plus its `/standalone` twin) to assert the summary properties remain discoverable via IL metadata. This catches regressions where the typed tree looks correct but the emitted property shells disappear.
  6. **Standalone regression** — ✅ `TypeProviderDependencyInvalidationTests.provided type publishes members into the TAST` now loads both the default consumer assembly and its `/standalone` twin, invokes `Value`/`MapParameters`/`Optional*`/`IndexerParameters` via reflection, and asserts the returned strings match. This proves the IlxGen-emitted IL behaves identically regardless of relocation.

With the relocation smoke-tests green, Fs1023 consumers now build/run successfully in both normal and `/standalone` configurations.

---

## Phase 5 — Tests (Status: In progress)

### 5.1 Compiler unit tests

  - ✅ `record input compiles generated summaries` — after restoring the source record’s `Value` field and fixing the summary helper to use a named identifier, this regression now passes for both the default and `/standalone` consumer builds. The test reflects over the emitted IL to ensure `RecordProvided.Value` and `MapParameters` survive relocation.
  - ✅ `union input compiles generated summaries` — now mirrors the record coverage: we compile/reflect both binaries and assert `ShapeProvided.MapParameters`/`Value` produce the expected strings, confirming union metadata also survives relocation.
  - ✅ `GenericInput_multipleInstantiations` — the tast-reflection typar scope fix (11 Nov @ 22:15 UTC) plus today’s metadata tweaks mean the regression now finishes in ~3 s even under `timeout 300s env FS1023_TRACE=1 FS1023_KEEP_TEMP=1 dotnet test …`. We now reflect over both `Fs1023Consumer.ProvidedGenericInt` and `Fs1023Consumer.ProvidedGenericString` and assert the summary properties (`MapParameters`, `OptionalParameter`, `IndexerParameters`, etc.) expose the expected strings, so we catch future metadata regressions automatically. (Historical context for the original hang remains below for posterity.)
  - ✅ 12 Nov 2025 — Parameter metadata parity: `TastReflection.computeParameterMetadata` now treats F# `?value` parameters like real CLR optional arguments (we set `ParameterAttributes.Optional`, force `HasDefaultValue = true`, and surface `Type.Missing` as the raw/default value) and `TxTypeDef.GetPropertyImpl` now interprets a `null` `types` array as “no constraint”, matching `System.RuntimeType` so indexers remain discoverable even when callers don’t specify the index parameter types. Together these changes make the provider observe `OptionalParameter = "value:true:true:OptionalArgumentAttribute"` and `IndexerParameters = "index:Int32"` for both generic instantiations, fixing the snapped regression and giving us parity with the earlier non-generic scenarios.
  - ✅ 13 Nov 2025 — Added a JSON serializer sample/provider regression (`json serializer provider roundtrip works`). The test compiles a `JsonSerializerProvider<Source = Order>` that emits `ToJson`/`FromJson` helpers (backed by `System.Text.Json`) and verifies round-tripping through the generated type to ensure real-world provider scenarios (serialization helpers) function with FS-1023.
  - ✅ 13 Nov 2025 — Re-ran the full component suite via `timeout 600s dotnet test tests/FSharp.Compiler.ComponentTests/FSharp.Compiler.ComponentTests.fsproj -c Release`; it passed with the usual nullability warnings, confirming the recent TastReflection changes didn’t regress the broader tests.
    - **Timeout repro artifacts:** the original 10 minute watchdog run (`FS1023_TRACE=1 FS1023_KEEP_TEMP=1 timeout 600s …`) left behind `/tmp/fs1023_generic_timeout.log`, `/tmp/fs1023_generic_trace.nettrace`, `/private/tmp/fs1023_generic_dump.dmp`, and `/tmp/fs1023_generic_sample.txt`. Even though the regression is fixed, these artifacts give us a reproducible picture of the pre-fix state (consumer parse entered, never exited; MSBuild copy tasks blocked).
    - **MSBuild thread dump:** re-running `dotnet-dump analyze /private/tmp/fs1023_generic_dump.dmp` confirms the earlier diagnosis: thread 0 (OS id `0x1a34e41`) is blocked in `Microsoft.Build.Execution.BuildSubmission.Execute`, threads 25–26 are draining the out-of-proc node packet queue, and threads 27/28/34 (e.g., `0x1a34f1d`) sit inside `Microsoft.Build.Tasks.Copy.ParallelCopyTask` → `System.Threading.WaitHandle.WaitOne`. Those thread ids give us specific targets for future MSBuild instrumentation or binlog capture.
    - **Repro binlogs (11 Nov @ 00:02 UTC):** the latest `FS1023_TRACE=1 FS1023_KEEP_TEMP=1 dotnet test …GenericInput_multipleInstantiations` run preserved `/var/folders/.../fs1023-7b61d17d03f74b2da4df9ce385ec96f4/` and emitted MSBuild binary logs:  
      `generic-provider.binlog` under `/var/folders/.../fs1023-msbuild/107afee3301b4129890f6b9f7360fca8/` and  
      `generic-consumer.binlog` under `/var/folders/.../fs1023-msbuild/74bc185781c34957a9b052315d6992f5/`.  
      These give us a precise view of which targets (and copy operations) ran during the successful repro, so we can diff against future hangs.
    - **BuildRequest logging (11 Nov @ 12:20 UTC):** `compileWithLogging` now stamps every provider/consumer invocation with a submission ID, the computed target list, and begin/parse/compile/end timestamps. The run that timed out at **12:20:11Z** produced `/var/folders/.../fs1023-70d433ad62fe4deda136937bc564b2ad/generic.log` showing that the consumer never emitted `build-request compile-end` even though it logged `parse+check-begin`. MSBuild `.binlog` directories for that run live under `/var/folders/.../fs1023-msbuild/e880f6104e274d2db38f3e52360be2e9` (provider) and `/var/folders/.../fs1023-msbuild/378e041332cb40e3b15304ef580772af` (consumer).
    - **BackgroundCompiler instrumentation (11 Nov):** the new `FS1023_TRACE` hooks inside `src/Compiler/Service/BackgroundCompiler.fs` emit `[builder-cache]` and `[project-check]` entries into `/tmp/fs1023_trace.log`. The latest timed run (`fs1023-e62dd12d595f4ff48712f2659857bb8e` temp dir, log timestamp **03:36:39Z**) shows `generic-consumer.fsproj` hitting a cache miss, successfully building an incremental builder, logging `full-check begin`, and then never producing the matching `full-check end`. The provider project completes the same path in ~700 ms, so the hang is now conclusively narrowed to `builder.GetFullCheckResultsAndImplementationsForProject` on the consumer.
    - **IncrementalBuilder tracing (11 Nov @ 04:40 UTC):** added `[incrementalbuilder]` hooks inside `src/Compiler/Service/IncrementalBuild.fs` so each `GetCheckResults*` call and the `FinalizeTypeCheckTask` boundary emits begin/end markers. The latest timeout run (`fs1023-c4477d9c73e248a3b07dce38096d9075` temp dir) shows the provider project logging both `[bound-model] finalize begin` and `end`, followed by `[project-check] get-full end`. The consumer logs `get-full begin` → `get-results begin` → `[bound-model] finalize begin` for `GenericConsumer.dll` **but never emits the matching finalize/end entries**, confirming the deadlock sits inside `FinalizeTypeCheckTask` while producing the final bound model for the consumer.
    - **FinalizeTypeCheckTask deep trace (11 Nov @ 07:40 UTC):** expanded the instrumentation to log each `boundModel` evaluation and `tc-info` computation (including the syntax tree file name). The latest timeout (`fs1023-1d42c559b513407088fabc9c4d480a4b`) shows `[tc-info] typecheck begin file=…/Generic.fs` followed by `[tc-info] node begin file=startup` with no matching end, so `BoundModel.GetOrComputeTcInfo()` hangs while `CheckOneInput` type-checks `startup.fs` long before `tcInfoExtras` or IL emission.
    - **Manual `fsc` replay:** running the recorded provider command from `generic.log` via `dotnet artifacts/bin/fsc/Release/net10.0/fsc.dll …` inside the preserved temp directory completes immediately. Once `FSharp.TypeProviders.SDK.dll` is copied beside the provider, the consumer command now succeeds as well (pre-fix it reproduced the hang). This double-check confirms the remaining deadlock signature was squarely in TastReflection rather than MSBuild/fsc.
    - **Opt-out experiment (11 Nov @ 12:51 UTC):** new env toggles `FS1023_FORCE_FRESH_CHECKER=1` (instantiate a per-build checker) and `FS1023_SKIP_PARSE_AND_CHECK=1` let us bypass the cached incremental builder and skip the `ParseAndCheckProject` hop entirely. Even with both toggles enabled the consumer still logged `build-request compile-begin` with no matching `compile-end` before the watchdog killed the test (`/var/folders/.../fs1023-3214be90b5df4d6692771032325f1433/generic.log`). MSBuild dumped its partial logs into `/var/folders/.../fs1023-msbuild/488c263c837e4db4a89f9d86ca35300c`, so the remaining deadlock now definitively happens during `checker.Compile` rather than during type-checking.
    - **CompileOps instrumentation (11 Nov @ 13:27 UTC):** `compileFromArgs` plus `main4`/`main5`/`main6` now emit `[fs1023][compileops] …` markers. The provider compile shows `main4/main5/main6 begin/end` entries within 200 ms, but **the consumer never logs `compileFromArgs begin` or any `main4` entry**, even when `FS1023_SKIP_PARSE_AND_CHECK=1`. Combined with the `build-request compile-begin` log, this proves we stall before the driver enters the TAST→IL phase—likely while evaluating provider static arguments or preparing the bound model.
    - **Manual CLI replay (11 Nov @ 13:30 UTC):** running the captured consumer command via `dotnet artifacts/bin/fsc/.../fsc.dll @consumer.rsp` now fails immediately (missing `ParamArray` + `TxTType` support) instead of hanging, which reinforces that the pathological behavior is specific to the FSharpChecker/MSBuild hosting layer.
    - **Provider-call traces (11 Nov @ 13:55 UTC):** `ProvidedType.ApplyStaticArguments` and `ProvidedMethodBase.ApplyStaticArgumentsForMethod` now emit `[fs1023][typeproviders]` begin/end/fail markers. The newest timeout shows the provider-side callbacks firing for `Fs1023Provider` but we still never log a matching `compile-call-end` for the consumer, so the deadlock sits after the provider returns but before IlxGen starts.
    - **Provider instrumentation (11 Nov @ 14:05 UTC):** the inline `Fs1023Provider` spike now logs `define-start/end`, every `GetMethods`/`GetProperties` enumeration, each member synthesis (`addMember-*`, `addSummary-*`), and the detailed parameter walks. These `[fs1023][provider] …` breadcrumbs flow into STDOUT during the hung run, so the next timeout should tell us exactly which reflection call or provided-member addition never completes.
    - **Timeout sample (11 Nov @ 16:50 UTC):** running `timeout 300s env FS1023_TRACE=1 FS1023_KEEP_TEMP=1 dotnet test … --filter "GenericInput_multipleInstantiations"` now leaves the provider trace under `/tmp/fs1023_provider.log`. The run captured `define-start`/`methods-begin` for both `ProvidedGenericInt` and `ProvidedGenericString` but never reached `methods-end`, proving we hung while enumerating `sourceType.GetMethods` for the consumer `Generic<'T>` type (before any member synthesis starts). The preserved workspace for that run is `/var/folders/.../fs1023-ad9dde56f73a41c981784d0ada048c7f`.
    - **Dump/trace capture (11 Nov @ 17:05 UTC):** replaced the shell `timeout` with a Python watchdog that (a) streams stdout to `/tmp/fs1023_generic_timeout.log`, (b) collects `/tmp/fs1023_generic_dump_latest.dmp` (~7.2 GB) and `/tmp/fs1023_generic_trace.nettrace` (11 MB Speedscope) after 270 s, (c) sends `SIGQUIT`, then terminates the test before the 300 s limit. `dotnet-dump analyze … clrthreads/setthread 29` still shows multiple MSBuild copy workers blocked inside `Microsoft.Build.Tasks.Copy.ParallelCopyTask`, but the new TastReflection traces now pinpoint the true culprit: `ReflectTypeDefinition.GetMethods` recursed forever because `asm.TxTType` tried to resolve the open-type typars and immediately re-entered itself.
    - **CLI repro (11 Nov @ 17:20 UTC):** rebuilding the recorded `consumer.rsp` (183 args harvested from `generic.log`) and running `timeout 300s dotnet …/fsc.dll @consumer.rsp` inside the preserved temp directory now succeeds once `FSharp.TypeProviders.SDK.dll` is copied beside the provider. Combined with the TastReflection logs, that confirms the hang was entirely inside the compiler’s projection layer, not MSBuild/fsc.
    - **Typar resolution fix (11 Nov @ 22:20 UTC):** `ReflectAssembly.TxTType` now (1) maps solved typars via `tp.Solution`, (2) pushes/pops scoped dictionaries for each generated type, and (3) falls back to a global name+kind lookup for alpha-renamed typars. `GetMethods`/`GetFields`/`GetProperties`/etc. wrap their work in that scope, so type parameters are resolved deterministically and no longer trigger infinite recursion. With these changes the watchdog repro finishes in ~3 s instead of hanging for five minutes.
    - **Property reflection fix (11 Nov @ 22:25 UTC):** `ReflectTypeDefinition.GetPropertyImpl` was already implemented but `ReflectTypeSymbol.GetPropertyImpl` returned `notRequired`, causing the provider to throw once `Generic<'T>` was instantiated. Delegating the `ReflectTypeSymbol` override back to the underlying type made the entire `GenericInput_multipleInstantiations` test go green under `timeout 300s env FS1023_TRACE=1 …`.
    - **Next debugging hop:**
        1. ✅ 13 Nov 2025 — Captured a fresh MSBuild binary log via  
           `timeout 600s env FS1023_TRACE=1 FS1023_KEEP_TEMP=1 dotnet test tests/FSharp.Compiler.Service.Tests/FSharp.Compiler.Service.Tests.fsproj -c Release --filter "DisplayName~GenericInput_multipleInstantiations" /bl:/tmp/fs1023-generic.binlog`  
           and fed it through the updated `BinlogDump` helper. `BinlogDump` now records every `Copy` task plus the underlying file moves (see `/tmp/fs1023-binlogdump.txt`). The log shows 58 `Copy` task invocations: the long-running ones were `CopyMIBC` inside `eng/restore/optimizationData.targets` (replicating the `DotNet_FSharp.mibc` payload across all RID-specific artifact folders) and `_CopyOutOfDateSourceItemsToOutputDirectoryAlways`/`CopyFilesToOutputDirectory` for `FSharp.TypeProviders.SDK` and `FSharp.Compiler.Service.Tests` (copying `ProvidedTypes.fs(i)` and `System.Collections.Immutable.dll` into the test output). No standalone `ParallelCopyTask` entries appear in the binlog—matching the earlier dump analysis that those worker threads live inside the `Copy` task implementation—so the stranded threads we saw in `/tmp/fs1023_generic_dump.dmp` were blocked inside these bulk copy targets, not on any provider temp directory. That rules out a race between provider relocation and MSBuild copies; the remaining hangs we observed were exclusively on the TastReflection side and are already fixed by the typar/indexer metadata patches.
        2. ✅ 13 Nov 2025 — Scoped the heavy TastReflection logging behind a dedicated `FS1023_TRACE_TAST` flag. Turning on `FS1023_TRACE` no longer spams the log by default; when deeper projection traces are needed we opt in by setting both environment variables. The default developer experience stays quiet while keeping the diagnostics one environment variable away.
        3. ✅ 13 Nov 2025 — Added `assertGenericSourceType` inside `TypeProviderDependencyInvalidationTests.GenericInput_multipleInstantiations`. The new helper reflects `Fs1023Consumer.Generic\`1` from both the normal and `/standalone` consumer binaries, closes it over `int`/`string`, and asserts that `Value`, `Map`, `Optional`, `Item`, and `OptionalLiteral` expose the expected parameter metadata (param-array attribute, optional/default flags, default value 42, etc.). This replaces the skipped reflection assertions and guarantees we exercise the same metadata the provider inspects via `TypeReflectionBuilder`.
  - Negative coverage (anonymous records, type parameters, provided types as static arguments) stays enabled and green.
  - ✅ `TypeReflectionBuilder captures dependencies for Fs1023 static arguments` — new coverage in `TypeProviderDependencyInvalidationTests` spins up `FSharpChecker` with `keepAssemblyContents`, reflects `Fs1023Consumer.Model` via `ImportMap.ReflectTypeWithDependencies`, and asserts the returned `System.Type` is the compiler proxy while the dependency list includes the model `TyconRef`. The test uses reflection to grab the underlying `TyconRef`/`thisCcu`, ensuring the public service surface can drive the same APIs providers will call.
  - ✅ Component sweep: `dotnet test tests/FSharp.Compiler.ComponentTests/FSharp.Compiler.ComponentTests.fsproj -c Release` now runs cleanly, so the Fs1023 plumbing no longer regresses the broader suite.

### 5.2 SDK + C# consumer tests

- **Goal:** add a minimal C# consumer that references the generated F# assembly, executes the relocation-safe members, and validates the IL does not reference the provider binary.
- **Status:** ✅ `csharp consumer executes generated member` now targets `net10.0`, references the freshly built Fs1023 consumer DLL plus `FSharp.Core`, and the reflection asserts prove both `Value` and `MapParameters` flow across the language boundary. The Fs1023 generic regression now also recompiles the consumer with `--standalone`, so static-link coverage guards the IlxGen-emitted IL path.
  - ✅ IDE-style TypeProvider suite re-enabled: the `#if !NETCOREAPP` guard in `tests/fsharp/TypeProviderTests.fs` is gone, so the legacy suite now compiles/runs on `net10.0`. Verified locally via `dotnet test tests/FSharp/FSharpSuite.Tests.fsproj -c Release --filter "FullyQualifiedName~TypeProviderTests"`, which compiles the provider samples, runs the multi-stage FSC/FSI scenarios, and reactivates the regression coverage across hello-world, diamond, and bincompat cases. `eng/Build.ps1` now invokes this filter as part of the default `-testCompiler` pass (netcore everywhere + netfx on Windows), so CI covers both the CLI and desktop FSI scenarios.
  - ✅ **TPSDK proxy regression:** `FSharp.TypeProviders.SDK/tests/ProxyTypeTests.fs` now calls the new `FSharpEntity.GetTypeReflectionProxy()` API (exposed from `FSharp.Compiler.Service`) to obtain an actual TastReflection proxy before round-tripping it through `ProvidedTypesContext`. The test project now references `fsharp/src/FSharp.Core/FSharp.Core.fsproj` instead of pulling `FSharp.Core` from NuGet, so running `DOTNET_MULTILEVEL_LOOKUP=0 DOTNET_ROLL_FORWARD=LatestMajor ../fsharp/.dotnet/dotnet test tests/FSharp.TypeProviders.SDK.Tests.fsproj -c Release --filter FullyQualifiedName~ProxyTypeTests` no longer trips the MSB3277 “conflicting FSharp.Core” warning. (NU1504 + the legacy net5.0 analyzer warnings remain until the SDK repo updates its test TFMs, but they don’t block coverage.)

### 5.3 Negative tests

- ✅ Static-argument rejections: `anonymous record static argument is rejected`, `type parameter static argument is rejected`, and `provided type static argument is rejected` all assert that `TcStaticConstantParameter` surfaces the expected diagnostics for anonymous records, typars, and provided types respectively.
- ✅ Provider-surface guard: `non-generated provider types are rejected` compiles a provider that attempts to return an erased/non-generated type and verifies the compiler surfaces the relocation failure (“type could not be found in that assembly”) rather than emitting IL for it, proving we block providers from projecting project-defined types.

2. **Create developer guidance**
   - Draft doc (e.g., `docs/upcoming/fs-1023.md`) describing how provider authors can use the new capability and restrictions (no anonymous records, no direct references to project types).

---

## Phase 6 — SDK alignment (Status: Not started)

1. **ProvidedTypes documentation**
   - ✅ `docs/upcoming/fs-1023.md` now includes detailed guidance for provider authors (use TastReflection proxies, keep invoker quotations self-contained, rely on `ConvertTargetTypeToSource`, avoid double-registering relocated types). When we cut the next TPSDK preview, mirror the same text into the SDK changelog so the guidance ships alongside the SDK NuGet.
2. **TPSDK regression coverage**
   - ✅ `actual TastReflection proxy round-trips through ProvidedTypesContext` lives in `FSharp.TypeProviders.SDK/tests/ProxyTypeTests.fs`, uses `FSharpChecker` + `ImportMap.ReflectTypeWithDependencies` to obtain a real compiler proxy, and now passes under the preview `.NET 10.0` toolset via `DOTNET_ROLL_FORWARD=LatestMajor ../fsharp/.dotnet/dotnet test … --filter FullyQualifiedName~ProxyTypeTests`. MSBuild still warns about duplicate `FSharp.Core` references (nuget 4.7.2 vs. the locally built 9.x binary); follow-up: consolidate the references so the test runs warning-free without shimming `global.json`.
3. **Release coordination**
   - ✅ Added a “Version pairing & rollout guidance” section to `docs/upcoming/fs-1023.md` and mirrored the same note in `FSharp.TypeProviders.SDK/RELEASE_NOTES.md` (8.2.0-preview entry) so provider authors know to use the fs-1023 compiler/nightly TPSDK pairing until GA.

---

## Phase 7 — Clean-up and validation (Status: Not started)

1. **Run full test suite** (`./build.sh Test`).
   - ✅ 12 Nov 2025 — Started the sweep by running `dotnet test tests/FSharp.Compiler.ComponentTests/FSharp.Compiler.ComponentTests.fsproj -c Release`. The run succeeded (only the long-standing nullability warnings appeared), so component-level coverage is green.
   - ✅ 12 Nov 2025 — Followed up with `dotnet test tests/FSharp.Compiler.Service.Tests/FSharp.Compiler.Service.Tests.fsproj -c Release`. The service suite also passed (same baseline warnings).
   - ✅ 12 Nov 2025 — Ran the TypeProvider slice of the FSharpSuite (`dotnet test tests/FSharp/FSharpSuite.Tests.fsproj -c Release --filter "FullyQualifiedName~TypeProviderTests"`). All cases passed under the preview toolset; the only noise is the existing nullability warnings.
   - ✅ 12 Nov 2025 — Completed the top-level sweep via `./build.sh --testcoreclr -c Release /p:WarningsNotAsErrors=FS0066;FS3261;FS0760;FS0026` (equivalent to `./build.sh Test` for the .NET Core leg). Build + tests succeeded end-to-end, so Phase 7’s “full suite” milestone is complete.
2. **Review API surface** to ensure no public breaking changes.
   - ✅ 12 Nov 2025 — Tagged the new `FSharpEntity.GetTypeReflectionProxy` and `TcImports.GetTypeReflectionBuilder` APIs with `[<Experimental("FS-1023 preview API. Subject to change.")>]` so consumers see the preview banner in IntelliSense; documented the requirement in `docs/upcoming/fs-1023.md`/TPSDK release notes earlier.
   - ✅ 12 Nov 2025 — `docs/upcoming/fs-1023-api-review.md` records the review results (only those two APIs were added; both are experimental/preview-only). No additional public surface changes were detected.
3. **Prepare PR** targeting `dotnet/fsharp` with summary, risk assessment, and links to design docs.
   - ✅ `docs/upcoming/fs-1023-pr-draft.md` captures the PR outline (feature summary, architecture highlights, experimental API callouts, and the commands we ran). Update it as final polish gets scheduled, then paste into the PR when ready.
4. **Coordinate with SDK release plan** if updates are required in `FSharp.TypeProviders.SDK` NuGet package.

> **2025-11-13 status note for Phase 4.4:** Fs1023 provided members now cover setters, constructors, and CLI events, and we have a regression that inspects the emitted IL.
>
> - Restored the `MutableProvider` fixtures (provider/model/consumer source snippets) so the regression compiles under `--mlcompatibility --langversion:5.0`, then tightened the provider setter to use untyped `Expr.Coerce`/`Expr.Call` so we no longer trip the `Expr<string>` vs `Expr` mismatch during provider compilation.
> - Updated `publishProvidedMembers` to emit property setters (when `CanWrite` is true) and constructors (via `ProvidedType.GetConstructors()`), wiring each accessor through `ProvidedMemberBindingHelpers.createFor{Method,Constructor}` so IlxGen can lean on the stored invoker expressions when generating IL.
> - Added per-tycon event metadata (`TyconProvidedEventInfo`), taught `publishProvidedMembers` to project `ProvidedEventInfo` (adder/remover bindings plus handler types), and extended `collectProvidedMembersForTycon`/`GenProvidedTypeDef` so IlxGen emits `ILEventDef`s from the compiler-generated catalog instead of depending on provider IL.
> - `dotnet test tests/FSharp.Compiler.Service.Tests/FSharp.Compiler.Service.Tests.fsproj -c Release --filter "DisplayName~mutable provided type exposes setter and constructor"` stays green, and the new `"provided event surfaces in emitted IL"` regression reflects over both the normal and `/standalone` consumers to assert that `GetEvent("Triggered")` plus `Get{Add,Remove}Method()` survive relocation.

> **2025-11-13 module reflection follow-up:** Finished the first pass of `ReflectModule` and revalidated the Fs1023 smoke tests.
>
> - `ReflectModule` now inherits `System.Reflection.Module`, reuses `ReflectAssembly.GetTypes()` for discovery, and only overrides the virtual members available in the netstandard2.0 surface (`GetTypes`, `FindTypes`, `GetFields`, `GetMethods`, `GetMethodImpl`, `Resolve*`, etc.). We explicitly *do not* attempt to override sealed accessors such as `ModuleHandle`, so unsupported APIs funnel through `NotSupportedException` instead—this was necessary after a failed experiment to replace `GetModuleHandleImpl`, which netstandard treats as a non-virtual helper.
> - Extracting the module proxy temporarily severed the `TxTypeDef`/`TxTType`/`TryBindType` helpers from `ReflectAssembly`; those members (plus typar-scope management and tracing) are now reintroduced so `TypeReflectionBuilder` still has a single locus for caching and dependency capture. This keeps TastReflection projections + dependency tracking stable while the module object focuses purely on reflection APIs.
> - Re-ran the targeted regressions under `-c Release` to confirm nothing regressed:
>   - `timeout 300s dotnet test tests/FSharp.Compiler.Service.Tests/FSharp.Compiler.Service.Tests.fsproj --filter "DisplayName~mutable provided type exposes setter and constructor"`
>   - `timeout 300s dotnet test tests/FSharp.Compiler.Service.Tests/FSharp.Compiler.Service.Tests.fsproj --filter "DisplayName~provided event surfaces in emitted IL"`
>   - `timeout 300s dotnet test tests/FSharp.Compiler.Service.Tests/FSharp.Compiler.Service.Tests.fsproj --filter "DisplayName~record input compiles generated summaries"`
>   All three passed on `net10.0`, giving us confidence that both the mutable setter/ctor path and the newer event/property regressions survived the reflection changes.
> - Augmented `Fs1023Provider` to log both `EventSummary` (`ValueChanged`) and `ModuleTypeSummary` (`Model;Provided;UseProvided`) so we now assert the TastReflection proxies satisfy `Type.GetEvents` and `Type.Module.GetTypes` end-to-end, plus the `HiddenMethodVisibility`/`HiddenPropertyVisibility` probes that exercise `BindingFlags.NonPublic` on `Type.GetMethod`/`Type.GetProperty`.
> - `ReflectTypeDefinition.GetConstructors`, `GetMethods`, `GetFields`, `GetProperties`, and `GetEvents` now rely on `TxConstructorDef` + the shared visibility/scope filter, merge declared/provided members (including `tcaug_provided_events`), deduplicate by `ValRef.Stamp`, and honor `BindingFlags`. The updated regression proves private instance members remain discoverable only when `BindingFlags.NonPublic` is specified.
> - `ReflectModule` picked up a `DebuggerDisplay`/`ToString` implementation that reports both the module name and parent assembly, so the debugger view now points back to the projected assembly identity.
> - Broader verification: `timeout 600s dotnet test tests/FSharp.Compiler.Service.Tests/FSharp.Compiler.Service.Tests.fsproj -c Release --filter "FullyQualifiedName~TypeProviderDependencyInvalidationTests"` now passes end-to-end on `net10.0`, so every Fs1023 regression in that suite is green after the recent TastReflection changes.
> - Next up for Phase 1: finish the lingering parity polish (the indexer/event edge cases called out earlier) and turn the `fs-1023` work into a PR using `docs/upcoming/fs-1023-pr-draft.md`, then begin Phase 8 partner dogfooding + telemetry once the PR is up.
>
> **Gate:** Do **not** begin Phase 8 (preview packaging / partner dogfooding) until the Phase 1 checklist above is fully closed and explicitly marked “Complete” in this plan.

---

## Phase 8 — Rollout and monitoring (Status: Not started)

1. **Feature flag & preview builds**
   - Gate compiler changes behind `--langversion:preview` and an internal MSBuild property until validation completes.
   - Publish preview TPSDK + compiler packages to the internal `fs1023-preview` feed and circulate install instructions.

2. **Dogfood with partner providers**
   - Line up at least two early adopters (target: `FSharp.Data` + the SQLClient provider) and give them preview bits plus the migration checklist from `docs/upcoming/fs-1023.md`.
   - Track feedback in the shared FS-1023 spreadsheet (state, repro steps, owner) and feed actionable items back into Phases 1–5.

3. **Observability & telemetry**
   - Wire the existing `FS1023_TRACE` hooks into a structured EventSource that host IDEs can toggle; document the knobs in `docs/upcoming/fs-1023.md`.
   - Add a short “how to collect traces” appendix so partner teams can capture dumps/binlogs if they hit issues.

4. **Release sign-off**
   - Produce release notes outlining new diagnostics, supported scenarios, and known limitations.
   - Coordinate with .NET release management to align compiler and SDK publishing timelines.

---

## Risk register

| Risk | Phase trigger | Mitigation |
|------|---------------|------------|
| Projection cache leaks increase memory usage | Phase 1 rollout | Add stress tests with scripted provider reloads; monitor with VS memory snapshots before GA. |
| Provider invalidation misses edits in signature files | Phase 2/5 testing | Guarded by `type provider re-runs when signature file changes`, which mutates only the `.fsi` while counting provider re-executions and inspecting `DependencyFiles`; continue to audit dependency stamps for mixed `.fsi`/`.fs` edits. |
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

## Session handoff — 13 Nov 2025 @ 14:40 UTC

- **Completed this iteration**
  - Added signature-only invalidation coverage (`type provider re-runs when signature file changes`) with fresh fixtures (`Fs1023Signature`). The test mutates only the `.fsi`, confirms the provider reruns (via `[fs1023][provider] define-start` counts), and verifies the signature path is recorded in `DependencyFiles`.
  - Updated the architecture + plan docs to highlight that both implementation and signature edits are now under regression.
- **Outstanding TODOs for next agent**
  1. Phase 4.4 follow-up: extend the IlxGen catalog to surface provided events (plus CLI-event metadata) and verify `/standalone` builds continue to boot without touching the provider IL. The remaining work lives next to `collectProvidedMembersForTycon`/`genProvidedMemberBinding` in `src/Compiler/CodeGen/IlxGen.fs`.
  2. Phase 1 parity: `TastReflection` still lacks event exposure, `Module.GetMember` parity, and debugger-friendly display strings. See §1.1 “Remaining gaps”.
  3. Once IlxGen emits every member, extend the Fs1023 regressions to inspect the emitted IL (Mono.Cecil reflection) so relocation bugs surface automatically.
- **Testing / commands relied upon**
  - `dotnet test tests/FSharp.Compiler.Service.Tests/FSharp.Compiler.Service.Tests.fsproj -c Release --no-build --filter "DisplayName~type provider re-runs when source type changes"`
  - `dotnet test tests/FSharp.Compiler.Service.Tests/FSharp.Compiler.Service.Tests.fsproj -c Release --filter "DisplayName~type provider re-runs when signature file changes"`
  - Set `FS1023_PROVIDER_LOG=<path>` plus `FS1023_TRACE=1`/`FS1023_KEEP_TEMP=1` when diagnosing; logs land under `/tmp/fs1023_trace.log` and `fs1023-*` temp directories printed by the tests.
- **Lessons learned / context**
  - Default-value assertions should cast to the expected CLR type (`ParameterInfo.DefaultValue :?> int`) to avoid xUnit overload ambiguity.
  - When working on invalidation scenarios, the `[fs1023][provider] define-start` count is the most reliable signal that the provider actually re-ran.
  - Keeping provider logs configurable (now via `configureProviderLog`) makes it easy to share repro artifacts without editing every test.
