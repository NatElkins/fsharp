
module internal FSharp.Compiler.TastReflect

#nowarn "40"
#nowarn "3261"

open System
open System.IO
open System.Collections.Generic
open System.Collections.Concurrent
open System.Diagnostics
open System.Globalization
open System.Reflection
open System.Runtime.Serialization
open System.Threading
open FSharp.Compiler
open FSharp.Compiler.CompilerGlobalState
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.AbstractIL.IL
open Internal.Utilities.Library
open Internal.Utilities.Collections
open FSharp.Compiler.TypedTree
open FSharp.Compiler.TypedTreeOps
open FSharp.Compiler.TypedTreeBasics
open FSharp.Compiler.TcGlobals
open FSharp.Compiler.Text
open FSharp.Compiler.Text.Range
open FSharp.Compiler.Syntax
#if !NO_TYPEPROVIDERS
open FSharp.Compiler.TypeProviders
#endif

type private TyparScope =
    { ByStamp: Dictionary<Stamp, Type>
      ByKey: Dictionary<string, Type> }


[<AutoOpen>]
module Utils = 
    let nullToOption x = match x with null -> None | _ -> Some x
    let optionToNull x = match x with None -> null | Some x -> x

    let notRequired msg =
       failwith (sprintf "SHOULD NOT BE REQUIRED! %s. Stack trace:\n%s" msg (System.Diagnostics.StackTrace().ToString()))

    // A table tracking how wrapped type definition objects are translated to cloned objects.
    // Unique wrapped type definition objects must be translated to unique wrapper objects, based 
    // on object identity.
    type TxTable<'T2>() = 
        let tab = Dictionary<Stamp, 'T2>()
        member __.Get inp f = 
            if tab.ContainsKey inp then 
                tab.[inp] 
            else 
                let res = f() 
                tab.[inp] <- res
                res

        member __.ContainsKey inp = tab.ContainsKey inp 
        member __.Values = tab.Values

    let lengthsEqAndForall2 (arr1: 'T1[]) (arr2: 'T2[]) f = 
        (arr1.Length = arr2.Length) &&
        (arr1,arr2) ||> Array.forall2 f

    // Instantiate a type's generic parameters
    let rec instType inst (ty:Type) = 
        if ty.IsGenericType then 
            let args = Array.map (instType inst) (ty.GetGenericArguments())
            ty.GetGenericTypeDefinition().MakeGenericType(args)
        elif ty.HasElementType then 
            let ety = instType inst (ty.GetElementType()) 
            if ty.IsArray then 
                let rank = ty.GetArrayRank()
                if rank = 1 then ety.MakeArrayType()
                else ety.MakeArrayType(rank)
            elif ty.IsPointer then ety.MakePointerType()
            elif ty.IsByRef then ety.MakeByRefType()
            else ty
        elif ty.IsGenericParameter then 
            let pos = ty.GenericParameterPosition
            let (inst1: Type[], inst2: Type[]) = inst 
            if pos < inst1.Length then inst1.[pos]
            elif pos < inst1.Length + inst2.Length then inst2.[pos - inst1.Length]
            else ty
        else ty

    let instParameterInfo inst (inp: ParameterInfo) = 
        { new ParameterInfo() with 
            override __.Name = inp.Name 
            override __.ParameterType = inp.ParameterType |> instType inst
            override __.Attributes = inp.Attributes
            override __.RawDefaultValue = inp.RawDefaultValue
            override __.GetCustomAttributesData() = inp.GetCustomAttributesData()
            override x.ToString() = inp.ToString() + "@inst" }

    let rec eqType (ty1:Type) (ty2:Type) = 
        if ty1.IsGenericType then ty2.IsGenericType && lengthsEqAndForall2 (ty1.GetGenericArguments()) (ty2.GetGenericArguments()) eqType
        elif ty1.IsArray then ty2.IsArray && ty1.GetArrayRank() = ty2.GetArrayRank() && eqType (ty1.GetElementType()) (ty2.GetElementType()) 
        elif ty1.IsPointer then ty2.IsPointer && eqType (ty1.GetElementType()) (ty2.GetElementType()) 
        elif ty1.IsByRef then ty2.IsByRef && eqType (ty1.GetElementType()) (ty2.GetElementType()) 
        else ty1.Equals(box ty2)

    let ilFieldInitToObject init =
        match init with
        | ILFieldInit.String s -> box s
        | ILFieldInit.Bool bool -> box bool
        | ILFieldInit.Char u16 -> box (char (int u16))
        | ILFieldInit.Int8 i8 -> box i8
        | ILFieldInit.Int16 i16 -> box i16
        | ILFieldInit.Int32 i32 -> box i32
        | ILFieldInit.Int64 i64 -> box i64
        | ILFieldInit.UInt8 u8 -> box u8
        | ILFieldInit.UInt16 u16 -> box u16
        | ILFieldInit.UInt32 u32 -> box u32
        | ILFieldInit.UInt64 u64 -> box u64
        | ILFieldInit.Single ieee32 -> box ieee32
        | ILFieldInit.Double ieee64 -> box ieee64
        | ILFieldInit.Null -> null

    let constToObject (cnst: Const) =
        match cnst with
        | Const.Bool b -> box b
        | Const.Byte b -> box b
        | Const.Char c -> box c
        | Const.Decimal d -> box d
        | Const.Double d -> box d
        | Const.Int16 i -> box i
        | Const.Int32 i -> box i
        | Const.Int64 i -> box i
        | Const.IntPtr i -> box i
        | Const.SByte sb -> box sb
        | Const.Single f -> box f
        | Const.String s -> box s
        | Const.UInt16 i -> box i
        | Const.UInt32 i -> box i
        | Const.UInt64 i -> box i
        | Const.UIntPtr i -> box i
        | Const.Unit -> box ()
        | Const.Zero -> box 0

#if !NO_TYPEPROVIDERS
    module ProvidedReflectionHelpers =
        open System.Reflection

        let emptyCustomAttributesData : IList<CustomAttributeData> =
            Array.empty<CustomAttributeData> :> IList<CustomAttributeData>

        let private providedParameterAttributes (param: ProvidedParameterInfo) =
            let mutable attrs = enum<ParameterAttributes>(0)
            if param.IsIn then attrs <- attrs ||| ParameterAttributes.In
            if param.IsOut then attrs <- attrs ||| ParameterAttributes.Out
            if param.IsOptional then attrs <- attrs ||| ParameterAttributes.Optional
            if param.HasDefaultValue then attrs <- attrs ||| ParameterAttributes.HasDefault
            attrs

        let private providedParameterType (paramType: ProvidedType MaybeNull) =
            if obj.ReferenceEquals(paramType, null) then
                typeof<obj>
            else
                paramType.RawSystemType

        let private providedDefaultValue (param: ProvidedParameterInfo) =
            if param.HasDefaultValue then
                param.RawDefaultValue
            else
                Type.Missing

        let makeProvidedParameterInfo (memberGetter: unit -> MemberInfo) position (param: ProvidedParameterInfo) =
            let parameterType = providedParameterType param.ParameterType
            let name =
                let rawName = param.Name
                if String.IsNullOrEmpty rawName then
                    sprintf "arg%d" (position + 1)
                else
                    rawName
            let defaultValue = providedDefaultValue param
            let attrs = providedParameterAttributes param
            { new ParameterInfo() with
                override _.Member = memberGetter()
                override _.Name = name
                override _.ParameterType = parameterType
                override _.Attributes = attrs
                override _.Position = position
                override _.RawDefaultValue = defaultValue
                override _.DefaultValue = defaultValue
                override _.HasDefaultValue = param.HasDefaultValue
                override _.GetCustomAttributesData() = emptyCustomAttributesData
                override _.GetCustomAttributes(_inherit) = notRequired "Provided parameter GetCustomAttributes"
                override _.GetCustomAttributes(_attributeType, _inherit) = notRequired "Provided parameter GetCustomAttributesTyped"
                override _.IsDefined(_attributeType, _inherit) = false
                override _.ToString() = sprintf "%s %s" parameterType.Name name }

        let makeProvidedReturnParameter (memberGetter: unit -> MemberInfo) (param: ProvidedParameterInfo) =
            let parameterType = providedParameterType param.ParameterType
            let defaultValue = providedDefaultValue param
            let attrs = providedParameterAttributes param
            let name =
                let rawName = param.Name
                if String.IsNullOrEmpty rawName then "return" else rawName
            { new ParameterInfo() with
                override _.Member = memberGetter()
                override _.Name = name
                override _.ParameterType = parameterType
                override _.Attributes = attrs
                override _.Position = -1
                override _.RawDefaultValue = defaultValue
                override _.DefaultValue = defaultValue
                override _.HasDefaultValue = param.HasDefaultValue
                override _.GetCustomAttributesData() = emptyCustomAttributesData
                override _.GetCustomAttributes(_inherit) = notRequired "Provided return parameter GetCustomAttributes"
                override _.GetCustomAttributes(_attributeType, _inherit) = notRequired "Provided return parameter GetCustomAttributesTyped"
                override _.IsDefined(_attributeType, _inherit) = false
                override _.ToString() = sprintf "%s %s" parameterType.Name name }

        let tryGetProvidedParameters (binding: ProvidedMemberBinding) =
            binding.Parameters
            |> Option.map (fun parameters ->
                if obj.ReferenceEquals(parameters, null) then
                    [||]
                else
                    Unchecked.unbox<ProvidedParameterInfo[]>(parameters))
#endif

    let invokeMemberCore (self: Type) name (invokeAttr: BindingFlags) (binder: Binder) target (args: obj[]) (modifiers: ParameterModifier[]) (culture: CultureInfo) (namedParameters: string[]) =
        ignore namedParameters
        let bindingFlags = invokeAttr
        let hasFlag flag = bindingFlags &&& flag = flag
        let argsArray =
            match args with
            | null -> [||]
            | arr -> arr
        let modifiersArray =
            match modifiers with
            | null -> [||]
            | mods -> mods
        let argTypes =
            argsArray
            |> Array.map (fun arg ->
                if isNull arg || Object.ReferenceEquals(arg, Type.Missing) then typeof<obj>
                else arg.GetType())

        let missingMember () =
            raise (MissingMemberException(sprintf "Member '%s' not found on '%s'." name self.FullName))

        if hasFlag BindingFlags.CreateInstance then
            let ctor = self.GetConstructor(bindingFlags, binder, CallingConventions.Any, argTypes, modifiersArray)
            if isNull ctor then missingMember()
            else ctor.Invoke(argsArray)

        elif hasFlag BindingFlags.GetField then
            let field = self.GetField(name, bindingFlags)
            if isNull field then missingMember()
            else field.GetValue(target)

        elif hasFlag BindingFlags.SetField then
            if argsArray.Length = 0 then missingMember()
            else
                let field = self.GetField(name, bindingFlags)
                if isNull field then missingMember()
                else
                    field.SetValue(target, argsArray.[0], bindingFlags, binder, culture)
                    null

        elif hasFlag BindingFlags.GetProperty then
            let expectedIndexCount = argsArray.Length
            let property =
                self.GetProperties(bindingFlags)
                |> Array.tryFind (fun p ->
                    String.Equals(p.Name, name, StringComparison.Ordinal)
                    && p.GetIndexParameters().Length = expectedIndexCount)
                |> Option.defaultWith (fun () -> self.GetProperty(name, bindingFlags))
            if isNull property then missingMember()
            else property.GetValue(target, bindingFlags, binder, argsArray, culture)

        elif hasFlag BindingFlags.SetProperty then
            if argsArray.Length = 0 then missingMember()
            else
                let value = argsArray.[argsArray.Length - 1]
                let indices = argsArray |> Array.take (argsArray.Length - 1)
                let expectedIndexCount = indices.Length
                let property =
                    self.GetProperties(bindingFlags)
                    |> Array.tryFind (fun p ->
                        String.Equals(p.Name, name, StringComparison.Ordinal)
                        && p.GetIndexParameters().Length = expectedIndexCount)
                    |> Option.defaultWith (fun () -> self.GetProperty(name, bindingFlags))
                if isNull property then missingMember()
                else
                    property.SetValue(target, value, bindingFlags, binder, indices, culture)
                    null

        elif hasFlag BindingFlags.InvokeMethod then
            let typeArgs =
                if argTypes.Length = 0 then Type.EmptyTypes
                else argTypes

            let method =
                match self.GetMethod(name, bindingFlags, binder, CallingConventions.Any, typeArgs, modifiersArray) with
                | null ->
                    let candidates =
                        self.GetMethods(bindingFlags)
                        |> Array.filter (fun m -> String.Equals(m.Name, name, StringComparison.Ordinal))
                    if candidates.Length = 0 then null
                    elif isNull binder then null
                    else
                        match binder.SelectMethod(bindingFlags, candidates |> Array.map (fun m -> m :> MethodBase), typeArgs, modifiersArray) with
                        | :? MethodInfo as mi -> mi
                        | _ -> null
                | m -> m

            if isNull method then missingMember()
            else method.Invoke(target, bindingFlags, binder, argsArray, culture)

        else
            missingMember ()

    //let hashILParameterTypes (ps: ILParameters) = 
    //   // This hash code doesn't need to be very good as hashing by name is sufficient to give decent hash granularity
    //   ps.Length 

    //let eqAssemblyAndCcu (_ass1: Assembly) (_sco2: ILScopeRef) = 
    //    true // TODO (though omitting this is not a problem in practice since type equivalence by name is sufficient to bind methods)


    //let rec eqTypeAndTyconRef (ty1: Type) (ty2: ILTypeRef) = 
    //    ty1.Name = ty2.Name && 
    //    ty1.Namespace = (uoptionToNull ty2.Namespace) &&
    //    match ty2.Scope with 
    //    | ILTypeRefScope.Top scoref2 -> eqAssemblyAndCcu ty1.Assembly scoref2
    //    | ILTypeRefScope.Nested tref2 -> ty1.IsNested && eqTypeAndTyconRef ty1.DeclaringType tref2

    //let rec eqTypesAndTTypes (tys1: Type[]) (tys2: ILType[]) = 
    //    eqTypesAndTTypesWithInst [| |] tys1 tys2 

    //and eqTypesAndTTypesWithInst inst2 (tys1: Type[]) (tys2: ILType[]) = 
    //    lengthsEqAndForall2 tys1 tys2 (eqTypeAndTTypeWithInst inst2)

    //and eqTypeAndTTypeWithInst inst2 (ty1: Type) (ty2: ILType) = 
    //    match ty2 with 
    //    | (ILType.Value(tspec2) | ILType.Boxed(tspec2))->
    //        if tspec2.GenericArgs.Length > 0 then 
    //            ty1.IsGenericType && eqTypeAndTyconRef (ty1.GetGenericTypeDefinition()) tspec2.TypeRef && eqTypesAndTTypesWithInst inst2 (ty1.GetGenericArguments()) tspec2.GenericArgs
    //        else 
    //            not ty1.IsGenericType && eqTypeAndTyconRef ty1 tspec2.TypeRef
    //    | ILType.Array(rank2, arg2) ->
    //        ty1.IsArray && ty1.GetArrayRank() = rank2.Rank && eqTypeAndTTypeWithInst inst2 (ty1.GetElementType()) arg2
    //    | ILType.Ptr(arg2) -> 
    //        ty1.IsPointer && eqTypeAndTTypeWithInst inst2 (ty1.GetElementType()) arg2
    //    | ILType.Byref(arg2) ->
    //        ty1.IsByRef && eqTypeAndTTypeWithInst inst2 (ty1.GetElementType()) arg2
    //    | ILType.Var(arg2) ->
    //        if int arg2 < inst2.Length then 
    //             eqType ty1 inst2.[int arg2]  
    //        else
    //             ty1.IsGenericParameter && ty1.GenericParameterPosition = int arg2
                
    //    | _ -> false

    //let eqParametersAndILParameterTypesWithInst inst2 (ps1: ParameterInfo[])  (ps2: ILParameters) = 
    //    lengthsEqAndForall2 ps1 ps2 (fun p1 p2 -> eqTypeAndTTypeWithInst inst2 p1.ParameterType p2.ParameterType)

    //let adjustTypeAttributes isNested attributes = 
    //    let visibilityAttributes = 
    //        match attributes &&& TypeAttributes.VisibilityMask with 
    //        | TypeAttributes.Public when isNested -> TypeAttributes.NestedPublic
    //        | TypeAttributes.NotPublic when isNested -> TypeAttributes.NestedAssembly
    //        | TypeAttributes.NestedPublic when not isNested -> TypeAttributes.Public
    //        | TypeAttributes.NestedAssembly 
    //        | TypeAttributes.NestedPrivate 
    //        | TypeAttributes.NestedFamORAssem
    //        | TypeAttributes.NestedFamily
    //        | TypeAttributes.NestedFamANDAssem when not isNested -> TypeAttributes.NotPublic
    //        | a -> a
    //    (attributes &&& ~~~TypeAttributes.VisibilityMask) ||| visibilityAttributes



    //let convFieldInit x = 
    //    match x with 
    //    | ILFieldInit.String s       -> box s
    //    | ILFieldInit.Bool bool      -> box bool   
    //    | ILFieldInit.Char u16       -> box (char (int u16))  
    //    | ILFieldInit.Int8 i8        -> box i8     
    //    | ILFieldInit.Int16 i16      -> box i16    
    //    | ILFieldInit.Int32 i32      -> box i32    
    //    | ILFieldInit.Int64 i64      -> box i64    
    //    | ILFieldInit.UInt8 u8       -> box u8     
    //    | ILFieldInit.UInt16 u16     -> box u16    
    //    | ILFieldInit.UInt32 u32     -> box u32    
    //    | ILFieldInit.UInt64 u64     -> box u64    
    //    | ILFieldInit.Single ieee32 -> box ieee32 
    //    | ILFieldInit.Double ieee64 -> box ieee64 
    //    | ILFieldInit.Null            -> (null :> Object)

/// Represents the type constructor in a provided symbol type.
[<RequireQualifiedAccess>]
type ReflectTypeSymbolKind = 
    | SDArray 
    | Array of int 
    | Pointer 
    | ByRef 
    | Generic of ReflectTypeDefinition


