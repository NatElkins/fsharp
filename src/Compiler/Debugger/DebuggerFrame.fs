// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

namespace FSharp.Compiler.Debugger

open System
open System.Collections.Generic
open Internal.Utilities
open Internal.Utilities.Library
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AbstractIL.ILBinaryReader
open FSharp.Compiler.AbstractIL.ILBinaryWriter
open FSharp.Compiler.AbstractIL.ILPdbWriter
open FSharp.Compiler.AbstractIL.Morphs

/// Where a frame variable lives while query IL runs in place of the frame method.
[<RequireQualifiedAccess>]
type internal VariableStorage =
    /// `ldarg index`: `this` (argument 0 of instance methods) or a declared parameter.
    | Argument of index: int
    /// `ldloc slot`: a local of the frame method that the PDB places in scope.
    | LocalSlot of slot: int
    /// `ldarg.0; ldfld`: an instance field of `this`. F# stores captured variables of closures
    /// and `let` bindings of classes this way, so this is what makes them visible to the query.
    | ThisField of ILFieldSpec

/// A variable the query may read, with its IL type re-scoped to the debuggee assembly.
type internal FrameVariable =
    {
        Name: string
        Type: ILType
        Storage: VariableStorage
    }

/// The frame method as read from the debuggee's module, with every type re-scoped so that a
/// separate assembly can reference it.
type internal FrameShape =
    {
        /// `this` first for instance methods, then the declared parameters.
        Arguments: ILParameter list
        /// The frame method's local signature, in slot order.
        Locals: ILLocal list
        /// The declaring type as a boxed or value type (never a byref) for field access; ValueNone for static frames.
        ThisType: ILType voption
        /// Types enclosing the declaring type, outermost first; the outermost name carries the namespace.
        Enclosing: ILTypeDef list
        DeclaringType: ILTypeDef
        /// The debuggee assembly the frame method lives in.
        AssemblyRef: ILAssemblyRef
    }

