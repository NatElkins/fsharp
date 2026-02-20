module FSharp.Compiler.ComponentTests.HotReload.ApplyUpdateShared

let baselineSourceText = """
using System;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;

[assembly: System.Diagnostics.Debuggable(System.Diagnostics.DebuggableAttribute.DebuggingModes.Default |
                                         System.Diagnostics.DebuggableAttribute.DebuggingModes.DisableOptimizations |
                                         System.Diagnostics.DebuggableAttribute.DebuggingModes.EnableEditAndContinue)]

namespace Sample
{
    public static class MethodDemo
    {
        public static string GetMessage() => "Hello baseline";
    }

    public static class ModuleInfo
    {
        static partial class Accessors
        {
            [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetDebuggerInfoBits")]
            public static extern int CallGetDebuggerInfoBits(Module module);

            [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "IsEditAndContinueCapable")]
            public static extern bool CallIsEnCCapable(Module module);

            [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "IsEditAndContinueEnabled")]
            public static extern bool CallIsEncEnabled(Module module);
        }

        public static int? TryGetDebuggerInfoBits()
        {
            var mod = typeof(ModuleInfo).Assembly.ManifestModule;
            try { return Accessors.CallGetDebuggerInfoBits(mod); } catch { }
            var t = mod.GetType();
            var m = t.GetMethod("GetDebuggerInfoBits", BindingFlags.Instance | BindingFlags.NonPublic);
            if (m != null)
                return (int)m.Invoke(mod, null);
            var f = t.GetField("m_debuggerBits", BindingFlags.Instance | BindingFlags.NonPublic)
                 ?? t.GetField("m_debuggerInfoBits", BindingFlags.Instance | BindingFlags.NonPublic);
            if (f != null)
                return (int)f.GetValue(mod);
            return null;
        }

        public static bool? TryIsEditAndContinueCapable()
        {
            var mod = typeof(ModuleInfo).Assembly.ManifestModule;
            try { return Accessors.CallIsEnCCapable(mod); } catch { }
            var t = mod.GetType();
            var m = t.GetMethod("IsEditAndContinueCapable", BindingFlags.Instance | BindingFlags.NonPublic);
            return m != null ? (bool)m.Invoke(mod, null) : (bool?)null;
        }

        public static bool? TryIsEditAndContinueEnabled()
        {
            var mod = typeof(ModuleInfo).Assembly.ManifestModule;
            try { return Accessors.CallIsEncEnabled(mod); } catch { }
            var t = mod.GetType();
            var m = t.GetMethod("IsEditAndContinueEnabled", BindingFlags.Instance | BindingFlags.NonPublic);
            return m != null ? (bool)m.Invoke(mod, null) : (bool?)null;
        }

        public static (bool isSystem, bool isReflectionEmit, bool isReadyToRun)? TryPeFlags()
        {
            try
            {
                var path = typeof(ModuleInfo).Assembly.Location;
                using var fs = File.OpenRead(path);
                using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
                var md = pe.GetMetadataReader();
                var asm = md.GetAssemblyDefinition();
                bool isSystem = string.Equals(md.GetString(asm.Name), "System.Private.CoreLib", StringComparison.Ordinal);
                bool isRefEmit = false;
                bool isR2R = pe.PEHeaders.CorHeader.Flags.HasFlag(System.Reflection.PortableExecutable.CorFlags.ILOnly) == false;
                return (isSystem, isRefEmit, isR2R);
            }
            catch { return null; }
        }
    }
}
"""