/// Represents an array or other symbolic type involving a provided type as the argument.
/// See the type provider spec for the methods that must be implemented.
/// Note that the type provider specification does not require us to implement pointer-equality for provided types.
and [<DebuggerDisplay("{FullName}")>] ReflectTypeSymbol(kind: ReflectTypeSymbolKind, args: Type[]) =
    inherit Type()

    let notRequired msg = 
        System.Diagnostics.Debugger.Break()
        failwith ("not required: " + msg)

    override __.FullName =   
        match kind,args with 
        | ReflectTypeSymbolKind.SDArray,[| arg |] -> arg.FullName + "[]" 
        | ReflectTypeSymbolKind.Array _,[| arg |] -> arg.FullName + "[*]" 
        | ReflectTypeSymbolKind.Pointer,[| arg |] -> arg.FullName + "*" 
        | ReflectTypeSymbolKind.ByRef,[| arg |] -> arg.FullName + "&"
        | ReflectTypeSymbolKind.Generic gtd, args -> gtd.FullName + "[" + (args |> Array.map (fun arg -> arg.FullName) |> String.concat ",") + "]"
        | _ -> failwith "unreachable"

    override __.DeclaringType =                                                                 
        match kind,args with 
        | ReflectTypeSymbolKind.SDArray,[| arg |] 
        | ReflectTypeSymbolKind.Array _,[| arg |] 
        | ReflectTypeSymbolKind.Pointer,[| arg |] 
        | ReflectTypeSymbolKind.ByRef,[| arg |] -> arg.DeclaringType
        | ReflectTypeSymbolKind.Generic gtd,_ -> gtd.DeclaringType
        | _ -> failwith "unreachable"

    override __.IsAssignableFrom(otherTy) = 
        match kind with
        | ReflectTypeSymbolKind.Generic gtd ->
            if otherTy.IsGenericType then
                let otherGtd = otherTy.GetGenericTypeDefinition()
                let otherArgs = otherTy.GetGenericArguments()
                let yes = gtd.Equals(otherGtd) && Seq.forall2 eqType args otherArgs
                yes
            else
                base.IsAssignableFrom(otherTy)
        | _ -> base.IsAssignableFrom(otherTy)

    override this.IsSubclassOf(otherTy) = 
        base.IsSubclassOf(otherTy) ||
        match kind with
        | ReflectTypeSymbolKind.Generic gtd -> 
            let md : TyconRef = gtd.Metadata
            md.IsFSharpDelegateTycon && (otherTy = typeof<Delegate>) // F# quotations implementation
        | _ -> false

    override __.Name =
        match kind,args with 
        | ReflectTypeSymbolKind.SDArray,[| arg |] -> arg.Name + "[]" 
        | ReflectTypeSymbolKind.Array _,[| arg |] -> arg.Name + "[*]" 
        | ReflectTypeSymbolKind.Pointer,[| arg |] -> arg.Name + "*" 
        | ReflectTypeSymbolKind.ByRef,[| arg |] -> arg.Name + "&"
        | ReflectTypeSymbolKind.Generic gtd, _args -> gtd.Name 
        | _ -> failwith "unreachable"

    override __.BaseType =
        match kind with 
        | ReflectTypeSymbolKind.SDArray -> typeof<System.Array>
        | ReflectTypeSymbolKind.Array _ -> typeof<System.Array>
        | ReflectTypeSymbolKind.Pointer -> typeof<System.ValueType>
        | ReflectTypeSymbolKind.ByRef -> typeof<System.ValueType>
        | ReflectTypeSymbolKind.Generic gtd  -> 
            if gtd.BaseType = null
            then null 
            else instType (args, [| |]) gtd.BaseType
        
    override this.Assembly = 
        match kind, args with 
        | ReflectTypeSymbolKind.SDArray,[| arg |] 
        | ReflectTypeSymbolKind.Array _,[| arg |] 
        | ReflectTypeSymbolKind.Pointer,[| arg |] 
        | ReflectTypeSymbolKind.ByRef,[| arg |] -> arg.Assembly
        | ReflectTypeSymbolKind.Generic gtd, _ -> gtd.Assembly
        | _ -> notRequired "Assembly" this.Name

    override this.Namespace = 
        match kind, args with 
        | ReflectTypeSymbolKind.SDArray,[| arg |] 
        | ReflectTypeSymbolKind.Array _,[| arg |] 
        | ReflectTypeSymbolKind.Pointer,[| arg |] 
        | ReflectTypeSymbolKind.ByRef,[| arg |] -> arg.Namespace
        | ReflectTypeSymbolKind.Generic gtd, _ -> gtd.Namespace 
        | _ -> failwith "unreachable"

    override __.GetArrayRank() = (match kind with ReflectTypeSymbolKind.Array n -> n | ReflectTypeSymbolKind.SDArray -> 1 | _ -> invalidOp "non-array type")
    override __.IsValueTypeImpl() = (match kind with ReflectTypeSymbolKind.Generic gtd -> gtd.IsValueType | _ -> false)
    override __.IsArrayImpl() = (match kind with ReflectTypeSymbolKind.Array _ | ReflectTypeSymbolKind.SDArray -> true | _ -> false)
    override __.IsByRefImpl() = (match kind with ReflectTypeSymbolKind.ByRef -> true | _ -> false)
    override __.IsPointerImpl() = (match kind with ReflectTypeSymbolKind.Pointer -> true | _ -> false)
    override __.IsPrimitiveImpl() = false
    override __.IsGenericType = (match kind with ReflectTypeSymbolKind.Generic _ -> true | _ -> false)
    override __.GetGenericArguments() = (match kind with ReflectTypeSymbolKind.Generic _ -> args | _ -> [| |])
    override __.GetGenericTypeDefinition() = (match kind with ReflectTypeSymbolKind.Generic e -> (e :> Type) | _ -> invalidOp "non-generic type")
    override __.IsCOMObjectImpl() = false
    override __.HasElementTypeImpl() = (match kind with ReflectTypeSymbolKind.Generic _ -> false | _ -> true)
    override __.GetElementType() = (match kind,args with (ReflectTypeSymbolKind.Array _  | ReflectTypeSymbolKind.SDArray | ReflectTypeSymbolKind.ByRef | ReflectTypeSymbolKind.Pointer),[| e |] -> e | _ -> invalidOp (sprintf "%+A, %+A: not an array, pointer or byref type" kind args))

    override this.Module : Module =
        match kind, args with
        | ReflectTypeSymbolKind.SDArray, [| arg |]
        | ReflectTypeSymbolKind.Array _, [| arg |]
        | ReflectTypeSymbolKind.Pointer, [| arg |]
        | ReflectTypeSymbolKind.ByRef, [| arg |] -> arg.Module
        | ReflectTypeSymbolKind.Generic gtd, _ -> gtd.Module
        | _ -> notRequired "Module" this.Name

    override this.GetHashCode()                                                                    = 
        match kind,args with 
        | ReflectTypeSymbolKind.SDArray,[| arg |] -> 10 + hash arg
        | ReflectTypeSymbolKind.Array _,[| arg |] -> 163 + hash arg
        | ReflectTypeSymbolKind.Pointer,[| arg |] -> 283 + hash arg
        | ReflectTypeSymbolKind.ByRef,[| arg |] -> 43904 + hash arg
        | ReflectTypeSymbolKind.Generic gtd,_ -> 9797 + hash gtd + Array.sumBy hash args
        | _ -> failwith "unreachable"
    
    override this.Equals(other: obj) =
        match other with
        | :? ReflectTypeSymbol as otherTy -> (kind, args) = (otherTy.Kind, otherTy.Args)
        | _ -> false

    member this.Kind = kind
    member this.Args = args
    
    override this.GetConstructors _bindingAttr =
        match kind,args with 
        | ReflectTypeSymbolKind.SDArray,[| arg |] 
        | ReflectTypeSymbolKind.Array _,[| arg |] 
        | ReflectTypeSymbolKind.Pointer,[| arg |]         
        | ReflectTypeSymbolKind.ByRef,[| arg |] -> arg.GetConstructors(_bindingAttr)
        | ReflectTypeSymbolKind.Generic gtd, _ -> gtd.GetConstructors(_bindingAttr)
        | _ -> failwith "unreachable"

    override this.GetMethodImpl(name, bindingAttr, binder, _callConvention, types, modifiers) =
        let allMethods =
            this.GetMethods(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance ||| BindingFlags.Static)

        let matchesName (m: MethodInfo) =
            String.Equals(m.Name, name, StringComparison.Ordinal)

        let hasFlag flag = bindingAttr &&& flag = flag

        let includePublic = hasFlag BindingFlags.Public
        let includeNonPublic = hasFlag BindingFlags.NonPublic
        let includeStatic = hasFlag BindingFlags.Static
        let includeInstance = hasFlag BindingFlags.Instance

        let matchesVisibility (m: MethodInfo) =
            if includePublic && not includeNonPublic then m.IsPublic
            elif includeNonPublic && not includePublic then not m.IsPublic
            elif includePublic || includeNonPublic then true
            else m.IsPublic

        let matchesScope (m: MethodInfo) =
            if includeStatic && not includeInstance then m.IsStatic
            elif includeInstance && not includeStatic then not m.IsStatic
            elif includeStatic || includeInstance then true
            else true

        let matchesParameters (m: MethodInfo) =
            match types with
            | null
            | [||] -> true
            | expected ->
                let parameters = m.GetParameters()
                parameters.Length = expected.Length &&
                Array.forall2 (fun (paramInfo: ParameterInfo) (expectedType: Type) -> eqType paramInfo.ParameterType expectedType) parameters expected

        let candidates =
            allMethods
            |> Array.filter (fun m -> matchesName m && matchesVisibility m && matchesScope m && matchesParameters m)

        match candidates with
        | [||] -> null
        | [|single|] -> single
        | many when not (isNull binder) ->
            match binder.SelectMethod(bindingAttr, many |> Array.map (fun m -> m :> MethodBase), types, modifiers) with
            | :? MethodInfo as mi -> mi
            | _ -> null
        | many -> many.[0]

    override this.GetConstructorImpl(bindingAttr, binder, _callConvention, types, modifiers) =
        let allConstructors =
            this.GetConstructors(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance ||| BindingFlags.Static)

        let hasFlag flag = bindingAttr &&& flag = flag
        let includePublic = hasFlag BindingFlags.Public
        let includeNonPublic = hasFlag BindingFlags.NonPublic
        let includeStatic = hasFlag BindingFlags.Static
        let includeInstance = hasFlag BindingFlags.Instance

        let matchesVisibility (c: ConstructorInfo) =
            if includePublic && not includeNonPublic then c.IsPublic
            elif includeNonPublic && not includePublic then not c.IsPublic
            elif includePublic || includeNonPublic then true
            else c.IsPublic

        let matchesScope (c: ConstructorInfo) =
            if includeStatic && not includeInstance then c.IsStatic
            elif includeInstance && not includeStatic then not c.IsStatic
            elif includeStatic || includeInstance then true
            else true

        let matchesParameters (c: ConstructorInfo) =
            match types with
            | null
            | [||] -> true
            | expected ->
                let parameters = c.GetParameters()
                parameters.Length = expected.Length &&
                Array.forall2 (fun (paramInfo: ParameterInfo) (expectedType: Type) -> eqType paramInfo.ParameterType expectedType) parameters expected

        let candidates =
            allConstructors
            |> Array.filter (fun c -> matchesVisibility c && matchesScope c && matchesParameters c)

        match candidates with
        | [||] -> null
        | [|single|] -> single
        | many when not (isNull binder) ->
            match binder.SelectMethod(bindingAttr, many |> Array.map (fun c -> c :> MethodBase), types, modifiers) with
            | :? ConstructorInfo as ctor -> ctor
            | _ -> null
        | many -> many.[0]

    override this.AssemblyQualifiedName                                                            = "[" + this.Assembly.FullName + "]" + this.FullName

    override this.GetMembers _bindingAttr =
        match kind,args with 
        | ReflectTypeSymbolKind.SDArray,[| arg |] 
        | ReflectTypeSymbolKind.Array _,[| arg |] 
        | ReflectTypeSymbolKind.Pointer,[| arg |]         
        | ReflectTypeSymbolKind.ByRef,[| arg |] -> arg.GetMembers(_bindingAttr)
        | ReflectTypeSymbolKind.Generic gtd, _ -> gtd.GetMembers(_bindingAttr)
        | _ -> failwith "unreachable"

    override this.GetMethods _bindingAttr =
        match kind,args with 
        | ReflectTypeSymbolKind.SDArray,[| arg |] 
        | ReflectTypeSymbolKind.Array _,[| arg |] 
        | ReflectTypeSymbolKind.Pointer,[| arg |]         
        | ReflectTypeSymbolKind.ByRef,[| arg |] -> arg.GetMethods(_bindingAttr)
        | ReflectTypeSymbolKind.Generic gtd, _ -> gtd.GetMethods(_bindingAttr)
        | _ -> failwith "unreachable"

    override this.GetField(_name, _bindingAttr) =
        match kind,args with 
        | ReflectTypeSymbolKind.SDArray,[| arg |] 
        | ReflectTypeSymbolKind.Array _,[| arg |] 
        | ReflectTypeSymbolKind.Pointer,[| arg |]         
        | ReflectTypeSymbolKind.ByRef,[| arg |] -> arg.GetField(_name,_bindingAttr)
        | ReflectTypeSymbolKind.Generic gtd, _ -> gtd.GetField(_name,_bindingAttr)
        | _ -> failwith "unreachable"

    override this.GetFields _bindingAttr =
        match kind,args with 
        | ReflectTypeSymbolKind.SDArray,[| arg |] 
        | ReflectTypeSymbolKind.Array _,[| arg |] 
        | ReflectTypeSymbolKind.Pointer,[| arg |]         
        | ReflectTypeSymbolKind.ByRef,[| arg |] -> arg.GetFields(_bindingAttr)
        | ReflectTypeSymbolKind.Generic gtd, _ -> gtd.GetFields(_bindingAttr)
        | _ -> failwith "unreachable"

    override this.GetInterface(_name, _ignoreCase) =
        match kind,args with 
        | ReflectTypeSymbolKind.SDArray,[| arg |] 
        | ReflectTypeSymbolKind.Array _,[| arg |] 
        | ReflectTypeSymbolKind.Pointer,[| arg |]         
        | ReflectTypeSymbolKind.ByRef,[| arg |] -> arg.GetInterface(_name, _ignoreCase)
        | ReflectTypeSymbolKind.Generic gtd, _ -> gtd.GetInterface(_name, _ignoreCase)
        | _ -> failwith "unreachable"

    override this.GetInterfaces() =
        match kind,args with 
        | ReflectTypeSymbolKind.SDArray,[| arg |] 
        | ReflectTypeSymbolKind.Array _,[| arg |] 
        | ReflectTypeSymbolKind.Pointer,[| arg |]         
        | ReflectTypeSymbolKind.ByRef,[| arg |] -> arg.GetInterfaces()
        | ReflectTypeSymbolKind.Generic gtd, _ -> gtd.GetInterfaces()
        | _ -> failwith "unreachable"

    override this.GetEvent(_name, _bindingAttr) =
        match kind,args with 
        | ReflectTypeSymbolKind.SDArray,[| arg |] 
        | ReflectTypeSymbolKind.Array _,[| arg |] 
        | ReflectTypeSymbolKind.Pointer,[| arg |]         
        | ReflectTypeSymbolKind.ByRef,[| arg |] -> arg.GetEvent(_name, _bindingAttr)
        | ReflectTypeSymbolKind.Generic gtd, _ -> gtd.GetEvent(_name, _bindingAttr)
        | _ -> failwith "unreachable"

    override this.GetEvents _bindingAttr =
        match kind,args with 
        | ReflectTypeSymbolKind.SDArray,[| arg |] 
        | ReflectTypeSymbolKind.Array _,[| arg |] 
        | ReflectTypeSymbolKind.Pointer,[| arg |]         
        | ReflectTypeSymbolKind.ByRef,[| arg |] -> arg.GetEvents(_bindingAttr)
        | ReflectTypeSymbolKind.Generic gtd, _ -> gtd.GetEvents(_bindingAttr)
        | _ -> failwith "unreachable"

    override this.GetProperties _bindingAttr =
        match kind,args with 
        | ReflectTypeSymbolKind.SDArray,[| arg |] 
        | ReflectTypeSymbolKind.Array _,[| arg |] 
        | ReflectTypeSymbolKind.Pointer,[| arg |]         
        | ReflectTypeSymbolKind.ByRef,[| arg |] -> arg.GetProperties(_bindingAttr)
        | ReflectTypeSymbolKind.Generic gtd, _ -> gtd.GetProperties(_bindingAttr)
        | _ -> failwith "unreachable"

    override this.GetPropertyImpl(name, bindingAttr, _binder, _returnType, _types, _modifiers) =
        match kind, args with
        | ReflectTypeSymbolKind.SDArray, [| arg |]
        | ReflectTypeSymbolKind.Array _, [| arg |]
        | ReflectTypeSymbolKind.Pointer, [| arg |]
        | ReflectTypeSymbolKind.ByRef, [| arg |] -> arg.GetProperty(name, bindingAttr)
        | ReflectTypeSymbolKind.Generic gtd, _ -> gtd.GetProperty(name, bindingAttr)
        | _ -> notRequired "GetPropertyImpl" this.Name
    override this.GetNestedTypes _bindingAttr =
        match kind,args with 
        | ReflectTypeSymbolKind.SDArray,[| arg |] 
        | ReflectTypeSymbolKind.Array _,[| arg |] 
        | ReflectTypeSymbolKind.Pointer,[| arg |]         
        | ReflectTypeSymbolKind.ByRef,[| arg |] -> arg.GetNestedTypes(_bindingAttr)
        | ReflectTypeSymbolKind.Generic gtd, _ -> gtd.GetNestedTypes(_bindingAttr)
        | _ -> failwith "unreachable"

    override this.GetNestedType(_name, _bindingAttr) =
        match kind,args with 
        | ReflectTypeSymbolKind.SDArray,[| arg |] 
        | ReflectTypeSymbolKind.Array _,[| arg |] 
        | ReflectTypeSymbolKind.Pointer,[| arg |]         
        | ReflectTypeSymbolKind.ByRef,[| arg |] -> arg.GetNestedType(_name, _bindingAttr)
        | ReflectTypeSymbolKind.Generic gtd, _ -> gtd.GetNestedType(_name, _bindingAttr)
        | _ -> failwith "unreachable"

    override this.GetAttributeFlagsImpl() =
        match kind,args with 
        | ReflectTypeSymbolKind.SDArray,[| arg |] 
        | ReflectTypeSymbolKind.Array _,[| arg |] 
        | ReflectTypeSymbolKind.Pointer,[| arg |]         
        | ReflectTypeSymbolKind.ByRef,[| arg |] -> arg.Attributes
        | ReflectTypeSymbolKind.Generic gtd, _ -> gtd.Attributes
        | _ -> failwith "unreachable"
    
    override this.UnderlyingSystemType = (this :> Type)

    override this.GetCustomAttributesData()                                                        =  ([| |] :> IList<_>)
    override this.MemberType                                                                       = notRequired "MemberType" this.Name
    override this.GetMember(name, memberTypes, bindingAttr) =
        match kind, args with
        | ReflectTypeSymbolKind.SDArray, [| arg |]
        | ReflectTypeSymbolKind.Array _, [| arg |]
        | ReflectTypeSymbolKind.Pointer, [| arg |]
        | ReflectTypeSymbolKind.ByRef, [| arg |] -> arg.GetMember(name, memberTypes, bindingAttr)
        | ReflectTypeSymbolKind.Generic gtd, _ -> gtd.GetMember(name, memberTypes, bindingAttr)
        | _ -> [||]
    override this.GUID                                                                             = notRequired "GUID" this.Name
    override this.InvokeMember(name, invokeAttr, binder, target, args, modifiers, culture, namedParameters) =
        invokeMemberCore (this :> Type) name invokeAttr binder target args modifiers culture namedParameters
    override this.GetCustomAttributes(_inherit)                                                    = [| |]
    override this.GetCustomAttributes(_attributeType, _inherit)                                    = [| |]
    override this.IsDefined(_attributeType, _inherit)                                              = false
    override this.MakeArrayType() = ReflectTypeSymbol(ReflectTypeSymbolKind.SDArray, [| this |]) :> Type
    override this.MakeArrayType arg = ReflectTypeSymbol(ReflectTypeSymbolKind.Array arg, [| this |]) :> Type
    override this.MakePointerType() = ReflectTypeSymbol(ReflectTypeSymbolKind.Pointer, [| this |]) :> Type
    override this.MakeByRefType() = ReflectTypeSymbol(ReflectTypeSymbolKind.ByRef, [| this |]) :> Type

    override this.ToString() = this.FullName

