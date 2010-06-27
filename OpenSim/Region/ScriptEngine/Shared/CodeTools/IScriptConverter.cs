﻿using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace OpenSim.Region.ScriptEngine.Shared.CodeTools
{
    public interface IScriptConverter : OpenSim.Framework.IPlugin
    {
        void Initialise(Compiler compiler);
        void Convert(string Script, out string CompiledScript, out string[] Warnings, out Dictionary<KeyValuePair<int, int>, KeyValuePair<int, int>> PositionMap);
        CompilerResults Compile(CompilerParameters parameters, string Script);
    }
}