module internal DebuggerFrame =

    let readerOptions =
        {
            pdbDirPath = None
            reduceMemoryUsage = ReduceMemoryFlag.Yes
            metadataOnly = MetadataOnlyFlag.No
            tryGetMetadataSnapshot = fun _ -> None
        }

    let queryTypeName = "<>x"

    let rec private typeDefsWithEnclosing (enclosing: ILTypeDef list) (tdefs: ILTypeDefs) =
        seq {
            for tdef in tdefs.AsArray() do
                yield struct (enclosing, tdef)
                yield! typeDefsWithEnclosing (enclosing @ [ tdef ]) tdef.NestedTypes
        }

    /// Finds the method with the given metadata token together with the types enclosing its declaring type.
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

    let private compilationMappingFlags (attrs: ILAttributes) =
        attrs.AsArray()
        |> Array.tryPick (fun attr ->
            let mspec =
                match attr with
                | ILAttribute.Encoded(mspec, _, _)
                | ILAttribute.Decoded(mspec, _, _) -> mspec

            if mspec.DeclaringType.TypeRef.FullName = "Microsoft.FSharp.Core.CompilationMappingAttribute" then
                match decodeILAttribData attr with
                | ILAttribElem.Int32 flags :: _, _ -> Some flags
                | _ -> None
            else
                None)

    /// F# modules compile to static classes marked CompilationMapping(SourceConstructFlags.Module).
    let isFSharpModule (tdef: ILTypeDef) =
        match compilationMappingFlags tdef.CustomAttrs with
        | Some flags -> (flags &&& 31) = 7
        | None -> false

    let frameShape (asmRef: ILAssemblyRef) (enclosing: ILTypeDef list) (tdef: ILTypeDef) (mdef: ILMethodDef) =
        if not (List.isEmpty tdef.GenericParams) || not (List.isEmpty mdef.GenericParams) then
            Error "The F# expression evaluator does not support generic methods or methods of generic types yet."
        else
            let scope = ILScopeRef.Assembly asmRef

            let thisType =
                if mdef.IsStatic then
                    ValueNone
                else
                    let tref =
                        match enclosing with
                        | [] -> mkILTyRef (scope, tdef.Name)
                        | _ -> mkILNestedTyRef (scope, enclosing |> List.map (fun t -> t.Name), tdef.Name)

                    ValueSome(
                        if tdef.IsStructOrEnum then
                            mkILNonGenericValueTy tref
                        else
                            mkILNonGenericBoxedTy tref
                    )

            let thisArgs =
                match thisType with
                | ValueNone -> []
                | ValueSome thisTy ->
                    let argTy = if tdef.IsStructOrEnum then ILType.Byref thisTy else thisTy
                    [ mkILParamNamed ("this", argTy) ]

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
                    ThisType = thisType
                    Enclosing = enclosing
                    DeclaringType = tdef
                    AssemblyRef = asmRef
                }

    /// `open` paths that bring the frame's namespace and enclosing F# modules into scope.
    let openPaths (shape: FrameShape) =
        let chain = shape.Enclosing @ [ shape.DeclaringType ]
        let outermostName = chain.Head.Name

        let namespaceOpen =
            match outermostName.LastIndexOf '.' with
            | -1 -> []
            | i -> [ outermostName.Substring(0, i) ]

        let moduleOpens =
            chain
            |> List.mapi (fun i tdef -> i, tdef)
            |> List.filter (fun (_, tdef) -> isFSharpModule tdef)
            |> List.map (fun (i, _) -> String.Join(".", chain |> List.take (i + 1) |> List.map (fun t -> t.Name)))

        namespaceOpen @ moduleOpens

    /// Every variable the query may read: arguments, then PDB locals (the innermost of a name wins),
    /// then instance fields of `this` that no argument or local shadows.
    let frameVariables (shape: FrameShape) (localsInScope: (string * int) list) =
        let taken = HashSet<string>(StringComparer.Ordinal)

        let arguments =
            shape.Arguments
            |> List.mapi (fun i (p: ILParameter) ->
                let name =
                    match p.Name with
                    | Some n -> n
                    | None -> $"arg{i}"

                taken.Add name |> ignore

                {
                    Name = name
                    Type = p.Type
                    Storage = VariableStorage.Argument i
                })

        let locals =
            localsInScope
            |> List.rev
            |> List.choose (fun (name, slot) ->
                if slot >= 0 && slot < shape.Locals.Length && taken.Add name then
                    Some
                        {
                            Name = name
                            Type = shape.Locals[slot].Type
                            Storage = VariableStorage.LocalSlot slot
                        }
                else
                    None)
            |> List.rev

        let fields =
            match shape.ThisType with
            | ValueNone -> []
            | ValueSome thisTy ->
                shape.DeclaringType.Fields.AsList()
                |> List.choose (fun (f: ILFieldDef) ->
                    if f.IsStatic || not (taken.Add f.Name) then
                        None
                    else
                        let fieldTy = rescopeType shape.AssemblyRef f.FieldType

                        Some
                            {
                                Name = f.Name
                                Type = fieldTy
                                Storage = VariableStorage.ThisField(mkILFieldSpecInTy (thisTy, f.Name, fieldTy))
                            })

        arguments @ locals @ fields

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

    let loadVariable (variable: FrameVariable) =
        match variable.Storage with
        | VariableStorage.Argument i -> [ I_ldarg(uint16 i) ]
        | VariableStorage.LocalSlot s -> [ I_ldloc(uint16 s) ]
        | VariableStorage.ThisField f -> [ I_ldarg 0us; I_ldfld(ILAlignment.Aligned, ILVolatility.Nonvolatile, f) ]

    let variableAccessor (shape: FrameShape) (variable: FrameVariable) (name: string) =
        let ty, deref = derefIfByref variable.Type
        mkQueryMethod name shape ty (Array.ofList (loadVariable variable @ deref @ [ I_ret ]))

    /// Rewrites the checker's `<>m0`, whose parameters are the source-level variables in order,
    /// into the frame's shape: variable reads become the frame's `ldarg`/`ldloc`/`ldfld`, the
    /// method's own locals move behind the frame's local slots, and the parameter list becomes
    /// the frame's arguments.
    let rewriteQueryMethod (shape: FrameShape) (sourceParameters: FrameVariable[]) (mdef: ILMethodDef) =
        let frameLocalCount = uint16 shape.Locals.Length
        let storageOf (i: uint16) = sourceParameters[int i].Storage

        let mapInstr instr =
            match instr with
            | I_ldarg i ->
                match storageOf i with
                | VariableStorage.Argument j -> [ I_ldarg(uint16 j) ]
                | VariableStorage.LocalSlot s -> [ I_ldloc(uint16 s) ]
                | VariableStorage.ThisField f -> [ I_ldarg 0us; I_ldfld(ILAlignment.Aligned, ILVolatility.Nonvolatile, f) ]
            | I_ldarga i ->
                match storageOf i with
                | VariableStorage.Argument j -> [ I_ldarga(uint16 j) ]
                | VariableStorage.LocalSlot s -> [ I_ldloca(uint16 s) ]
                | VariableStorage.ThisField f -> [ I_ldarg 0us; I_ldflda f ]
            | I_starg i ->
                match storageOf i with
                | VariableStorage.Argument j -> [ I_starg(uint16 j) ]
                | VariableStorage.LocalSlot s -> [ I_stloc(uint16 s) ]
                | VariableStorage.ThisField _ -> invalidOp "Assigning to a captured variable or field is not supported yet."
            | I_ldloc j -> [ I_ldloc(j + frameLocalCount) ]
            | I_stloc j -> [ I_stloc(j + frameLocalCount) ]
            | I_ldloca j -> [ I_ldloca(j + frameLocalCount) ]
            | other -> [ other ]

        let body = mdef.MethodBody

        let code =
            { morphILInstrsInILCode mapInstr body.Code with
                Locals = []
            }

        let body =
            { body with
                Locals = shape.Locals @ body.Locals
                Code = code
                MaxStack = body.MaxStack + 2
            }

        mdef.With(parameters = shape.Arguments, body = notlazy (MethodBody.IL(notlazy body)))

    /// The runtime honors System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute when the
    /// assembly declares the attribute type itself. Applied for every debuggee assembly, it lets
    /// query IL read non-public fields and call non-public members, as the C# evaluator's queries do.
    let private ignoresAccessChecksTo (ilg: ILGlobals) (assemblyNames: string list) =
        let tref =
            mkILTyRef (ILScopeRef.Local, "System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute")

        let attributeTy = mkILNonGenericBoxedTy tref
        let fieldSpec = mkILFieldSpecInTy (attributeTy, "assemblyName", ilg.typ_String)

        let ctorBody =
            mkMethodBody (
                false,
                [],
                8,
                mkCode
                    [|
                        I_ldarg 0us
                        mkNormalCall (mkILCtorMethSpecForTy (ilg.typ_Attribute, []))
                        I_ldarg 0us
                        I_ldarg 1us
                        I_stfld(ILAlignment.Aligned, ILVolatility.Nonvolatile, fieldSpec)
                        I_ret
                    |],
                None,
                None
            )

        let ctor =
            mkILCtor (ILMemberAccess.Public, [ mkILParamNamed ("assemblyName", ilg.typ_String) ], ctorBody)

        let attributeType =
            mkILGenericClass (
                tref.Name,
                ILTypeDefAccess.Public,
                [],
                ilg.typ_Attribute,
                [],
                mkILMethods [ ctor ],
                mkILFields
                    [
                        mkILInstanceField ("assemblyName", ilg.typ_String, None, ILMemberAccess.Private)
                    ],
                mkILTypeDefs [],
                emptyILProperties,
                emptyILEvents,
                emptyILCustomAttrs,
                ILTypeInit.BeforeField
            )

        let attributes =
            assemblyNames
            |> List.map (fun name ->
                mkILCustomAttribMethRef (mkILCtorMethSpecForTy (attributeTy, [ ilg.typ_String ]), [ ILAttribElem.String(Some name) ], []))

        attributeType, attributes

    /// Declares IgnoresAccessChecksToAttribute in the module and applies it for each assembly name.
    let withIgnoredAccessChecks (ilg: ILGlobals) (assemblyNames: string list) (modul: ILModuleDef) =
        let attributeType, attributes = ignoresAccessChecksTo ilg assemblyNames

        let manifest =
            match modul.Manifest with
            | Some manifest ->
                { manifest with
                    CustomAttrsStored = storeILCustomAttrs (mkILCustomAttrs (manifest.CustomAttrs.AsList() @ attributes))
                }
            | None -> invalidOp "The query module has no assembly manifest."

        { modul with
            TypeDefs = mkILTypeDefs (modul.TypeDefs.AsList() @ [ attributeType ])
            Manifest = Some manifest
        }

    let writeQueryAssembly
        (ilg: ILGlobals)
        (metadataVersion: string)
        (assemblyName: string)
        (accessTo: string list)
        (methods: ILMethodDef list)
        =
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
            |> withIgnoredAccessChecks ilg accessTo

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