and ReflectMethodSymbol(gmd: MethodInfo, gargs: Type[]) =
    inherit MethodInfo() 

    override __.Attributes        = gmd.Attributes
    override __.Name              = gmd.Name
    override __.DeclaringType     = gmd.DeclaringType
    override __.MemberType        = gmd.MemberType

    override __.GetParameters()   = gmd.GetParameters() |> Array.map (instParameterInfo (gmd.DeclaringType.GetGenericArguments(), gargs))
    override __.CallingConvention = gmd.CallingConvention
    override __.ReturnType        = gmd.ReturnType |> instType (gmd.DeclaringType.GetGenericArguments(), gargs)
    override __.IsGenericMethod   = true
    override __.GetGenericArguments() = gargs
    override __.MetadataToken = gmd.MetadataToken

    override __.GetCustomAttributesData() = gmd.GetCustomAttributesData()

    override __.GetHashCode() = gmd.GetHashCode()
    override this.Equals(that:obj) = 
        match that with 
        | :? MethodInfo as thatMI -> thatMI.IsGenericMethod && gmd.Equals(thatMI.GetGenericMethodDefinition()) && lengthsEqAndForall2 (gmd.GetGenericArguments()) (thatMI.GetGenericArguments()) (=)
        | _ -> false

    override __.MethodHandle = notRequired "MethodHandle"
    override __.ReturnParameter   = notRequired "ReturnParameter" 
    override __.IsDefined(_attributeType, _inherited)                   = notRequired "IsDefined"
    override __.ReturnTypeCustomAttributes                            = notRequired "ReturnTypeCustomAttributes"
    override __.GetBaseDefinition()                                   = notRequired "GetBaseDefinition"
    override __.GetMethodImplementationFlags()                        = notRequired "GetMethodImplementationFlags"
    override __.Invoke(_obj, _invokeAttr, _binder, _parameters, _culture)  = notRequired "Invoke"
    override __.ReflectedType                                         = notRequired "ReflectedType"
    override __.GetCustomAttributes(_inherited)                        = notRequired "GetCustomAttributes"
    override __.GetCustomAttributes(_attributeType, _inherited)         = notRequired "GetCustomAttributes"

    override __.ToString() = string gmd + "@inst"
    

/// Clones namespaces, type providers, types and members provided by tp, renaming namespace nsp1 into namespace nsp2.

/// Makes a type definition read from a binary available as a System.Type. Not all methods are implemented.
and [<DebuggerDisplay("{FullName}")>] ReflectTypeDefinition (asm: ReflectAssembly, declTyOpt: Type option, tcref: TyconRef) as this = 
    inherit Type()

    let g = asm.TcGlobals

    let ilAccessToMethodAttributes access =
        match access with
        | ILMemberAccess.Public -> MethodAttributes.Public
        | ILMemberAccess.Private -> MethodAttributes.Private
        | ILMemberAccess.Family -> MethodAttributes.Family
        | ILMemberAccess.Assembly -> MethodAttributes.Assembly
        | ILMemberAccess.FamilyAndAssembly -> MethodAttributes.FamANDAssem
        | ILMemberAccess.FamilyOrAssembly -> MethodAttributes.FamORAssem
        | ILMemberAccess.CompilerControlled -> MethodAttributes.PrivateScope

    let computeMethodAttributes (vref: ValRef) (compiledName: string) (isConstructor: bool) =
        let baseAttrs = ilAccessToMethodAttributes (vref.Accessibility.AsILMemberAccess()) ||| MethodAttributes.HideBySig
        let memberFlagsOpt = vref.MemberInfo |> Option.map (fun info -> info.MemberFlags)

        let attrs =
            match memberFlagsOpt with
            | Some flags when not flags.IsInstance -> baseAttrs ||| MethodAttributes.Static
            | _ when vref.IsInstanceMember -> baseAttrs
            | _ -> baseAttrs ||| MethodAttributes.Static

        let attrs =
            if isConstructor then attrs ||| MethodAttributes.SpecialName ||| MethodAttributes.RTSpecialName
            else attrs

        let attrs =
            match memberFlagsOpt with
            | Some flags when
                    flags.MemberKind = SynMemberKind.PropertyGet
                    || flags.MemberKind = SynMemberKind.PropertySet
                    || flags.MemberKind = SynMemberKind.PropertyGetSet ->
                attrs ||| MethodAttributes.SpecialName
            | Some flags when
                    flags.MemberKind = SynMemberKind.Constructor
                    || flags.MemberKind = SynMemberKind.ClassConstructor ->
                attrs ||| MethodAttributes.SpecialName ||| MethodAttributes.RTSpecialName
            | _ when compiledName.StartsWith("get_", StringComparison.Ordinal)
                    || compiledName.StartsWith("set_", StringComparison.Ordinal)
                    || compiledName.StartsWith("add_", StringComparison.Ordinal)
                    || compiledName.StartsWith("remove_", StringComparison.Ordinal)
                    || compiledName.StartsWith("op_", StringComparison.Ordinal) ->
                attrs ||| MethodAttributes.SpecialName
            | _ -> attrs

        let attrs =
            match memberFlagsOpt with
            | Some flags when flags.IsDispatchSlot ->
                attrs ||| MethodAttributes.Abstract ||| MethodAttributes.Virtual
            | _ -> attrs

        let attrs =
            match memberFlagsOpt with
            | Some flags when flags.IsOverrideOrExplicitImpl -> attrs ||| MethodAttributes.Virtual
            | _ -> attrs

        let attrs =
            match memberFlagsOpt with
            | Some flags when flags.IsFinal -> attrs ||| MethodAttributes.Final
            | _ -> attrs

        attrs

    let computeCallingConvention isStatic =
        if isStatic then CallingConventions.Standard
        else CallingConventions.HasThis ||| CallingConventions.Standard

    let computeParameterMetadata (argTy: TType) (argInfo: ArgReprInfo) =
        let isInArg = HasFSharpAttribute g g.attrib_InAttribute argInfo.Attribs && isByrefTy g argTy
        let isOutArg = HasFSharpAttribute g g.attrib_OutAttribute argInfo.Attribs && isByrefTy g argTy
        let hasFSharpOptionalArg = HasFSharpAttribute g g.attrib_OptionalArgumentAttribute argInfo.Attribs
        let hasClrOptionalArg = HasFSharpAttributeOpt g g.attrib_OptionalAttribute argInfo.Attribs
        let isParamArrayArg = HasFSharpAttribute g g.attrib_ParamArrayAttribute argInfo.Attribs

        let attrs =
            ParameterAttributes.None
            |> fun attrs -> if isInArg then attrs ||| ParameterAttributes.In else attrs
            |> fun attrs -> if isOutArg then attrs ||| ParameterAttributes.Out else attrs
            |> fun attrs ->
                if hasClrOptionalArg || hasFSharpOptionalArg then
                    attrs ||| ParameterAttributes.Optional
                else
                    attrs

        let defaultValueFromAttribute =
            TryFindFSharpAttributeOpt g g.attrib_DefaultParameterValueAttribute argInfo.Attribs
            |> Option.bind (fun (Attrib (_, _, exprs, _, _, _, _)) ->
                exprs
                |> List.tryPick (fun (AttribExpr (_, evaluatedExpr)) ->
                    match stripDebugPoints evaluatedExpr with
                    | Expr.Const (cnst, _, _) ->
                        match cnst with
                        | ConstToILFieldInit fieldInit -> Some (ilFieldInitToObject fieldInit)
                        | _ -> Some (constToObject cnst)
                    | _ -> None))

        let defaultValueOpt =
            match defaultValueFromAttribute with
            | Some value -> Some value
            | None when hasFSharpOptionalArg -> Some Type.Missing
            | _ -> None

        let attrs = if defaultValueOpt.IsSome then attrs ||| ParameterAttributes.HasDefault else attrs
        attrs, defaultValueOpt, isParamArrayArg, hasFSharpOptionalArg, hasClrOptionalArg

    let tryResolveRuntimeTypeFromTycon (tref: TyconRef) : Type option =
        let fullNames : string list =
            [ tref.CompiledRepresentationForNamedType.FullName
              tref.CompiledRepresentationForNamedType.BasicQualifiedName ]
            |> List.choose (fun name -> if String.IsNullOrEmpty name then None else Some name)

        let assemblyNames : string list =
            match ccuOfTyconRef tref with
            | Some ccu when not (String.IsNullOrEmpty ccu.AssemblyName) -> [ ccu.AssemblyName ]
            | _ -> []

        let combinedCandidates : string list =
            let withAssembly =
                [ for name in fullNames do
                    for asmName in assemblyNames do
                        yield name + ", " + asmName ]
            (fullNames @ withAssembly)
            |> List.distinct

        let rec tryNames names =
            match names with
            | [] -> None
            | candidate :: rest ->
                let resolved =
                    try Type.GetType(candidate, throwOnError = false)
                    with _ -> null
                if isNull resolved then tryNames rest
                else Some resolved

        tryNames combinedCandidates

    let attributeTypeMatchesTycon (attributeType: Type) (tref: TyconRef) =
        let attributeTypeFullName = attributeType.FullName
        let attrFullName = tref.CompiledRepresentationForNamedType.FullName

        let namesMatch =
            not (String.IsNullOrEmpty attributeTypeFullName)
            && not (String.IsNullOrEmpty attrFullName)
            && String.Equals(attributeTypeFullName, attrFullName, StringComparison.Ordinal)

        if namesMatch then true
        else
            match tryResolveRuntimeTypeFromTycon tref with
            | Some runtimeType ->
                try attributeType.IsAssignableFrom runtimeType
                with _ -> false
            | None ->
                try
                    let proxyType = asm.TxTypeDef None tref
                    attributeType.IsAssignableFrom proxyType
                with _ -> false

    let rec instantiateAttributeArgument (arg: CustomAttributeTypedArgument) =
        match arg.Value with
        | null -> null
        | :? IList<CustomAttributeTypedArgument> as nested when
            not (isNull arg.ArgumentType)
            && arg.ArgumentType.IsArray ->
            let elemType =
                match arg.ArgumentType.GetElementType() with
                | null -> typeof<obj>
                | et -> et
            let array = Array.CreateInstance(elemType, nested.Count)
            for i = 0 to nested.Count - 1 do
                array.SetValue(instantiateAttributeArgument nested.[i], i)
            array :> obj
        | value -> value

    let instantiateAttributeInstances (data: IList<CustomAttributeData>) =
        data
        |> Seq.choose (fun cad ->
            try
                match cad.Constructor with
                | null -> None
                | ctor ->
                    let ctorArgs =
                        cad.ConstructorArguments
                        |> Seq.map instantiateAttributeArgument
                        |> Seq.toArray
                    Some (ctor.Invoke(ctorArgs))
            with _ -> None)
        |> Seq.toArray

    let filterAttributesByType (attributeType: Type) (instances: obj[]) =
        if isNull attributeType then
            nullArg "attributeType"
        instances |> Array.filter attributeType.IsInstanceOfType

    // Note: For F# type providers we never need to view the custom attributes
    let rec TxCustomAttributesArg (AttribExpr(_,v)) =
        match v with
        | Expr.Const (cnst, _, ttype) -> 
        //TODO: This can probably be removed completly.
            CustomAttributeTypedArgument(asm.TxTType ttype, TxConst cnst)
        | _ -> failwithf "Missing case for CustomAttributesArg %+A" v
    and TxCustomAttributesDatum (Attrib(tyconRef, _, exprs,_,_,_,_)) = 
        let proxyAttrType : Type = asm.TxTypeDef None tyconRef
        let runtimeAttrTypeOpt = tryResolveRuntimeTypeFromTycon tyconRef
        let bindingFlags = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance

        let tryPickConstructor (ty: Type) =
            try
                let ctors = ty.GetConstructors(bindingFlags)
                if ctors.Length = 0 then None
                else
                    match ctors |> Array.tryFind (fun c -> c.GetParameters().Length = exprs.Length) with
                    | Some c -> Some c
                    | None -> Some ctors.[0]
            with _ -> None

        let ctorOpt =
            match runtimeAttrTypeOpt |> Option.bind tryPickConstructor with
            | Some ctor -> Some ctor
            | None -> tryPickConstructor proxyAttrType

        { new CustomAttributeData () with
            member __.Constructor =
                match ctorOpt with
                | Some ctor -> ctor
                | None -> null
            member __.ConstructorArguments =
                [| for exp in exprs -> TxCustomAttributesArg exp |] :> IList<_>
            // Note, named arguments of custom attributes are not required by F# compiler on binding context elements.
            member __.NamedArguments = [| |] :> IList<_> 
         }

    and TxCustomAttributesData (attribs:Attribs) =
        [|
            for a in attribs do
                yield TxCustomAttributesDatum a
        |]
        :> IList<CustomAttributeData>

    /// Makes a parameter definition read from a binary available as a ParameterInfo. Not all methods are implemented.
    //let rec TxILParameter gps (inp : TyconRef) = 
    //    { new ParameterInfo() with 

    //        override __.Name = inp.MembersOfFSharpTyconByName.["Foo"].[0].MemberInfo.Value.
    //        override __.ParameterType = inp.ParameterType |> TxILType gps
    //        override __.RawDefaultValue = (match inp.Default with None -> null | Some v -> convFieldInit v)
    //        override __.Attributes = inp.Attributes
    //        override __.GetCustomAttributesData() = inp.CustomAttrs  |> TxCustomAttributesData

    //        override x.ToString() = sprintf "ctxt parameter %s" x.Name }
 
    /// Makes a method definition read from a binary available as a ConstructorInfo.
    and TxConstructorDef (declTy: Type) (vref: ValRef) =
        let g = asm.TcGlobals
        let compilerGlobalState = g.CompilerGlobalState
        let compiledName = vref.CompiledName compilerGlobalState
        let _typars, curriedArgInfos, _, _ = GetTypeOfMemberInFSharpForm g vref
        let parameterData = curriedArgInfos |> List.collect id
#if !NO_TYPEPROVIDERS
        let providedParametersOpt =
            match vref.TryDeref with
            | ValueSome v -> v.TryGetProvidedBinding |> Option.bind ProvidedReflectionHelpers.tryGetProvidedParameters
            | ValueNone -> None
