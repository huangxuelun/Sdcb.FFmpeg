﻿using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CppSharp;
using CppSharp.AST;
using CppSharp.Parser;
using FFmpeg.AutoGen.CppSharpUnsafeGenerator.Definitions;
using FFmpeg.AutoGen.CppSharpUnsafeGenerator.Processors;
using ClangParser = CppSharp.ClangParser;
using MacroDefinition = FFmpeg.AutoGen.CppSharpUnsafeGenerator.Definitions.MacroDefinition;

namespace FFmpeg.AutoGen.CppSharpUnsafeGenerator
{
    internal class Generator
    {
        private readonly ASTProcessor _astProcessor;
        private bool _hasParsingErrors;

        public Generator(ASTProcessor astProcessor) => _astProcessor = astProcessor;

        public string[] Defines { get; init; } = Array.Empty<string>();
        public string[] IncludeDirs { get; init; } = Array.Empty<string>();
        public FunctionExport[] Exports { get; init; }

        public string Namespace { get; init; }
        public string ClassName { get; init; }

        public bool SuppressUnmanagedCodeSecurity { get; init; }

        public InlineFunctionDefinition[] ExistingInlineFunctions { get; init; }

        public void Parse(params string[] sourceFiles)
        {
            _hasParsingErrors = false;
            var context = ParseInternal(sourceFiles);
            if (_hasParsingErrors)
                throw new InvalidOperationException();

            Process(context);
        }


        public void WriteLibraries(string combine)
        {
            WriteInternal(combine,
                (units, writer) =>
                {
                    writer.WriteLine("using System.Collections.Generic;");
                    writer.WriteLine();

                    writer.WriteLine($"public unsafe static partial class {ClassName}");

                    using (writer.BeginBlock())
                    {
                        writer.WriteLine(
                            "public static Dictionary<string, int> LibraryVersionMap =  new ()");

                        using (writer.BeginBlock(true))
                        {
                            var libraryVersionMap = Exports.Select(x => new { x.LibraryName, x.LibraryVersion })
                                .Distinct()
                                .ToDictionary(x => x.LibraryName, x => x.LibraryVersion);
                            foreach (var pair in libraryVersionMap)
                                writer.WriteLine($"[\"{pair.Key}\"] = {pair.Value},");
                        }

                        writer.WriteLine(";");
                    }
                });
        }

        public void WriteEnums(string outputFile)
        {
            WriteInternal(outputFile,
                (units, writer) =>
                {
                    units.OfType<EnumerationDefinition>()
                        .OrderBy(x => x.Name)
                        .ToList()
                        .ForEach(x =>
                        {
                            writer.WriteEnumeration(x);
                            writer.WriteLine();
                        });
                });
        }

        public void WriteDelegates(string outputFile)
        {
            WriteInternal(outputFile,
                (units, writer) =>
                {
                    units.OfType<DelegateDefinition>().ForEach((x, i) =>
                    {
                        writer.WriteDelegate(x);
                        if (!i.IsLast) writer.WriteLine();
                    });
                });
        }

        public void WriteMacros(string outputFile)
        {
            MacroEnumDef[] knownConstEnums = new MacroEnumDef[]
            {
                new ("AV_CODEC_FLAG_", "AVCodecFlags"),
                new ("AV_CODEC_FLAG2_", "AVCodecFlags2"),
                new ("AV_PKG_FLAG_", "AVPacketFlags"),
                new ("SLICE_FLAG_", "CodecSliceFlags"),
                new ("AV_CH_", "AVChannel"),
                new ("AV_CODEC_CAP_", "AVCodecCompability"),
                new ("FF_MB_DECISION_", "FFMacroblockDecision"),
                new ("FF_CMP_", "FFComparison"),
                new ("PARSER_FLAG_", "ParserFlags"),
                new ("AVIO_FLAG_", "AVIOFlags"),
                new ("FF_PROFILE_", "FFProfile"),
                new ("AVSEEK_FLAG_", "AVSeekFlags"),
                new ("AV_PIX_FMT_FLAG_", "AVPixelFormatFlags"),
                new ("AV_OPT_FLAG_", "OptionFlags"),
                new ("AV_LOG_", "AVLog", Except: new []{ "AV_LOG_C" }.ToHashSet()),
                new ("AV_CPU_FLAG_", "AVCpuFlags"),
                new ("AV_PKT_FLAG_", "AVPacketFlags"),
                new ("AVFMT_FLAG_", "AVFormatFlags"), 
            };
            Dictionary<string, MacroEnumDef> knownConstEnumMapping = knownConstEnums.ToDictionary(k => k.Prefix, v => v);

            WriteInternal(outputFile,
                (units, writer) =>
                {
                    writer.WriteLine($"public unsafe static partial class {ClassName}");
                    using (writer.BeginBlock())
                        units.OfType<MacroDefinition>()
                            .OrderBy(x => x.Name)
                            .GroupBy(x => knownConstEnums.FirstOrDefault(known => known.Match(x.Name)) switch
                            {
                                null => null,
                                MacroEnumDef prefix => prefix.Prefix,
                            })
                            .ForEach((group, i) =>
                            {
                                if (group.Key == null)
                                {
                                    foreach (MacroDefinition macro in group)
                                    {
                                        writer.WriteMacro(macro);
                                    }
                                }
                                else
                                {
                                    writer.WriteMacroEnum(group, knownConstEnumMapping[group.Key]);
                                    if (!i.IsLast) writer.WriteLine();
                                }
                            });
                });
        }

