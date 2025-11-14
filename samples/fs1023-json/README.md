# FS-1023 JSON serializer sample

This sample shows how to use the FS-1023 compiler build from this repo to consume a type provider in a normal console application.

## Layout

```
Provider/JsonSerializerProvider.fsproj   -- simple generative provider that mirrors the regression fixture
Consumer/JsonConsumer.fsproj            -- console app that references the provider and uses the generated OrderJson type
Directory.Build.props                   -- pulls in UseLocalCompiler.Directory.Build.props so the projects can opt into the local toolchain
```

## Prerequisites

1. Build the compiler + TPSDK from this branch:

   ```bash
   ./build.sh -c Release compiler
   ./build.sh -c Release tpsdk
   ```

2. Point the sample at the compiler you just built by setting `FS1023CompilerDir` to this repo’s root before running any `dotnet` commands:

   ```bash
   export FS1023CompilerDir=/Users/nat/Projects/source_generation_consumption/fsharp
   ```

   (If the variable is unset the projects fall back to the NuGet `FSharp.Core` package and the in-box compiler.)

## Build & run

From the repo root:

```bash
# Build the provider first
dotnet build samples/fs1023-json/Provider/JsonSerializerProvider.fsproj -c Release

# Build + run the console app with the same compiler
dotnet run samples/fs1023-json/Consumer/JsonConsumer.fsproj -c Release
```

If you’d like to rely on the in-box SDK instead (for example on a clean machine) just skip the `FS1023CompilerDir` environment variable. The projects will restore the NuGet `FSharp.Core` package and use the default F# compiler while keeping the same source.
