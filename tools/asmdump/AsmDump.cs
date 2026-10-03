// AsmDump - read-only reflection signature dumper for RimWorld assemblies.
// Usage: AsmDump.exe <assemblyPath> <managedDir> <typeFilter(t1|t2|*)> [memberFilter] [flags]
//   flags: "np" = include non-public members (default: public only)
// Never invokes game code: only metadata reflection + GetRawConstantValue for literals.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

internal static class AsmDump
{
    private static string managedDir;
    private static bool includeNonPublic;
    private static string memberFilter;
    private static readonly Dictionary<Type, string> alias = new Dictionary<Type, string>();

    private static void Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("usage: AsmDump.exe <asmPath> <managedDir> <typeFilter> [memberFilter] [np]");
            return;
        }
        string asmPath = args[0];
        managedDir = args[1];
        string[] typeFilters = args[2] == "*" ? null : args[2].Split('|');
        memberFilter = args.Length > 3 && args[3] != "*" ? args[3] : null;
        includeNonPublic = args.Any(a => a == "np");

        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            try
            {
                string simple = new AssemblyName(e.Name).Name + ".dll";
                string p = Path.Combine(managedDir, simple);
                if (File.Exists(p)) return Assembly.LoadFrom(p);
            }
            catch { }
            return null;
        };

        var asm = Assembly.LoadFrom(asmPath);
        Console.WriteLine("== ASSEMBLY: " + asm.FullName);
        Console.WriteLine("== LOCATION: " + asm.Location);

        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray();
            Console.WriteLine("== NOTE: ReflectionTypeLoadException, " + types.Length + " types usable");
        }

        foreach (Type t in types.Where(t => t != null).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            try { DumpType(t, typeFilters); }
            catch (Exception ex)
            {
                Console.WriteLine("    !! TYPE DUMP FAILED [" + Safe(() => t.FullName) + "]: " +
                                  ex.GetType().Name + ": " + ex.Message);
            }
            Console.Out.Flush();
        }
    }

    private static string NS(Type t)
    {
        try { return N(t); }
        catch (Exception e) { return "<type-load-failed: " + Safe(() => t.FullName) + " / " + e.GetType().Name + ">"; }
    }

    private static string Safe(Func<string> f)
    {
        try { return f(); } catch { return "<unnameable>"; }
    }

    private static void DumpType(Type t, string[] typeFilters)
    {
        {
            string full = t.FullName ?? t.Name;
            if (typeFilters != null && !typeFilters.Any(f =>
                    f.StartsWith("=") ? full == f.Substring(1)
                                      : full.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)) return;

            Console.WriteLine();
            Console.WriteLine("### TYPE " + full);
            Console.WriteLine("    kind=" + KindOf(t) +
                              (t.BaseType != null ? "  base=" + N(t.BaseType) : "") +
                              (t.IsAbstract ? " abstract" : "") + (t.IsSealed ? " sealed" : "") +
                              (t.IsInterface ? " interface" : ""));
            if (t.IsEnum)
            {
                foreach (string n in Enum.GetNames(t))
                    Console.WriteLine("    ENUMVAL " + n + " = " + Convert.ToInt64(Enum.Parse(t, n)));
                return;
            }

            const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic |
                                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (FieldInfo f in t.GetFields(BF).OrderBy(f => f.Name, StringComparer.Ordinal))
            {
              try
              {
                if (!Show(f.Name, f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly)) continue;
                string extra = "";
                if (f.IsLiteral)
                {
                    try { extra = " = " + (f.GetRawConstantValue() ?? "null"); } catch { }
                }
                Console.WriteLine("    FIELD " + Vis(f) + (f.IsStatic ? "static " : "") +
                                  (f.IsLiteral ? "const " : f.IsInitOnly ? "readonly " : "") +
                                  NS(f.FieldType) + " " + f.Name + extra);
              }
              catch (Exception fe)
              {
                Console.WriteLine("    FIELD " + f.Name + " <dump failed: " + fe.GetType().Name + ": " + fe.Message + ">");
              }
            }

            foreach (PropertyInfo p in t.GetProperties(BF).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                MethodInfo g = p.GetGetMethod(true), s = p.GetSetMethod(true);
                bool pub = (g != null && (g.IsPublic || g.IsFamily)) || (s != null && (s.IsPublic || s.IsFamily));
                if (!Show(p.Name, pub)) continue;
                Console.WriteLine("    PROP " + N(p.PropertyType) + " " + p.Name +
                                  " { " + (g != null ? Vis(g) + "get; " : "") + (s != null ? Vis(s) + "set; " : "") + " }" +
                                  (g != null && g.IsStatic ? "  [static]" : ""));
            }

            foreach (EventInfo ev in t.GetEvents(BF))
            {
                if (!Show(ev.Name, true)) continue;
                Console.WriteLine("    EVENT " + N(ev.EventHandlerType) + " " + ev.Name);
            }

            foreach (ConstructorInfo c in t.GetConstructors(BF).OrderBy(c => c.GetParameters().Length))
            {
                if (!Show(".ctor", c.IsPublic || c.IsFamily)) continue;
                Console.WriteLine("    CTOR " + Vis(c) + t.Name + "(" + Params(c.GetParameters()) + ")");
            }

            foreach (MethodInfo m in t.GetMethods(BF)
                        .Where(m => !m.IsSpecialName)
                        .OrderBy(m => m.Name, StringComparer.Ordinal)
                        .ThenBy(m => m.GetParameters().Length))
            {
                if (!Show(m.Name, m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly)) continue;
                bool isExt = m.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false);
                Console.WriteLine("    METHOD " + Vis(m) + (m.IsStatic ? "static " : "") +
                                  N(m.ReturnType) + " " + m.Name + "(" + Params(m.GetParameters(), isExt) + ")" +
                                  (isExt ? "   [EXTENSION]" : ""));
            }
        }
    }

    private static bool Show(string memberName, bool isPublic)
    {
        if (!isPublic && !includeNonPublic) return false;
        if (memberFilter == null) return true;
        return memberName.IndexOf(memberFilter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string Params(ParameterInfo[] ps)
    {
        return Params(ps, false);
    }

    private static string Params(ParameterInfo[] ps, bool isExtension)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < ps.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            ParameterInfo p = ps[i];
            if (isExtension && i == 0) sb.Append("this ");
            if (p.IsOut || (p.ParameterType.IsByRef && (p.Attributes & ParameterAttributes.Out) != 0)) sb.Append("out ");
            else if (p.ParameterType.IsByRef) sb.Append("ref[" + p.Attributes + "] ");
            if (p.IsDefined(typeof(ParamArrayAttribute), false)) sb.Append("params ");
            sb.Append(N(p.ParameterType)).Append(' ').Append(p.Name);
            if (p.HasDefaultValue)
            {
                object dv = null; try { dv = p.DefaultValue; } catch { }
                sb.Append(" = ").Append(dv == null ? "null" : dv.ToString());
            }
        }
        return sb.ToString();
    }

    private static string Vis(MethodBase m)
    {
        if (m.IsPublic) return "public ";
        if (m.IsFamily) return "protected ";
        if (m.IsFamilyOrAssembly) return "protected internal ";
        if (m.IsAssembly) return "internal ";
        if (m.IsFamilyAndAssembly) return "private protected ";
        return "private ";
    }

    private static string Vis(FieldInfo f)
    {
        if (f.IsPublic) return "public ";
        if (f.IsFamily) return "protected ";
        if (f.IsFamilyOrAssembly) return "protected internal ";
        if (f.IsAssembly) return "internal ";
        return "private ";
    }

    private static string KindOf(Type t)
    {
        if (t.IsEnum) return "enum";
        if (t.IsValueType) return "struct";
        if (t.IsInterface) return "interface";
        if (t.IsClass) return "class";
        return "?";
    }

    private static string N(Type t)
    {
        if (t == null) return "?";
        if (t.IsByRef) return N(t.GetElementType());
        if (t.IsArray) return N(t.GetElementType()) + "[]";
        if (t.IsGenericType)
        {
            string baseName = t.Name;
            int tick = baseName.IndexOf('`');
            if (tick > 0) baseName = baseName.Substring(0, tick);
            return baseName + "<" + string.Join(", ", t.GetGenericArguments().Select(N).ToArray()) + ">";
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
            case "System.Byte": return "byte";
            case "System.Char": return "char";
        }
        return t.FullName ?? t.Name;
    }
}