#endif

        let methodAttributeData = lazy (vref.Attribs |> TxCustomAttributesData)
        let methodAttributeInstances = lazy (instantiateAttributeInstances methodAttributeData.Value)

        let createParameterInfos memberGetter =
            parameterData
            |> List.mapi (fun position (argTy, argInfo) ->
                let attrs, defaultValueOpt, isParamArrayArg, hasFSharpOptionalArg, hasClrOptionalArg =
                    computeParameterMetadata argTy argInfo
                let normalizedArgTy = stripTyEqns g argTy
                let parameterType =
                    if isParamArrayArg then
                        if isArrayTy g normalizedArgTy then
                            let elemTy = destArrayTy g normalizedArgTy
                            (asm.TxTType elemTy).MakeArrayType()
                        elif isListTy g normalizedArgTy then
                            let elemTy = destListTy g normalizedArgTy
                            (asm.TxTType elemTy).MakeArrayType()
                        else
                            asm.TxTType normalizedArgTy
                    else
                        asm.TxTType normalizedArgTy
                let name =
                    argInfo.Name
                    |> Option.map (fun ident -> ident.idText)
                    |> Option.defaultValue (sprintf "arg%d" (position + 1))
                let customAttributesData = TxCustomAttributesData argInfo.Attribs
                let customAttributesInstances = lazy (instantiateAttributeInstances customAttributesData)
                let attrTyconRefs =
                    argInfo.Attribs
                    |> List.map (fun (Attrib(tref, _, _, _, _, _, _)) -> tref)
                { new ParameterInfo() with
                    override _.Member = memberGetter()
                    override _.Name = name
                    override _.ParameterType = parameterType
                    override _.Attributes = attrs
                    override _.Position = position
                    override _.RawDefaultValue = defaultValueOpt |> Option.defaultValue Type.Missing
                    override _.DefaultValue = defaultValueOpt |> Option.defaultValue Type.Missing
                    override _.HasDefaultValue = defaultValueOpt.IsSome
                    override _.GetCustomAttributesData() = customAttributesData
                    override _.GetCustomAttributes(_inherit) =
                        customAttributesInstances.Value |> Array.copy
                    override _.GetCustomAttributes(attributeType, _inherit) =
                        filterAttributesByType attributeType customAttributesInstances.Value
                    override _.IsDefined(attributeType, _inherit) =
                        let declaredMatch =
                            attrTyconRefs
                            |> List.exists (attributeTypeMatchesTycon attributeType)

                        let matchesKnown (knownType: Type) =
                            let knownFullName = knownType.FullName
                            let attributeFullName = attributeType.FullName
                            (not (String.IsNullOrEmpty attributeFullName)
                                && String.Equals(attributeFullName, knownFullName, StringComparison.Ordinal))
                            || attributeType.IsAssignableFrom knownType

                        let paramArrayMatch =
                            isParamArrayArg && matchesKnown typeof<ParamArrayAttribute>

                        let optionalMatch =
                            (hasFSharpOptionalArg && matchesKnown typeof<Microsoft.FSharp.Core.OptionalArgumentAttribute>)
                            || (hasClrOptionalArg && matchesKnown typeof<System.Runtime.InteropServices.OptionalAttribute>)

                        declaredMatch || paramArrayMatch || optionalMatch
                    override _.ToString() = sprintf "%s %s" parameterType.Name name })
            |> List.toArray

        let isStatic =
            match vref.MemberInfo with
            | Some info -> not info.MemberFlags.IsInstance
            | None -> not vref.IsInstanceMember

        let attributes = computeMethodAttributes vref compiledName true
        let callingConvention = computeCallingConvention isStatic

        let signatureHash =
            let declaringName =
                if isNull declTy then String.Empty
                else
                    match declTy.AssemblyQualifiedName with
                    | null -> String.Empty
                    | name -> name
            hash (declaringName, compiledName, parameterData.Length)

        let metadataTokenValue =
            let token = abs signatureHash + 1
            if token = 0 then 1 else token

        let rec ctorInfo : ConstructorInfo =
            let parametersLazy =
                lazy (
#if !NO_TYPEPROVIDERS
                    match providedParametersOpt with
                    | Some providedParams ->
                        providedParams
                        |> Array.mapi (fun idx param ->
                            ProvidedReflectionHelpers.makeProvidedParameterInfo (fun () -> ctorInfo :> MemberInfo) idx param)
                    | None ->
#endif
                        createParameterInfos (fun () -> ctorInfo :> MemberInfo))

            { new ConstructorInfo() with 
                override _.Name = compiledName
                override _.Attributes = attributes
                override _.MemberType = MemberTypes.Constructor
                override _.DeclaringType = declTy
                override _.CallingConvention = callingConvention

                override _.GetParameters() = parametersLazy.Value

                override _.GetHashCode() = signatureHash
                override this.Equals(that: obj) =
                    match that with
                    | :? ConstructorInfo as other ->
                        let paramsEqual =
                            let these = parametersLazy.Value
                            let those = other.GetParameters()
                            these.Length = those.Length
                            && Array.forall2 (fun (p1: ParameterInfo) (p2: ParameterInfo) -> eqType p1.ParameterType p2.ParameterType) these those
                        eqType this.DeclaringType other.DeclaringType
                        && other.Name = compiledName
                        && paramsEqual
                    | _ -> false

                override _.IsDefined(attributeType, _inherit) =
                    vref.Attribs
                    |> List.exists (fun (Attrib(tref, _, _, _, _, _, _)) ->
                        let attrTy = asm.TxTypeDef None tref
                        attributeType.IsAssignableFrom attrTy)
                override _.Invoke(_invokeAttr, _binder, _parameters, _culture) = notRequired "Constructor.Invoke"
                override _.Invoke(_obj, _invokeAttr, _binder, _parameters, _culture) = notRequired "Constructor.Invoke"
                override _.ReflectedType = declTy
                override _.GetMethodImplementationFlags() = MethodImplAttributes.IL
                override _.MethodHandle = notRequired "Constructor.MethodHandle"
                override _.GetCustomAttributesData() = methodAttributeData.Value
                override _.GetCustomAttributes(_inherit) = methodAttributeInstances.Value |> Array.copy
                override _.GetCustomAttributes(attributeType, _inherit) =
                    filterAttributesByType attributeType methodAttributeInstances.Value
                override _.MetadataToken = metadataTokenValue
                override _.ToString() = sprintf "ctxt constructor %s(...) in type %s" compiledName declTy.FullName }

        ctorInfo

    /// Makes a method definition read from a binary available as a MethodInfo. Not all methods are implemented.
    and TxMethodDef (asmArg: ReflectAssembly) (declTy: Type) (vref: ValRef) =
        let asm: ReflectAssembly = asmArg
        let gps : Type[] = if declTy.IsGenericType then declTy.GetGenericArguments() else [| |]
        let g = asm.TcGlobals
        let compilerGlobalState = g.CompilerGlobalState
        let compiledName : string = vref.CompiledName compilerGlobalState
        let typars, curriedArgInfos, retTy, _ = GetTypeOfMemberInFSharpForm g vref
        let parameterData = curriedArgInfos |> List.collect id
#if !NO_TYPEPROVIDERS
        let providedBindingOpt =
            match vref.TryDeref with
            | ValueSome v -> v.TryGetProvidedBinding
            | ValueNone -> None
        let providedParametersOpt =
            providedBindingOpt
            |> Option.bind ProvidedReflectionHelpers.tryGetProvidedParameters
        let providedReturnParameterOpt =
            providedBindingOpt
            |> Option.bind (fun binding ->
                if obj.ReferenceEquals(binding.ReturnParameter, null) then None else Some binding.ReturnParameter)
        let providedReturnTypeOpt =
            providedBindingOpt
            |> Option.bind (fun binding ->
                let resultType = binding.ResultType
                if obj.ReferenceEquals(resultType, null) then None else Some resultType.RawSystemType)
#else
        let providedParametersOpt = None
        let providedReturnParameterOpt = None
        let providedReturnTypeOpt = None
#endif

        let methodAttributeData = lazy (vref.Attribs |> TxCustomAttributesData)
        let methodAttributeInstances = lazy (instantiateAttributeInstances methodAttributeData.Value)

        let createParameterInfos memberGetter =
            parameterData
            |> List.mapi (fun position (argTy, argInfo) ->
                let attrs, defaultValueOpt, isParamArrayArg, hasFSharpOptionalArg, hasClrOptionalArg =
                    computeParameterMetadata argTy argInfo
                let normalizedArgTy = stripTyEqns g argTy
                let parameterType =
                    if isParamArrayArg then
                        if isArrayTy g normalizedArgTy then
                            let elemTy = destArrayTy g normalizedArgTy
                            (asm.TxTType elemTy).MakeArrayType()
                        elif isListTy g normalizedArgTy then
                            let elemTy = destListTy g normalizedArgTy
                            (asm.TxTType elemTy).MakeArrayType()
                        else
                            asm.TxTType normalizedArgTy
                    else
                        asm.TxTType normalizedArgTy
                let name =
                    argInfo.Name
                    |> Option.map (fun ident -> ident.idText)
                    |> Option.defaultValue (sprintf "arg%d" (position + 1))
                let customAttributesData = TxCustomAttributesData argInfo.Attribs
                let customAttributesInstances = lazy (instantiateAttributeInstances customAttributesData)
                let attrTyconRefs =
                    argInfo.Attribs
                    |> List.map (fun (Attrib(tref, _, _, _, _, _, _)) -> tref)
                { new ParameterInfo() with
                    override _.Member = memberGetter()
                    override _.Name = name
                    override _.ParameterType = parameterType
                    override _.Attributes = attrs
                    override _.Position = position
                    override _.RawDefaultValue = defaultValueOpt |> Option.defaultValue Type.Missing
                    override _.DefaultValue = defaultValueOpt |> Option.defaultValue Type.Missing
                    override _.HasDefaultValue = defaultValueOpt.IsSome
                    override _.GetCustomAttributesData() = customAttributesData
                    override _.GetCustomAttributes(_inherit) =
                        customAttributesInstances.Value |> Array.copy
                    override _.GetCustomAttributes(attributeType, _inherit) =
                        filterAttributesByType attributeType customAttributesInstances.Value
                    override _.IsDefined(attributeType, _inherit) =
                        let declaredMatch =
                            attrTyconRefs
                            |> List.exists (attributeTypeMatchesTycon attributeType)

                        let matchesKnown (knownType: Type) =
                            let knownFullName = knownType.FullName
                            let attributeFullName = attributeType.FullName
                            (not (String.IsNullOrEmpty attributeFullName)
                                && String.Equals(attributeFullName, knownFullName, StringComparison.Ordinal))
                            || attributeType.IsAssignableFrom knownType

                        let paramArrayMatch =
                            isParamArrayArg && matchesKnown typeof<ParamArrayAttribute>

                        let optionalMatch =
                            (hasFSharpOptionalArg && matchesKnown typeof<Microsoft.FSharp.Core.OptionalArgumentAttribute>)
                            || (hasClrOptionalArg && matchesKnown typeof<System.Runtime.InteropServices.OptionalAttribute>)

                        declaredMatch || paramArrayMatch || optionalMatch
                    override _.ToString() = sprintf "%s %s" parameterType.Name name })
            |> List.toArray

        let rec gps2 : Type[] =
            typars
            |> List.mapi (fun i gp -> TxGenericParam asm (fun () -> gps, gps2) (i + gps.Length) gp)
            |> List.toArray

        let pairTyparsWithTypes (typars: Typar list) (types: Type[]) =
            if List.isEmpty typars then
                []
            elif Array.isEmpty types then
                []
            else
                let typarArray = typars |> List.toArray
                if typarArray.Length <> types.Length then
                    []
                else
                    Array.zip typarArray types |> Array.toList

        let enclosingTyconPairs =
            if vref.IsMember then
                let tycon = vref.MemberApparentEntity
                pairTyparsWithTypes tycon.TyparsNoRange gps
            else
                match vref.TryDeref with
                | ValueSome v ->
                    match v.TryDeclaringEntity with
                    | Parent tcref -> pairTyparsWithTypes tcref.TyparsNoRange gps
                    | ParentNone -> []
                | ValueNone -> []

        let methodTyparPairs = pairTyparsWithTypes typars gps2

        let pushTypars () =
            let combined =
                if List.isEmpty methodTyparPairs then enclosingTyconPairs
                elif List.isEmpty enclosingTyconPairs then methodTyparPairs
                else enclosingTyconPairs @ methodTyparPairs

            if List.isEmpty combined then
                { new IDisposable with member _.Dispose() = () }
            else
                asm.PushTyparScope combined

        use _typarScope = pushTypars ()

        let isStatic =
            match vref.MemberInfo with
            | Some info -> not info.MemberFlags.IsInstance
            | None -> not vref.IsInstanceMember

        let methodAttributes = computeMethodAttributes vref compiledName false
        let callingConvention = computeCallingConvention isStatic
        let returnType =
#if !NO_TYPEPROVIDERS
            match providedReturnTypeOpt with
            | Some providedType -> providedType
            | None ->
#endif
                if isUnitTy g retTy then typeof<Void>
                else asm.TxTType retTy

        let signatureHash =
            let declaringName =
                if isNull declTy then String.Empty
                else
                    match declTy.AssemblyQualifiedName with
                    | null -> String.Empty
                    | name -> name
            hash (declaringName, compiledName, parameterData.Length, gps2.Length)

        let metadataTokenValue =
            let token = abs signatureHash + 1
            if token = 0 then 1 else token

        let rec methodInfo : MethodInfo =
            let parametersLazy =
                lazy (
#if !NO_TYPEPROVIDERS
                    match providedParametersOpt with
                    | Some providedParams ->
                        providedParams
                        |> Array.mapi (fun idx param ->
                            ProvidedReflectionHelpers.makeProvidedParameterInfo (fun () -> methodInfo :> MemberInfo) idx param)
                    | None ->
#endif
                        createParameterInfos (fun () -> methodInfo :> MemberInfo))

            { new MethodInfo() with 

            override __.Name              = compiledName  
            override __.DeclaringType     = declTy
            override __.MemberType        = MemberTypes.Method
            override __.Attributes        = methodAttributes
            override __.GetParameters()   = parametersLazy.Value
            override __.CallingConvention = callingConvention
            override __.ReturnType        = returnType
            override __.GetCustomAttributesData() = methodAttributeData.Value
            override __.GetGenericArguments() = gps2
            override __.IsGenericMethod = (gps2.Length <> 0)
            override __.IsGenericMethodDefinition = __.IsGenericMethod

            override __.GetHashCode() = signatureHash
            override this.Equals(that:obj) = 
                match that with 
                | :? MethodInfo as thatMI ->
                    let thatDeclaringType = thatMI.DeclaringType
                    let namesEqual = String.Equals(compiledName, thatMI.Name, StringComparison.Ordinal)
                    let declEqual =
                        not (isNull declTy)
                        && not (isNull thatDeclaringType)
                        && eqType declTy thatDeclaringType
                    let paramsEqual =
                        let these = parametersLazy.Value
                        let those = thatMI.GetParameters()
                        these.Length = those.Length &&
                        Array.forall2 (fun (p1: ParameterInfo) (p2: ParameterInfo) -> eqType p1.ParameterType p2.ParameterType) these those
                    let genericsEqual =
                        let otherGenericCount =
                            if thatMI.IsGenericMethod then thatMI.GetGenericArguments().Length else 0
                        gps2.Length = otherGenericCount
                    namesEqual && declEqual && paramsEqual && genericsEqual
                | _ -> false

            override this.MakeGenericMethod(args) = ReflectMethodSymbol(this, args) :> MethodInfo

            override __.MetadataToken = metadataTokenValue

            // unused
            override __.MethodHandle = notRequired "MethodHandle"
            override __.IsDefined(attributeType, _inherited) =
                vref.Attribs
                |> List.exists (fun (Attrib(tref, _, _, _, _, _, _)) ->
                    let attrTy = asm.TxTypeDef None tref
                    attributeType.IsAssignableFrom attrTy)
            override __.ReturnTypeCustomAttributes = notRequired "ReturnTypeCustomAttributes"
            override __.GetBaseDefinition() = methodInfo
            override __.GetMethodImplementationFlags() = MethodImplAttributes.IL
            override __.Invoke(obj, invokeAttr, binder, parameters, culture)  = notRequired "Invoke"
            override __.ReflectedType = declTy
            override __.GetCustomAttributes(_inherited) = methodAttributeInstances.Value |> Array.copy
            override __.GetCustomAttributes(attributeType, _inherited) =
                filterAttributesByType attributeType methodAttributeInstances.Value 
#if !NO_TYPEPROVIDERS
            override __.ReturnParameter =
                match providedReturnParameterOpt with
                | Some providedParam ->
                    ProvidedReflectionHelpers.makeProvidedReturnParameter (fun () -> methodInfo :> MemberInfo) providedParam
                | None -> notRequired "ReturnParameter"
#else
            override __.ReturnParameter = notRequired "ReturnParameter"
#endif
            override __.ToString() = sprintf "ctxt method %s(...) in type %s" compiledName declTy.FullName  }

        methodInfo

    /// Makes a property definition read from a binary available as a PropertyInfo. Not all methods are implemented.
    and TxPropertyDefinition (asmArg: ReflectAssembly) declTy (gps: Type[]) (tycon:TyconRef) (inp: ValRef) = 
        let asm: ReflectAssembly = asmArg
        let tcGlobals: TcGlobals = asm.TcGlobals
        let compilerGlobalState = tcGlobals.CompilerGlobalState
        let compiledName : string = inp.CompiledName compilerGlobalState
        let pushTypars () =
            let typars = tycon.TyparsNoRange
            if List.isEmpty typars || Array.isEmpty gps then
                { new IDisposable with member _.Dispose() = () }
            else
                let pairs =
                    (typars, gps |> Array.toList)
                    ||> List.zip
                asm.PushTyparScope pairs

        use _typarScope = pushTypars ()

        let propertyReturnType =
            try
                ReturnTypeOfPropertyVal tcGlobals inp.Deref
            with _ ->
                inp.Type
        let propertyType : Type =
#if !NO_TYPEPROVIDERS
            match inp.Deref.TryGetProvidedBinding with
            | Some binding when not (obj.ReferenceEquals(binding.ResultType, null)) ->
                binding.ResultType.RawSystemType
            | _ -> asm.TxTType propertyReturnType
#else
            asm.TxTType propertyReturnType
