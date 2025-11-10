namespace FSharp.Compiler.Service.Tests

open System
open System.Reflection
open Microsoft.FSharp.Reflection
open Xunit
open FSharp.Quotations
open FSharp.Core.CompilerServices
open FSharp.Compiler.TypeProviders
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.Text
open FSharp.Compiler.Text.Position

type private DummyProvider() =
    let invalidate = Event<EventHandler, EventArgs>()

    interface ITypeProvider with
        member _.Dispose() = ()

        member _.GetNamespaces() = Array.Empty()

        member _.GetStaticParameters _ = Array.Empty()

        member _.ApplyStaticArguments(typeWithoutArguments, _, _) = typeWithoutArguments

        member _.GetInvokerExpression(_, _) = <@@ () @@>


        [<CLIEvent>]
        member _.Invalidate = invalidate.Publish

        member _.GetGeneratedAssemblyContents _ = Array.Empty()

module private ProvidedBindingTestHelpers =

    let compilerAssembly = typeof<FSharp.Compiler.TypeProviders.TypeProviderDesignation>.Assembly

    let getType name =
        compilerAssembly.GetType(name, throwOnError = true, ignoreCase = false)

    let providedTypeContextEmpty =
        let ty = getType "FSharp.Compiler.TypeProviders+ProvidedTypeContext"
        ty.GetProperty("Empty", BindingFlags.Public ||| BindingFlags.Static).GetValue(null)

    let createProvidedMethodInfo runtimeMethod =
        let ty = getType "FSharp.Compiler.TypeProviders+ProvidedMethodInfo"
        let mi = ty.GetMethod("CreateNonNull", BindingFlags.Public ||| BindingFlags.Static)
        mi.Invoke(null, [| providedTypeContextEmpty; runtimeMethod |])

    let createProvidedPropertyInfo runtimeProperty =
        let ty = getType "FSharp.Compiler.TypeProviders+ProvidedPropertyInfo"
        let mi = ty.GetMethod("CreateNonNull", BindingFlags.Public ||| BindingFlags.Static)
        mi.Invoke(null, [| providedTypeContextEmpty; runtimeProperty |])

    let createProvidedConstructorInfo runtimeCtor =
        let ty = getType "FSharp.Compiler.TypeProviders+ProvidedConstructorInfo"
        let mi = ty.GetMethod("CreateNonNull", BindingFlags.Public ||| BindingFlags.Static)
        mi.Invoke(null, [| providedTypeContextEmpty; runtimeCtor |])

    let makeTainted value valueType (provider: ITypeProvider) =
        let lockType = getType "FSharp.Compiler.TypeProviderLock"
        let lockInstance = Activator.CreateInstance(lockType)

        let contextType = getType "FSharp.Compiler.TaintedContext"
        let contextCtor =
            contextType.GetConstructors(BindingFlags.NonPublic ||| BindingFlags.Instance)
            |> Array.exactlyOne

        let context =
            contextCtor.Invoke([| box provider; box ILScopeRef.Local; lockInstance |])

        let taintedType =
            (getType "FSharp.Compiler.Tainted`1").MakeGenericType([| valueType |])

        Activator.CreateInstance(taintedType, [| context; value |])

    let createBindingVia helperName range value valueType provider : ProvidedMemberBinding =
        let helperType = getType "FSharp.Compiler.TypeProviders+ProvidedMemberBindingHelpers"
        let helper = helperType.GetMethod(helperName, BindingFlags.Public ||| BindingFlags.Static)
        let tainted = makeTainted value valueType provider
        helper.Invoke(null, [| box range; tainted |]) :?> ProvidedMemberBinding

    let withDefinitionLocation binding rangeOpt : ProvidedMemberBinding =
        let helperType = getType "FSharp.Compiler.TypeProviders+ProvidedMemberBindingHelpers"
        let meth = helperType.GetMethod("withDefinitionLocation", BindingFlags.Public ||| BindingFlags.Static)
        meth.Invoke(null, [| box rangeOpt; binding |]) :?> ProvidedMemberBinding

