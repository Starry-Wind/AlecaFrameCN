using System;
using System.Drawing;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class TradePatcher
{
    public static int Main(string[] args)
    {
        if (args.Length != 3) { Console.Error.WriteLine("Usage: TradePatcher <official DLL> <helper DLL> <output DLL>"); return 2; }
        try
        {
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1])));
            using (var assembly = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver }))
            using (var helper = AssemblyDefinition.ReadAssembly(args[1]))
            {
                var module = assembly.MainModule;
                if (assembly.Name.Name != "AlecaFrameClientLib" || module.AssemblyReferences.Any(r => r.Name == "AlecaFrameZh.TradeHelper"))
                    throw new InvalidOperationException("Expected an unpatched official AlecaFrameClientLib.dll");
                var helperType = helper.MainModule.Types.Single(t => t.FullName == "AlecaFrameZh.TradeHelper");
                Func<string, MethodReference> import = name => module.ImportReference(helperType.Methods.Single(m => m.Name == name));
                var stats = module.Types.Single(t => t.FullName == "AlecaFrameClientLib.Data.StatsHandler");
                var ee = module.Types.Single(t => t.FullName == "AlecaFrameClientLib.Utils.EELogProcessor");
                var wrapper = module.Types.Single(t => t.FullName == "AlecaFrameClientLib.OverwolfWrapper");

                var enabled = wrapper.Methods.Single(m => m.Name == "IsTradeHistoryEnabled");
                AddTruePrefix(enabled, import("IsChineseGame"));

                var beginning = stats.Methods.Single(m => m.Name == "IsBeginninigOfTradeLog");
                var il = beginning.Body.GetILProcessor();
                var first = beginning.Body.Instructions[0];
                il.InsertBefore(first, il.Create(OpCodes.Call, import("IsChineseGame")));
                il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Call, import("IsChineseTradeBeginning")));
                il.InsertBefore(first, il.Create(OpCodes.Ret));

                var cancelPending = new MethodDefinition("CancelPendingTradeZH", MethodAttributes.Assembly | MethodAttributes.Static, module.TypeSystem.Void);
                stats.Methods.Add(cancelPending);
                il = cancelPending.Body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ldsfld, module.ImportReference(typeof(DateTime).GetField("MinValue"))));
                il.Append(il.Create(OpCodes.Stsfld, stats.Fields.Single(f => f.Name == "tradeProcessedTimestamp")));
                il.Append(il.Create(OpCodes.Ret));

                var process = ee.Methods.Single(m => m.Name == "ProcessLine");
                il = process.Body.GetILProcessor();
                first = process.Body.Instructions[0];
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Call, import("NormalizeLogLine")));
                il.InsertBefore(first, il.Create(OpCodes.Starg, process.Parameters[0]));
                var waiting = ee.Fields.Single(f => f.Name == "waitingForTradeMessageConfirm");
                var multiline = ee.Fields.Single(f => f.Name == "gettingTradeMessageMultiline");
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Call, import("IsChineseTradeCancellation")));
                il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
                il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_0));
                il.InsertBefore(first, il.Create(OpCodes.Stsfld, waiting));
                il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_0));
                il.InsertBefore(first, il.Create(OpCodes.Stsfld, multiline));
                il.InsertBefore(first, il.Create(OpCodes.Call, cancelPending));

                var convert = stats.Methods.Single(m => m.Name == "ConvertItemNameToId");
                il = convert.Body.GetILProcessor();
                first = convert.Body.Instructions[0];
                il.InsertBefore(first, il.Create(OpCodes.Call, import("IsChineseGame")));
                il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Call, import("ConvertChineseItem")));
                il.InsertBefore(first, il.Create(OpCodes.Ret));

                var finish = stats.Methods.Single(m => m.Name == "TradeLogsFinished");
                var log = stats.Fields.Single(f => f.Name == "currentTradeLog");
                var trade = stats.Fields.Single(f => f.Name == "currentProcessingTrade");
                var timestamp = stats.Fields.Single(f => f.Name == "tradeProcessedTimestamp");
                var processTrade = stats.Methods.Single(m => m.Name == "ProcessCurrentTrade");
                il = finish.Body.GetILProcessor();
                first = finish.Body.Instructions[0];
                var end = il.Create(OpCodes.Ret);
                il.InsertBefore(first, il.Create(OpCodes.Call, import("IsChineseGame")));
                il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
                // Invalidate the old trade before parsing, so a failed / canceled
                // confirmation cannot later submit the previous successful one.
                il.InsertBefore(first, il.Create(OpCodes.Ldsfld, module.ImportReference(typeof(DateTime).GetField("MinValue"))));
                il.InsertBefore(first, il.Create(OpCodes.Stsfld, timestamp));
                il.InsertBefore(first, il.Create(OpCodes.Ldsfld, log));
                il.InsertBefore(first, il.Create(OpCodes.Call, import("ParseChineseTrade")));
                il.InsertBefore(first, il.Create(OpCodes.Stsfld, trade));
                il.InsertBefore(first, il.Create(OpCodes.Ldsfld, trade));
                il.InsertBefore(first, il.Create(OpCodes.Brfalse, end));
                il.InsertBefore(first, il.Create(OpCodes.Call, processTrade));
                il.InsertBefore(first, il.Create(OpCodes.Call, module.ImportReference(typeof(DateTime).GetProperty("UtcNow").GetGetMethod())));
                il.InsertBefore(first, il.Create(OpCodes.Stsfld, timestamp));
                il.InsertBefore(first, end);

                var display = new MethodDefinition("GetTradeDisplayNamesZH", MethodAttributes.Public | MethodAttributes.HideBySig, module.TypeSystem.String);
                display.Parameters.Add(new ParameterDefinition("idsJson", ParameterAttributes.None, module.TypeSystem.String));
                wrapper.Methods.Add(display);
                il = display.Body.GetILProcessor();
                il.Append(il.Create(OpCodes.Ldarg_1));
                il.Append(il.Create(OpCodes.Call, import("GetTradeDisplayNames")));
                il.Append(il.Create(OpCodes.Ret));

                PatchRelicRecommendation(module, helper);

                // Inserted instructions can push an existing short branch past
                // its signed-byte range. Widen all short branches in edited code.
                foreach (var method in new [] { enabled, beginning, process, convert, finish }) WidenBranches(method);
                assembly.Write(args[2]);
            }
            Console.WriteLine("Chinese trade and relic recommendation patch built: " + args[2]);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void PatchRelicRecommendation(ModuleDefinition module, AssemblyDefinition helper)
    {
        var overlays = module.Types.Single(t => t.FullName == "AlecaFrameClientLib.Utils.OverlaysHandler");
        var method = overlays.Methods.Single(m => m.Name == "OpenRelicRecommendationHandler");
        var recommendation = helper.MainModule.Types.Single(t => t.FullName == "AlecaFrameZh.RelicRecommendation");
        Func<string, MethodReference> import = name => module.ImportReference(recommendation.Methods.Single(m => m.Name == name));
        var languageCall = method.Body.Instructions.Single(i => i.OpCode == OpCodes.Call &&
            i.Operand is MethodReference && ((MethodReference)i.Operand).FullName.Contains("Misc::GetWarframeLanguage("));
        var ocrCall = method.Body.Instructions.Single(i => i.OpCode == OpCodes.Call &&
            i.Operand is MethodReference && ((MethodReference)i.Operand).Name == "SendRAWRivenOCR");
        var english = module.Types.Single(t => t.FullName == "AlecaFrameClientLib.Utils.Misc")
            .NestedTypes.Single(t => t.Name == "WarframeLanguage").Fields.Single(f => f.Name == "English");
        var il = method.Body.GetILProcessor();
        // Change only this handler's local language value, leaving global game
        // language, reward OCR, Russian and other unsupported languages intact.
        var englishValue = il.Create(OpCodes.Ldc_I4, Convert.ToInt32(english.Constant));
        il.InsertAfter(languageCall, englishValue);
        il.InsertAfter(englishValue, il.Create(OpCodes.Call, import("GetSupportedLanguage")));

        // Pass the original OCR as a delegate so supported languages keep the
        // original path and the helper has no circular dependency on the client.
        var originalOcr = (MethodReference)ocrCall.Operand;
        var delegateConstructor = module.ImportReference(typeof(Func<Bitmap, string>).GetConstructors().Single());
        il.InsertBefore(ocrCall, il.Create(OpCodes.Ldnull));
        il.InsertBefore(ocrCall, il.Create(OpCodes.Ldftn, originalOcr));
        il.InsertBefore(ocrCall, il.Create(OpCodes.Newobj, delegateConstructor));
        ocrCall.Operand = import("ReadCategory");
        WidenBranches(method);

        var wrapper = module.Types.Single(t => t.FullName == "AlecaFrameClientLib.OverwolfWrapper");
        var version = new MethodDefinition("GetRelicRecommendationPatchVersionZH", MethodAttributes.Public | MethodAttributes.HideBySig, module.TypeSystem.String);
        wrapper.Methods.Add(version);
        var versionIl = version.Body.GetILProcessor();
        versionIl.Append(versionIl.Create(OpCodes.Ldstr, "3"));
        versionIl.Append(versionIl.Create(OpCodes.Ret));
    }

    private static void AddTruePrefix(MethodDefinition method, MethodReference condition)
    {
        var il = method.Body.GetILProcessor();
        var first = method.Body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Call, condition));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_1));
        il.InsertBefore(first, il.Create(OpCodes.Ret));
    }

    private static void WidenBranches(MethodDefinition method)
    {
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.OpCode.OperandType != OperandType.ShortInlineBrTarget) continue;
            switch (instruction.OpCode.Code)
            {
                case Code.Br_S: instruction.OpCode = OpCodes.Br; break;
                case Code.Brfalse_S: instruction.OpCode = OpCodes.Brfalse; break;
                case Code.Brtrue_S: instruction.OpCode = OpCodes.Brtrue; break;
                case Code.Beq_S: instruction.OpCode = OpCodes.Beq; break;
                case Code.Bne_Un_S: instruction.OpCode = OpCodes.Bne_Un; break;
                case Code.Bge_S: instruction.OpCode = OpCodes.Bge; break;
                case Code.Bge_Un_S: instruction.OpCode = OpCodes.Bge_Un; break;
                case Code.Bgt_S: instruction.OpCode = OpCodes.Bgt; break;
                case Code.Bgt_Un_S: instruction.OpCode = OpCodes.Bgt_Un; break;
                case Code.Ble_S: instruction.OpCode = OpCodes.Ble; break;
                case Code.Ble_Un_S: instruction.OpCode = OpCodes.Ble_Un; break;
                case Code.Blt_S: instruction.OpCode = OpCodes.Blt; break;
                case Code.Blt_Un_S: instruction.OpCode = OpCodes.Blt_Un; break;
                case Code.Leave_S: instruction.OpCode = OpCodes.Leave; break;
                default: throw new InvalidOperationException("Unsupported branch: " + instruction.OpCode);
            }
        }
    }
}
