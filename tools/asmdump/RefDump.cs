// RefDump - read-only metadata-level reference scanner for RimWorld assemblies.
// Usage: RefDump.exe <asmPath> <managedDir> <typeFilter|*> [memberFilter|*]
//   typeFilter: "=Full.Name" exact, otherwise substring. "*" = any.
//   Scans IL metadata tokens (calls / ldtoken / typeof in code) AND custom-attribute
//   Type/string arguments (Harmony [HarmonyPatch(typeof(X), nameof(Y))]).
// Never executes game code.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

internal static class RefDump
{
    private static string managedDir;
    private static readonly Dictionary<short, OpCode> ops = new Dictionary<short, OpCode>();
    private static string[] typeFilters;
    private static string[] memberFilters;

    private static void Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("usage: RefDump.exe <asmPath> <managedDir> <typeFilter|*> [memberFilter|*]");
            return;
        }
        foreach (FieldInfo fi in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            OpCode op = (OpCode)fi.GetValue(null);
            ops[op.Value] = op;
        }
        managedDir = args[1];
        // typeFilter supports "|" separated alternatives; "=X" means exact match for that alternative.
        typeFilters = args[2] == "*" ? new[] { "*" } : args[2].Split('|');
        memberFilters = (args.Length < 4 || args[3] == "*") ? null : args[3].Split('|');

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

        Assembly asm;
        try { asm = Assembly.LoadFrom(args[0]); }
        catch (Exception ex) { Console.WriteLine("LOAD FAILED: " + ex.GetType().Name + ": " + ex.Message); return; }

        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

        const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic |
                                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        int hits = 0;
        foreach (Type t in types)
        {
            if (t == null) continue;
            var members = new List<MethodBase>();
            try
            {
                members.AddRange(t.GetMethods(BF).Cast<MethodBase>());
                members.AddRange(t.GetConstructors(BF));
            }
            catch { continue; }

            foreach (MethodBase m in members)
            {
                // 1) custom attributes (Harmony attribute declarations)
                try
                {
                    foreach (CustomAttributeData cad in CustomAttributeData.GetCustomAttributes(m))
                    {
                        foreach (CustomAttributeTypedArgument a in cad.ConstructorArguments)
                        {
                            if (!Matches(a, out string why)) continue;
                            Console.WriteLine(t.FullName + "::" + m.Name + "  [attr " + cad.AttributeType.Name + "]  ->  " + why);
                            hits++;
                        }
                    }
                }
                catch { }

                // 2) IL metadata tokens
                MethodBody body;
                try { body = m.GetMethodBody(); } catch { continue; }
                if (body == null) continue;
                byte[] il;
                try { il = body.GetILAsByteArray(); } catch { continue; }
                if (il == null) continue;
                Module mod = m.Module;
                int pos = 0;
                while (pos < il.Length)
                {
                    int start = pos;
                    short code;
                    byte b = il[pos++];
                    if (b == 0xFE) { code = (short)(0xFE00 | il[pos++]); }
                    else code = b;
                    OpCode op;
                    if (!ops.TryGetValue(code, out op)) break;
                    int size = 0;
                    switch (op.OperandType)
                    {
                        case OperandType.InlineNone: size = 0; break;
                        case OperandType.ShortInlineI:
                        case OperandType.ShortInlineVar:
                        case OperandType.ShortInlineBrTarget: size = 1; break;
                        case OperandType.InlineVar: size = 2; break;
                        case OperandType.InlineI:
                        case OperandType.InlineBrTarget:
                        case OperandType.InlineField:
                        case OperandType.InlineMethod:
                        case OperandType.InlineSig:
                        case OperandType.InlineString:
                        case OperandType.InlineTok:
                        case OperandType.InlineType:
                        case OperandType.InlineSwitch: size = 4; break;
                        case OperandType.InlineI8:
                        case OperandType.InlineR: size = 8; break;
                        case OperandType.ShortInlineR: size = 4; break;
                        default: size = 0; break;
                    }
                    if (op.OperandType == OperandType.InlineSwitch)
                    {
                        if (pos + 4 > il.Length) break;
                        int n = BitConverter.ToInt32(il, pos);
                        size = 4 + 4 * n;
                    }
                    if (pos + size > il.Length) break;

                    if (op.OperandType == OperandType.InlineMethod || op.OperandType == OperandType.InlineField ||
                        op.OperandType == OperandType.InlineType || op.OperandType == OperandType.InlineTok)
                    {
                        int token = BitConverter.ToInt32(il, pos);
                        MemberInfo mi = null;
                        try { mi = mod.ResolveMember(token); } catch { }
                        if (mi != null && Matches(mi, out string why2))
                        {
                            Console.WriteLine(t.FullName + "::" + m.Name + "  [IL_" + start.ToString("X4") + " " + op.Name + "]  ->  " + why2);
                            hits++;
                        }
                    }
                    pos += size;
                }
            }
        }
        Console.WriteLine("== HITS: " + hits);
    }

    private static bool Matches(CustomAttributeTypedArgument a, out string why)
    {
        why = null;
        if (a.ArgumentType == typeof(Type) && a.Value is Type tt) return Matches(tt, out why);
        if (a.Value is string s && memberFilters != null && NameOk(s))
        {
            why = "(string) " + s;
            return true;
        }
        if (a.Value is IEnumerable<CustomAttributeTypedArgument> seq)
        {
            foreach (CustomAttributeTypedArgument sub in seq)
                if (Matches(sub, out why)) return true;
        }
        return false;
    }

    private static bool Matches(Type t, out string why)
    {
        why = null;
        if (t == null) return false;
        if (!TypeOk(t.FullName)) return false;
        why = t.FullName;
        return true;
    }

    private static bool Matches(MemberInfo mi, out string why)
    {
        why = null;
        // ldtoken of a type resolves to a Type; Type.DeclaringType is null, so it must
        // be handled separately or every [HarmonyPatch(typeof(X))] is missed.
        if (mi is Type asType) return Matches(asType, out why);
        Type dt = mi.DeclaringType;
        if (dt == null) return false;
        if (!TypeOk(dt.FullName)) return false;
        if (!NameOk(mi.Name)) return false;
        why = dt.FullName + "::" + mi.Name;
        return true;
    }

    private static bool TypeOk(string fullName)
    {
        if (fullName == null) return false;
        foreach (string f in typeFilters)
        {
            if (f == "*") return true;
            if (f.StartsWith("="))
            {
                if (fullName == f.Substring(1)) return true;
            }
            else if (fullName.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    private static bool NameOk(string name)
    {
        if (memberFilters == null) return true;
        return memberFilters.Any(f => name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