type ProvidedMemberBindingTests() =

    let provider : ITypeProvider = new DummyProvider() :> _

    let assertCommonMetadata (binding: ProvidedMemberBinding) (expectedMember: ProvidedMemberInfo) =
        Assert.False(obj.ReferenceEquals(binding, null))
        Assert.True(obj.ReferenceEquals(provider, binding.Provider.PUntaintNoFailure id))
        Assert.True(obj.ReferenceEquals(expectedMember, binding.Member.PUntaintNoFailure id))

    let getInvokerExpr (binding: ProvidedMemberBinding) =
        match binding.InvokerExpression with
        | Some invoker ->
            let providedExpr = invoker.PUntaintNoFailure id
            Assert.False(obj.ReferenceEquals(providedExpr, null))
            providedExpr
        | None -> failwith "Expected invoker expression"

    [<Fact>]
    member _.``createForMethod wires provider and populates method metadata``() =
        let runtimeMethod = typeof<string>.GetMethod("ToString", Type.EmptyTypes)
        let providedMethodInfo = ProvidedBindingTestHelpers.createProvidedMethodInfo runtimeMethod

        let binding =
            ProvidedBindingTestHelpers.createBindingVia
                "createForMethod"
                Range.range0
                providedMethodInfo
                (providedMethodInfo.GetType())
                provider

        assertCommonMetadata binding (providedMethodInfo :?> ProvidedMemberInfo)

        match binding.Parameters with
        | Some parameters -> Assert.Empty(parameters)
        | None -> failwith "Expected parameters"

        Assert.False(obj.ReferenceEquals(binding.ReturnParameter, null))
        Assert.Equal(typeof<string>, binding.ReturnParameter.ParameterType.RawSystemType)
        Assert.False(obj.ReferenceEquals(binding.ResultType, null))
        Assert.Equal(None, binding.DefinitionLocation)

        match binding.InvokerVars with
        | Some vars -> Assert.Equal(1, vars.Length)
        | None -> failwith "Expected invoker vars"

        let invokerExpr = getInvokerExpr binding
        Assert.Equal(typeof<unit>, invokerExpr.Type.RawSystemType)

        match ProvidedMemberBindingHelpers.tryGetBindingByProvidedMemberInfo (providedMethodInfo :?> ProvidedMemberInfo) with
        | Some registered -> Assert.True(obj.ReferenceEquals(binding, registered))
        | None -> failwith "Expected binding to be registered"
        Assert.True(binding.AssociatedMember.IsNone)

    [<Fact>]
    member _.``createForMethod captures parameter metadata``() =
        let runtimeMethod = typeof<string>.GetMethod("IndexOf", [| typeof<char>; typeof<int> |])
        Assert.NotNull(runtimeMethod)

        let providedMethodInfo = ProvidedBindingTestHelpers.createProvidedMethodInfo runtimeMethod

        let binding =
            ProvidedBindingTestHelpers.createBindingVia
                "createForMethod"
                Range.range0
                providedMethodInfo
                (providedMethodInfo.GetType())
                provider

        let parameters =
            match binding.Parameters with
            | Some ps when not (obj.ReferenceEquals(ps, null)) -> ps
            | _ -> failwith "Expected non-empty parameter metadata"

        Assert.Equal(2, parameters.Length)
        Assert.Equal("value", parameters.[0].Name)
        Assert.Equal(typeof<char>, parameters.[0].ParameterType.RawSystemType)
        Assert.False(parameters.[0].IsOptional)
        Assert.Equal("startIndex", parameters.[1].Name)
        Assert.Equal(typeof<int>, parameters.[1].ParameterType.RawSystemType)
        Assert.False(parameters.[1].IsOptional)

        Assert.NotNull(binding.ResultType)
        Assert.Equal(typeof<int>, binding.ResultType.RawSystemType)

        Assert.False(obj.ReferenceEquals(binding.ReturnParameter, null))
        Assert.Equal(typeof<int>, binding.ReturnParameter.ParameterType.RawSystemType)

        match binding.InvokerVars with
        | Some vars -> Assert.Equal(3, vars.Length)
        | None -> failwith "Expected invoker vars"

        let invokerExpr = getInvokerExpr binding
        Assert.Equal(typeof<unit>, invokerExpr.Type.RawSystemType)

        match ProvidedMemberBindingHelpers.tryGetBindingByProvidedMemberInfo (providedMethodInfo :?> ProvidedMemberInfo) with
        | Some registered -> Assert.True(obj.ReferenceEquals(binding, registered))
        | None -> failwith "Expected binding to be registered"
        Assert.True(binding.AssociatedMember.IsNone)

    [<Fact>]
    member _.``createForProperty captures indexer parameters``() =
        let dictionaryType = typeof<System.Collections.Generic.Dictionary<int, string>>
        let runtimeProperty = dictionaryType.GetProperty("Item")
        Assert.NotNull(runtimeProperty)

        let providedPropertyInfo = ProvidedBindingTestHelpers.createProvidedPropertyInfo runtimeProperty

        let binding =
            ProvidedBindingTestHelpers.createBindingVia
                "createForProperty"
                Range.range0
                providedPropertyInfo
                (providedPropertyInfo.GetType())
                provider

        let parameters =
            match binding.Parameters with
            | Some ps when not (obj.ReferenceEquals(ps, null)) -> ps
            | _ -> failwith "Expected indexer parameter metadata"

        Assert.Equal(1, parameters.Length)
        Assert.Equal("key", parameters.[0].Name)
        Assert.Equal(typeof<int>, parameters.[0].ParameterType.RawSystemType)
        Assert.False(parameters.[0].IsOptional)

        Assert.NotNull(binding.ResultType)
        Assert.Equal(typeof<string>, binding.ResultType.RawSystemType)

        Assert.True(obj.ReferenceEquals(binding.ReturnParameter, null))
        Assert.True(binding.InvokerExpression.IsNone)
        Assert.True(binding.InvokerVars.IsNone)

        match ProvidedMemberBindingHelpers.tryGetBindingByProvidedMemberInfo (providedPropertyInfo :?> ProvidedMemberInfo) with
        | Some registered -> Assert.True(obj.ReferenceEquals(binding, registered))
        | None -> failwith "Expected binding to be registered"
        Assert.True(binding.AssociatedMember.IsNone)

    [<Fact>]
    member _.``createForConstructor captures parameter metadata``() =
        let runtimeCtor =
            typeof<System.Text.StringBuilder>.GetConstructor([| typeof<int>; typeof<int> |])
        Assert.NotNull(runtimeCtor)

        let providedCtorInfo = ProvidedBindingTestHelpers.createProvidedConstructorInfo runtimeCtor

        let binding =
            ProvidedBindingTestHelpers.createBindingVia
                "createForConstructor"
                Range.range0
                providedCtorInfo
                (providedCtorInfo.GetType())
                provider

        let parameters =
            match binding.Parameters with
            | Some ps when not (obj.ReferenceEquals(ps, null)) -> ps
            | _ -> failwith "Expected constructor parameter metadata"

        Assert.Equal(2, parameters.Length)
        Assert.Equal("capacity", parameters.[0].Name)
        Assert.Equal(typeof<int>, parameters.[0].ParameterType.RawSystemType)
        Assert.Equal("maxCapacity", parameters.[1].Name)
        Assert.Equal(typeof<int>, parameters.[1].ParameterType.RawSystemType)

        Assert.NotNull(binding.ResultType)
        Assert.Equal(typeof<System.Text.StringBuilder>, binding.ResultType.RawSystemType)

        Assert.True(binding.AssociatedMember.IsNone)

        Assert.True(obj.ReferenceEquals(binding.ReturnParameter, null))

        match binding.InvokerVars with
        | Some vars -> Assert.Equal(2, vars.Length)
        | None -> failwith "Expected invoker vars"

        let invokerExpr = getInvokerExpr binding
        Assert.Equal(typeof<unit>, invokerExpr.Type.RawSystemType)

        match ProvidedMemberBindingHelpers.tryGetBindingByProvidedMemberInfo (providedCtorInfo :?> ProvidedMemberInfo) with
        | Some registered -> Assert.True(obj.ReferenceEquals(binding, registered))
        | None -> failwith "Expected binding to be registered"

    [<Fact>]
    member _.``createForProperty wires provider and leaves metadata empty``() =
        let runtimeProperty = typeof<System.Text.StringBuilder>.GetProperty("Length")
        let providedPropertyInfo = ProvidedBindingTestHelpers.createProvidedPropertyInfo runtimeProperty

        let binding =
            ProvidedBindingTestHelpers.createBindingVia
                "createForProperty"
                Range.range0
                providedPropertyInfo
                (providedPropertyInfo.GetType())
                provider

        assertCommonMetadata binding (providedPropertyInfo :?> ProvidedMemberInfo)

        match binding.Parameters with
        | Some parameters -> Assert.Empty(parameters)
        | None -> failwith "Expected parameters"

        Assert.True(obj.ReferenceEquals(binding.ReturnParameter, null))
        Assert.False(obj.ReferenceEquals(binding.ResultType, null))
        Assert.True(binding.InvokerExpression.IsNone)
        Assert.True(binding.InvokerVars.IsNone)
        Assert.Equal(None, binding.DefinitionLocation)

        match ProvidedMemberBindingHelpers.tryGetBindingByProvidedMemberInfo (providedPropertyInfo :?> ProvidedMemberInfo) with
        | Some registered -> Assert.True(obj.ReferenceEquals(binding, registered))
        | None -> failwith "Expected binding to be registered"

    [<Fact>]
    member _.``createForConstructor wires provider and leaves metadata empty``() =
        let runtimeCtor = typeof<System.Text.StringBuilder>.GetConstructor(Type.EmptyTypes)
        let providedCtorInfo = ProvidedBindingTestHelpers.createProvidedConstructorInfo runtimeCtor

        let binding =
            ProvidedBindingTestHelpers.createBindingVia
                "createForConstructor"
                Range.range0
                providedCtorInfo
                (providedCtorInfo.GetType())
                provider

        assertCommonMetadata binding (providedCtorInfo :?> ProvidedMemberInfo)

        match binding.Parameters with
        | Some parameters -> Assert.Empty(parameters)
        | None -> failwith "Expected parameters"

        Assert.True(obj.ReferenceEquals(binding.ReturnParameter, null))
        // Declaring type for parameterless ctor should be present
        Assert.False(obj.ReferenceEquals(binding.ResultType, null))
        match binding.InvokerVars with
        | Some vars -> Assert.Empty(vars)
        | None -> failwith "Expected invoker vars"
        let invokerExpr = getInvokerExpr binding
        Assert.Equal(typeof<unit>, invokerExpr.Type.RawSystemType)
        Assert.Equal(None, binding.DefinitionLocation)

        match ProvidedMemberBindingHelpers.tryGetBindingByProvidedMemberInfo (providedCtorInfo :?> ProvidedMemberInfo) with
        | Some registered -> Assert.True(obj.ReferenceEquals(binding, registered))
        | None -> failwith "Expected binding to be registered"

    [<Fact>]
    member _.``withDefinitionLocation replaces definition location``() =
        let runtimeMethod = typeof<string>.GetMethod("ToString", Type.EmptyTypes)
        let providedMethodInfo = ProvidedBindingTestHelpers.createProvidedMethodInfo runtimeMethod

        let originalBinding =
            ProvidedBindingTestHelpers.createBindingVia
                "createForMethod"
                Range.range0
                providedMethodInfo
                (providedMethodInfo.GetType())
                provider

        let newRangeOpt =
            let pos = Position.mkPos 1 0
            Some (Range.mkRange "ProvidedBinding.fs" pos pos)
        let updatedBinding = ProvidedBindingTestHelpers.withDefinitionLocation originalBinding newRangeOpt

        Assert.Equal(newRangeOpt, updatedBinding.DefinitionLocation)

        match ProvidedMemberBindingHelpers.tryGetBindingByProvidedMemberInfo (providedMethodInfo :?> ProvidedMemberInfo) with
        | Some registered ->
            Assert.True(obj.ReferenceEquals(updatedBinding, registered))
            Assert.Equal(newRangeOpt, registered.DefinitionLocation)
        | None -> failwith "Expected binding to be registered"
