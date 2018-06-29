﻿// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

namespace rec Microsoft.VisualStudio.FSharp.Editor


open System
open System.Windows.Controls
open System.Windows.Media
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Formatting
open Microsoft.CodeAnalysis
open System.Threading
open Microsoft.FSharp.Compiler.SourceCodeServices
open System.Windows
open System.Collections.Generic
open Microsoft.FSharp.Compiler.Range
open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.Ast
open Microsoft.CodeAnalysis.Editor.Shared.Extensions
open Microsoft.CodeAnalysis.Editor.Shared.Utilities
open Microsoft.CodeAnalysis.Classification
open Internal.Utilities.StructuredFormat
open System.Windows.Media.Animation

open Microsoft.VisualStudio.FSharp.Editor.Logging
open Microsoft.VisualStudio.Text.Classification

type internal CodeLens(taggedText, computed, fullTypeSignature, uiElement) =
    member val TaggedText: Async<(ResizeArray<Layout.TaggedText> * QuickInfoNavigation) option> = taggedText
    member val Computed: bool = computed with get, set
    member val FullTypeSignature: string = fullTypeSignature 
    member val UiElement: UIElement = uiElement with get, set

type internal FSharpCodeLensService
    (
        serviceProvider: IServiceProvider,
        workspace: Workspace, 
        documentId: Lazy<DocumentId>,
        buffer: ITextBuffer, 
        checker: FSharpChecker,
        projectInfoManager: FSharpProjectOptionsManager,
        classificationFormatMapService: IClassificationFormatMapService,
        typeMap: Lazy<ClassificationTypeMap>,
        codeLens : CodeLensDisplayService
    ) as self =

    let lineLens = codeLens

    let visit pos parseTree = 
        AstTraversal.Traverse(pos, parseTree, { new AstTraversal.AstVisitorBase<_>() with 
            member __.VisitExpr(_path, traverseSynExpr, defaultTraverse, expr) =
                defaultTraverse(expr)
            
            override __.VisitInheritSynMemberDefn (_, _, _, _, range) = Some range

            override __.VisitTypeAbbrev( _, range) = Some range

            override __.VisitLetOrUse(binding, range) = 
                match binding |> Seq.tryFind (fun b -> b.RangeOfHeadPat.StartLine = pos.Line) with
                | Some entry ->
                    Some entry.RangeOfBindingAndRhs
                | _ -> Some range // This can be invalid and should not happen

            override __.VisitBinding (fn, binding) =
                Some binding.RangeOfBindingAndRhs
        })

    let formatMap = lazy classificationFormatMapService.GetClassificationFormatMap "tooltip"

    let mutable lastResults = Dictionary<string, ITrackingSpan * CodeLens>()
    let mutable firstTimeChecked = false
    let mutable bufferChangedCts = new CancellationTokenSource()
    let uiContext = SynchronizationContext.Current

    let layoutTagToFormatting (layoutTag: LayoutTag) =
        layoutTag
        |> RoslynHelpers.roslynTag
        |> ClassificationTags.GetClassificationTypeName
        |> typeMap.Value.GetClassificationType
        |> formatMap.Value.GetTextProperties   

    let createTextBox (lens:CodeLens) =
        async {
            do! Async.SwitchToContext uiContext
            let! res = lens.TaggedText
            match res with
            | Some (taggedText, navigation) -> 
                let textBlock = new TextBlock(Background = Brushes.AliceBlue, Opacity = 0.0, TextTrimming = TextTrimming.None)
                DependencyObjectExtensions.SetDefaultTextProperties(textBlock, formatMap.Value)

                let prefix = Documents.Run Settings.CodeLens.Prefix
                prefix.Foreground <- SolidColorBrush(Color.FromRgb(153uy, 153uy, 153uy))
                textBlock.Inlines.Add prefix

                for text in taggedText do

                    let coloredProperties = layoutTagToFormatting text.Tag
                    let actualProperties =
                        if Settings.CodeLens.UseColors
                        then
                            // If color is gray (R=G=B), change to correct gray color.
                            // Otherwise, use the provided color.
                            match coloredProperties.ForegroundBrush with
                            | :? SolidColorBrush as b ->
                                let c = b.Color
                                if c.R = c.G && c.R = c.B
                                then coloredProperties.SetForeground(Color.FromRgb(153uy, 153uy, 153uy))
                                else coloredProperties
                            | _ -> coloredProperties
                        else
                            coloredProperties.SetForeground(Color.FromRgb(153uy, 153uy, 153uy))

                    let run = Documents.Run text.Text
                    DependencyObjectExtensions.SetTextProperties (run, actualProperties)

                    let inl =
                        match text with
                        | :? Layout.NavigableTaggedText as nav when navigation.IsTargetValid nav.Range ->
                            let h = Documents.Hyperlink(run, ToolTip = nav.Range.FileName)
                            h.Click.Add (fun _ -> 
                                navigation.NavigateTo nav.Range)
                            h :> Documents.Inline
                        | _ -> run :> _
                    DependencyObjectExtensions.SetTextProperties (inl, actualProperties)
                    textBlock.Inlines.Add inl
            

                textBlock.Measure(Size(Double.PositiveInfinity, Double.PositiveInfinity))
                lens.Computed <- true
                lens.UiElement <- textBlock
                return true
            | _ -> 
                return false
        }  

    let executeCodeLenseAsync () =  
        asyncMaybe {
            do! Async.Sleep 800 |> liftAsync
            let! document = workspace.CurrentSolution.GetDocument(documentId.Value) |> Option.ofObj
            let! _, options = projectInfoManager.TryGetOptionsForEditingDocumentOrProject(document)
            let! _, parsedInput, checkFileResults = checker.ParseAndCheckDocument(document, options, true, "LineLens")
            let! symbolUses = checkFileResults.GetAllUsesOfAllSymbolsInFile() |> liftAsync
            let textSnapshot = buffer.CurrentSnapshot
            
            // Clear existing data and cache flags
            // The results which are left.
            let oldResults = Dictionary(lastResults)

            let newResults = Dictionary()
            // Symbols which cache wasn't found yet
            let unattachedSymbols = ResizeArray()
            // Tags which are new or need to be updated due to changes.
            let tagsToUpdate = Dictionary()
            let codeLensToAdd = ResizeArray()

            let useResults (displayContext: FSharpDisplayContext, func: FSharpMemberOrFunctionOrValue, realPosition: range) =
                async {
                    try
                        let textSnapshot = buffer.CurrentSnapshot
                        let lineNumber = Line.toZ func.DeclarationLocation.StartLine
                        if (lineNumber >= 0 || lineNumber < textSnapshot.LineCount) then
                            let! displayEnv = checkFileResults.GetDisplayEnvForPos func.DeclarationLocation.Start
                            let displayContext =
                                match displayEnv with
                                | Some denv -> FSharpDisplayContext(fun _ -> denv)
                                | None -> displayContext

                            let typeLayout = func.FormatLayout displayContext
                            let taggedText = ResizeArray()
                                    
                            Layout.renderL (Layout.taggedTextListR taggedText.Add) typeLayout |> ignore
                            let navigation = QuickInfoNavigation(serviceProvider, checker, projectInfoManager, document, realPosition)
                            // Because the data is available notify that this line should be updated, displaying the results
                            return Some (taggedText, navigation)
                        else return None
                    with e -> 
#if DEBUG
                        logErrorf "Error in lazy line lens computation. %A" e
#endif
                        return None
                }
            
            let inline setNewResultsAndWarnIfOverriden fullDeclarationText value = 
                if newResults.ContainsKey fullDeclarationText then
#if DEBUG
                    logWarningf "New results already contains: %A" fullDeclarationText
#endif
                newResults.[fullDeclarationText] <- value
            for symbolUse in symbolUses do
                if symbolUse.IsFromDefinition then
                    match symbolUse.Symbol with
                    | :? FSharpMemberOrFunctionOrValue as func when func.IsModuleValueOrMember || func.IsProperty ->
                        let funcID = func.LogicalName + (func.FullType.ToString() |> hash |> string)
                        // Use a combination of the the function name + the hashed value of the type signature
                        let fullDeclarationText = funcID // (textSnapshot.GetText declarationSpan).Replace(func.CompiledName, funcID)
                        let fullTypeSignature = func.FullType.ToString()
                        // Try to re-use the last results
                        if lastResults.ContainsKey fullDeclarationText then
                            // Make sure that the results are usable
                            let inline setNewResultsAndWarnIfOverridenLocal value = setNewResultsAndWarnIfOverriden fullDeclarationText value
                            let lastTrackingSpan, codeLens as lastResult = lastResults.[fullDeclarationText]
                            if codeLens.FullTypeSignature = fullTypeSignature then
                                setNewResultsAndWarnIfOverridenLocal lastResult
                                oldResults.Remove fullDeclarationText |> ignore
                            else
                                let declarationLine, range = 
                                    match visit func.DeclarationLocation.Start parsedInput with
                                    | Some range -> range.StartLine - 1, range
                                    | _ -> func.DeclarationLocation.StartLine - 1, func.DeclarationLocation
                                // Track the old element for removal
                                let declarationSpan = 
                                    let line = textSnapshot.GetLineFromLineNumber declarationLine
                                    let offset = line.GetText() |> Seq.findIndex (Char.IsWhiteSpace >> not)
                                    SnapshotSpan(line.Start.Add offset, line.End).Span
                                let newTrackingSpan = 
                                    textSnapshot.CreateTrackingSpan(declarationSpan, SpanTrackingMode.EdgeExclusive)
                                // Push back the new results
                                let res =
                                        CodeLens( Async.cache (useResults (symbolUse.DisplayContext, func, range)),
                                            false,
                                            fullTypeSignature,
                                            null)
                                // The old results aren't computed at all, because the line might have changed create new results
                                tagsToUpdate.[lastTrackingSpan] <- (newTrackingSpan, fullDeclarationText, res)
                                setNewResultsAndWarnIfOverridenLocal (newTrackingSpan, res)
                                        
                                oldResults.Remove fullDeclarationText |> ignore
                        else
                            // The symbol might be completely new or has slightly changed. 
                            // We need to track this and iterate over the left entries to ensure that there isn't anything
                            unattachedSymbols.Add((symbolUse, func, fullDeclarationText, fullTypeSignature))
                    | _ -> ()
            
            // In best case this works quite `covfefe` fine because often enough we change only a small part of the file and not the complete.
            for unattachedSymbol in unattachedSymbols do
                let symbolUse, func, fullDeclarationText, fullTypeSignature = unattachedSymbol
                let declarationLine, range = 
                    match visit func.DeclarationLocation.Start parsedInput with
                    | Some range -> range.StartLine - 1, range
                    | _ -> func.DeclarationLocation.StartLine - 1, func.DeclarationLocation
                    
                let test (v:KeyValuePair<_, _>) =
                    let _, (codeLens:CodeLens) = v.Value
                    codeLens.FullTypeSignature = fullTypeSignature
                match oldResults |> Seq.tryFind test with
                | Some res ->
                    let (trackingSpan : ITrackingSpan), (codeLens : CodeLens) = res.Value
                    let declarationSpan = 
                        let line = textSnapshot.GetLineFromLineNumber declarationLine
                        let offset = line.GetText() |> Seq.findIndex (Char.IsWhiteSpace >> not)
                        SnapshotSpan(line.Start.Add offset, line.End).Span
                    let newTrackingSpan = 
                        textSnapshot.CreateTrackingSpan(declarationSpan, SpanTrackingMode.EdgeExclusive)
                    if codeLens.Computed && (isNull codeLens.UiElement |> not) then
                        newResults.[fullDeclarationText] <- (newTrackingSpan, codeLens)
                        tagsToUpdate.[trackingSpan] <- (newTrackingSpan, fullDeclarationText, codeLens)
                    else
                        let res = 
                            CodeLens(
                                Async.cache (useResults (symbolUse.DisplayContext, func, range)),
                                false,
                                fullTypeSignature,
                                null)
                        // The tag might be still valid but it hasn't been computed yet so create fresh results
                        tagsToUpdate.[trackingSpan] <- (newTrackingSpan, fullDeclarationText, res)
                        newResults.[fullDeclarationText] <- (newTrackingSpan, res)
                    let key = res.Key
                    oldResults.Remove key |> ignore // no need to check this entry again
                | None ->
                    // This function hasn't got any cache and so it's completely new.
                    // So create completely new results
                    // And finally add a tag for this.
                    let res = 
                        CodeLens(
                            Async.cache (useResults (symbolUse.DisplayContext, func, range)),
                            false,
                            fullTypeSignature,
                            null)
                    try
                        let declarationSpan = 
                            let line = textSnapshot.GetLineFromLineNumber declarationLine
                            let offset = line.GetText() |> Seq.findIndex (Char.IsWhiteSpace >> not)
                            SnapshotSpan(line.Start.Add offset, line.End).Span
                        let trackingSpan = 
                            textSnapshot.CreateTrackingSpan(declarationSpan, SpanTrackingMode.EdgeExclusive)
                        codeLensToAdd.Add (trackingSpan, res)
                        newResults.[fullDeclarationText] <- (trackingSpan, res)
                    with e -> 
#if DEBUG
                        logExceptionWithContext (e, "Line Lens tracking tag span creation")
#endif
                ()
            lastResults <- newResults
            do! Async.SwitchToContext uiContext |> liftAsync
            let createCodeLensUIElement (codeLens:CodeLens) trackingSpan _ =
                if codeLens.Computed |> not then
                    async {
                        let! res = createTextBox codeLens
                        if res then
                            do! Async.SwitchToContext uiContext
                            let uiElement = codeLens.UiElement
                            let animation = 
                                DoubleAnimation(
                                    To = Nullable 0.8,
                                    Duration = (TimeSpan.FromMilliseconds 800. |> Duration.op_Implicit),
                                    EasingFunction = QuadraticEase()
                                    )
                            let sb = Storyboard()
                            Storyboard.SetTarget(sb, uiElement)
                            Storyboard.SetTargetProperty(sb, PropertyPath Control.OpacityProperty)
                            sb.Children.Add animation
                            lineLens.AddUiElementToCodeLensOnce (trackingSpan, uiElement)
                            lineLens.RelayoutRequested.Enqueue ()
                            sb.Begin()
                        else
#if DEBUG
                            logWarningf "Couldn't retrieve code lens information for %A" codeLens.FullTypeSignature
#endif
                    } |> (RoslynHelpers.StartAsyncSafe CancellationToken.None) "UIElement creation"

            for value in tagsToUpdate do
                let trackingSpan, (newTrackingSpan, _, codeLens) = value.Key, value.Value
                lineLens.RemoveCodeLens trackingSpan |> ignore
                let Grid = lineLens.AddCodeLens newTrackingSpan
                if codeLens.Computed && (isNull codeLens.UiElement |> not) then
                    let uiElement = codeLens.UiElement
                    lineLens.AddUiElementToCodeLensOnce (newTrackingSpan, uiElement)
                else
                    Grid.IsVisibleChanged
                    |> Event.filter (fun eventArgs -> eventArgs.NewValue :?> bool)
                    |> Event.add (createCodeLensUIElement codeLens newTrackingSpan)

            for value in codeLensToAdd do
                let trackingSpan, codeLens = value
                let Grid = lineLens.AddCodeLens trackingSpan
                
                Grid.IsVisibleChanged
                |> Event.filter (fun eventArgs -> eventArgs.NewValue :?> bool)
                |> Event.add (createCodeLensUIElement codeLens trackingSpan)

            for oldResult in oldResults do
                let trackingSpan, _ = oldResult.Value
                lineLens.RemoveCodeLens trackingSpan |> ignore
            
            if not firstTimeChecked then
                firstTimeChecked <- true
        } |> Async.Ignore
    
    do  
        begin
            buffer.Changed.AddHandler(fun _ e -> (self.BufferChanged e))
            async {
              let mutable numberOfFails = 0
              while not firstTimeChecked && numberOfFails < 10 do
                  try
                      do! executeCodeLenseAsync()
                      do! Async.Sleep(1000)
                  with
                  | e -> 
#if DEBUG
                    logErrorf "Line Lens startup failed with: %A" e
#endif
                    numberOfFails <- numberOfFails + 1
           } |> Async.Start
        end

    member __.BufferChanged ___ =
        bufferChangedCts.Cancel() // Stop all ongoing async workflow. 
        bufferChangedCts.Dispose()
        bufferChangedCts <- new CancellationTokenSource()
        executeCodeLenseAsync () |> Async.Ignore |> RoslynHelpers.StartAsyncSafe bufferChangedCts.Token "Buffer Changed"