#endif

        let membersForProperty : ValRef list =
            let direct =
                tycon.MembersOfFSharpTyconByName
                |> NameMultiMap.find inp.PropertyName
            let fallback =
                if direct.Length > 0 then direct
                else
                    tcref.MembersOfFSharpTyconSorted
                    |> List.filter (fun vref ->
                        (vref.IsPropertyGetterMethod || vref.IsPropertySetterMethod)
                        && String.Equals(vref.PropertyName, inp.PropertyName, StringComparison.Ordinal))
            fallback

        let getterValOpt =
            membersForProperty
            |> List.tryFind (fun (vref: ValRef) -> vref.IsPropertyGetterMethod)

        let setterValOpt =
            membersForProperty
            |> List.tryFind (fun (vref: ValRef) -> vref.IsPropertySetterMethod)

        let getterMethodOpt = getterValOpt |> Option.map (TxMethodDef asm declTy)
        let setterMethodOpt = setterValOpt |> Option.map (TxMethodDef asm declTy)
        let propertyAttributeData = lazy (inp.Attribs |> TxCustomAttributesData)
        let propertyAttributeInstances = lazy (instantiateAttributeInstances propertyAttributeData.Value)

        let propertyAttributes =
            if String.Equals(inp.PropertyName, "Item", StringComparison.Ordinal) then
                PropertyAttributes.SpecialName ||| PropertyAttributes.HasDefault
            else
                PropertyAttributes.SpecialName

        let propertyInfo : PropertyInfo =
            { new PropertyInfo() with 

            override __.Name = inp.PropertyName
            override __.Attributes        = propertyAttributes
            override __.MemberType = MemberTypes.Property
            override __.DeclaringType = declTy

            override __.PropertyType = propertyType
            override __.GetGetMethod(_nonPublic) =
                getterMethodOpt |> optionToNull
            override __.GetSetMethod(_nonPublic) =
                setterMethodOpt |> optionToNull
            override __.GetIndexParameters() =
                match getterMethodOpt with
                | Some getter ->
                    getter.GetParameters()
                | None ->
                    match setterMethodOpt with
                    | Some setter ->
                        let setterParameters = setter.GetParameters()
                        if setterParameters.Length <= 1 then
                            Array.empty
                        else
                            setterParameters |> Array.take (setterParameters.Length - 1)
                    | None -> Array.empty
            override __.CanRead = getterMethodOpt.IsSome
            override __.CanWrite = setterMethodOpt.IsSome
            override __.GetAccessors(nonPublic) =
                [|
                    match getterMethodOpt with
                    | Some getter when nonPublic || getter.IsPublic -> yield getter
                    | _ -> ()
                    match setterMethodOpt with
                    | Some setter when nonPublic || setter.IsPublic -> yield setter
                    | _ -> ()
                |]
            override __.GetCustomAttributesData() = propertyAttributeData.Value

            override this.GetHashCode() = hash compiledName
            override this.Equals(that:obj) = 
                match that with 
                | :? PropertyInfo as thatPI -> 
                    compiledName = thatPI.Name  &&
                    eqType this.DeclaringType thatPI.DeclaringType 
                | _ -> false

            override __.GetValue(obj, invokeAttr, binder, index, culture) = notRequired "GetValue"
            override __.SetValue(obj, _value, invokeAttr, binder, index, culture) = notRequired "SetValue"
            override __.ReflectedType = declTy
            override __.GetCustomAttributes(_inherited) = propertyAttributeInstances.Value |> Array.copy
            override __.GetCustomAttributes(attributeType, _inherited) =
                filterAttributesByType attributeType propertyAttributeInstances.Value
            override __.IsDefined(attributeType, _inherited) =
                inp.Attribs
                |> List.exists (fun (Attrib(tref, _, _, _, _, _, _)) -> attributeTypeMatchesTycon attributeType tref)

            override __.ToString() = sprintf "ctxt property %s(...) in type %s" compiledName declTy.Name }

        propertyInfo

    and TxEventDefinition
        (asmArg: ReflectAssembly)
        (declTy: Type)
        (eventName: string)
        (propertyVal: ValRef option)
        (addVal: ValRef)
        (removeVal: ValRef)
        (handlerTypeOverride: Type option)
        =
        let asm: ReflectAssembly = asmArg
        let g = asm.TcGlobals

        let addMethod = TxMethodDef asm declTy addVal
        let removeMethod = TxMethodDef asm declTy removeVal

        let handlerType =
            match handlerTypeOverride with
            | Some overrideTy -> overrideTy
            | None ->
                let parameters = addMethod.GetParameters()
                if parameters.Length = 0 then
                    asm.TxTType g.system_MulticastDelegate_ty
                else
                    parameters.[parameters.Length - 1].ParameterType

        let eventAttribs =
            match propertyVal with
            | Some vref -> vref.Attribs
            | None -> []
        let eventAttributeData = lazy (eventAttribs |> TxCustomAttributesData)
        let eventAttributeInstances = lazy (instantiateAttributeInstances eventAttributeData.Value)

        { new EventInfo() with
            override _.Name = eventName
            override _.MemberType = MemberTypes.Event
            override _.DeclaringType = declTy
            override _.EventHandlerType = handlerType
            override _.Attributes = EventAttributes.SpecialName
            override _.GetAddMethod(_nonPublic) = addMethod
            override _.GetRemoveMethod(_nonPublic) = removeMethod
            override _.GetRaiseMethod(_nonPublic) = null
            override _.GetCustomAttributesData() = eventAttributeData.Value
            override _.GetCustomAttributes(_inherited) = eventAttributeInstances.Value |> Array.copy
            override _.GetCustomAttributes(attributeType, _inherited) =
                filterAttributesByType attributeType eventAttributeInstances.Value
            override _.IsDefined(attributeType, _inherited) =
                eventAttribs
                |> List.exists (fun (Attrib(tref, _, _, _, _, _, _)) -> attributeTypeMatchesTycon attributeType tref)
            override _.ReflectedType = declTy
            override this.GetHashCode() = hash (declTy, eventName)
            override this.Equals(other: obj) =
                match other with
                | :? EventInfo as ev -> ev.Name = eventName && eqType this.DeclaringType ev.DeclaringType
                | _ -> false
            override _.ToString() = sprintf "ctxt event %s(...) in type %s" eventName declTy.FullName }

    and TxConst (cnst:Const) = 
        match cnst with 
        | Const.Bool b -> box b
        | Const.Byte b -> box b
        | Const.Char c -> box c
        | Const.Decimal d -> box d
        | Const.Double d -> box d
        | Const.Int16 i -> box i
        | Const.Int32 i -> box i
        | Const.Int64 i -> box i
        | Const.IntPtr i -> box i
        | Const.SByte sb -> box sb
        | Const.Single f -> box f
        | Const.String s -> box s
        | Const.UInt16 i -> box i
        | Const.UInt32 i -> box i
        | Const.UInt64 i -> box i
        | Const.UIntPtr i -> box i
        | Const.Unit -> box ()
        | Const.Zero -> box 0
    
    and TxFieldDefinition (asm: ReflectAssembly) declTy _ (* gps *) (inp: RecdField) =
        let fieldAttributeData = lazy (inp.FieldAttribs |> TxCustomAttributesData)
        let fieldAttributeInstances = lazy (instantiateAttributeInstances fieldAttributeData.Value)
        { new FieldInfo() with 

            override __.Name = inp.rfield_id.idText 
            override __.Attributes = 
                [|
                    yield if inp.rfield_static then Some FieldAttributes.Static else None
                    yield if inp.rfield_const.IsSome then Some FieldAttributes.Literal else None
                |] 
                |> Array.choose id
                |> Array.fold (|||) (if inp.rfield_secret then FieldAttributes.Public else FieldAttributes.Private)
            override __.MemberType = MemberTypes.Field 
            override __.DeclaringType = declTy
            override __.FieldType = inp.FormalType |> asm.TxTType
            override __.GetRawConstantValue()  = match inp.LiteralValue with None -> null | Some v -> TxConst v
            override __.GetCustomAttributesData() = fieldAttributeData.Value

            override __.GetHashCode() = hash inp.rfield_id.idText
            override this.Equals(that:obj) = 
                match that with 
                | :? EventInfo as thatFI -> 
                    inp.rfield_id.idText = thatFI.Name  &&
                    eqType this.DeclaringType thatFI.DeclaringType 
                | _ -> false
   
            override __.ReflectedType = notRequired "ReflectedType"
            override __.GetCustomAttributes(_inherited) = fieldAttributeInstances.Value |> Array.copy
            override __.GetCustomAttributes(attributeType, _inherited) =
                filterAttributesByType attributeType fieldAttributeInstances.Value
            override __.IsDefined(attributeType, _inherited) =
                inp.FieldAttribs
                |> List.exists (fun (Attrib(tref, _, _, _, _, _, _)) -> attributeTypeMatchesTycon attributeType tref)
            override __.SetValue(obj, _value, invokeAttr, binder, culture) = notRequired "SetValue"
            override __.GetValue(obj) = notRequired "GetValue"
            override __.FieldHandle = notRequired "FieldHandle"

            override __.ToString() = sprintf "ctxt literal field %s(...) in type %s" inp.rfield_id.idText declTy.FullName }

    and TxRecordFieldPropertyDefinition (asm: ReflectAssembly) declTy (gps: Type[]) (tycon: TyconRef) (field: RecdField) =
        let pushTypars () =
            let typars = tycon.TyparsNoRange
            if List.isEmpty typars || Array.isEmpty gps then
                { new IDisposable with member _.Dispose() = () }
            else
                let pairs =
                    (typars, gps |> Array.toList)
                    ||> List.zip
                asm.PushTyparScope pairs

        use _typarScope = pushTypars ()
        let getter =
            tycon.MembersOfFSharpTyconSorted
            |> List.tryFind (fun vref -> vref.IsPropertyGetterMethod && vref.PropertyName = field.LogicalName)
            |> Option.map (TxMethodDef asm declTy)

        let setter =
            tycon.MembersOfFSharpTyconSorted
            |> List.tryFind (fun vref -> vref.IsPropertySetterMethod && vref.PropertyName = field.LogicalName)
            |> Option.map (TxMethodDef asm declTy)

        let propertyType = field.FormalType |> asm.TxTType
        let propertyAttributeData = lazy (field.PropertyAttribs |> TxCustomAttributesData)
        let propertyAttributeInstances = lazy (instantiateAttributeInstances propertyAttributeData.Value)

        { new PropertyInfo() with
            override _.Name = field.LogicalName
            override _.DeclaringType = declTy
            override _.MemberType = MemberTypes.Property
            override _.PropertyType = propertyType
            override _.Attributes = PropertyAttributes.None
            override _.GetIndexParameters() = [||]
            override _.CanRead = true
            override _.CanWrite = field.IsMutable
            override _.GetGetMethod(_nonPublic) = getter |> optionToNull
            override _.GetSetMethod(_nonPublic) = setter |> optionToNull
            override _.GetCustomAttributesData() = propertyAttributeData.Value

            override this.GetHashCode() = hash field.LogicalName

            override this.Equals(other: obj) =
                match other with
                | :? PropertyInfo as propertyInfo ->
                    this.Name = propertyInfo.Name && eqType this.DeclaringType propertyInfo.DeclaringType
                | _ -> false

            override _.GetCustomAttributes(_inherited) = propertyAttributeInstances.Value |> Array.copy
            override _.GetCustomAttributes(attributeType, _inherited) =
                filterAttributesByType attributeType propertyAttributeInstances.Value
            override _.IsDefined(attributeType, _inherited) =
                field.PropertyAttribs
                |> List.exists (fun (Attrib(tref, _, _, _, _, _, _)) -> attributeTypeMatchesTycon attributeType tref)
            override _.GetValue(_obj, _invokeAttr, _binder, _index, _culture) = notRequired "TxRecordFieldPropertyDefinition.GetValue"
            override _.SetValue(_obj, _value, _invokeAttr, _binder, _index, _culture) = notRequired "TxRecordFieldPropertyDefinition.SetValue"
            override _.GetAccessors(_nonPublic) = notRequired "TxRecordFieldPropertyDefinition.GetAccessors"
            override _.ReflectedType = notRequired "TxRecordFieldPropertyDefinition.ReflectedType"

            override _.ToString() = sprintf "ctxt record property %s(...) in type %s" field.LogicalName declTy.FullName }

    /// Convert an ILGenericParameterDef read from a binary to a System.Type.
    and TxGenericParam asm _ (* gpsf *) pos (inp: Typar) =
        { new Type() with 
            override __.Name = inp.Name 
            override __.Assembly = (asm :> Assembly)
            override __.FullName = inp.Name
            override __.IsGenericParameter = true
            override __.GenericParameterPosition = pos
            override __.GetGenericParameterConstraints() = [||]
                //TODO: Implement generic parameter constraints
                //inp.Constraints |> Array.map (fun x -> x TxILType (gpsf()))
                    
            override __.MemberType = enum 0

            override __.Namespace = null //notRequired "Namespace"
            override __.DeclaringType = notRequired "DeclaringType"
            override __.BaseType = notRequired "BaseType"
            override __.GetInterfaces() = notRequired "GetInterfaces"

            override this.GetConstructors(_bindingFlags) = notRequired "GetConstructors"
            override this.GetMethods(_bindingFlags) = notRequired "GetMethods"
            override this.GetField(name, _bindingFlags) = notRequired "GetField"
            override this.GetFields(_bindingFlags) = notRequired "GetFields"
            override this.GetEvent(name, _bindingFlags) = notRequired "GetEvent"
            override this.GetEvents(_bindingFlags) = notRequired "GetEvents"
            override this.GetProperties(_bindingFlags) = notRequired "GetProperties"
            override this.GetMembers(_bindingFlags) = notRequired "GetMembers"
            override this.GetNestedTypes(_bindingFlags) = notRequired "GetNestedTypes"
            override this.GetNestedType(name, _bindingFlags) = notRequired "GetNestedType"
            override this.GetPropertyImpl(name, _bindingFlags, _binder, _returnType, _types, _modifiers) = notRequired "GetPropertyImpl"
            override this.MakeGenericType(args) = notRequired "MakeGenericType"
            override this.MakeArrayType() = ReflectTypeSymbol(ReflectTypeSymbolKind.SDArray, [| this |]) :> Type
            override this.MakeArrayType arg = ReflectTypeSymbol(ReflectTypeSymbolKind.Array arg, [| this |]) :> Type
            override this.MakePointerType() = ReflectTypeSymbol(ReflectTypeSymbolKind.Pointer, [| this |]) :> Type
            override this.MakeByRefType() = ReflectTypeSymbol(ReflectTypeSymbolKind.ByRef, [| this |]) :> Type

            override __.GetAttributeFlagsImpl() = TypeAttributes.Public ||| TypeAttributes.Class ||| TypeAttributes.Sealed 

            override __.IsArrayImpl() = false
            override __.IsByRefImpl() = false
            override __.IsPointerImpl() = false
            override __.IsPrimitiveImpl() = false
            override __.IsCOMObjectImpl() = false
            override __.IsGenericType = false
            override __.IsGenericTypeDefinition = false

            override __.HasElementTypeImpl() = false

            override this.UnderlyingSystemType = this
            override __.GetCustomAttributesData() = inp.Attribs |> TxCustomAttributesData

            override this.Equals(that:obj) = System.Object.ReferenceEquals (this, that) 

            override __.ToString() = sprintf "ctxt generic param %s" inp.Name 

            override this.AssemblyQualifiedName                                                            = "[" + this.Assembly.FullName + "]" + this.FullName

            override __.GetGenericArguments() = notRequired "GetGenericArguments"
            override __.GetGenericTypeDefinition() = notRequired "GetGenericTypeDefinition"
            override __.GetMember(name,mt,_bindingFlags)                                                      = notRequired "TxILGenericParam: GetMember"
            override __.GUID                                                                                      = notRequired "TxILGenericParam: GUID"
            override __.GetMethodImpl(name, _bindingFlags, binder, callConvention, types, modifiers)          = notRequired "TxILGenericParam: GetMethodImpl"
            override __.GetConstructorImpl(_bindingFlags, binder, callConvention, types, modifiers)           = notRequired "TxILGenericParam: GetConstructorImpl"
            override __.GetCustomAttributes(inherited)                                                            = notRequired "TxILGenericParam: GetCustomAttributes"
            override __.GetCustomAttributes(attributeType, inherited)                                             = notRequired "TxILGenericParam: GetCustomAttributes"
            override __.IsDefined(attributeType, inherited)                                                       = notRequired "TxILGenericParam: IsDefined"
            override __.GetInterface(name, ignoreCase)                                                            = notRequired "TxILGenericParam: GetInterface"
            override __.Module                                                                                    = notRequired "TxILGenericParam: Module" : Module 
            override __.GetElementType()                                                                          = notRequired "TxILGenericParam: GetElementType"
            override __.InvokeMember(name, invokeAttr, binder, target, args, modifiers, culture, namedParameters) = notRequired "TxILGenericParam: InvokeMember"

        }

    let rec gps = tcref.TyparsNoRange |> List.mapi (fun i gp -> TxGenericParam asm (fun () -> gps, [| |]) i gp) |> List.toArray
    let typarScopePairsOpt =
        match tcref.TyparsNoRange with
        | [] -> None
        | typars -> Some(List.zip typars (gps |> Array.toList))

    let pushTyparScope () =
        match typarScopePairsOpt with
        | None ->
            if asm.Fs1023TraceEnabled() then
                asm.Fs1023TraceMessage(
                    sprintf "[typeproxy] push-typar-scope skipped ty=%s reason=empty" tcref.CompiledName)

            { new IDisposable with member _.Dispose() = () }
        | Some pairs ->
            if asm.Fs1023TraceEnabled() then
                let summary =
                    pairs
                    |> List.map (fun (tp, _) -> sprintf "%s/%A" tp.DisplayName tp.Stamp)
                    |> String.concat ","
                asm.Fs1023TraceMessage(
                    sprintf "[typeproxy] push-typar-scope ty=%s pairs=[%s]" tcref.CompiledName summary)

            asm.PushTyparScope pairs

    let isNested = declTyOpt.IsSome

    let normalizeBindingFlags (bindingAttr: BindingFlags) (defaultFlags: BindingFlags) =
        if bindingAttr = BindingFlags.Default then defaultFlags else bindingAttr

    let createVisibilityScopePredicate bindingAttr defaultFlags =
        let flags = normalizeBindingFlags bindingAttr defaultFlags
        let hasVisibilityFlags = (flags &&& (BindingFlags.Public ||| BindingFlags.NonPublic)) <> enum<BindingFlags> 0
        let includePublic =
            if hasVisibilityFlags then (flags &&& BindingFlags.Public) <> enum 0 else true
        let includeNonPublic =
            if hasVisibilityFlags then (flags &&& BindingFlags.NonPublic) <> enum 0 else false
        let hasScopeFlags = (flags &&& (BindingFlags.Instance ||| BindingFlags.Static)) <> enum 0
        let includeInstance =
            if hasScopeFlags then (flags &&& BindingFlags.Instance) <> enum 0 else true
        let includeStatic =
            if hasScopeFlags then (flags &&& BindingFlags.Static) <> enum 0 else true

        fun (isPublic: bool) (isStatic: bool) ->
            let visibilityOk =
                if includePublic && includeNonPublic then true
                elif includePublic then isPublic
                elif includeNonPublic then not isPublic
                else isPublic

            let scopeOk =
                if includeInstance && includeStatic then true
                elif includeInstance then not isStatic
                elif includeStatic then isStatic
                else true

            visibilityOk && scopeOk

    member internal _.TyconRef = tcref

    override __.Name = tcref.CompiledName 
    override __.Assembly = (asm :> Assembly) 
    override __.DeclaringType = declTyOpt |> optionToNull
    override _.Module = asm.ManifestModule
    override __.MemberType = if isNested then MemberTypes.NestedType else MemberTypes.TypeInfo

    override __.FullName = tcref.CompiledRepresentationForNamedType.FullName
                    
    override __.Namespace =
        let nsParts =
            tcref.CompilationPath.AccessPath
            |> List.choose (fun (name, kind) ->
                match kind with
                | ModuleOrNamespaceKind.Namespace _ -> Some(CompilationPath.DemangleEntityName name kind)
                | _ -> None)

        match nsParts with
        | [] -> null
        | _ -> String.concat "." nsParts
    override __.BaseType = null//inp. |> Option.map (TxILType (gps, [| |])) |> optionToNull
    override __.GetInterfaces() = tcref.ImmediateInterfaceTypesOfFSharpTycon |> List.map asm.TxTType |> List.toArray

    override this.GetConstructors(bindingFlags) = 
        use _ = pushTyparScope ()
        let visibilityFilter = createVisibilityScopePredicate bindingFlags (BindingFlags.Public ||| BindingFlags.Instance)
        tcref.MembersOfFSharpTyconSorted
        |> List.filter (fun (vref: ValRef) -> vref.IsConstructor)
        |> List.map (fun vref -> TxConstructorDef (this :> Type) vref)
        |> List.filter (fun ctor -> visibilityFilter ctor.IsPublic ctor.IsStatic)
        |> List.toArray

    override this.GetMethods(bindingFlags) =
        use _ = pushTyparScope ()
        let entering = asm.Fs1023TraceEnabled()
        let stopwatch =
            if entering then Stopwatch.StartNew() else null
        if entering then
            asm.Fs1023TraceMessage(
                sprintf
                    "[typeproxy] get-methods begin ty=%s flags=%s"
                    this.FullName
                    (bindingFlags.ToString()))

        let providedMethods =
            tcref.Deref.entity_tycon_tcaug.tcaug_adhoc_list
            |> Seq.map (fun (_, vref: ValRef) -> vref)

        let declaredMethods =
            tcref.MembersOfFSharpTyconSorted
            |> Seq.filter (fun vref ->
                vref.IsMember
                && not vref.IsConstructor
                && not vref.IsExtensionMember)

        let methods =
            Seq.append declaredMethods providedMethods
            |> Seq.distinctBy (fun vref -> vref.Stamp)
            |> Seq.map (fun vref -> TxMethodDef asm this vref)
            |> Seq.toArray
        //inp.Methods.Elements |> Array.map (TxILMethodDef this)

        if entering then
            let elapsed =
                match stopwatch with
                | null -> Double.NaN
                | sw ->
                    sw.Stop()
                    sw.Elapsed.TotalMilliseconds

            asm.Fs1023TraceMessage(
                sprintf
                    "[typeproxy] get-methods end ty=%s flags=%s count=%d elapsedMs=%.3f"
                    this.FullName
                    (bindingFlags.ToString())
                    methods.Length
                    elapsed)

        let visibilityFilter = createVisibilityScopePredicate bindingFlags (BindingFlags.Public ||| BindingFlags.Instance)
        methods |> Array.filter (fun m -> visibilityFilter m.IsPublic m.IsStatic)

    override this.GetField(name, _bindingFlags) = 
        use _ = pushTyparScope ()
        let fieldOpt: RecdField option = tcref.AllFieldTable.FieldByName(name)
        fieldOpt
        |> Option.map (fun field -> TxFieldDefinition asm this gps field) 
        |> optionToNull

    override this.GetFields(bindingAttr) = 
        use _ = pushTyparScope ()
        let visibilityFilter = createVisibilityScopePredicate bindingAttr (BindingFlags.Public ||| BindingFlags.Instance)
        tcref.AllFieldsArray
        |> Array.map (fun field -> TxFieldDefinition asm this gps field)
        |> Array.filter (fun fi -> visibilityFilter fi.IsPublic fi.IsStatic)

    override this.GetEvent(name, bindingFlags) =
        use _ = pushTyparScope ()
        this.GetEvents(bindingFlags)
        |> Array.tryFind (fun ev -> String.Equals(ev.Name, name, StringComparison.Ordinal))
        |> optionToNull

    override this.GetEvents(bindingAttr) =
        use _ = pushTyparScope ()
        let visibilityFilter = createVisibilityScopePredicate bindingAttr (BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.Static)
        let g = asm.TcGlobals
        let eventMap = Dictionary<string, ValRef option ref * ValRef option ref * ValRef option ref>()

        let isFSharpEventProperty (vref: ValRef) =
            vref.IsMember && CompileAsEvent g vref.Attribs && not vref.IsExtensionMember

        let ensureEventParts eventName =
            match eventMap.TryGetValue eventName with
            | true, parts -> parts
            | _ ->
                let parts = (ref None, ref None, ref None)
                eventMap[eventName] <- parts
                parts

        for (vref: ValRef) in tcref.MembersOfFSharpTyconSorted do
            let logicalName = vref.LogicalName
            if isFSharpEventProperty vref then
                let eventName = vref.PropertyName
                let propertyRef, _, _ = ensureEventParts eventName
                propertyRef := Some vref
            elif logicalName.StartsWith("add_", StringComparison.Ordinal) then
                let eventName = logicalName.Substring(4)
                let _, addRef, _ = ensureEventParts eventName
                addRef := Some vref
            elif logicalName.StartsWith("remove_", StringComparison.Ordinal) then
                let eventName = logicalName.Substring(7)
                let _, _, removeRef = ensureEventParts eventName
                removeRef := Some vref

        let declaredEvents =
            eventMap
            |> Seq.choose (fun kvp ->
                let eventName = kvp.Key
                let propertyRef, addRef, removeRef = kvp.Value
                match !addRef, !removeRef with
                | Some addVal, Some removeVal ->
                    let eventInfo =
                        TxEventDefinition asm this eventName !propertyRef addVal removeVal None
                    Some eventInfo
                | _ -> None)

        let providedEvents =
            tcref.Deref.entity_tycon_tcaug.tcaug_provided_events
            |> List.toSeq
            |> Seq.map (fun info ->
                let handlerTy = asm.TxTType info.HandlerType
                TxEventDefinition asm this info.EventName None info.AddMethod info.RemoveMethod (Some handlerTy))

        let events =
            Seq.append declaredEvents providedEvents
            |> Seq.filter (fun ev ->
            let addMethod = ev.GetAddMethod(true)
            let handlerIsStatic = if isNull addMethod then false else addMethod.IsStatic
            let handlerIsPublic = if isNull addMethod then true else addMethod.IsPublic
            visibilityFilter handlerIsPublic handlerIsStatic)
            |> Seq.toArray

        if asm.Fs1023TraceEnabled() then
            asm.Fs1023TraceMessage(
                sprintf
                    "[tastreflection] get-events ty=%s count=%d"
                    this.FullName
                    events.Length)

        events

    override this.GetProperties(bindingAttr) =
        use _ = pushTyparScope ()
        let visibilityFilter = createVisibilityScopePredicate bindingAttr (BindingFlags.Public ||| BindingFlags.Instance)
        let propertyValRefs =
            tcref.MembersOfFSharpTyconSorted
            |> List.choose (fun x ->
                match x.MemberInfo with
                | Some info when
                    info.MemberFlags.MemberKind = SynMemberKind.PropertyGet
                    || info.MemberFlags.MemberKind = SynMemberKind.PropertySet
                    || info.MemberFlags.MemberKind = SynMemberKind.PropertyGetSet ->
                    Some x
                | _ -> None)

        let propertyInfos =
            propertyValRefs
            |> List.map (TxPropertyDefinition asm this gps tcref)

        let existingPropertyNames = HashSet<string>()
        propertyValRefs
        |> List.iter (fun vref -> existingPropertyNames.Add(vref.PropertyName) |> ignore)

        let recordFieldProperties =
            if tcref.IsRecordTycon then
                tcref.AllFieldsArray
                |> Array.toList
                |> List.filter (fun field -> existingPropertyNames.Add field.LogicalName)
                |> List.map (TxRecordFieldPropertyDefinition asm this gps tcref)
            else
                []

        let properties =
            Array.append (propertyInfos |> List.toArray) (recordFieldProperties |> List.toArray)
        properties
        |> Array.filter (fun prop ->
            let accessor =
                match prop.GetGetMethod(true) with
                | null -> prop.GetSetMethod(true)
                | getter -> getter
            if isNull accessor then true else visibilityFilter accessor.IsPublic accessor.IsStatic)

    override this.GetMembers(bindingAttr) = 
        let defaultFlags = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance ||| BindingFlags.Static
        let flags = normalizeBindingFlags bindingAttr defaultFlags
        [| for x in this.GetMethods(flags) do yield (x :> MemberInfo)
           for x in this.GetConstructors(flags) do yield (x :> MemberInfo)
           for x in this.GetFields(flags) do yield (x :> MemberInfo)
           for x in this.GetProperties(flags) do yield (x :> MemberInfo)
           for x in this.GetEvents(flags) do yield (x :> MemberInfo)
           for x in this.GetNestedTypes(flags) do yield (x :> MemberInfo) |]
 
