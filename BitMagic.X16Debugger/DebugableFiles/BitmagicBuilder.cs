﻿using BitMagic.TemplateEngine.Compiler;
using BitMagic.Common;
using BitMagic.Compiler;
using BitMagic.TemplateEngine.X16;
using BitMagic.Compiler.Files;
using BitMagic.X16Debugger.Extensions;

namespace BitMagic.X16Debugger.DebugableFiles;

internal class BitmagicBuilder
{
    private readonly DebugableFileManager _fileManager;
    private readonly CodeGeneratorManager _codeGeneratorManager;
    private readonly IEmulatorLogger _logger;

    public BitmagicBuilder(DebugableFileManager fileManager, CodeGeneratorManager codeGeneratorManager, IEmulatorLogger logger)
    {
        _logger = logger;
        _fileManager = fileManager;
        _codeGeneratorManager = codeGeneratorManager;
    }

    /// <summary>
    /// Build project and return the main binary file
    /// </summary>
    /// <param name="debugProject"></param>
    /// <returns>Binary file for the main segment</returns>
    public async Task<(DebugWrapper?, CompileState)> Build(X16DebugProject debugProject)
    {
        var project = new Project();
        _logger.LogLine($"Compiling {debugProject.Source} ");

        if (debugProject.CompileOptions != null)
            project.CompileOptions = debugProject.CompileOptions;

        debugProject.Source = Path.GetFullPath(debugProject.Source).FixFilename();
        var codeFile = new BitMagicProjectFile(debugProject.Source);
        project.Code = codeFile;
        await codeFile.Load();

        var engine = CsasmEngine.CreateEngine();
        var content = project.Code.Content;

        if (content.Any()) // ??
        {
            var templateOptions = debugProject.CompileOptions!.AsTemplateOptions(debugProject.BasePath);
            var templateResult = engine.ProcessFile(project.Code, debugProject.Source, templateOptions, _logger).GetAwaiter().GetResult();

            templateResult.ReferenceId = _codeGeneratorManager.Register(debugProject.Source, templateResult);
            var filename = (Path.GetFileNameWithoutExtension(debugProject.Source) + ".generated.bmasm").FixFilename();

            templateResult.SetName(filename);
            templateResult.SetParentAndMap(project.Code);

            if (debugProject.CompileOptions != null && debugProject.CompileOptions.SaveGeneratedBmasm)
            {
                File.WriteAllText(Path.Combine(templateOptions.BinFolder, filename), templateResult.Source.Code);
            }

            project.Code = templateResult;
        }

        var compiler = new Compiler.Compiler(project, _logger);

        var compileResult = await compiler.Compile();

        compileResult.CreateBinarySourceFiles();
        project.Code.MapChildren();

        _fileManager.AddFiles(project.Code); // loads whole family and sets their ID

        var mainFile = compileResult.Data.Values.FirstOrDefault(i => i.IsMain);

        DebugWrapper? toReturn = null;
        if (mainFile != null)
        {
            toReturn = _fileManager.GetFile_New(mainFile.FileName.ToUpper());
        }

        if (compileResult.Warnings.Any())
        {
            _logger.LogLine("Warnings:");
            foreach (var warning in compileResult.Warnings)
            {
                _logger.LogLine(warning);
            }
        }
        else
        {
            _logger.LogLine("... Done.");
        }

        return (toReturn, compileResult.State);
    }
}
