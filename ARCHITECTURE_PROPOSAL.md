# FS-1023 — “Type Providers Generate Types From Types”

This document proposes an updated architecture for implementing the FS-1023 feature on top of the current `dotnet/fsharp` tree.  FS-1023 allows a type provider to accept an existing type (e.g. an F# record) as a static parameter and emit new types or members that are derived from the shape of that type.  The original proof-of-concept lives on the historical `visualfsharp` fork (`colinbull/rfc/fs-1023-type-providers`).  That branch pre-dates the current repository layout, the split `Check*` modules, the modern type-provider hosting API, and the Roslyn-based toolchain.  We treat it as conceptual guidance only.

The remainder of this document records the data structures that must change, the information that has to be threaded through the compiler pipeline, integration points with the Type Provider SDK, and the major open questions that must be answered before work begins on a production-quality implementation.

---

## Architectural Status — 2025-11-06

- `TcImports.GetProvidedAssemblyInfo` now refreshes provider metadata using the provider’s `GetManifestModuleContents` hook, so we observe the in-memory IL for generated assemblies.
- Diagnostic traces confirm the sample provider only supplies infrastructure types (`Fs1023.Fs1023Provider`, `<StartupCode$…>`). The consumer-facing type `Fs1023Consumer.Provided` materialises via the compiler-generated tycon; we validate its presence through `ProvidedGeneratedTypeRegistry` entries and by traversing the typed tree.
- `TypeProviderDependencyInvalidationTests` is re-enabled end-to-end: `ServiceAssemblyContent` now includes generated provided types instead of filtering everything flagged as provider-backed, the new `FS1023_TRACE` logs (persisted to `/tmp/fs1023_trace.log`) show each `Fs1023Consumer.Provided` registering twice (once for the provider namespace path `Fs1023/Provided`, once for the relocated consumer path `Fs1023Consumer/Provided`) under the provider assembly inside `ProvidedGeneratedTypeRegistry`, and the regression test now walks nested `FSharpImplementationFileDeclaration`s so it observes the generated `Tycon` inside the `Fs1023Consumer` namespace. The typed tree exposes populated `tcaug_adhoc_list` entries for all five summary getters, so downstream consumer work can key off the published `Val`s instead of re-querying the provider.
- Invalidation coverage now includes both implementation **and** signature mutations. `type provider re-runs when source type changes` edits only `Model.fs`, while the new `type provider re-runs when signature file changes` builds a paired `SignatureModel.fsi/fs` and tweaks the `.fsi` comment; both tests assert the provider re-executes (via `[fs1023][provider] define-start` counts) and that `DependencyFiles` report the edited path, closing the risk that signature edits would be ignored by incremental builds.
- IlxGen now mirrors that typed-tree ordering: `collectProvidedMembersForTycon` walks `tcaug_adhoc_list`, captures the `ValRef`/`ProvidedMemberBinding` pairs (plus invoker bodies and property accessor grouping), and feeds the generated member list back to `GenProvidedTypeDef`. `genProvidedMemberBinding` consumes these catalog entries directly, so provided methods/constructors are emitted strictly in provider order and we can lean on the cached invoker expressions instead of always calling `vspec.ReflectedDefinition`. After the methods land we project the catalog’s accessor pairs into `ILPropertyDef`s (while keeping the existing CLI-event emission path), so property metadata now comes from a single pass instead of ad-hoc getter registration.
- Static linking skips the provider relocation path as soon as we register a generated tycon: `RecordGeneratedTycon` flips `SkipProviderStaticLinking`, and IlxGen’s `[ilxgen][provided] summary …` log confirms how many members each emitted type contributes. `/standalone` builds therefore reuse the compiler-emitted IL instead of embedding the provider DLL.
- Fixture sanity checks now mirror the provider contract: anytime we expect a generated type to expose `Value`, the source shape must also expose `Value`. The recent regression (“RecordProvided does not define member 'Value'”) traced back to the sample `RecordInput` having renamed the field; the provider simply copies whatever properties exist on the shape. The fix (restore `Value` and bind the summary helper to a named identifier) reinforces that these tests exercise compiler plumbing, not provider magic.
- Type Provider SDK (`ConvertTargetTypeToSource`) now recognises `TastReflection` proxies and preserves them without wrapping, with new regression coverage ensuring proxy properties/attributes remain visible.
- Mangled names for `System.Type` static arguments now include assembly-qualified identities, and metadata reload flows decode them back into real `System.Type` instances before re-applying provider static arguments.
- Immediate focus: (1) ingest `ProvidedMethodInfo`/`ProvidedPropertyInfo`, (2) translate `ProvidedExpr` invokers into the typed tree, (3) teach IlxGen to emit the resulting members, and (4) keep the reflection logging behind a flag once green.
- Test coverage focus: record/union/C# scenarios and the component suite are green, and the once-blocking generic static-argument regression now passes after the TastReflection optional/indexer metadata fix (12 Nov 2025). We are keeping all of the instrumentation (`compileWithLogging`, MSBuild binlogs, `[tc-info]` tracing, etc.) and captured artifacts around for future investigations, but the current plan can proceed with the generic scenario enabled. Historical context (pre-fix): The latest 600 s rerun (11 Nov 2025) with the enhanced `compileWithLogging` helper recorded `generic-consumer parse+check begin` with no matching completion, proving the hang occurred inside `checker.ParseAndCheckProject` before we ever called `Checker.Compile`. The preserved temp tree (`/var/folders/m_/…/fs1023-bebc21923f8d4674b77729c84a40c488/`) contains `generic.log` plus the MSBuild scratch roots (`fs1023-msbuild/332b5c72…` and `fs1023-msbuild/7311a82…`); the directories stay empty because MSBuild never flushed the requested `.binlog`. Correlating that log with `/tmp/fs1023_generic_dump.dmp` shows thread 16 stuck in `Microsoft.Build.BackEnd.InProcNode.Run`, threads 22–25 parked in `NodeProviderOutOfProcBase+NodeContext.DrainPacketQueue`, and six `Microsoft.Build.Tasks.Copy.ParallelCopyTask` workers (threads 27–32) waiting on `WaitHandle.WaitOne`, so the BuildSubmission stays blocked while waiting for out-of-proc node traffic. The new `FS1023_TRACE` hooks in `BackgroundCompiler.fs` add `[builder-cache]`/`[project-check]` entries to `/tmp/fs1023_trace.log`; the 03:36:39Z run (`fs1023-e62d…` temp dir) shows `full-check begin` for `generic-consumer.fsproj` with no corresponding `full-check end`. After adding `[incrementalbuilder]` tracing in `src/Compiler/Service/IncrementalBuild.fs`, the 04:40–05:28 UTC timeouts (`fs1023-c4477d9c73e248a3b07dce38096d9075`, `fs1023-5b4cf1036ce243ababb5f6cb9e968b7b`) logged `[bound-model] compute … complete` and `[tc-info] node begin file=startup` for the consumer but never reached `[tc-info] node end`, so the hang then pointed at `BoundModel.GetOrComputeTcInfo()` (i.e., TastReflection/provided-type rehydration of `startup.fs`) rather than MSBuild. The 12:20 UTC timeout (`fs1023-70d433ad62fe4deda136937bc564b2ad`) showed the build-request instrumentation (submission IDs + target lists) logging `compile-begin` without a matching `compile-end`, while the follow-up run with `FS1023_SKIP_PARSE_AND_CHECK=1` (`fs1023-3214be90b5df4d6692771032325f1433`) proved the hang persisted even when we bypassed the incremental builder entirely. Those `[fs1023][compileops]` hooks (`compileFromArgs` + `main4`/`main5`/`main6`) only fired for the provider, so the consumer never reached the TAST→IL phase; the `[fs1023][typeproviders]` traces showed that `ApplyStaticArguments` completed normally, and replaying the captured input via `fsc.dll @consumer.rsp` still failed immediately (missing `ParamArray`, unsupported `TxTType`), which reinforced that the deadlock was unique to the FSharpChecker/MSBuild hosting layer. Legacy IDE-style tests under `tests/fsharp/typeProviders` remain `#if !NETCOREAPP`; running `dotnet test tests/fsharp/FSharpSuite.Tests.fsproj --filter "FullyQualifiedName~TypeProviderTests"` on net10 currently discovers zero tests, so re-enabling that suite is an outstanding backlog item. Next steps (now historical):
1. Extend the compileWithLogging/background-builder hooks to capture each BuildRequestData submission (project path, targets, timestamps) so we know exactly when the MSBuild host stops responding.
2. Always set MSBUILDLOGFILE/MSBUILDDEBUGPATH for the regression harness and snapshot any partial .binlog just before the watchdog kills the process; even a truncated log should reveal the last emitted MSBuild target.
3. Try reproducing the hang outside the incremental builder (force tcConfigB.useIncrementalBuilder <- false or compile the preserved consumer project via FSharpChecker.CompileToDynamicAssembly). If it still hangs we instrument BoundModel.GetOrComputeTcInfo per file; if it doesn’t we concentrate on the builder graph / MSBuild deadlock.
- Regression plan: add targeted component tests that (a) interrogate the reflection builder (`Type.GetProperty("Value")`), (b) assert the generated `Tycon` publishes `get_Value`/`Map` pre-emission, and (c) inspect the generated IL to catch regressions before the end-to-end assembly load.
- Rather than trying to mutate the provider CCU directly, we now register each generated `Tycon` in a `ProvidedGeneratedTypeRegistry` and let `NonLocalEntityRef.TryDeref` fall back to that cache. This keeps name resolution happy without bespoke injection logic, and the temporary `printfn` tracing used during the spike has been removed.
- `ProvidedMemberBindingHelpers` now live in `TypeProviders` and are invoked from `CheckDeclarations` for root and nested generative members. The helpers register bindings (provider/member handles, result types, definition locations, parameter arrays, method return parameters, and cached invoker expressions for methods/constructors), and fresh unit tests cover empty plus multi-argument methods, indexers, and ctors so the captured metadata stays honest while the pipeline is incomplete. Downstream, `ProvidedMethodCalls.TranslateInvokerExpressionForProvidedMethodCall` consults the binding first, and the `TastReflection` surfaces (`TxMethodDef`/`TxConstructorDef`) hydrate reflection metadata from the binding, all without re-querying the provider.
- `MethodCalls.methodCallToExpr` now recognizes when a `ProvidedMemberBinding` is associated with a generated `ValRef` and rewrites any `ProvidedCallExpr` to invoke that `ValRef` directly (`mkApps` against the relocated tycon) before returning control to IlxGen. This means the cached invoker lambdas created in `publishProvidedMembers` are fully self-contained—no more Reflection.Emit calls back into `Fs1023.Provider`—and we can safely reuse those bodies when the IL backend starts emitting the provider-generated members.
- Zero-argument static getters currently stay on the provider-invoker path. The `tryCallAssociatedMemberWithArgs` fast-path only engages once there is a receiver or at least one explicit parameter, which removes the previous `GenLambda` crash (`expr=Val`) and makes the missing-type failure the primary blocker now that static linking is disabled.
- `GenProvidedTypeDef` now mirrors the provider metadata when emitting the relocated IL type: the type kind (class/value type/interface/enum/delegate), base type, and interface list come directly from `TProvidedTypeInfo`/`Tycon.ImmediateInterfaceTypesOfFSharpTycon`, so the generated IL matches what reflection reports (e.g., `ValueType`, `MulticastDelegate`, or additional interfaces).
- Static linking skips the relocation pipeline whenever there are no provider-generated assemblies to process (`SkipProviderStaticLinking = true`), so `/standalone` builds preserve the IlxGen-emitted type definitions instead of reinserting placeholder classes.
- The Type Provider SDK now exposes `ProvidedMethod.GetInvokeCode`/`ProvidedConstructor.GetInvokeCode` for generative types, so `ProvidedMemberBinding` captures the original quotations even after the provider has emitted IL. IlxGen’s `ensureProvidedBody` now compiles those quotations directly into the consumer assembly, eliminating the earlier `TypeLoadException` where `Fs1023Consumer.Provided.get_Value` attempted to call `[Fs1023Provider]Fs1023.Provided.get_Value`.
- `TastReflection.computeParameterMetadata` now fully matches the CLR’s expectations: we stamp both `ParameterAttributes.Optional` and `ParameterAttributes.HasDefault` for F# `?value` arguments (synthesising `Type.Missing` as the default payload), keep `[<Optional>]` parameters wired through unchanged, and teach `TxTypeDef.GetPropertyImpl` to treat a `null` `types` filter as “no constraint” the same way `System.RuntimeType` does. Providers therefore rediscover indexers even when they omit explicit index metadata, and the Fs1023 regression now observes `OptionalParameter = "value:true:true:OptionalArgumentAttribute"` plus `IndexerParameters = "index:Int32"` for both `ProvidedGenericInt` and `ProvidedGenericString`.
- `TxConstructorDef`, `TxMethodDef`, `TxPropertyDefinition`, and the record/field projections now instantiate real attribute objects for `GetCustomAttributes` (on top of the existing `GetCustomAttributesData` support), so providers can inspect attributes such as `OptionalArgumentAttribute` via the standard reflection API instead of parsing summary strings.
- `ProvidedMethodCalls.TranslateInvokerExpressionForProvidedMethodCall` mirrors that rewrite for consumer invocations: whenever a provided getter/method already carries an associated `ValRef`, we now bypass the provider-supplied invoker expression and emit a direct `mkApps` against the published member. `FS1023_TRACE` shows `[tp-invoker-call] rerouted via ValRef get_Value` during `TypeProviderDependencyInvalidationTests`, so call sites no longer inline provider IL. The regression still fails with `Undefined value 'get_Value'`, narrowing the remaining blocker to name resolution—`Fs1023Consumer.Provided.Value` is still elaborated as an unqualified `get_Value`. Temporary logging in `NameResolution.fs` (also guarded by `FS1023_TRACE`) now records the scope that rejects that lookup; the next milestone is to ensure the generated tycon contributes its published members to the ambient name environment before property resolution fires.
- `publishProvidedMembers` now runs as part of `TcTyconDefnCore_Phase1C`, building the self type via `TType_app`, initialising `ValMemberInfo`/`ValReprInfo`, and publishing the generated method/property stubs into `tcaug_adhoc`. We translate the cached invoker expressions into bodies, and `registerGeneratedTycon` now records both the provider path and the relocated consumer path under the provider assembly, so `NonLocalEntityRef.TryDeref` succeeds regardless of which namespace a lookup used. The remaining compiler work is to teach IlxGen/static-linking to emit IL bodies for these cached `Val`s and to stop consulting the provider DLL altogether.
- IlxGen now consumes those cached bodies and emits method/property IL for the relocated `Fs1023Consumer.Provided`, but the invoker quotations we cache still reference the provider’s original `Fs1023.Provided` Reflection.Emit type. When the generated IL executes it tries to load that provider-only type and crashes with `TypeLoadException`. The next compiler step is to retarget invoker expressions (e.g., rewrite `ProvidedCallExpr` nodes that point at generated members so they call the relocated `ValRef`) before IlxGen lowers them, guaranteeing the consumer binary no longer depends on IL that only lives inside the provider DLL.
- Cross-checking the `fsharp-main` worktree confirms upstream continues to skip `Val` synthesis for generative members; all consumer sites inspect `ProvidedMemberBinding`. Our branch has to finish the new publication logic (and the associated consumer pivots) before we can claim parity.

Key lesson: relying on the provider to bake the generated type into its DLL is insufficient; the compiler must consume the provider’s invoker expressions as the source of truth and ensure they flow through to IlxGen.

## Architectural options

### 1. “Colin Bull redux” (current branch)

- **Idea:** project `TyconRef`/`TType` into `System.Type` proxies, mine `ProvidedMethodInfo`/`ProvidedPropertyInfo`, create synthetic `Val`s with `ValMemberInfo`, translate `ProvidedExpr` into real bodies, and let IlxGen emit IL.
- **Pros:** maximum compatibility. Providers see real reflection objects, reuse existing reflection-heavy code, and the compiler changes fit into the existing type-provider contract. Matches what Colin Bull’s prototype achieved.
- **Cons:** large surface area. We must emulate the whole System.Type/MethodInfo/PropertyInfo API pre-emit, thread provider-specific cases through the checker and IlxGen, and keep that code in sync with future reflection changes. Hard to maintain and reason about.

### 2. Custom “shape” API instead of System.Type

- **Idea:** don’t give providers `System.Type` at design time. Instead expose a purpose-built `ITypeShape` abstraction (much like Roslyn’s `ITypeSymbol`), covering fields, methods, generics, attributes, etc. The compiler would implement it directly over the typed tree.
- **Pros:** much simpler compiler implementation (no TastReflect proxies), safer (we control the surface area), and more future-proof.
- **Cons:** providers can only inspect what we expose; no arbitrary reflection. They must port existing reflection code to the new shape API. We’d have to design and version that interface carefully.
- **Parity with source generators:** Roslyn source generators already work this way (they get `ISymbol`/`ITypeSymbol`, not `System.Type`). As long as our shape API is as expressive, this route keeps parity with C# generators. We can still hand back `System.Type` for compiled assemblies if needed.

### 3. Provider-supplied IL

- **Idea:** require providers to emit IL (via `ProvidedAssembly` or similar) for the generated members. The compiler loads and relocates that IL during static linking; no translation of `ProvidedExpr` is needed.
- **Pros:** the compiler mostly treats generated code like extra DLLs; no need for tastefully translating quotations.
- **Cons:** puts the burden on providers to generate IL (difficult) and still requires a way to inspect the consuming project’s types (we’d still need TastReflect or a shape API). Harder provider authoring experience.

### 4. Hybrid—shape or System.Type externally, dedicated binding internally

- **Idea:** keep giving providers the familiar view (System.Type proxies or a shape API) but encapsulate provider members explicitly in the typed tree (e.g., `ProvidedMemberBinding`). Checker, InfoReader, and IlxGen would consult that binding rather than treating provider members as ordinary `Val`s.
- **Pros:** isolates provider-specific logic, reduces invasiveness of special cases, still compatible with existing provider expectations.
- **Cons:** still requires us to translate `ProvidedExpr` into TAST/IL, but with cleaner boundary; more internal refactor than architectural change.
- **Current mess:** the existing implementation scatter-guns provider checks through the compiler. Examples include `CheckDeclarations.fs`, `CheckExpressions.fs`, `NameResolution.fs` (pattern matching on `TProvidedTypeRepr`/`ProvidedMeth`/`ProvidedProp`), `TypedTree.fs`/`TypedTreeOps.fs` (numerous `IsProvided` branches), `IlxGen.fs` (special `ProvidedMeth` cases), static-linking (`StaticLinking.fs`, `CompilerImports.fs`: `ProviderGeneratedType` remapping), and `TastReflection.fs` (reconstructing reflection info). Every pass rediscovering “this member came from a provider” increases maintenance cost and makes new features harder to layer on.
- **Refactor plan:** introduce a dedicated `ProvidedMemberBinding` carried alongside the `Val`. The checker (e.g., `TcTyconDefnCore_Phase1C`) populates it; consumers (`InfoReader`, `IlxGen`, static linker, reflection builder, FCS symbol layer) pivot to `match vspec.ProvidedBinding` rather than re-deriving provider metadata. This brings all provider-specific behaviour under one roof and makes the pipeline extensible (e.g., translating `ProvidedExpr` to IL once, recording static dependencies once, etc.).
- **Risk/profile:** the refactor touches core files but it’s mechanical—coalescing existing code. With the existing provider test suite and the new regression tests, it’s a manageable change that reduces long-term maintenance burden.

### Reflection vs shape API—trade-offs

- Giving providers `System.Type` (or proxies) is powerful and aligns with current expectations, but forces us to emulate the reflection API and carry provider-specific special cases throughout the compiler.
- A custom shape API would reduce compiler complexity and mirror the Roslyn source generator experience, but asks providers to adopt a new abstraction. Because FS-1023 is new, we *could* introduce such an API without breaking existing providers—but it’s a conceptual shift and requires a comprehensive, well-designed surface.
- Hybrid designs let us keep compatibility while reducing the compiler diff by encapsulating provider metadata in dedicated bindings.

### Bottom line

- The branch currently follows the “Colin Bull redux” path. We’ve already ported the reflection builder (`TastReflection.fs`), and the remaining work is synthesising provider members and emitting IL.
- If we were starting from scratch, a hybrid or shape-based approach might be cleaner, but the current branch has sunk cost in the reflection route. Finishing it keeps provider authors on the familiar reflection contract.
- Regardless of the path, introducing explicit regression tests (reflection builder parity, TAST publication, IL emission) is critical to make the solution maintainable.

**Next step:** even while waiting on core-team feedback, land the scaffolding for the `ProvidedMemberBinding` refactor in a low-risk way—add the binding field/accessors, keep existing behaviour intact, and commit the change as groundwork. Once feedback arrives we can either proceed with the full refactor or revert the scaffolding if the team prefers the minimal spike.

---

## 1. Overview of the existing pipeline

1. Parsing produces `SynType.StaticConstant*` nodes for static parameters on type provider invocations.
2. The checker runs `TcStaticConstantParameter` (in `CheckExpressions.fs`) to evaluate each static argument, enforcing that only primitive literal types are used.
3. For type provider applications, `CrackStaticConstantArgs` evaluates each static argument, and `TcProvidedTypeAppToStaticConstantArgs` forwards the resulting `obj[]` to the provider via `ProvidedType.ApplyStaticArguments`.
4. The provider supplies a `ProvidedTypeDefinition` that becomes a `Tycon` with `TProvidedTypeRepr` representation.  Subsequent compilation of the consumer project uses the standard provided-type machinery—no knowledge of the source F# type is retained after static argument evaluation.

## 2. New capability we must add

We must support a new kind of static parameter whose CLR type is `System.Type`.  When the user writes:

```fsharp
type MyRecord = { Id: int; Name: string }

type Poco = PocoProvider<MyRecord>
```

the compiler must:

1. Resolve `MyRecord` to a `TType`.
2. Produce a live `System.Type` object that exposes the same members as the target type (even though the project has not been emitted to disk yet).
3. Pass that `System.Type` instance to the provider.
4. Track the dependency so that edits to `MyRecord` invalidate the provided types that were created from it.

The provider may call reflection APIs on the `System.Type`—`GetFields`, `GetCustomAttributes`, `GetGenericArguments`, `DeclaringType`, etc.  The implementation must therefore provide a full fidelity proxy over the compiler’s typed tree.

---

## 3. Proposed architecture

### 3.1 Representing F# types as `System.Type`

* Add a new internal module (suggested location: `src/Compiler/TypedTree/TastReflection.fs`) that can project `TyconRef`, `TType`, and their members into runtime reflection objects.
  * The historical branch introduced `AssemblyReaderReflection` and `TastReflect`.  We can modernize that code to fit the current compiler and reuse the conceptual approach: implement subclasses of `System.Type`, `MethodInfo`, `PropertyInfo`, etc. that delegate queries to the typed tree.
  * Caches keyed by `Stamp` and `TyconRef` are required so that repeated projections return reference-equal objects.  This ensures provider-side caching and comparison continues to work.
  * Provide helpers to fabricate generic parameter types, generic instantiations (`MakeGenericType`), nested types, and array/pointer/byref wrappers.
  * Propagate custom attributes: field/property attributes, record/union metadata, measure annotations, obsolete attributes, etc.  The new module should leverage `AttribInfo` → `ICustomAttributeData` translation that the type provider runtime already uses.
  * Ensure the projected `System.Type` objects behave like genuine runtime types so that generative providers can embed them into emitted IL without surprises (a concern raised in discussion #125).

* Thread access to this projection service through `ImportMap` / `TcImports`.
  * Add a memo-table `ProvidedTypeReflectionCache` to `TcImportsData`.
  * Expose a method `TcImports.GetOrCreateProvidedTypeForTType : CompilationThreadToken * range * TType -> ProvidedTypeReflection`.

### 3.2 Extending static parameter evaluation

* Update `TcStaticConstantParameter` (in `src/Compiler/Checking/Expressions/CheckExpressions.fs`) to recognise when the expected static-argument kind is `g.system_Type_typ`.
  * Parse and type-check the argument expression as a type (support `SynType.LongIdent`, `SynType.AnonRecd`, generic instantiations, etc).
  * Produce a `TType` value `tyArg` (ensuring it is fully resolved and not itself provided).
  * Use the new projection module to obtain a `System.Type` proxy for `tyArg`.  Record the dependency between the provided type application and the referenced `Entity`.
  * Box the resulting `System.Type` and return it to `CrackStaticConstantArgs`.
  * For generative providers, ensure that the projected type can be re-resolved later when the provider emits IL (e.g., capture sufficient `TyconRef` identity to rebuild the proxy during static-linking).

* Extend error reporting for unsupported cases:
  * If the referenced type is itself provided or otherwise unresolved, produce a diagnostic (`etStaticParameterRequiresConcreteType`).
  * If the type’s shape cannot be projected (e.g., type providers generating types from other providers), fail with a clear message and do not crash.

### 3.3 Tracking dependencies and invalidation

* When a `System.Type` proxy is produced, register the underlying `TyconRef` with the existing provided-type invalidation mechanism (`CompilerImports.RecordGeneratedTypeRoot` and the `TypeProviderInlinedRepresentation` dependency graph).
* Extend the provided entity metadata to record “type arguments consumed as `System.Type`”.  On recompilation we should re-run the provider whenever any of those entities change.
* Update design-time tooling (FSharp.Compiler.Service) so that cross-project invalidation triggers provider refreshes when the referenced type comes from another project or script.

### 3.4 Type Provider SDK adjustments

* The SDK already accepts `ProvidedStaticParameter(parameterType = typeof<Type>)`.  No API change is necessary, but we must ensure the test suite covers the end-to-end scenario.
* Provide a sample provider (`TypePassingProvider`) that exercises the new feature and demonstrates recommended practices (e.g., defensive copying, dealing with generic types, attribute access).

### 3.5 Compiler service and tooling surface

* FCS exposes `FSharpProjectOptions.TypeProviderAssemblies`.  The projection service must be available in the service layer so that design-time checks use the same implementation as command-line compilation.
* Ensure `FSharpChecker.GetDeclarationListInfo` and other language service features handle the new provider outputs—no additional changes anticipated beyond provider invalidation.

---

## 4. Detailed data structure changes

| Area | Proposed change |
|------|-----------------|
| `TcImportsData` | Add caches for the new reflection proxies (`Dictionary<Stamp, ProvidedSymbol>`) and plumb them through the `ImportMap`. |
| `cenv` (`CheckExpressions`) | Extend the environment to carry a `TypeReflectionBuilder` handle so that `TcStaticConstantParameter` can request proxies without reaching back into global state. |
| `TProvidedTypeInfo` (in `TypeProviders.fs`) | Record the set of `TyconRef`s that were used as `System.Type` arguments when instantiating the provider.  Use this for invalidation and for debugging information. |
| `GraphChecking/FileContentMapping` | Static parameter expressions are already visited; no change beyond recognising the new literal form. |
| `CompilerDiagnostics.fs` | Add diagnostics for unsupported cases (e.g., static argument is a provided type, static argument references a type that cannot be materialised). |
| `FSharp.TypeProviders.SDK` | Add regression tests in `tests/` to verify that `ProvidedStaticParameter(typeof<Type>)` receives the fields/properties defined in the F# type. |

---

## 5. Execution flow with the new feature

1. **Parse** user code; static arguments referencing types are represented as `SynType.StaticConstantExpr (SynExpr.LongIdent ...)`.
2. **Type check**; when `TcStaticConstantParameter` sees that the expected parameter type is `System.Type`, it:
   * Resolves the `SynType` to a `TType`.
   * Requests a `System.Type` proxy from the `TypeReflectionBuilder`.
   * Records dependency information.
3. **Apply static arguments**; `CrackStaticConstantArgs` packs the `System.Type` objects into the argument array passed to the provider.
4. **Provider execution** uses reflection on the supplied type to generate new types.
5. **Generated types** flow back through the existing provided-type pipeline, with additional dependency metadata so that changes to the original F# type trigger invalidation.

---

## 6. Open questions

1. **Type identity and lifetime** – Should we expose a single proxy per `TyconRef`, or should we create fresh proxies per instantiation (e.g., different nullness or unit-of-measure instantiations)?  Recommendation: cache per `TType` including generic arguments, and ensure equality semantics mimic `System.Type`.  
   *Answer:* we cache by fully-instantiated `TType` (including measure/nullability info) in `TcImports.ProvidedTypeReflectionCache`.  Each proxy carries the `TyconRef` and instantiation so `Equals`/`GetHashCode` mirrors CLR semantics.  This matches both the prototype (`lookupTyconRef`/`lookupILTypeRef`) and the TPSDK `TargetTypeDefinition`, keeping provider caches stable.
2. **Generic type parameters** – How do we represent type parameters that originate from the consumer’s generic type definitions?  We must fabricate `RuntimeTypeHandle`-less objects that behave like open generic parameters.  The historical implementation used custom subclasses of `Type` and `MethodInfo`; we should port that design.  
   *Answer:* generic parameters reuse the TPSDK `TypeSymbol` machinery.  Each `Typar` projects to a proxy `System.Type` with `IsGenericParameter = true` and the correct `GenericParameterPosition`, and instantiations go through the existing `MakeGenericType` helper.
3. **Provided → provided** – Should a provider be allowed to pass a provided type as the `System.Type` argument to another provider?  The design discussion (fslang-design/125) highlights demand for provider chaining, but it complicates dependency tracking and debugging.  Initial implementation should likely restrict input types to concrete, non-provided entities and revisit once the foundations are stable.  
   *Answer:* the first release rejects provided inputs with a targeted diagnostic.  The dependency graph (`ProviderGeneratedType` roots) currently assumes concrete IL; extending it to cover provider chaining can be a follow-up once the base scenario is solid.
4. **Erasing vs generative providers** – Generative providers expect to emit IL consumed by other assemblies.  Can the projected `System.Type` be safely embedded in generated assemblies before the current project is emitted?  Do we need metadata-only reference images to make this work?  
   *Answer:* generative providers remain supported.  The `TastReflection` assembly proxies report the final assembly identity, and the existing static-linking map (`ProvidedAssemblyStaticLinkingMap` applied in `CheckDeclarations.fs`) rewrites IL type refs when we inline generated assemblies.  No reference-only image is needed.
5. **Metadata preservation** – Which compiler-generated features must the proxy expose?  At minimum: record/union shape APIs, property getters/setters, field mutability, attributes, module static members, interface implementations, measure annotations, and optional/default values, as requested in the design discussion.  
   *Answer:* we reuse the attribute and member translation routines already in `TypeProviders.fs`, so proxies expose the same metadata the compiler emits (record copy methods, union tags, optional parameters, measure attributes, etc.).  The TastReflect test suite (noted by @dsyme) will return to validate parity against CLR reflection.
6. **Performance** – The reflection layer must avoid blocking the compilation thread with heavy allocations.  We should measure impact on large solutions and consider lazy materialisation for expensive members (e.g., attributes) plus shared caches across providers.  
   *Answer:* proxy construction is memoised via `ConcurrentDictionary<Stamp, ProvidedSymbol>` caches.  Members/attributes are materialised lazily, matching the current TPSDK behaviour, and we will profile the TPSDK test matrix before enabling the feature.
7. **Debuggability** – Provide a straightforward way to inspect the proxies (friendly `ToString`, `DebuggerDisplay`) so provider authors can reason about the projected types.  
   *Answer:* we retain the same `DebuggerDisplay`/`ToString` helpers used by `ProvidedTypeDefinition`, so proxies print as their logical full names in the debugger.
8. **Testing generative outputs** – Add integration tests that compile downstream assemblies (e.g., C# consumers) against provider output to validate binary compatibility, especially for INPC, serialization, and proxy scenarios mentioned in the discussion.  
   *Answer:* new integration tests under `tests/fsharp/typeProviders/typePassing` will cover both erasing and generative providers, including a small C# consumer that references the generated assembly.
9. **Provider invalidation semantics** – When the input type changes shape (field order, optionality, generic arguments) how granular should invalidation be?  Providers may need fine-grained change data to avoid expensive recomputation.  
   *Answer:* each provider application records the `TyconRef` stamps it reads during static-parameter evaluation.  These feed into the existing invalidation path (`RecordGeneratedTypeRoot` plus the incremental builder), so any structural change reruns the provider.  If profiling shows we need finer granularity we can extend the recorded metadata.
10. **Limitations on input types** – Should private/internal types be allowed?  How do signature files influence visibility?  Are anonymous records, type abbreviations, or provided types legal inputs?  We need explicit rules.  
   *Answer:* any accessible concrete type (respecting signature-file visibility) is allowed.  Type abbreviations are expanded.  Anonymous records and provided types are rejected initially; users can wrap them in named records if necessary.  Metadata-defined types continue to work unchanged because we pass the actual `System.Type`.
11. **Tooling story** – IDEs must surface diagnostics and completions when provider output depends on project types.  What hooks are required in FSharp.Compiler.Service to expose the new proxy layer to tooling?  
   *Answer:* `FSharpChecker` already drives the same pipeline as the command-line compiler, so once the proxy layer lives in `TcImports` the IDE benefits automatically.  We will surface a lightweight `TypeReflectionBuilder` handle via FCS for tooling needing direct access and make sure the incremental builder invalidates design-time caches when dependent types change.

    *11 Nov 2025 update:* `TypeProviderDependencyInvalidationTests` now exercises that path end-to-end: the test spins up `FSharpChecker` with `keepAssemblyContents`, uses `TcImports.GetImportMap().ReflectTypeWithDependencies` to project `Fs1023Consumer.Model`, and verifies both the proxy `System.Type` and the reported `TyconRef` dependencies round-trip through the public service API. We also exposed `FSharpEntity.GetTypeReflectionProxy()` so tooling (and the TPSDK proxy tests) can request the TastReflection proxy directly without reaching into internal `TcImports` helpers.
12. **Design-time vs target types** – Providers interact with design-time types while the compiler emits target types. How are projected proxies translated back to the design-time view?  
   *Answer:* the new `TastReflection` proxies implement the target-type contracts.  Before handing them to the provider we run them through the existing TPSDK translation layer, which converts target representations into the design-time types the provider author expects (this mirrors the workflow described by @dsyme in the original prototype notes).
13. **Forward references / mutually recursive types** – What happens when a provider consumes a type declared later in the same recursive group?  
   *Answer:* the type-abbreviation pipeline already runs in multiple passes.  During the first pass the right-hand-side type is fully elaborated before the tycon’s abbreviation is recorded, so the projected proxy can be created immediately.  If the type is still incomplete we fall back to the existing `NewErrorType` recovery.
14. **Scripts and interactive sessions** – How does the design operate in F# Interactive where snippets are re-typechecked repeatedly?  
   *Answer:* the projection cache lives inside each checker/session (`TcImports`).  FSI creates a new checker instance per session, so proxies are rebuilt per session and safely invalidate between submissions just like other type-provider state.
15. **Cross-project invalidation** – What if the consumed type lives in another project and is only available via metadata?  
   *Answer:* static parameters are resolved through `TcImports`. If the type comes from metadata we simply reuse the existing `System.Type` from the loaded assembly; no projection or additional invalidation is required beyond the standard project-system rebuild when that upstream assembly changes.
16. **Static-linking remapper and additional references** – Can FS-1023 generated assemblies reference other types in the current project?  
   *Answer:* inlining still relies on `ProvidedAssemblyStaticLinkingMap`. Providers should continue to reference types via the `System.Type` objects they were given.  We do not expand the remapper to cover arbitrary project types; generating IL that references unrelated project members remains unsupported.
17. **Anonymous record support** – Is rejecting anonymous records a design choice or an implementation limitation?  
   *Answer:* it’s an initial limitation. Supporting anonymous records would require projecting compiler-generated symbol names.  We may add that later if real scenarios emerge; for now provider authors should use named records or type abbreviations that expand to anonymous records.
18. **Design-time translation mechanics** – The TPSDK distinguishes “design-time” and “target” types. How will the new proxies flow through that machinery?  
   *Answer:* the proxies implement the same “target model” contracts (`System.Type` plus `TryGetTyconRef`) that the SDK already understands.  We will extend `ProvidedTypes.ConvertTargetTypeToSource` (and the surrounding helpers) so when it encounters a `TastReflection` proxy it routes through the existing `TargetTypeDefinition` adapter, producing a design-time type without changing provider APIs.
19. **Reflection surface completeness** – Which reflection members must be implemented on the proxy types?  
   *Answer:* every member that CLR reflection exposes for F# declarations and that providers routinely use: constructors, methods (including generated record/union helpers), properties, events, nested types, generic parameters, `GetCustomAttributes`/`GetCustomAttributesData`, default values, and the metadata flags (`IsSealed`, `IsAbstract`, etc.).  The TastReflect parity tests from the original prototype—comparing proxy output against `typeof` for concrete assemblies—will return to guard this contract.
20. **Dependency tracking granularity** – How do we ensure we rerun providers when indirect dependencies (base types, signature files) change?  
   *Answer:* projection records every `TyconRef` visited (base types, interfaces, field types, etc.).  Those stamps are fed into the existing invalidation machinery, and the incremental builder already treats signature edits as type changes, so indirect modifications invalidate provider output automatically.
21. **Provider output referencing other project types** – How do we keep providers from emitting IL that references arbitrary project members?  
   *Answer:* the compiler enforces this today via `IsGeneratedTypeDirectReference`; if a generated assembly references a non-generated project type we report an error during static linking.  Documentation for FS-1023 will call this out so provider authors stick to the `System.Type` handles supplied via the static parameters.
22. **Proxy cache lifecycle and concurrency** – Do we need special handling when multiple checker instances run in parallel?  
   *Answer:* proxy caches live inside each `TcImports` instance and are protected by the existing `tciLock`.  IDE background builds already rely on this lock for thread safety, so FS-1023 can reuse the same mechanism; no additional locking is required.
23. **Equality semantics across translation** – Will provider authors see the expected reference equality once the proxy flows through the design-time adapter?  
   *Answer:* yes.  The TPSDK’s `ConvertTargetTypeToSource` returns the same `System.Type` instance for repeated requests, and those values compare equal to both the proxy and any direct `typeof` results for metadata types, preserving dictionary behaviour.
24. **Interop with future partial support** – How will FS-1023 interact with forthcoming partial-type work?  
   *Answer:* FS-1023 ships independently.  When partial class support arrives we simply allow providers to observe both parts via the proxy; no contract changes are required now, but we’ve noted this dependency so the later work reuses the same projection infrastructure.
25. **Diagnostics for FS-1023 constraints** – What experience do developers get when constraints are violated?  
   *Answer:* existing diagnostics such as `etErasedTypeUsedInGeneration` cover most cases, and we plan to add a targeted error (with source range) when a provider emits IL referencing non-generated project types.  This keeps feedback actionable during compilation.

---

## 7. Next steps

1. Prototype the `TypeReflectionBuilder` in isolation using the current typed-tree APIs.
2. Extend `TcStaticConstantParameter` to emit stub `System.Type` proxies and add unit tests that ensure the provider receives the expected field/property metadata.
3. Integrate dependency tracking so that editing the source type re-runs the provider.
4. Round out the proxy to cover generics, nested types, attributes, and arrays.
5. Land end-to-end tests in both the compiler (`tests/fsharp/typeProviders`) and the Type Provider SDK.

Once this foundation is in place, we can proceed towards integrating partial-type support and, eventually, Roslyn source generator interop as described in the broader plan.

---

## 8. Risk assessment

- **Cache divergence** — If proxy caches fall out of sync with the typed tree, providers could observe stale metadata or crash during static-linking. *Mitigation:* scope caches to a checker lifetime, key by `Stamp`, and invalidate through the existing incremental builder hooks exercised in Phase 2.
- **Performance regressions** — Large providers may project thousands of types per compilation. *Mitigation:* measure allocations with ETW/perfview on the TPSDK test matrix, keep projections lazy, and gate the feature behind an `langVersion` flag until profiling shows acceptable overhead.
- **Incomplete metadata fidelity** — Missing attributes or member flags would break providers that emit IL mirroring the source type. *Mitigation:* extend TastReflection unit tests to cover every construct listed in §6.5 and block merge unless parity is proven on CI.
- **Error handling gaps** — Improper diagnostics for unsupported types would give users confusing failures. *Mitigation:* add targeted errors (`etStaticParameterRequiresConcreteType`, etc.) with integration tests exercising anonymous-record and provided-type inputs.
- **Cross-repository drift** — TPSDK and compiler changes must ship in lockstep; mismatches could strand users on incompatible package versions. *Mitigation:* stage changes behind feature flags, publish preview NuGets, and document the minimum compiler/SDK pairing in release notes.

## 9. Validation metrics

- 100% of new TastReflection unit tests covering records, unions, generics, attributes, and nested types must pass on Windows, macOS, and Linux.
- End-to-end provider scenarios in `tests/fsharp/typeProviders/typePassing` must show zero delta in compilation time >5% compared to baseline projects.
- IDE smoke tests (VS, Ionide) must complete without additional provider reloads beyond those triggered by deliberate edits to dependent types.
- At least two partner providers (one generative, one erasing) should confirm compatibility via experimental packages before general availability.

---

## References

* [FS-1023 RFC — Allow type providers to generate types from types](https://raw.githubusercontent.com/fsharp/fslang-design/refs/heads/main/RFCs/FS-1023-type-providers-generate-types-from-types.md)
* [FS-1023 approved suggestion](https://github.com/fsharp/fslang-suggestions/issues/212)
* [fslang-design discussion #125 — Allow type providers to generate types from other types](https://github.com/fsharp/fslang-design/discussions/125)
* Historical prototype: [colinbull/visualfsharp `rfc/fs-1023-type-providers`](https://github.com/colinbull/visualfsharp/tree/rfc/fs-1023-type-providers)
* Related source-generator threads:
  * [fsharp/fslang-suggestions #864 — Support C# source generators](https://github.com/fsharp/fslang-suggestions/issues/864)
  * [dotnet/fsharp #14300 — Consumption of .NET libraries built with source generators](https://github.com/dotnet/fsharp/issues/14300)
* Roslyn documentation (future interop):
  * [Source generators overview](https://github.com/dotnet/roslyn/blob/main/docs/features/source-generators.md)
  * [Incremental generators](https://github.com/dotnet/roslyn/blob/main/docs/features/incremental-generators.md)
* Supporting resources:
  * [F# Type Provider SDK](https://github.com/fsprojects/FSharp.TypeProviders.SDK)
  * [System.Text.Json source generation docs](https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/source-generation)
  * [Myriad metaprogramming discussion / commentary](https://github.com/fsharp/fslang-suggestions/issues/864#issuecomment-758969586)

---

_Prepared on branch `fs-1023` to guide modern implementation work for FS-1023._