#if !NO_TYPEPROVIDERS
    override this.GetNestedTypes(_bindingFlags) =
        [| for entity in tcref.ModuleOrNamespaceType.TypesByAccessNames.Values do
               yield asm.TxTypeDef (Some (this :> Type))  (tcref.NestedTyconRef entity) |]
#else
    override this.GetNestedTypes(_bindingFlags) =
        [| for entity in tcref.ModuleOrNamespaceType.TypesByAccessNames.Values do
               yield asm.TxTypeDef (Some (this :> Type))  (tcref.NestedTyconRef entity) |]
#endif

    // GetNestedType is used for linking to the binding context
#if !NO_TYPEPROVIDERS
    override this.GetNestedType(name, _bindingFlags) =
        match tcref.ModuleOrNamespaceType.TypesByMangledName.TryFind name with
        | None -> null
        | Some entity -> asm.TxTypeDef (Some (this :> Type))  (tcref.NestedTyconRef entity)
#else
    override this.GetNestedType(name, _bindingFlags) =
        match tcref.ModuleOrNamespaceType.TypesByMangledName.TryFind name with 
        | None -> null
        | Some entity -> asm.TxTypeDef (Some (this :> Type))  (tcref.NestedTyconRef entity) 
#endif

        
    override this.GetMethodImpl(name, bindingFlags, binder, _callConvention, types, modifiers) =
        let allMethods = this.GetMethods(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance ||| BindingFlags.Static)

        let matchesName (m: MethodInfo) =
            String.Equals(m.Name, name, StringComparison.Ordinal)

        let hasFlag flag = bindingFlags &&& flag = flag

        let includePublic = hasFlag BindingFlags.Public
        let includeNonPublic = hasFlag BindingFlags.NonPublic
        let includeStatic = hasFlag BindingFlags.Static
        let includeInstance = hasFlag BindingFlags.Instance

        let matchesVisibility (m: MethodInfo) =
            if includePublic && not includeNonPublic then m.IsPublic
            elif includeNonPublic && not includePublic then not m.IsPublic
            elif includePublic || includeNonPublic then true
            else m.IsPublic

        let matchesScope (m: MethodInfo) =
            if includeStatic && not includeInstance then m.IsStatic
            elif includeInstance && not includeStatic then not m.IsStatic
            elif includeStatic || includeInstance then true
            else true

        let matchesParameters (m: MethodInfo) =
            match types with
            | null
            | [||] -> true
            | expected ->
                let parameters = m.GetParameters()
                parameters.Length = expected.Length &&
                Array.forall2 (fun (paramInfo: ParameterInfo) (expectedType: Type) -> eqType paramInfo.ParameterType expectedType) parameters expected

        let candidates =
            allMethods
            |> Array.filter (fun m -> matchesName m && matchesVisibility m && matchesScope m && matchesParameters m)

        match candidates with
        | [||] -> null
        | [|single|] -> single
        | many when not (isNull binder) ->
            let selected = binder.SelectMethod(bindingFlags, many |> Array.map (fun m -> m :> MethodBase), types, modifiers)
            match selected with
            | null -> null
            | :? MethodInfo as mi -> mi
            | _ -> null
        | many -> many.[0]

    override this.GetPropertyImpl(name, bindingFlags, _binder, returnType, types, _modifiers) =
        let allProperties = this.GetProperties(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance ||| BindingFlags.Static)

        let matchesName (p: PropertyInfo) =
            String.Equals(p.Name, name, StringComparison.Ordinal)

        let hasFlag flag = bindingFlags &&& flag = flag

        let includePublic = hasFlag BindingFlags.Public
        let includeNonPublic = hasFlag BindingFlags.NonPublic
        let includeStatic = hasFlag BindingFlags.Static
        let includeInstance = hasFlag BindingFlags.Instance

        let accessorFor (p: PropertyInfo) =
            match p.GetGetMethod(true) with
            | null -> p.GetSetMethod(true)
            | getter -> getter

        let matchesVisibility (p: PropertyInfo) =
            let accessor = accessorFor p
            if isNull accessor then true
            elif includePublic && not includeNonPublic then accessor.IsPublic
            elif includeNonPublic && not includePublic then not accessor.IsPublic
            elif includePublic || includeNonPublic then true
            else accessor.IsPublic

        let matchesScope (p: PropertyInfo) =
            let accessor = accessorFor p
            if isNull accessor then true
            elif includeStatic && not includeInstance then accessor.IsStatic
            elif includeInstance && not includeStatic then not accessor.IsStatic
            elif includeStatic || includeInstance then true
            else true

        let matchesReturnType (p: PropertyInfo) =
            if isNull returnType then true
            else
                let propertyType = p.PropertyType
                eqType propertyType returnType
                ||
                (not (isNull propertyType)
                 && not (isNull returnType)
                 && String.Equals(propertyType.FullName, returnType.FullName, StringComparison.Ordinal))

        let matchesParameters (p: PropertyInfo) =
            match types with
            | null ->
                // Treat a null 'types' array as "no constraint" just like System.RuntimeType does so callers
                // of Type.GetProperty("Item") still see indexers even if they don't provide index parameter metadata.
                true
            | [||] -> p.GetIndexParameters().Length = 0
            | expected ->
                let parameters = p.GetIndexParameters()
                let typesEqual (a: Type) (b: Type) =
                    eqType a b
                    || (not (isNull a) && not (isNull b) && String.Equals(a.FullName, b.FullName, StringComparison.Ordinal))
                parameters.Length = expected.Length &&
                Array.forall2 (fun (paramInfo: ParameterInfo) expectedType -> typesEqual paramInfo.ParameterType expectedType) parameters expected

        let rec tryFind index =
            if index >= allProperties.Length then null
            else
                let prop = allProperties.[index]
                let nameOk = matchesName prop
                let visibilityOk = nameOk && matchesVisibility prop
                let scopeOk = visibilityOk && matchesScope prop
                let returnOk = scopeOk && matchesReturnType prop
                let parametersOk = returnOk && matchesParameters prop
                if parametersOk then
                    prop
                else
                    tryFind (index + 1)

        let primary = tryFind 0

        let result =
            if not (isNull primary) then primary
            elif not (isNull types) then
                allProperties
                |> Array.tryFind (fun p ->
                    String.Equals(p.Name, name, StringComparison.Ordinal)
                    && (let idxParams = p.GetIndexParameters()
                        idxParams.Length = types.Length))
                |> Option.defaultValue null
            else
                null

        result
        //inp.Methods.FindByNameAndArity(name, types.Length)
        //|> Array.find (fun md -> eqTypesAndTTypes types md.ParameterTypes)
        //|> TxILMethodDef this

    override this.GetConstructorImpl(bindingFlags, binder, _callConvention, types, modifiers) = 
        let allConstructors = this.GetConstructors(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance ||| BindingFlags.Static)

        let hasFlag flag = bindingFlags &&& flag = flag
        let includePublic = hasFlag BindingFlags.Public
        let includeNonPublic = hasFlag BindingFlags.NonPublic
        let includeStatic = hasFlag BindingFlags.Static
        let includeInstance = hasFlag BindingFlags.Instance

        let matchesVisibility (c: ConstructorInfo) =
            if includePublic && not includeNonPublic then c.IsPublic
            elif includeNonPublic && not includePublic then not c.IsPublic
            elif includePublic || includeNonPublic then true
            else c.IsPublic

        let matchesScope (c: ConstructorInfo) =
            if includeStatic && not includeInstance then c.IsStatic
            elif includeInstance && not includeStatic then not c.IsStatic
            elif includeStatic || includeInstance then true
            else true

        let matchesParameters (c: ConstructorInfo) =
            match types with
            | null
            | [||] -> true
            | expected ->
                let parameters = c.GetParameters()
                parameters.Length = expected.Length &&
                Array.forall2 (fun (paramInfo: ParameterInfo) (expectedType: Type) -> eqType paramInfo.ParameterType expectedType) parameters expected

        let candidates =
            allConstructors
            |> Array.filter (fun c -> matchesVisibility c && matchesScope c && matchesParameters c)

        match candidates with
        | [||] -> null
        | [|single|] -> single
        | many when not (isNull binder) ->
            match binder.SelectMethod(bindingFlags, many |> Array.map (fun c -> c :> MethodBase), types, modifiers) with
            | :? ConstructorInfo as ctor -> ctor
            | _ -> null
        | many -> many.[0]

    // Every implementation of System.Type must meaningfully implement these
    override this.MakeGenericType(args) = ReflectTypeSymbol(ReflectTypeSymbolKind.Generic this, args) :> Type
    override this.MakeArrayType() = ReflectTypeSymbol(ReflectTypeSymbolKind.SDArray, [| this |]) :> Type
    override this.MakeArrayType arg = ReflectTypeSymbol(ReflectTypeSymbolKind.Array arg, [| this |]) :> Type
    override this.MakePointerType() = ReflectTypeSymbol(ReflectTypeSymbolKind.Pointer, [| this |]) :> Type
    override this.MakeByRefType() = ReflectTypeSymbol(ReflectTypeSymbolKind.ByRef, [| this |]) :> Type

    override __.GetAttributeFlagsImpl() = 
        let tycon = tcref.Deref

        let add flag condition acc =
            if condition then acc ||| flag else acc

        let visibility =
            let access = tycon.Accessibility
            if isNested then
                if access.IsPublic then TypeAttributes.NestedPublic
                elif access.IsInternal then TypeAttributes.NestedAssembly
                else TypeAttributes.NestedPrivate
            else
                if access.IsPublic then TypeAttributes.Public
                else TypeAttributes.NotPublic

        let kindAttrs =
            if isInterfaceTyconRef tcref then
                TypeAttributes.Interface ||| TypeAttributes.Abstract
            else
                enum<TypeAttributes>(0)

        let baseAttr = visibility ||| kindAttrs

        let attrWithValueSemantics =
            add TypeAttributes.Sealed (tcref.IsStructOrEnumTycon || tcref.IsFSharpStructOrEnumTycon) baseAttr

        let attrWithProvidedOrILInfo =
            match tycon.TypeReprInfo with
            | TProvidedTypeRepr info ->
                attrWithValueSemantics
                |> add TypeAttributes.Sealed info.IsSealed
                |> add TypeAttributes.Abstract info.IsAbstract
            | TILObjectRepr (TILObjectReprData(_, _, td)) ->
                attrWithValueSemantics
                |> add TypeAttributes.Sealed td.IsSealed
                |> add TypeAttributes.Abstract td.IsAbstract
            | _ -> attrWithValueSemantics

        attrWithProvidedOrILInfo

    override __.IsValueTypeImpl() = tcref.IsStructOrEnumTycon || tcref.IsFSharpStructOrEnumTycon
    override __.IsArrayImpl() = false
    override __.IsByRefImpl() = false
    override __.IsPointerImpl() = false
    override __.IsPrimitiveImpl() = false
    override __.IsCOMObjectImpl() = false
    override __.IsGenericType = (gps.Length <> 0)
    override __.IsGenericTypeDefinition = (gps.Length <> 0)
    override __.HasElementTypeImpl() = false

    override this.UnderlyingSystemType = (this :> Type)
    override __.GetCustomAttributesData() = tcref.Attribs |> TxCustomAttributesData

    override this.Equals(that:obj) = System.Object.ReferenceEquals (this, that)  
    override this.GetHashCode() =  hash tcref.CompiledName
    override this.IsAssignableFrom(otherTy) = base.IsAssignableFrom(otherTy) || this.Equals(otherTy)
    override this.IsSubclassOf(otherTy) = base.IsSubclassOf(otherTy) || tcref.IsFSharpDelegateTycon && otherTy = typeof<Delegate> // F# quotations implementation

    override this.AssemblyQualifiedName                                                            = this.FullName + ", " + this.Assembly.FullName

    override this.ToString() = this.FullName
    
    override __.GetGenericArguments() = gps
    override __.GetGenericTypeDefinition() = this :> Type //notRequired "GetGenericTypeDefinition"
    override __.GetMember(_name, _memberType, _bindingFlags)                                                      = notRequired "TxILTypeDef: GetMember"
    override __.GUID                                                                                      = notRequired "TxILTypeDef: GUID"
    override __.GetCustomAttributes(_inherited)                                                            = notRequired "TxILTypeDef: GetCustomAttributes"
    override __.GetCustomAttributes(_attributeType, _inherited)                                             = notRequired "TxILTypeDef: GetCustomAttributes"
    override __.IsDefined(_attributeType, _inherited)                                                       = notRequired "TxILTypeDef: IsDefined"
    override __.GetInterface(_name, _ignoreCase)                                                            = notRequired "TxILTypeDef: GetInterface"
    override __.GetElementType()                                                                          = notRequired "TxILTypeDef: GetElementType"
    override this.InvokeMember(name, invokeAttr, binder, target, args, modifiers, culture, namedParameters) =
        invokeMemberCore (this :> Type) name invokeAttr binder target args modifiers culture namedParameters

    member x.Metadata = tcref
    member x.MakeMethodInfo (declTy,md) = TxMethodDef asm declTy md
    member x.MakeConstructorInfo (declTy,md) = TxConstructorDef declTy md

    //interface IReflectableType with 
    //    member x.GetTypeInfo() = 
    //        { new TypeInfo() with 
    //            member __.AsType() = x :?> Type 
    //        }
             

