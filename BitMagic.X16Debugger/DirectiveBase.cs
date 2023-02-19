﻿//using System;
//using System.Collections.Generic;
//using System.Globalization;
//using System.Linq;
//using System.Text;
//using System.Threading.Tasks;

//namespace BitMagic.X16Debugger;

//internal interface IDirective
//{
//    string Name { get; }
//    bool Execute(string[] args, StringBuilder output);
//    object ParseArgs(string[] args);
//}

//internal abstract class DirectiveBase<TArgs> : IDirective
//    where TArgs : class, new()
//{
//    #region Constructor

//    protected DirectiveBase(string directiveName)
//    {
//        this.Name = directiveName;
//    }

//    #endregion

//    #region Protected Members

//    protected abstract bool ExecuteCore(TArgs arguments, StringBuilder output);

//    #endregion

//    #region IDirective Implementation

//    public string Name { get; private set; }

//    public bool Execute(string[] args, StringBuilder output)
//    {
//        CommandLineParser parser = new CommandLineParser(typeof(TArgs));
//        TArgs arguments = null;

//        try
//        {
//            arguments = (TArgs)parser.Parse(args);
//        }
//        catch (CommandLineArgumentException ex)
//        {
//            output.AppendLine(Invariant($"Error parsing ${this.Name} directive: {ex.Message}"));
//            using (StringWriter writer = new StringWriter(output, CultureInfo.InvariantCulture))
//            {
//                WriteUsageOptions options = new WriteUsageOptions()
//                {
//                    UsagePrefix = Invariant($"    Usage: ${this.Name}"),
//                    Indent = 8
//                };

//                parser.WriteUsage(writer, 0, options);
//            }

//            return false;
//        }

//        return this.ExecuteCore(arguments, output);
//    }

//    public object ParseArgs(string[] args)
//    {
//        CommandLineParser parser = new CommandLineParser(typeof(TArgs));
//        TArgs arguments = null;

//        try
//        {
//            arguments = (TArgs)parser.Parse(args);
//        }
//        catch (CommandLineArgumentException)
//        {
//            return null;
//        }

//        return arguments;
//    }

//    #endregion
//}