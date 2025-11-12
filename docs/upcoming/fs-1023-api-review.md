# FS-1023 API Review Checklist

Date: 2025-11-12
Reviewer: FS-1023 implementation crew

## Public surface touched in this branch

| API | Location | Notes |
| --- | --- | --- |
| `FSharpEntity.GetTypeReflectionProxy: unit -> System.Type` | `src/Compiler/Symbols/Symbols.fs[i]` | Marked `[<Experimental("FS-1023 preview API. Subject to change.")>]`; only available when type providers are enabled. |
| `TcImports.GetTypeReflectionBuilder: unit -> TypeReflectionBuilder` | `src/Compiler/Driver/CompilerImports.fs[i]` | Marked `[<Experimental("FS-1023 preview API. Subject to change.")>]`; exposed solely under `#if !NO_TYPEPROVIDERS`. |

## Verification steps
- Searched for other new public members in `src/Compiler` and `src/FSharp.Core` (no additional API surface was added).
- Confirmed experimental attributes compile (FCS builds) and propagate through IntelliSense, keeping the preview contract clear for tooling consumers.
- `docs/upcoming/fs-1023.md` and the TPSDK release notes already warn that FS-1023 APIs are preview-only and require matching compiler/SDK versions.

## Outstanding actions
- When we flip FS-1023 to GA, remove the experimental annotations and update both docs + release notes accordingly.