and [<DebuggerDisplay("{DebuggerDisplay,nq}")>] ReflectAssembly(builder: TypeReflectionBuilder, g: TcGlobals, ccu: CcuThunk, location:string) as asm =
    inherit Assembly()

    // A table tracking how type definition objects are translated.
    let txTable = TxTable<Type>()
    let typarScopeStack =
        ThreadLocal<ResizeArray<TyparScope>>(fun () -> ResizeArray())
    let globalTyparByKey = ConcurrentDictionary<string, Type>(StringComparer.Ordinal)
    let globalTyparByStamp = ConcurrentDictionary<Stamp, Type>()
    let typarDebugEnabled () =
        match Environment.GetEnvironmentVariable("FS1023_TRACE_TYPAR") with
        | null -> false
        | value -> value.Trim() = "1"

    let typarScopeKey (tp: Typar) =
        tp.DisplayName + "|" + string tp.Kind

    let pushTyparScope (pairs: seq<Typar * Type>) =
        let enumerated = pairs |> Seq.toArray
        if enumerated.Length = 0 then
            { new IDisposable with member _.Dispose() = () }
        else
            let scope =
                { ByStamp = Dictionary<Stamp, Type>(enumerated.Length)
                  ByKey = Dictionary<string, Type>(StringComparer.Ordinal) }
            for tp, ty in enumerated do
                let key = typarScopeKey tp
                scope.ByStamp[tp.Stamp] <- ty
                scope.ByKey[key] <- ty
                globalTyparByKey[key] <- ty
                globalTyparByStamp[tp.Stamp] <- ty
            let stack = typarScopeStack.Value
            stack.Add scope
            { new IDisposable with member _.Dispose() = stack.RemoveAt(stack.Count - 1) }
    let createTyparPlaceholder (tp: Typar) =
        { new Type() with
            override __.Name = tp.Name
            override __.Assembly = (asm :> Assembly)
            override __.FullName = tp.Name
            override __.IsGenericParameter = true
            override __.GenericParameterPosition = 0
            override __.GetGenericParameterConstraints() = [||]
            override __.MemberType = enum 0
            override __.Namespace = null
            override __.DeclaringType = null
            override __.BaseType = null
            override __.GetInterfaces() = [||]
            override this.GetConstructors(_bindingFlags) = [||]
            override this.GetMethods(_bindingFlags) = [||]
            override this.GetField(_name, _bindingFlags) = null
            override this.GetFields(_bindingFlags) = [||]
            override this.GetEvent(_name, _bindingFlags) = null
            override this.GetEvents(_bindingFlags) = [||]
            override this.GetProperties(_bindingFlags) = [||]
            override this.GetMembers(_bindingFlags) = [||]
            override this.GetNestedTypes(_bindingFlags) = [||]
            override this.GetNestedType(_name, _bindingFlags) = null
            override this.GetPropertyImpl(_name, _bindingFlags, _binder, _returnType, _types, _modifiers) = null
            override this.MakeGenericType(_args) = notRequired "MakeGenericType"
            override this.MakeArrayType() = ReflectTypeSymbol(ReflectTypeSymbolKind.SDArray, [| this |]) :> Type
            override this.MakeArrayType arg = ReflectTypeSymbol(ReflectTypeSymbolKind.Array arg, [| this |]) :> Type
            override this.MakePointerType() = ReflectTypeSymbol(ReflectTypeSymbolKind.Pointer, [| this |]) :> Type
            override this.MakeByRefType() = ReflectTypeSymbol(ReflectTypeSymbolKind.ByRef, [| this |]) :> Type
            override __.GetAttributeFlagsImpl() = TypeAttributes.Public ||| TypeAttributes.Class ||| TypeAttributes.Sealed
            override __.IsArrayImpl() = false
            override __.IsByRefImpl() = false
            override __.IsPointerImpl() = false
            override __.IsPrimitiveImpl() = false
            override __.IsCOMObjectImpl() = false
            override __.IsGenericType = false
            override __.IsGenericTypeDefinition = false
            override __.HasElementTypeImpl() = false
            override __.GetElementType() = null
            override __.InvokeMember(_name, _invokeAttr, _binder, _target, _args, _modifiers, _culture, _namedParameters) = notRequired "InvokeMember"
            override __.UnderlyingSystemType = typeof<obj>
            override __.GetCustomAttributes(_inherit) = [||]
            override __.GetCustomAttributes(_attributeType, _inherit) = [||]
            override __.IsDefined(_attributeType, _inherit) = false
            override __.StructLayoutAttribute = notRequired "StructLayoutAttribute"
            override __.GUID = Guid.Empty
            override __.GetHashCode() = hash tp.Stamp
            override __.Equals(other: obj) =
                match other with
                | :? Type as ty -> ty.IsGenericParameter && String.Equals(ty.Name, tp.Name, StringComparison.Ordinal)
                | _ -> false
            override __.AssemblyQualifiedName = tp.Name
            override __.Module = asm.ManifestModule
            override this.GetConstructorImpl(_bindingAttr, _binder, _callConvention, _types, _modifiers) = null
            override this.GetInterface(_name, _ignoreCase) = null
            override this.GetMethodImpl(_name, _bindingAttr, _binder, _callConvention, _types, _modifiers) = null
            override __.ToString() = tp.Name }

    let txTypeDef (declTyOpt: Type option) (inp: TyconRef) =
        let isNested = Option.isSome declTyOpt
        txTable.Get inp.Stamp (fun () ->
            let stopwatch =
                if builder.EnableProfiling || builder.Fs1023TraceEnabled() then Stopwatch.StartNew()
                else null
            if builder.Fs1023TraceEnabled() then
                builder.Fs1023TraceMessage(
                    sprintf "[tastreflection] type-begin name=%s nested=%b" inp.LogicalName isNested)

            let typ = ReflectTypeDefinition(asm, declTyOpt, inp) :> System.Type

            let duration =
                if isNull stopwatch then None
                else
                    stopwatch.Stop()
                    Some stopwatch.Elapsed
            builder.NotifyTypeCreated duration

            if builder.Fs1023TraceEnabled() then
                let ctorCount =
                    typ.GetConstructors(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance ||| BindingFlags.Static).Length
                let methodCount =
                    typ.GetMethods(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance ||| BindingFlags.Static).Length
                let elapsedTicks =
                    match duration with
                    | Some span -> span.Ticks.ToString(CultureInfo.InvariantCulture)
                    | None -> "n/a"

                builder.Fs1023TraceMessage(
                    sprintf "[tastreflection] type-end name=%s nested=%b ctorCount=%d methodCount=%d elapsedTicks=%s"
                        inp.LogicalName
                        isNested
                        ctorCount
                        methodCount
                        elapsedTicks)
            typ)

    let verboseModuleScan =
        lazy (
            match Environment.GetEnvironmentVariable("FS1023_TRACE_TAST_VERBOSE") with
            | "1" -> true
            | _ -> false)

    let rec collectModuleContents declTyOpt (mtyp: ModuleOrNamespaceType) : seq<Type> =
        if builder.Fs1023TraceEnabled() || verboseModuleScan.Value then
            let moduleCount = mtyp.ModuleAndNamespaceDefinitions |> List.length
            let typeCount = mtyp.TypeAndExceptionDefinitions |> List.length
            let message =
                sprintf
                    "[tastreflection] module-contents-scan nested=%b modules=%d types=%d"
                    (Option.isSome declTyOpt)
                    moduleCount
                    typeCount
            if builder.Fs1023TraceEnabled() then
                builder.Fs1023TraceMessage(message)
            elif verboseModuleScan.Value then
                printfn "%s" message
        seq {
            for modul in mtyp.ModuleAndNamespaceDefinitions do
                let isNamespace = modul.IsNamespace
                if builder.Fs1023TraceEnabled() then
                    let entityCount =
                        modul.ModuleOrNamespaceType.AllEntities
                        |> Seq.length
                    builder.Fs1023TraceMessage(
                        sprintf
                            "[tastreflection] module-contents module=%s nested=%b isNamespace=%b entityCount=%d"
                            modul.CompiledName
                            (Option.isSome declTyOpt)
                            isNamespace
                            entityCount)
                if isNamespace then
                    yield! collectModuleContents declTyOpt modul.ModuleOrNamespaceType
                else
                    let moduleType = txTypeDef declTyOpt (mkLocalModuleRef modul)
                    if builder.Fs1023TraceEnabled() then
                        builder.Fs1023TraceMessage(
                            sprintf
                                "[tastreflection] module-emitted name=%s nested=%b"
                                moduleType.FullName
                                (Option.isSome declTyOpt))
                    yield moduleType
                    yield! collectModuleContents (Some moduleType) modul.ModuleOrNamespaceType

            for tycon in mtyp.TypeAndExceptionDefinitions do
                let currentType = txTypeDef declTyOpt (mkLocalTyconRef tycon)
                if builder.Fs1023TraceEnabled() then
                    let childEntityCount =
                        tycon.ModuleOrNamespaceType.AllEntities
                        |> Seq.length
                    builder.Fs1023TraceMessage(
                        sprintf
                            "[tastreflection] module-contents type=%s nested=%b childEntityCount=%d isModule=%b isProvided=%b"
                            tycon.CompiledName
                            (Option.isSome declTyOpt)
                            childEntityCount
                            tycon.IsModuleOrNamespace
                            tycon.IsProvidedGeneratedTycon)
                yield currentType
                yield! collectModuleContents (Some currentType) tycon.ModuleOrNamespaceType
        }

    let manifestModule = lazy (ReflectModule(asm))
    let name = lazy new AssemblyName(match ccu.ILScopeRef with ILScopeRef.Local -> ccu.AssemblyName | _ -> ccu.ILScopeRef.QualifiedName)
    let fullName = lazy name.Value.ToString()
    let typesLock = obj()
    let mutable cachedTypes : Type[] = Array.empty
    let mutable typesComputed = false

    let computeTypes () =
        let collected = collectModuleContents None ccu.Contents.ModuleOrNamespaceType |> Seq.toArray
        let registeredTycons = builder.GetRegisteredTycons(ccu)
        let dedup = HashSet<string>()
        let addIfNew (ty: Type) =
            let fullName = ty.FullName
            let key =
                if String.IsNullOrEmpty fullName then ty.Name
                else fullName
            if String.IsNullOrEmpty key then
                true
            else
                dedup.Add key
        let initial =
            collected
            |> Array.filter (fun ty ->
                let added = addIfNew ty
                if not added && builder.Fs1023TraceEnabled() then
                    builder.Fs1023TraceMessage(
                        sprintf "[tastreflection] skip-duplicate type=%s assembly=%s" ty.FullName ccu.AssemblyName)
                added)
        let extras =
            registeredTycons
            |> Array.choose (fun tcref ->
                try
                    let ty = txTypeDef None tcref
                    if addIfNew ty then Some ty
                    else None
                with ex ->
                    if builder.Fs1023TraceEnabled() then
                        builder.Fs1023TraceMessage(
                            sprintf
                                "[tastreflection] skip-registered-tycon name=%s reason=%s"
                                tcref.CompiledName
                                ex.Message)
                    None)

        if initial.Length = 0 && extras.Length > 0 then
            if builder.Fs1023TraceEnabled() || verboseModuleScan.Value then
                let knownTypeCount = txTable.Values |> Seq.length
                let rootModules = ccu.RootModulesAndNamespaces |> List.map (fun m -> m.CompiledName)
                let registeredNames =
                    registeredTycons
                    |> Array.map (fun tcref -> tcref.CompiledName)
                let message =
                    sprintf
                        "[tastreflection] module-fallback assembly=%s stamp=%A knownTypes=%d registeredTycons=%d rootModuleCount=%d rootModules=%A registered=%A"
                        ccu.AssemblyName
                        ccu.Stamp
                        knownTypeCount
                        registeredTycons.Length
                        rootModules.Length
                        rootModules
                        registeredNames
                if builder.Fs1023TraceEnabled() then
                    builder.Fs1023TraceMessage(message)
                elif verboseModuleScan.Value then
                    printfn "%s" message

        let result = Array.append initial extras
        if builder.Fs1023TraceEnabled() then
            builder.Fs1023TraceMessage(
                sprintf
                    "[tastreflection] assembly-types assembly=%s count=%d registeredExtras=%d"
                    ccu.AssemblyName
                    result.Length
                    extras.Length)

        result

    member private asm.EnsureTypes() =
        if not typesComputed then
            lock typesLock (fun () ->
                if not typesComputed then
                    cachedTypes <- computeTypes ()
                    typesComputed <- true)
        cachedTypes

    member internal asm.InvalidateTypes() =
        lock typesLock (fun () -> typesComputed <- false)

    override x.GetTypes () = asm.EnsureTypes()
    override x.Location = location

    override x.ManifestModule = manifestModule.Value :> Module

    override x.GetModules(_getResourceModules: bool) : Module[] = [| manifestModule.Value :> Module |]

    override x.GetLoadedModules(_getResourceModules: bool) : Module[] = [| manifestModule.Value :> Module |]

    override x.GetModule(name: string) =
        let moduleInstance = manifestModule.Value :> Module
        if String.Equals(moduleInstance.Name, name, StringComparison.Ordinal) then
            moduleInstance
        else
            null

    override x.GetType (nm:string) = 
        if nm.Contains("+") then 
            let i = nm.LastIndexOf("+")
            let enc,nm2 = nm.[0..i-1], nm.[i+1..]
            match x.GetType(enc) with 
            | null -> null
            | t -> t.GetNestedType(nm2,BindingFlags.Public ||| BindingFlags.NonPublic)
        elif nm.Contains("`") then
            let argI, argE = nm.LastIndexOf("["), nm.LastIndexOf("]")
            let i = nm.LastIndexOf(".", nm.LastIndexOf("`"))
            let nsp,nm2, args = nm.[0..i-1], nm.[i+1..argI-1], nm.[argI+1..argE-1]
            let genTypeArgs = 
                args.Split([|","|], StringSplitOptions.RemoveEmptyEntries) 
                |> Array.map (fun a ->
                    match x.GetType(a) with
                    | null -> Type.GetType(a)
                    | a -> a
                )
            match x.TryBindType(Some nsp, nm2) with 
            | Some t -> t.MakeGenericType(genTypeArgs)
            | None -> null
        elif nm.Contains(".") then 
            let i = nm.LastIndexOf(".")
            let nsp,nm2 = nm.[0..i-1], nm.[i+1..]
            x.TryBindType(Some nsp, nm2) |> optionToNull
        else
            x.TryBindType(None, nm) |> optionToNull

    override x.GetName () = name.Value

    override x.FullName = fullName.Value

    override x.ReflectionOnly = true

    member private _.DebuggerDisplay =
        sprintf "TastReflectionAssembly (%s @ %s)" fullName.Value location

    override x.ToString() = x.DebuggerDisplay

    /// Makes a field definition read from a binary available as a FieldInfo. Not all methods are implemented.
    member asm.TxTType (typ: TType) =
        // TODO: may need something special for "System.Void"
        let typ = stripTyEqnsWrtErasure Erasure.EraseAll g typ
        if isUnitTy g typ then typeof<Void>
        else
            match typ with
            | AppTy g (tcref, tinst) ->
                builder.RecordDependency tcref
                let ccuofTyconRef =
                    match ccuOfTyconRef tcref with
                    | Some ccuofTyconRef -> ccuofTyconRef
                    | None -> ccu
                let reflAssem =
                    if obj.ReferenceEquals(ccuofTyconRef, ccu) then asm
                    else builder.GetOrAddAssembly ccuofTyconRef
                let tcrefR = reflAssem.TxTypeDef None tcref
                match tinst with
                | [] -> tcrefR
                | args -> tcrefR.MakeGenericType(Array.map asm.TxTType (Array.ofList args))
            | ty when isArrayTy g ty ->
                let ety = destArrayTy g ty
                let etyR = asm.TxTType ety
                match rankOfArrayTy g ty with
                | 1 -> etyR.MakeArrayType()
                | n -> etyR.MakeArrayType(n)
            | ty when isNativePtrTy g ty ->
                let etyR = destNativePtrTy g ty |> asm.TxTType
                etyR.MakePointerType()
            | ty when isByrefTy g ty ->
                let etyR = destByrefTy g ty |> asm.TxTType
                etyR.MakeByRefType()
            | ty when isTyparTy g ty ->
                let tp = destTyparTy g ty
                match tp.Solution with
                | Some solvedTy ->
                    asm.TxTType solvedTy
                | None ->
                    let stack = typarScopeStack.Value
                    let mutable resolved: Type = null
                    let mutable idx = stack.Count - 1
                    while idx >= 0 && isNull resolved do
                        let scope = stack[idx]
                        match scope.ByStamp.TryGetValue tp.Stamp with
                        | true, ty -> resolved <- ty
                        | _ ->
                            let key = typarScopeKey tp
                            match scope.ByKey.TryGetValue key with
                            | true, ty -> resolved <- ty
                            | _ -> ()
                        idx <- idx - 1

                    if isNull resolved then
                        let key = typarScopeKey tp
                        match globalTyparByKey.TryGetValue key with
                        | true, ty -> resolved <- ty
                        | _ -> ()

                    if isNull resolved then
                        match globalTyparByStamp.TryGetValue tp.Stamp with
                        | true, ty -> resolved <- ty
                        | _ -> ()

                    if isNull resolved then
                        let placeholder = createTyparPlaceholder tp
                        resolved <- placeholder
                        globalTyparByStamp[tp.Stamp] <- placeholder
                        let key = typarScopeKey tp
                        globalTyparByKey[key] <- placeholder
                        if builder.Fs1023TraceEnabled() then
                            builder.Fs1023TraceMessage(
                                sprintf "[typeproxy] typar-lookup miss name=%s stamp=%A (synthesized placeholder)" tp.DisplayName tp.Stamp)
                        elif typarDebugEnabled() then
                            let scopeDepth = typarScopeStack.Value.Count
                            System.Console.WriteLine(
                                "[fs1023][typar-miss] assembly={0} name={1} stamp={2} depth={3} synthesized=placeholder",
                                ccu.AssemblyName,
                                tp.DisplayName,
                                tp.Stamp,
                                scopeDepth)
                    resolved
            | _ -> failwithf "Unsupported TxTType %+A" typ

    member _.TcGlobals = g

    member x.TryBindType(nsp: string option, nm: string) : Type option =
        match ccu.RootModulesAndNamespaces |> List.tryFind (fun x -> x.CompiledName = nm) with
        | Some td -> txTypeDef None (mkLocalTyconRef td) |> Some
        | None ->
            match ccu.RootTypeAndExceptionDefinitions |> List.tryFind (fun x -> x.CompiledName = nm) with
            | Some td -> txTypeDef None (mkLocalTyconRef td) |> Some
            | None ->
                txTable.Values
                |> Seq.tryFind (fun t ->
                    (match nsp with
                     | Some ns -> t.Namespace = ns
                     | None -> true)
                    && t.Name = nm)

    member __.TxTypeDef declTyOpt inp = txTypeDef declTyOpt inp
    member internal _.Builder = builder
    member internal _.Fs1023TraceEnabled() = builder.Fs1023TraceEnabled()
    member internal _.Fs1023TraceMessage(message: string) = builder.Fs1023TraceMessage(message)
    member internal _.PushTyparScope(pairs: seq<Typar * Type>) = pushTyparScope pairs

