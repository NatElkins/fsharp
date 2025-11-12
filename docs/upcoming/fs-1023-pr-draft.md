# FS-1023 PR Draft

## Summary
- Enable generative type providers to accept `System.Type` static arguments and emit relocatable IL generated directly by the compiler (IlxGen + `ProvidedMemberBinding`).
- TastReflection / TypeReflectionBuilder proxies expose provider metadata without calling back into the provider assembly; consumers (FCS, reflection, relocation) now read from the cached binding data.
- New public helpers: `FSharpEntity.GetTypeReflectionProxy()` and `TcImports.GetTypeReflectionBuilder()` are surfaced for tooling and marked `[<Experimental("FS-1023 preview API. Subject to change.")>]`.
- Feature remains behind `--langversion:preview` + matching TPSDK snapshot; docs and TPSDK release notes document the required compiler/SDK pairing.

## Architecture highlights
1. **Type projection:** `ImportMap.ReflectTypeWithDependencies` + `TypeReflectionBuilder` capture every `TyconRef` touched so incremental builds invalidate consumers. (See `ARCHITECTURE_PROPOSAL.md`, Phase 1.)
2. **Binding capture:** `ProvidedMemberBindingHelpers` register invoker quotations, metadata, and definition locations for methods/ctors/properties; `TcTyconDefnCore_Phase1C` publishes the bindings into `tcaug_adhoc_list`.
3. **IL emission:** `GenProvidedTypeDef` consumes those bindings, emits IL methods/properties/ctors/events, and registers generated tycons so static linking avoids provider IL.
4. **Tooling APIs:** `FSharpEntity.GetTypeReflectionProxy` + `TcImports.GetTypeReflectionBuilder` let FCS/SDK callers obtain TastReflection proxies; proxy conversion is mirrored in `FSharp.TypeProviders.SDK` tests.
5. **Docs/rollout:** `docs/upcoming/fs-1023.md` + TPSDK `RELEASE_NOTES.md` capture version pairing, authoring checklist, and rollout guidance.

## Validation evidence
| Area | Command |
| --- | --- |
| Component suite | `dotnet test tests/FSharp.Compiler.ComponentTests/FSharp.Compiler.ComponentTests.fsproj -c Release` |
| Service suite | `dotnet test tests/FSharp.Compiler.Service.Tests/FSharp.Compiler.Service.Tests.fsproj -c Release` |
| FsSuite TypeProvider slice | `dotnet test tests/FSharp/FSharpSuite.Tests.fsproj -c Release --filter "FullyQualifiedName~TypeProviderTests"` |
| Full build + test leg | `./build.sh --testcoreclr -c Release /p:WarningsNotAsErrors=FS0066;FS3261;FS0760;FS0026` |

All suites pass (nullability warnings tracked separately).

## Outstanding items before merge
- Final API surface review (confirm only the intentional experimental APIs are exposed).
- Partner dogfooding + telemetry hooks (Phase 8).
- Prepare final changelog entries when we cut the preview TPSDK package.

## Risk / mitigation snapshot
- **Regression risk:** mitigated via the above test coverage + relocation tests (`TypeProviderDependencyInvalidationTests` reflect over `/standalone` outputs).
- **Preview surface:** APIs marked experimental; docs state compiler/SDK pairing and preview flag requirements.
