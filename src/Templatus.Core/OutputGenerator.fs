﻿namespace Templatus.Core

open System
open System.IO
open System.Text
open System.Text.RegularExpressions
open Chessie.ErrorHandling
open Microsoft.FSharp.Compiler.Interactive.Shell

module OutputGenerator =
    let private prep outputFileName templateParameters =
        let basis = [
            "open System"
            "let _indent: string list ref = ref []"
            "let _indentStr () = !_indent |> List.fold (fun state curr -> curr + state) \"\""
            "let pushIndent str = _indent := str :: !_indent"
            "let popIndent () = _indent := match !_indent with [] -> [] | _ :: t -> t"
            "let clearIndent () = _indent := []"
            "let templateOutputFile = new System.IO.StreamWriter \"" + outputFileName + "\""
            "let tprintf o = sprintf \"%O\" o |> templateOutputFile.Write"
            "let tprintfn o = sprintf \"%s%O\" (_indentStr ()) o |> templateOutputFile.WriteLine" ]

        let parameters = templateParameters |> List.map (fun (name, value) -> sprintf "let %s = \"%s\"" name value)

        List.append basis parameters

    let private finish = [ "templateOutputFile.Close ()" ]

    let private failed = [ "sprintf \"%s---FAILED---\" Environment.NewLine |> tprintf" ]

    let private prepareControl = function
        | ControlBlock block -> block.Replace("\t", "    ") // FSI complains about tabs
        | ControlExpression expr -> expr.Replace("\t", "    ") |> sprintf "%s |> tprintf"

    let private newlineRegex = new Regex (@"\r\n|\n\r|\n|\r", RegexOptions.Compiled)

    let private fsiStripperRegex = new Regex(@"input\.fsx\(\d+,\d+\):\s*")

    let private trimFsiErrors input = fsiStripperRegex.Replace(input.ToString().Trim(), "")

    let private prepareLiteral text =
        newlineRegex.Replace(text, Environment.NewLine).Replace("\"", "\"\"")
        |> sprintf "tprintf @\"%s\""

    let rec private prepareTemplateForEval processedTemplate =
        let assemblyReferences = processedTemplate.AssemblyReferences |> List.map (sprintf "#r @\"%s\"")

        let toEval =
            processedTemplate.ProcessedTemplateParts
            |> List.map (fun p -> match p with
                                  | ProcessedLiteral (Literal l) -> [prepareLiteral l]
                                  | ProcessedControl c -> [prepareControl c]
                                  | ProcessedInclude i -> prepareTemplateForEval i)
            |> List.concat

        List.append assemblyReferences toEval

    let generate templateParameters processedTemplate =
        let sbOut = StringBuilder ()
        let sbErr = StringBuilder ()

        let cfg = FsiEvaluationSession.GetDefaultConfiguration()
        let fsi = FsiEvaluationSession.Create(cfg, [|"--noninteractive"|], new StringReader (""), new StringWriter (sbOut), new StringWriter (sbErr))

        match processedTemplate.OutputFile with
        | None -> fail "No output file specified."
        | Some f ->
            prep f templateParameters |> List.iter fsi.EvalInteraction

            let preparedTemplate = prepareTemplateForEval processedTemplate |> List.toArray
            let lastExpr = ref 0;

            try
                preparedTemplate
                |> Array.iter (fun x -> x |> fsi.EvalInteraction
                                        incr lastExpr)
            with _ -> failed |> List.iter fsi.EvalInteraction

            finish |> List.iter fsi.EvalInteraction

            if !lastExpr = Array.length preparedTemplate
            then pass ()
            else sprintf "Expression: %s\n\n%s\n" preparedTemplate.[!lastExpr] (sbErr |> trimFsiErrors) |> fail