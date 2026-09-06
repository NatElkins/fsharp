// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

namespace FSharp.Compiler.Debugger

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open Internal.Utilities
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILBinaryReader
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.AbstractIL.ILPdbWriter
open FSharp.Compiler.AbstractIL.Morphs

[<Experimental("This FCS API is experimental and subject to change.")>]
type FSharpDebuggerLocal = { Name: string; Slot: int }

[<Experimental("This FCS API is experimental and subject to change.")>]
type FSharpDebuggerFrame =
    {
        ModulePath: string
        MethodToken: int
        ILOffset: int
        LocalsInScope: FSharpDebuggerLocal list
    }

[<Experimental("This FCS API is experimental and subject to change.")>]
type FSharpDebuggerLocalEntry =
    {
        Name: string
        MethodName: string
        IsArgument: bool
    }

[<Experimental("This FCS API is experimental and subject to change.")>]
type FSharpDebuggerLocalsQuery =
    {
        Assembly: byte[]
        TypeName: string
        Locals: FSharpDebuggerLocalEntry list
    }

[<Experimental("This FCS API is experimental and subject to change.")>]
type FSharpDebuggerCompiledQuery =
    {
        Assembly: byte[]
        TypeName: string
        MethodName: string
        ResultIsBool: bool
        HasSideEffects: bool
    }

/// The frame method as read from the debuggee's module, with every type re-scoped so that a
/// separate assembly can reference it.
type internal FrameShape =
    {
        /// `this` first for instance methods, then the declared parameters.
        Arguments: ILParameter list
        /// The frame method's local signature, in slot order.
        Locals: ILLocal list
    }