        public void WriteExportFunctions(string outputFile)
        {
            WriteInternal(outputFile,
                (units, writer) =>
                {
                    writer.WriteLine($"public unsafe static partial class {ClassName}");
                    using var _ = writer.BeginBlock();
                    writer.WriteLine("private const string PlatformNotSupportedMessageFormat = \"{0} is not supported on this platform.\";");
                    writer.WriteLine();
                    units.OfType<ExportFunctionDefinition>()
                        .OrderBy(x => x.LibraryName)
                        .ThenBy(x => x.Name)
                        .ForEach((x, i) =>
                        {
                            writer.WriteFunction(x);
                            if (!i.IsLast) writer.WriteLine();
                        });
                });
        }

        public void WriteInlineFunctions(string outputFile)
        {
            var existingInlineFunctionMap = ExistingInlineFunctions.ToDictionary(x => x.Name);
            WriteInternal(outputFile,
                (units, writer) =>
                {
                    writer.WriteLine($"public unsafe static partial class {ClassName}");
                    using (writer.BeginBlock())
                        units.OfType<InlineFunctionDefinition>()
                            .OrderBy(x => x.Name)
                            .Select(RewriteFunctionBody)
                            .ToList()
                            .ForEach(x =>
                            {
                                writer.WriteFunction(x);
                                writer.WriteLine();
                            });
                });

            InlineFunctionDefinition RewriteFunctionBody(InlineFunctionDefinition function)
            {
                if (existingInlineFunctionMap.TryGetValue(function.Name, out var existing) &&
                    function.OriginalBodyHash == existing.OriginalBodyHash)
                    function.Body = existing.Body;

                return function;
            }
        }

        public void WriteArrays(string outputFile)
        {
            WriteInternal(outputFile,
                (units, writer) =>
                {
                    writer.WriteLine();
                    units.OfType<FixedArrayDefinition>()
                        .OrderBy(x => x.Size)
                        .ThenBy(x => x.Name)
                        .ToList().ForEach(x =>
                        {
                            writer.WriteFixedArray(x);
                            writer.WriteLine();
                        });
                });
        }

        public void WriteStructures(string outputFile)
        {
            WriteInternal(outputFile,
                (units, writer) =>
                {
                    units.OfType<StructureDefinition>()
                        .Where(x => x.IsComplete)
                        .ToList()
                        .ForEach(x =>
                        {
                            writer.WriteStructure(x);
                            writer.WriteLine();
                        });
                });
        }

        public void WriteIncompleteStructures(string outputFile)
        {
            WriteInternal(outputFile,
                (units, writer) =>
                {
                    units.OfType<StructureDefinition>()
                        .Where(x => !x.IsComplete)
                        .ToList()
                        .ForEach(x =>
                        {
                            writer.WriteStructure(x);
                            writer.WriteLine();
                        });
                });
        }

        private ASTContext ParseInternal(string[] sourceFiles)
        {
            var parserOptions = new ParserOptions
            {
                Verbose = false,
                ASTContext = new CppSharp.Parser.AST.ASTContext(),
                LanguageVersion = LanguageVersion.C99_GNU
            };

            parserOptions.Setup();

            foreach (var includeDir in IncludeDirs) parserOptions.AddIncludeDirs(includeDir);

            foreach (var define in Defines) parserOptions.AddDefines(define);
            var result = ClangParser.ParseSourceFiles(sourceFiles, parserOptions);
            OnSourceFileParsed(sourceFiles, result);
            return ClangParser.ConvertASTContext(parserOptions.ASTContext);
        }

        private void OnSourceFileParsed(IEnumerable<string> files, ParserResult result)
        {
            switch (result.Kind)
            {
                case ParserResultKind.Success:
                    Diagnostics.Message("Parsed '{0}'", string.Join(", ", files));
                    break;
                case ParserResultKind.Error:
                    Diagnostics.Error("Error parsing '{0}'", string.Join(", ", files));
                    _hasParsingErrors = true;
                    break;
                case ParserResultKind.FileNotFound:
                    Diagnostics.Error("A file from '{0}' was not found", string.Join(",", files));
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            for (uint i = 0; i < result.DiagnosticsCount; ++i)
            {
                var diagnostics = result.GetDiagnostics(i);

                var message =
                    $"{diagnostics.FileName}({diagnostics.LineNumber},{diagnostics.ColumnNumber}): {diagnostics.Level.ToString().ToLower()}: {diagnostics.Message}";
                Diagnostics.Message(message);
            }
        }

        private void Process(ASTContext context) =>
            _astProcessor.Process(context.TranslationUnits.Where(x => !x.IsSystemHeader));

        private void WriteInternal(string outputFile, Action<IReadOnlyList<IDefinition>, Writer> execute)
        {
            using var streamWriter = File.CreateText(outputFile);
            using var textWriter = new IndentedTextWriter(streamWriter);
            var writer = new Writer(textWriter)
            {
                SuppressUnmanagedCodeSecurity = SuppressUnmanagedCodeSecurity
            };
            writer.WriteLine("using System;");
            writer.WriteLine("using System.Runtime.InteropServices;");
            if (SuppressUnmanagedCodeSecurity) writer.WriteLine("using System.Security;");
            writer.WriteLine();
            writer.WriteLine("#pragma warning disable 169");
            writer.WriteLine("#pragma warning disable CS0649");
            writer.WriteLine("#pragma warning disable CS0108");
            writer.WriteLine($"namespace {Namespace}");
            using (writer.BeginBlock()) execute(_astProcessor.Units, writer);
        }
    }

    public record MacroEnumDef(string Prefix, string EnumName, HashSet<string> Except = default)
    {
        public bool Match(string name) => name.StartsWith(Prefix) && (Except == null || !Except.Contains(name));
    }
}