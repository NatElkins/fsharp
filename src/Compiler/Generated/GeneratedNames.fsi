module internal FSharp.Compiler.GeneratedNames

open FSharp.Compiler.Text

type MethodGeneratedNameInfo =
    { MethodName: string
      MethodOrdinal: int
      MethodGeneration: int }

type EntityGeneratedNameInfo =
    { EntityOrdinal: int
      EntityGeneration: int }

val makeCompilerGeneratedValueName: baseName: string -> MethodGeneratedNameInfo option -> EntityGeneratedNameInfo option -> string
val makeStateMachineTypeName: methodInfo: MethodGeneratedNameInfo -> string
val makeLambdaClosureTypeName: methodInfo: MethodGeneratedNameInfo -> entityInfo: EntityGeneratedNameInfo option -> string
val makeLambdaMethodName: methodInfo: MethodGeneratedNameInfo -> entityInfo: EntityGeneratedNameInfo option -> string
val makeStaticFieldName: baseName: string -> ordinal: int -> string
val makeLocalValueName: baseName: string -> range -> string
val makeHotReloadName: baseName: string -> ordinal: int -> string