module internal DebuggerIL =

    let readerOptions =
        {
            pdbDirPath = None
            reduceMemoryUsage = ReduceMemoryFlag.Yes
            metadataOnly = MetadataOnlyFlag.No
            tryGetMetadataSnapshot = fun _ -> None
        }

    let queryTypeName = "<>x"

    let rec private typeDefsWithEnclosing (enclosing: string list) (tdefs: ILTypeDefs) =
        seq {
            for tdef in tdefs.AsArray() do
                yield struct (enclosing, tdef)
                yield! typeDefsWithEnclosing (enclosing @ [ tdef.Name ]) tdef.NestedTypes
        }

    /// Finds the method with the given metadata token together with the names of the types
    /// enclosing its declaring type.
    let tryFindMethod (modul: ILModuleDef) (token: int) =
        let rowId = token &&& 0x00FFFFFF
        let mutable found = ValueNone
        use types = (typeDefsWithEnclosing [] modul.TypeDefs).GetEnumerator()

        while found.IsNone && types.MoveNext() do
            let struct (enclosing, tdef) = types.Current

            for mdef in tdef.Methods.AsArray() do
                if found.IsNone && mdef.MetadataIndex = rowId then
                    found <- ValueSome(struct (enclosing, tdef, mdef))

        found

    /// Types the debuggee module defines read back as `ILScopeRef.Local`; the query assembly
    /// must reference them through the module's own assembly instead.
    let rescopeType (asmRef: ILAssemblyRef) (ty: ILType) =
        let rescopeRef scope =
            match scope with
            | ILScopeRef.Local
            | ILScopeRef.Module _ -> ILScopeRef.Assembly asmRef
            | other -> other

        morphILTypeRefsInILType (morphILScopeRefsInILTypeRef rescopeRef) ty

    let frameShape (asmRef: ILAssemblyRef) (enclosing: string list) (tdef: ILTypeDef) (mdef: ILMethodDef) =
        if not (List.isEmpty tdef.GenericParams) || not (List.isEmpty mdef.GenericParams) then
            Error "The F# expression evaluator does not support generic methods or methods of generic types yet."
        else
            let scope = ILScopeRef.Assembly asmRef

            let thisArgs =
                if mdef.IsStatic then
                    []
                else
                    let tref =
                        match enclosing with
                        | [] -> mkILTyRef (scope, tdef.Name)
                        | _ -> mkILNestedTyRef (scope, enclosing, tdef.Name)

                    let thisTy =
                        if tdef.IsStructOrEnum then
                            ILType.Byref(mkILNonGenericValueTy tref)
                        else
                            mkILNonGenericBoxedTy tref

                    [ mkILParamNamed ("this", thisTy) ]

            let declaredArgs =
                mdef.Parameters
                |> List.mapi (fun i (p: ILParameter) ->
                    let name =
                        match p.Name with
                        | Some n -> n
                        | None -> $"arg{i}"

                    mkILParamNamed (name, rescopeType asmRef p.Type))

            let locals =
                match mdef.Body with
                | MethodBody.IL body ->
                    body.Value.Locals
                    |> List.map (fun (l: ILLocal) ->
                        { l with
                            Type = rescopeType asmRef l.Type
                        })
                | _ -> []

            Ok
                {
                    Arguments = thisArgs @ declaredArgs
                    Locals = locals
                }

    let mkCode (instrs: ILInstr[]) : ILCode =
        {
            Labels = Dictionary<ILCodeLabel, int>()
            Instrs = instrs
            Exceptions = []
            Locals = []
        }

    /// A static method whose parameters and local signature mirror the frame, so the debugger
    /// can execute it in place of the frame method.
    let mkQueryMethod (name: string) (shape: FrameShape) (returnTy: ILType) (instrs: ILInstr[]) =
        let body = mkMethodBody (false, shape.Locals, 8, mkCode instrs, None, None)
        mkILNonGenericStaticMethod (name, ILMemberAccess.Public, shape.Arguments, mkILReturn returnTy, body)

    /// Byref arguments and struct `this` are managed pointers; read the value they point to.
    let derefIfByref (ty: ILType) =
        match ty with
        | ILType.Byref elemTy -> elemTy, [ I_ldobj(ILAlignment.Aligned, ILVolatility.Nonvolatile, elemTy) ]
        | _ -> ty, []

    let argumentAccessor (shape: FrameShape) (index: int) (name: string) =
        let ty, deref = derefIfByref shape.Arguments[index].Type
        mkQueryMethod name shape ty (Array.ofList (I_ldarg(uint16 index) :: deref @ [ I_ret ]))

    let localAccessor (shape: FrameShape) (slot: int) (name: string) =
        let ty, deref = derefIfByref shape.Locals[slot].Type
        mkQueryMethod name shape ty (Array.ofList (I_ldloc(uint16 slot) :: deref @ [ I_ret ]))

    let writeQueryAssembly (ilg: ILGlobals) (metadataVersion: string) (assemblyName: string) (methods: ILMethodDef list) =
        let queryType =
            mkILSimpleClass
                ilg
                (queryTypeName,
                 ILTypeDefAccess.Public,
                 mkILMethods methods,
                 emptyILFields,
                 mkILTypeDefs [],
                 emptyILProperties,
                 emptyILEvents,
                 emptyILCustomAttrs,
                 ILTypeInit.BeforeField)

        let typeDefs =
            mkILTypeDefs [ mkILTypeDefForGlobalFunctions ilg (emptyILMethods, emptyILFields); queryType ]

        let fileName = assemblyName + ".dll"

        let modul =
            mkILSimpleModule assemblyName fileName true (4, 0) true typeDefs None None 0 (mkILExportedTypes []) metadataVersion

        let writerOptions: options =
            {
                ilg = ilg
                outfile = fileName
                pdbfile = None
                portablePDB = false
                embeddedPDB = false
                embedAllSource = false
                embedSourceList = []
                allGivenSources = []
                sourceLink = ""
                checksumAlgorithm = HashAlgorithm.Sha256
                signer = None
                emitTailcalls = false
                deterministic = true
                dumpDebugInfo = false
                referenceAssemblyOnly = false
                referenceAssemblyAttribOpt = None
                referenceAssemblySignatureHash = None
                pathMap = PathMap.empty
                moduleCustomDebugInfoRows = []
                methodCustomDebugInfoRows = Map.empty
            }

        let bytes, _pdb = WriteILBinaryInMemory(writerOptions, modul, id)
        bytes