and [<DebuggerDisplay("{DebuggerDisplay,nq}")>] ReflectModule(asm: ReflectAssembly) =
    inherit Module()

    let moduleVersionId = Guid.NewGuid()
    let defaultBindingFlags = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static ||| BindingFlags.Instance
    let moduleName =
        let asmName = asm.GetName()
        if isNull asmName || String.IsNullOrEmpty asmName.Name then
            "TastReflectionModule"
        else
            asmName.Name

    let bindingFlagsOrDefault flags =
        if flags = BindingFlags.Default then defaultBindingFlags else flags

    let allTypes () = asm.GetTypes()

    let tryGetTypeByName (name: string) (ignoreCase: bool) =
        let comparison = if ignoreCase then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal
        allTypes ()
        |> Array.tryFind (fun ty ->
            String.Equals(ty.FullName, name, comparison)
            || String.Equals(ty.Name, name, comparison))

    let tryPick projection =
        allTypes () |> Array.tryPick projection

    let gather projection =
        allTypes () |> Array.collect projection

    let notSupported memberName =
        raise (NotSupportedException(sprintf "%s is not supported for TastReflection modules." memberName))

    override _.Assembly = asm :> Assembly
    override _.Name = moduleName
    override _.FullyQualifiedName = moduleName
    override _.ScopeName = moduleName
    override _.ModuleVersionId = moduleVersionId
    override _.MDStreamVersion = 0
    override _.MetadataToken = 0

    override _.GetPEKind(peKind, machine) =
        peKind <- PortableExecutableKinds.ILOnly
        machine <- ImageFileMachine.I386

    override _.IsResource() = false

    override _.IsDefined(_attributeType, _inherit) = false

    override _.GetTypes() = allTypes ()

    override _.GetType(name: string) =
        tryGetTypeByName name false |> optionToNull

    override _.GetType(name: string, ignoreCase: bool) =
        tryGetTypeByName name ignoreCase |> optionToNull

    override _.GetType(name: string, throwOnError: bool, ignoreCase: bool) =
        match tryGetTypeByName name ignoreCase with
        | Some ty -> ty
        | None when throwOnError ->
            raise (TypeLoadException(sprintf "Type '%s' not found in module '%s'." name moduleName))
        | None -> null

    override _.FindTypes(filter, filterCriteria) =
        let predicate =
            if isNull filter then (fun _ -> true)
            else fun ty -> filter.Invoke(ty, filterCriteria)
        allTypes () |> Array.filter predicate

    override _.GetCustomAttributesData() : IList<CustomAttributeData> =
        upcast List<CustomAttributeData>()

    override _.GetCustomAttributes(_inherit) = [| |]

    override _.GetCustomAttributes(_attributeType, _inherit) = [| |]

    override _.GetMethods(bindingAttr) =
        let flags = bindingFlagsOrDefault bindingAttr
        gather (fun ty -> ty.GetMethods(flags))

    override _.GetMethodImpl(name, bindingAttr, binder, callConvention, types, modifiers) =
        let flags = bindingFlagsOrDefault bindingAttr
        tryPick (fun ty ->
            match ty.GetMethod(name, flags, binder, callConvention, types, modifiers) with
            | null -> None
            | mi -> Some mi)
        |> optionToNull

    override _.GetFields(bindingAttr) =
        let flags = bindingFlagsOrDefault bindingAttr
        gather (fun ty -> ty.GetFields(flags))

    override _.GetField(name: string, bindingAttr) =
        let flags = bindingFlagsOrDefault bindingAttr
        tryPick (fun ty ->
            match ty.GetField(name, flags) with
            | null -> None
            | fi -> Some fi)
        |> optionToNull

    override _.ResolveField(metadataToken: int, _genericTypeArguments, _genericMethodArguments) =
        notSupported (sprintf "ResolveField(%d, ..., ...)" metadataToken)

    override _.ResolveMethod(metadataToken: int, _genericTypeArguments, _genericMethodArguments) =
        notSupported (sprintf "ResolveMethod(%d, ..., ...)" metadataToken)

    override _.ResolveMember(metadataToken: int, _genericTypeArguments, _genericMethodArguments) =
        notSupported (sprintf "ResolveMember(%d, ..., ...)" metadataToken)

    override _.ResolveType(metadataToken: int, _genericTypeArguments, _genericMethodArguments) =
        notSupported (sprintf "ResolveType(%d, ..., ...)" metadataToken)

    override _.ResolveSignature(metadataToken: int) = notSupported (sprintf "ResolveSignature(%d)" metadataToken)

    override _.ResolveString(metadataToken: int) = notSupported (sprintf "ResolveString(%d)" metadataToken)

    override _.GetObjectData(_info: SerializationInfo, _context: StreamingContext) =
        raise (SerializationException(sprintf "Module '%s' cannot be serialized." moduleName))

    member private _.DebuggerDisplay =
        let asmName =
            match asm.GetName() with
            | null -> "<unknown assembly>"
            | info -> info.Name
        sprintf "%s (%s)" moduleName asmName

    override x.ToString() = x.DebuggerDisplay

and TypeReflectionBuilderStats =
    { AssembliesCreated: int
      TypesProjected: int
      TotalProjectionTicks: int64 }
    member stats.TotalProjectionTime = TimeSpan.FromTicks stats.TotalProjectionTicks

and TypeReflectionBuilder(g: TcGlobals) as this =
        let assemblies = ConcurrentDictionary<Stamp, ReflectAssembly>()
        let assemblyCount = ref 0
        let typeCount = ref 0
        let projectionTicks = ref 0L
        let profilingEnabled = ref false
        let dependencyScopes =
            new ThreadLocal<ResizeArray<ResizeArray<TyconRef>>>(fun () -> ResizeArray())
        let registeredTycons = ConcurrentDictionary<Stamp, ConcurrentDictionary<Stamp, TyconRef>>()

        let recordProjection duration =
            Interlocked.Increment(&typeCount.contents) |> ignore
            match duration with
            | Some (elapsed: TimeSpan) when !profilingEnabled ->
                Interlocked.Add(&projectionTicks.contents, elapsed.Ticks) |> ignore
            | _ -> ()

        let createAssembly (ccu: CcuThunk) =
            ccu.EnsureDerefable([||])
            let location = defaultArg ccu.FileName ""
            Interlocked.Increment(&assemblyCount.contents) |> ignore
            let moduleCount = List.length ccu.RootModulesAndNamespaces
            let typeCount = List.length ccu.RootTypeAndExceptionDefinitions
            if this.Fs1023TraceEnabled() then
                this.Fs1023TraceMessage(
                    sprintf "[tastreflection] init assembly=%s modules=%d types=%d" ccu.AssemblyName moduleCount typeCount)
            ReflectAssembly(this, g, ccu, location)

        let isEnabled envVar =
            if String.Equals(envVar, "FS1023_TRACE", StringComparison.OrdinalIgnoreCase) then
                Fs1023TraceControl.isEnabled ()
            else
                match Environment.GetEnvironmentVariable(envVar) with
                | null -> false
                | value when String.IsNullOrWhiteSpace value -> false
                | value when String.Equals(value.Trim(), "0", StringComparison.Ordinal) -> false
                | _ -> true

        let fs1023TraceEnabled () =
            Fs1023TraceControl.isEnabled () && isEnabled "FS1023_TRACE_TAST"

        let fs1023Trace format =
            Printf.ksprintf
                (fun message ->
                    if fs1023TraceEnabled () then
                        let path =
                            match Environment.GetEnvironmentVariable("FS1023_TRACE_PATH") with
                            | null
                            | "" -> "/tmp/fs1023_trace.log"
                            | custom -> custom

                        let entry =
                            sprintf "%s [fs1023][tastreflection] %s%s" (DateTime.UtcNow.ToString("O")) message Environment.NewLine

                        try
                            File.AppendAllText(path, entry)
                        with _ -> ())
                format

        member this.CaptureTypeDependencies<'T>(projection: unit -> 'T) =
            let scopes = dependencyScopes.Value
            let scope = ResizeArray<TyconRef>()
            scopes.Add scope
            let entering = fs1023TraceEnabled ()
            if entering then
                fs1023Trace "[capture] begin depth=%d" scopes.Count
            let stopwatch =
                if entering then
                    let sw = Stopwatch.StartNew()
                    box sw
                else
                    null
            try
                let result = projection()
                let seen = HashSet<Stamp>()
                let deps =
                    scope
                    |> Seq.fold
                        (fun acc tcref ->
                            if seen.Add tcref.Stamp then
                                tcref :: acc
                            else
                                acc)
                        []
                    |> List.toArray
                    |> Array.rev
                result, deps
            finally
                if entering then
                    let elapsed =
                        match stopwatch with
                        | null -> Double.NaN
                        | :? Stopwatch as sw ->
                            sw.Stop()
                            sw.Elapsed.TotalMilliseconds
                        | _ -> Double.NaN

                    let depNames =
                        scope
                        |> Seq.map (fun t -> t.CompiledName)
                        |> String.concat ","

                    fs1023Trace "[capture] end depth=%d deps=[%s] elapsedMs=%.3f" (scopes.Count - 1) depNames elapsed

                scopes.RemoveAt(scopes.Count - 1)

        member private this.TryAddDependency(tcref: TyconRef) =
            let scopes = dependencyScopes.Value
            if scopes.Count > 0 then
                scopes[scopes.Count - 1].Add tcref

        member internal this.NotifyTypeCreated(duration: TimeSpan option) =
            recordProjection duration

        member this.EnableProfiling
            with get () = !profilingEnabled
            and set value = profilingEnabled := value

        member this.GetStats() =
            { AssembliesCreated = Volatile.Read(&assemblyCount.contents)
              TypesProjected = Volatile.Read(&typeCount.contents)
              TotalProjectionTicks = Volatile.Read(&projectionTicks.contents) }

        member internal _.Fs1023TraceEnabled() = fs1023TraceEnabled ()

        member internal _.Fs1023TraceMessage(message: string) =
            fs1023Trace "%s" message

        member internal this.GetOrAddAssembly(ccu: CcuThunk) =
            assemblies.GetOrAdd(ccu.Stamp, fun _ -> createAssembly ccu)

        member internal this.RecordDependency(tcref: TyconRef) =
            this.TryAddDependency tcref

        member internal this.RegisterTycon(ccu: CcuThunk, tcref: TyconRef) : unit =
            let bucket =
                registeredTycons.GetOrAdd(
                    ccu.Stamp,
                    fun _ -> ConcurrentDictionary<Stamp, TyconRef>())
            bucket[tcref.Stamp] <- tcref
            let logRegistrations =
                match System.Environment.GetEnvironmentVariable("FS1023_TRACE_REGISTER") with
                | null -> false
                | value -> value.Trim() = "1"
            if logRegistrations then
                let count = bucket.Count
                printfn "[fs1023][builder-register] assembly=%s count=%d added=%s" ccu.AssemblyName count tcref.CompiledName
            match assemblies.TryGetValue ccu.Stamp with
            | true, assembly -> assembly.InvalidateTypes()
            | _ -> ()

        member internal this.GetRegisteredTycons(ccu: CcuThunk) : TyconRef[] =
            match registeredTycons.TryGetValue ccu.Stamp with
            | true, bucket -> bucket.Values |> Seq.toArray
            | _ -> Array.empty<TyconRef>

        member this.GetSystemType(topCcu: CcuThunk, ty: TType) =
            let assembly = this.GetOrAddAssembly(topCcu)
            assembly.TxTType ty

        member this.GetTypeDefinition(topCcu: CcuThunk, tcref: TyconRef) =
            let ccu =
                match ccuOfTyconRef tcref with
                | Some c -> c
                | None -> topCcu
            let assembly =
                if obj.ReferenceEquals(ccu, topCcu) then this.GetOrAddAssembly(topCcu)
                else this.GetOrAddAssembly(ccu)
            assembly.TxTypeDef None tcref
