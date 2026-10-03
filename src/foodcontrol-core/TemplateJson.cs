using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FoodControl.Core
{
    /// <summary>
    /// **样板 ↔ JSON 的读写**。
    ///
    /// ## 为什么手写 JSON 而不是用 Newtonsoft
    ///
    /// `FoodControl.Core` 有一条硬约束：**不许引用游戏程序集，也不许加第三方依赖**
    /// （`scripts/build-core.mjs` 会静态检查）。因为 Core 要能在 .NET Framework 上
    /// 离线跑测试 —— 那是整个项目可测试性的地基。
    /// 而 Newtonsoft 是随 RimBridgeServer 提供的，不能进 Core。
    ///
    /// 所以这里写一个**极简 JSON 读写器**，只覆盖样板需要的形状
    /// （对象 / 数组 / 字符串 / 数字 / 布尔）。它是 Core 里唯一允许的 JSON 处理。
    ///
    /// ⚠ 它**不是通用 JSON 库**：不处理转义之外的复杂情形，遇到不认识的输入会抛异常。
    ///   这样做的理由是"样板格式是我们自己定的"，不需要通用性；
    ///   而少一个依赖就少一类环境问题。
    /// </summary>
    public static class TemplateJson
    {
        // ------------------------------------------------------------------
        // 写
        // ------------------------------------------------------------------

        public static string Write(BuildTemplate t)
        {
            if (t == null) throw new ArgumentNullException("t");
            t.NormalizeSize();

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"id\": ").Append(Quote(t.Id)).Append(",\n");
            sb.Append("  \"name\": ").Append(Quote(t.Name)).Append(",\n");
            sb.Append("  \"origin\": ").Append(Quote(t.Origin)).Append(",\n");
            sb.Append("  \"size\": { \"w\": ").Append(t.Width).Append(", \"h\": ").Append(t.Height).Append(" },\n");

            sb.Append("  \"tags\": [");
            for (int i = 0; i < t.Tags.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Quote(t.Tags[i]));
            }
            sb.Append("],\n");

            sb.Append("  \"rotatable\": ").Append(t.Rotatable ? "true" : "false").Append(",\n");
            sb.Append("  \"mirrorable\": ").Append(t.Mirrorable ? "true" : "false").Append(",\n");

            sb.Append("  \"rows\": [\n");
            for (int i = 0; i < t.Rows.Count; i++)
            {
                sb.Append("    ").Append(Quote(t.Rows[i]));
                if (i < t.Rows.Count - 1) sb.Append(",");
                sb.Append("\n");
            }
            sb.Append("  ],\n");

            sb.Append("  \"roles\": {\n");
            var roleKeys = new List<char>(t.Roles.Keys);
            roleKeys.Sort();
            for (int i = 0; i < roleKeys.Count; i++)
            {
                char key = roleKeys[i];
                TemplateRole r = t.Roles[key];
                sb.Append("    ").Append(Quote(key.ToString())).Append(": { ");
                sb.Append("\"name\": ").Append(Quote(r.Name)).Append(", ");
                sb.Append("\"accepts\": [");
                for (int k = 0; k < r.Accepts.Count; k++)
                {
                    if (k > 0) sb.Append(", ");
                    sb.Append(Quote(r.Accepts[k]));
                }
                sb.Append("]");
                if (r.IsWallLike) sb.Append(", \"isWallLike\": true");
                if (r.IsDoorLike) sb.Append(", \"isDoorLike\": true");
                if (r.IsFurniture) sb.Append(", \"isFurniture\": true");
                if (r.NeedsStuff) sb.Append(", \"needsStuff\": true");
                sb.Append(" }");
                if (i < roleKeys.Count - 1) sb.Append(",");
                sb.Append("\n");
            }
            sb.Append("  }\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        private static string Quote(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // 读（极简递归下降）
        // ------------------------------------------------------------------

        public static BuildTemplate Read(string json)
        {
            if (string.IsNullOrEmpty(json)) throw new ArgumentException("json 为空");
            int pos = 0;
            object root = ParseValue(json, ref pos);
            SkipWs(json, ref pos);
            if (pos < json.Length) throw new FormatException("JSON 结尾有多余内容，位置 " + pos);

            var map = root as Dictionary<string, object>;
            if (map == null) throw new FormatException("顶层不是对象");

            var t = new BuildTemplate();
            t.Id = GetString(map, "id", "");
            t.Name = GetString(map, "name", t.Id);
            t.Origin = GetString(map, "origin", "user");
            t.Rotatable = GetBool(map, "rotatable", true);
            t.Mirrorable = GetBool(map, "mirrorable", true);

            // tags
            object tagsObj;
            if (map.TryGetValue("tags", out tagsObj))
            {
                var arr = tagsObj as List<object>;
                if (arr != null)
                {
                    for (int i = 0; i < arr.Count; i++)
                    {
                        if (arr[i] is string) t.Tags.Add((string)arr[i]);
                    }
                }
            }

            // rows
            object rowsObj;
            if (map.TryGetValue("rows", out rowsObj))
            {
                var arr = rowsObj as List<object>;
                if (arr != null)
                {
                    for (int i = 0; i < arr.Count; i++)
                    {
                        if (arr[i] is string) t.Rows.Add((string)arr[i]);
                    }
                }
            }

            // roles
            object rolesObj;
            if (map.TryGetValue("roles", out rolesObj))
            {
                var rolesMap = rolesObj as Dictionary<string, object>;
                if (rolesMap != null)
                {
                    foreach (KeyValuePair<string, object> kv in rolesMap)
                    {
                        if (string.IsNullOrEmpty(kv.Key)) continue;
                        var rm = kv.Value as Dictionary<string, object>;
                        if (rm == null) continue;

                        var role = new TemplateRole();
                        role.Symbol = kv.Key[0];
                        role.Name = GetString(rm, "name", "");
                        role.IsWallLike = GetBool(rm, "isWallLike", false);
                        role.IsDoorLike = GetBool(rm, "isDoorLike", false);
                        role.IsFurniture = GetBool(rm, "isFurniture", false);
                        role.NeedsStuff = GetBool(rm, "needsStuff", false);

                        object acceptsObj;
                        if (rm.TryGetValue("accepts", out acceptsObj))
                        {
                            var arr = acceptsObj as List<object>;
                            if (arr != null)
                            {
                                for (int i = 0; i < arr.Count; i++)
                                {
                                    if (arr[i] is string) role.Accepts.Add((string)arr[i]);
                                }
                            }
                        }
                        t.Roles[role.Symbol] = role;
                    }
                }
            }

            t.NormalizeSize();
            return t;
        }

        // --- 极简递归下降解析 ---

        private static void SkipWs(string s, ref int p)
        {
            while (p < s.Length && (s[p] == ' ' || s[p] == '\t' || s[p] == '\n' || s[p] == '\r')) p++;
        }

        private static object ParseValue(string s, ref int p)
        {
            SkipWs(s, ref p);
            if (p >= s.Length) throw new FormatException("意外的结尾");
            char c = s[p];
            if (c == '{') return ParseObject(s, ref p);
            if (c == '[') return ParseArray(s, ref p);
            if (c == '"') return ParseString(s, ref p);
            if (c == 't' || c == 'f') return ParseBool(s, ref p);
            if (c == 'n') { p += 4; return null; }
            return ParseNumber(s, ref p);
        }

        private static Dictionary<string, object> ParseObject(string s, ref int p)
        {
            var map = new Dictionary<string, object>();
            p++;   // {
            SkipWs(s, ref p);
            if (p < s.Length && s[p] == '}') { p++; return map; }

            while (true)
            {
                SkipWs(s, ref p);
                if (p >= s.Length || s[p] != '"') throw new FormatException("对象键必须是字符串，位置 " + p);
                string key = ParseString(s, ref p);
                SkipWs(s, ref p);
                if (p >= s.Length || s[p] != ':') throw new FormatException("缺少 ':'，位置 " + p);
                p++;
                object val = ParseValue(s, ref p);
                map[key] = val;
                SkipWs(s, ref p);
                if (p < s.Length && s[p] == ',') { p++; continue; }
                if (p < s.Length && s[p] == '}') { p++; break; }
                throw new FormatException("对象里期待 ',' 或 '}'，位置 " + p);
            }
            return map;
        }

        private static List<object> ParseArray(string s, ref int p)
        {
            var list = new List<object>();
            p++;   // [
            SkipWs(s, ref p);
            if (p < s.Length && s[p] == ']') { p++; return list; }

            while (true)
            {
                object val = ParseValue(s, ref p);
                list.Add(val);
                SkipWs(s, ref p);
                if (p < s.Length && s[p] == ',') { p++; continue; }
                if (p < s.Length && s[p] == ']') { p++; break; }
                throw new FormatException("数组里期待 ',' 或 ']'，位置 " + p);
            }
            return list;
        }

        private static string ParseString(string s, ref int p)
        {
            p++;   // 开引号
            var sb = new StringBuilder();
            while (p < s.Length)
            {
                char c = s[p++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (p >= s.Length) break;
                    char e = s[p++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (p + 4 <= s.Length)
                            {
                                int code = int.Parse(s.Substring(p, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                                sb.Append((char)code);
                                p += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            throw new FormatException("字符串没有闭合");
        }

        private static bool ParseBool(string s, ref int p)
        {
            if (s.Length >= p + 4 && s.Substring(p, 4) == "true") { p += 4; return true; }
            if (s.Length >= p + 5 && s.Substring(p, 5) == "false") { p += 5; return false; }
            throw new FormatException("不是合法的布尔值，位置 " + p);
        }

        private static object ParseNumber(string s, ref int p)
        {
            int start = p;
            if (p < s.Length && (s[p] == '-' || s[p] == '+')) p++;
            while (p < s.Length && (char.IsDigit(s[p]) || s[p] == '.' || s[p] == 'e' || s[p] == 'E' || s[p] == '-' || s[p] == '+')) p++;
            string num = s.Substring(start, p - start);
            double d;
            if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            throw new FormatException("不是合法的数字：" + num);
        }

        private static string GetString(Dictionary<string, object> map, string key, string dflt)
        {
            object v;
            if (map.TryGetValue(key, out v) && v is string) return (string)v;
            return dflt;
        }

        private static bool GetBool(Dictionary<string, object> map, string key, bool dflt)
        {
            object v;
            if (map.TryGetValue(key, out v) && v is bool) return (bool)v;
            return dflt;
        }
    }
}
