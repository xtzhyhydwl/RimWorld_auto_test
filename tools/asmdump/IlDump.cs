// IlDump - read-only IL disassembler for RimWorld assemblies (no decompiler needed).
// Usage: IlDump.exe <assemblyPath> <managedDir> <TypeFullName> <memberNameFilter|*>
// Resolves metadata tokens to names. Never executes game code.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

internal static class IlDump
{
    private static string managedDir;
    private static readonly Dictionary<short, OpCode> ops = new Dictionary<short, OpCode>();

    private static void Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--usages")
        {
            RunUsages(args[1], args[2], args[3], args[4]);
            return;
        }
        if (args.Length < 4)
        {
            Console.WriteLine("usage: IlDump.exe <asmPath> <managedDir> <TypeFullName> <memberFilter|*>");
            return;
        }
        foreach (FieldInfo fi in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            OpCode op = (OpCode)fi.GetValue(null);
            ops[op.Value] = op;
        }
        managedDir = args[1];
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            try
            {
                string p = Path.Combine(managedDir, new AssemblyName(e.Name).Name + ".dll");
                if (File.Exists(p)) return Assembly.LoadFrom(p);
            }
            catch { }
            return null;
        };
        Assembly asm = Assembly.LoadFrom(args[0]);
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

        Type target = types.FirstOrDefault(t => t != null && t.FullName == args[2]);
        if (target == null) { Console.WriteLine("TYPE NOT FOUND: " + args[2]); return; }
        Console.WriteLine("### TYPE " + target.FullName);

        const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic |
                                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var methods = target.GetMethods(BF).Cast<MethodBase>()
                             .Concat(target.GetConstructors(BF)).ToList();
        var filters = args[3] == "*" ? null : args[3].Split('|');
        foreach (MethodBase m in methods)
        {
            if (filters != null && !filters.Any(f => m.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
            try { DumpMethod(m); }
            catch (Exception ex) { Console.WriteLine("  !! IL DUMP FAILED: " + ex.GetType().FullName + ": " + ex.Message); }
        }
    }

    private static void RunUsages(string asmPath, string managed, string fieldName, string typeFilter)
    {
        foreach (FieldInfo fi in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            OpCode op = (OpCode)fi.GetValue(null);
            ops[op.Value] = op;
        }
        managedDir = managed;
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            try
            {
                string p = Path.Combine(managedDir, new AssemblyName(e.Name).Name + ".dll");
                if (File.Exists(p)) return Assembly.LoadFrom(p);
            }
            catch { }
            return null;
        };
        Assembly asm = Assembly.LoadFrom(asmPath);
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

        const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic |
                                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        int hits = 0;
        foreach (Type t in types)
        {
            if (t == null) continue;
            IEnumerable<MethodBase> ms;
            try { ms = t.GetMethods(BF).Cast<MethodBase>().Concat(t.GetConstructors(BF)).ToList(); }
            catch { continue; }
            foreach (MethodBase m in ms)
            {
                MethodBody body;
                try { body = m.GetMethodBody(); } catch { continue; }
                if (body == null) continue;
                byte[] il;
                try { il = body.GetILAsByteArray(); } catch { continue; }
                if (il == null) continue;
                int i = 0;
                while (i < il.Length)
                {
                    short code = il[i++];
                    if (code == 0xFE) { if (i >= il.Length) break; code = (short)(0xFE00 | il[i++]); }
                    OpCode op;
                    if (!ops.TryGetValue(code, out op)) break;
                    if (op.OperandType == OperandType.InlineField)
                    {
                        if (i + 4 > il.Length) break;
                        int tok = BitConverter.ToInt32(il, i);
                        FieldInfo f = null;
                        try { f = m.Module.ResolveField(tok); } catch { }
                        if (f != null && f.Name == fieldName &&
                            (typeFilter == "*" || (f.DeclaringType != null && f.DeclaringType.FullName == typeFilter)))
                        {
                            Console.WriteLine(Sig(m) + "     [" + op.Name + " " + Name(f.DeclaringType) + "::" + f.Name + "]");
                            hits++;
                        }
                        i += 4;
                        continue;
                    }
                    i += OperandSize(op.OperandType, il, i);
                }
            }
        }
        Console.WriteLine("== USAGE HITS: " + hits);
    }

    private static int OperandSize(OperandType ot, byte[] il, int i)
    {
        switch (ot)
        {
            case OperandType.InlineNone: return 0;
            case OperandType.ShortInlineI:
            case OperandType.ShortInlineVar:
            case OperandType.ShortInlineBrTarget: return 1;
            case OperandType.InlineVar: return 2;
            case OperandType.InlineI:
            case OperandType.InlineBrTarget:
            case OperandType.InlineMethod:
            case OperandType.InlineField:
            case OperandType.InlineType:
            case OperandType.InlineString:
            case OperandType.InlineTok:
            case OperandType.InlineSig:
            case OperandType.ShortInlineR: return 4;
            case OperandType.InlineI8:
            case OperandType.InlineR: return 8;
            case OperandType.InlineSwitch:
                if (i + 4 > il.Length) return 0;
                int n = BitConverter.ToInt32(il, i);
                return 4 + 4 * n;
            default: return 0;
        }
    }

    private static void DumpMethod(MethodBase m)
    {
        Console.WriteLine();
        Console.Write("--- " + Sig(m));
        MethodBody body = null;
        try { body = m.GetMethodBody(); } catch (Exception e) { Console.WriteLine("  <no body: " + e.GetType().Name + ">"); return; }
        if (body == null) { Console.WriteLine("  <abstract/extern - no IL>"); return; }
        Console.WriteLine("  locals: " +
            (body.LocalVariables.Count == 0 ? "(none)" :
             string.Join(", ", body.LocalVariables.Select(v => "[" + v.LocalIndex + "] " + N(v.LocalType)).ToArray())));
        foreach (ExceptionHandlingClause c in body.ExceptionHandlingClauses)
        {
            string catchName = "-";
            try { if (c.Flags == ExceptionHandlingClauseOptions.Clause && c.CatchType != null) catchName = N(c.CatchType); }
            catch { catchName = "?"; }
            Console.WriteLine("  EH " + c.Flags + " try IL_" + c.TryOffset.ToString("X4") + "-" +
                              (c.TryOffset + c.TryLength).ToString("X4") + " handler IL_" + c.HandlerOffset.ToString("X4") +
                              " catch=" + catchName);
        }

        byte[] il = body.GetILAsByteArray();
        Module mod = m.Module;
        int i = 0;
        while (i < il.Length)
        {
            int start = i;
            short code = il[i++];
            if (code == 0xFE) code = (short)(0xFE00 | il[i++]);
            OpCode op;
            if (!ops.TryGetValue(code, out op)) { Console.WriteLine("  IL_" + start.ToString("X4") + ": <unknown 0x" + code.ToString("X4") + ">"); break; }

            string operand = "";
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineI:
                    { sbyte v = (sbyte)il[i]; i += 1; operand = v.ToString(); break; }
                case OperandType.ShortInlineVar:
                    { byte v = il[i]; i += 1; operand = v.ToString(); break; }
                case OperandType.InlineVar:
                    { ushort v = BitConverter.ToUInt16(il, i); i += 2; operand = v.ToString(); break; }
                case OperandType.InlineI:
                    { int v = BitConverter.ToInt32(il, i); i += 4; operand = v.ToString(); break; }
                case OperandType.InlineI8:
                    { long v = BitConverter.ToInt64(il, i); i += 8; operand = v.ToString(); break; }
                case OperandType.ShortInlineR:
                    { float v = BitConverter.ToSingle(il, i); i += 4; operand = v.ToString("R"); break; }
                case OperandType.InlineR:
                    { double v = BitConverter.ToDouble(il, i); i += 8; operand = v.ToString("R"); break; }
                case OperandType.InlineString:
                    {
                        int tok = BitConverter.ToInt32(il, i); i += 4;
                        try { operand = "\"" + mod.ResolveString(tok) + "\""; } catch { operand = "tok " + tok; }
                        break;
                    }
                case OperandType.InlineBrTarget:
                    {
                        int d = BitConverter.ToInt32(il, i); i += 4;
                        operand = "IL_" + (i + d).ToString("X4"); break;
                    }
                case OperandType.ShortInlineBrTarget:
                    {
                        sbyte d = (sbyte)il[i]; i += 1;
                        operand = "IL_" + (i + d).ToString("X4"); break;
                    }
                case OperandType.InlineSwitch:
                    {
                        int n = BitConverter.ToInt32(il, i); i += 4;
                        var targets = new List<string>();
                        int baseAfter = i + 4 * n;
                        for (int k = 0; k < n; k++) { targets.Add("IL_" + (baseAfter + BitConverter.ToInt32(il, i)).ToString("X4")); i += 4; }
                        operand = string.Join(", ", targets.ToArray());
                        break;
                    }
                case OperandType.InlineMethod:
                    {
                        int tok = BitConverter.ToInt32(il, i); i += 4;
                        operand = Resolve(mod, tok, 0); break;
                    }
                case OperandType.InlineField:
                    {
                        int tok = BitConverter.ToInt32(il, i); i += 4;
                        operand = Resolve(mod, tok, 1); break;
                    }
                case OperandType.InlineType:
                    {
                        int tok = BitConverter.ToInt32(il, i); i += 4;
                        operand = Resolve(mod, tok, 2); break;
                    }
                case OperandType.InlineTok:
                    {
                        int tok = BitConverter.ToInt32(il, i); i += 4;
                        operand = Resolve(mod, tok, 3); break;
                    }
                case OperandType.InlineSig:
                    {
                        int tok = BitConverter.ToInt32(il, i); i += 4;
                        operand = "sig 0x" + tok.ToString("X8"); break;
                    }
                default:
                    { operand = "?"; break; }
            }
            Console.WriteLine("  IL_" + start.ToString("X4") + ": " + op.Name + (operand.Length > 0 ? " " + operand : ""));
        }
    }

    private static string Resolve(Module mod, int tok, int kind)
    {
        try
        {
            if (kind == 0 || kind == 3) { var mb = mod.ResolveMethod(tok); if (mb != null) return Name(mb.DeclaringType) + "::" + mb.Name; }
        }
        catch { }
        try
        {
            if (kind == 1 || kind == 3) { var f = mod.ResolveField(tok); if (f != null) return Name(f.DeclaringType) + "::" + f.Name; }
        }
        catch { }
        try
        {
            if (kind == 2 || kind == 3) { var t = mod.ResolveType(tok); if (t != null) return N(t); }
        }
        catch { }
        return "tok 0x" + tok.ToString("X8");
    }

    private static string Name(Type t) { return t == null ? "?" : (t.FullName ?? t.Name); }

    private static string Sig(MethodBase m)
    {
        var mi = m as MethodInfo;
        string ret = mi != null ? N(mi.ReturnType) + " " : "";
        string st = m.IsStatic ? "static " : "";
        return st + ret + Name(m.DeclaringType) + "::" + m.Name + "(" +
               string.Join(", ", m.GetParameters().Select(p => N(p.ParameterType) + " " + p.Name).ToArray()) + ")";
    }

    private static string N(Type t)
    {
        if (t == null) return "?";
        if (t.IsByRef) return "ref " + N(t.GetElementType());
        if (t.IsArray) return N(t.GetElementType()) + "[]";
        if (t.IsGenericType)
        {
            string b = t.Name; int k = b.IndexOf('`'); if (k > 0) b = b.Substring(0, k);
            return b + "<" + string.Join(", ", t.GetGenericArguments().Select(N).ToArray()) + ">";
        }
        switch (t.FullName)
        {
            case "System.Void": return "void";
            case "System.Int32": return "int";
            case "System.Single": return "float";
            case "System.Double": return "double";
            case "System.Boolean": return "bool";
            case "System.String": return "string";
            case "System.Object": return "object";
        }
        return t.FullName ?? t.Name;
    }
}
