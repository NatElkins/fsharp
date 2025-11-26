# CLAUDE.md - F# Hot Reload Implementation

## Issue Resolution Workflow

When working through the `HOT_RELOAD_REVIEW_CHECKLIST.md`:

1. **Read the issue** - Understand the problem and affected files
2. **Make the fix** - Edit the relevant code
3. **Validate the change** - ALWAYS do one of:
   - Run `dotnet build src/Compiler/FSharp.Compiler.Service.fsproj` to verify compilation
   - Run relevant tests: `dotnet test tests/FSharp.Compiler.ComponentTests/FSharp.Compiler.ComponentTests.fsproj --filter "FullyQualifiedName~HotReload"`
   - For critical changes, do both
4. **Update the checklist** - Mark the item as `[x]` with `✅ FIXED` notation
5. **Commit the fix** - After tests pass, create a commit with a detailed message:
   - Reference the issue from the checklist (e.g., "Session 4: CustomAttribute parent encoding")
   - Describe what was wrong and how it was fixed
   - Note any ECMA-335 references if applicable
   - Example: `fix(hot-reload): implement all 21 HasCustomAttribute parent types per ECMA-335 II.24.2.6`
6. **Move to next issue** - Continue top-to-bottom through the checklist

## Build Commands

```bash
# Quick build (compiler only)
dotnet build src/Compiler/FSharp.Compiler.Service.fsproj --no-restore

# Full build
dotnet build FSharp.sln

# Run hot reload tests
dotnet test tests/FSharp.Compiler.ComponentTests/FSharp.Compiler.ComponentTests.fsproj --filter "FullyQualifiedName~HotReload"

# Run specific test file
dotnet test tests/FSharp.Compiler.ComponentTests/FSharp.Compiler.ComponentTests.fsproj --filter "FullyQualifiedName~MdvValidationTests"
```

## Key Files

- `HOT_RELOAD_REVIEW_CHECKLIST.md` - Master checklist of all issues (61 total)
- `src/Compiler/CodeGen/` - Delta emission, metadata serialization
- `src/Compiler/HotReload/` - Session management, state
- `src/Compiler/TypedTree/` - Semantic diff, definition map
- `tests/FSharp.Compiler.ComponentTests/HotReload/` - Component tests
- `tests/FSharp.Compiler.Service.Tests/HotReload/` - Service tests

## Priority Order

1. **Critical (6 issues)** - Merge blockers, must fix
2. **High (18 issues)** - Should fix before merge
3. **Medium (22 issues)** - Post-merge acceptable
4. **Low (15 issues)** - Technical debt

## Reporting Test Results Accurately

**Never say "all tests pass" if any tests fail.** Even if failures seem unrelated or infrastructure-caused, report the actual numbers:

- Bad: "130 HotReload service tests: All pass (4 fail due to infrastructure)"
- Good: "130 pass, 4 fail (infrastructure - missing fsi.dll from clean build)"

If failures are infrastructure-related, investigate or note them separately, but don't claim success when there are failures.

## Rebuilding After Clean Builds

After deleting `artifacts/bin` or `artifacts/obj` (e.g., during git bisect), some tests may fail with missing file errors like:
```
Couldn't find "fsi/Debug/net10.0/fsi.dll"
Couldn't find "fsc/Debug/net10.0/fsc.dll"
```

Rebuild these infrastructure projects:
```bash
dotnet build src/fsi/fsiProject/fsi.fsproj
dotnet build src/fsc/fscProject/fsc.fsproj
```

## Debugging Unexpected Test Failures

If a test fails unexpectedly (especially one that was previously passing):

1. **Use git bisect** to find which commit introduced the regression:
   ```bash
   git bisect start
   git bisect bad HEAD
   git bisect good <known-good-commit>
   # At each step: clean build, run test, mark good/bad
   ```

2. **Always do clean builds** when bisecting to avoid stale artifacts:
   ```bash
   rm -rf artifacts/bin artifacts/obj
   dotnet build tests/FSharp.Compiler.ComponentTests/FSharp.Compiler.ComponentTests.fsproj
   dotnet test ... --no-build
   ```

3. **Check the last 5-10 commits** - regressions are often recent

4. Once found, **diff the breaking commit** to understand the root cause

## Current Progress

Track progress in `HOT_RELOAD_REVIEW_CHECKLIST.md` by checking off completed items.