[<Experimental("This FCS API is experimental and subject to change."); Sealed>]
type FSharpDebuggerExpressionCompiler(runtimeModulePath: string, referencePaths: seq<string>) =

    let referencePaths = List.ofSeq referencePaths
    let readers = Dictionary<string, ILModuleReader>(StringComparer.OrdinalIgnoreCase)
    let mutable queryCount = 0

    let moduleOf (path: string) =
        match readers.TryGetValue path with
        | true, reader -> reader.ILModuleDef
        | _ ->
            let reader = OpenILModuleReader path DebuggerIL.readerOptions
            readers[path] <- reader
            reader.ILModuleDef

    let runtimeModule = lazy (moduleOf runtimeModulePath)

    let ilg =
        lazy
            (let primary =
                ILScopeRef.Assembly(mkRefToILAssembly runtimeModule.Value.ManifestOfAssembly)

             let fsharpCore =
                 referencePaths
                 |> List.tryFind (fun p -> String.Equals(Path.GetFileName p, "FSharp.Core.dll", StringComparison.OrdinalIgnoreCase))
                 |> Option.map (fun p -> ILScopeRef.Assembly(mkRefToILAssembly (moduleOf p).ManifestOfAssembly))
                 |> Option.defaultValue primary

             mkILGlobals (primary, [], fsharpCore))

    let nextAssemblyName () =
        queryCount <- queryCount + 1
        $"FSharpDebuggerQuery{queryCount}"

    let frameContext (frame: FSharpDebuggerFrame) =
        let modul = moduleOf frame.ModulePath

        match DebuggerIL.tryFindMethod modul frame.MethodToken with
        | ValueNone -> Error $"Method 0x%08X{frame.MethodToken} was not found in '{frame.ModulePath}'."
        | ValueSome(struct (enclosing, tdef, mdef)) ->
            DebuggerIL.frameShape (mkRefToILAssembly modul.ManifestOfAssembly) enclosing tdef mdef

    member _.CompileLocalsQuery(frame: FSharpDebuggerFrame, argumentsOnly: bool) =
        frameContext frame
        |> Result.map (fun shape ->
            let accessors = ResizeArray<ILMethodDef>()
            let entries = ResizeArray<FSharpDebuggerLocalEntry>()

            let add name isArgument (mkAccessor: string -> ILMethodDef) =
                let methodName = $"<>m{accessors.Count}"
                accessors.Add(mkAccessor methodName)

                entries.Add
                    {
                        Name = name
                        MethodName = methodName
                        IsArgument = isArgument
                    }

            shape.Arguments
            |> List.iteri (fun i arg ->
                let name =
                    match arg.Name with
                    | Some n -> n
                    | None -> $"arg{i}"

                add name true (DebuggerIL.argumentAccessor shape i))

            if not argumentsOnly then
                for local in frame.LocalsInScope do
                    if local.Slot >= 0 && local.Slot < shape.Locals.Length then
                        add local.Name false (DebuggerIL.localAccessor shape local.Slot)

            let bytes =
                DebuggerIL.writeQueryAssembly ilg.Value runtimeModule.Value.MetadataVersion (nextAssemblyName ()) (List.ofSeq accessors)

            {
                Assembly = bytes
                TypeName = DebuggerIL.queryTypeName
                Locals = List.ofSeq entries
            })

    member _.CompileExpression(frame: FSharpDebuggerFrame, expression: string) =
        frameContext frame
        |> Result.bind (fun shape ->
            match Int32.TryParse(expression.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, value ->
                let methodName = "<>m0"

                let query =
                    DebuggerIL.mkQueryMethod methodName shape ilg.Value.typ_Int32 [| AI_ldc(DT_I4, ILConst.I4 value); I_ret |]

                let bytes =
                    DebuggerIL.writeQueryAssembly ilg.Value runtimeModule.Value.MetadataVersion (nextAssemblyName ()) [ query ]

                Ok
                    {
                        Assembly = bytes
                        TypeName = DebuggerIL.queryTypeName
                        MethodName = methodName
                        ResultIsBool = false
                        HasSideEffects = false
                    }
            | _ -> Error "The F# expression evaluator prototype evaluates integer literals only so far.")

    interface IDisposable with
        member _.Dispose() =
            for reader in readers.Values do
                reader.Dispose()

            readers.Clear()
