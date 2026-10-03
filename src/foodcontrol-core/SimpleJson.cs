using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FoodControl.Core
{
    /// <summary>
    /// **极简 JSON 读写器**（Core 内部共用）。
    ///
    /// ## 为什么自己写而不用 Newtonsoft
    ///
    /// `FoodControl.Core` 有一条硬约束：**不许引用游戏程序集，也不许加第三方依赖**
    /// （`scripts/build-core.mjs` 会静态检查）。因为 Core 要能在 .NET Framework 上
    /// 离线跑测试 —— 那是整个项目可测试性的地基。
    /// 而 Newtonsoft 是随 RimBridgeServer 提供的，不能进 Core。
    ///
    /// ## 它覆盖什么
    ///
    /// 对象 / 数组 / 字符串（含转义与 `\uXXXX`）/ 数字 / 布尔 / null。
    /// 解析结果用 `Dictionary&lt;string,object&gt;`、`List&lt;object&gt;`、
    /// `string`、`double`、`bool`、`null` 表示。
    ///
    /// ⚠ 它**不是通用 JSON 库**：遇到不认识的输入会抛 `FormatException`。
    ///   这样做的理由是"我们只解析自己写出来的格式"，不需要通用性；
    ///   而少一个依赖就少一类环境问题。
    ///
    /// 从 `TemplateJson` 里提取出来共用（建筑账本 `BuildingRegistry` 也要用），
    /// 避免出现第二份手写解析器 —— 重复实现这类逻辑是本项目出 bug 的主要来源。
    /// </summary>
    public static class SimpleJson
    {
        /// <summary>解析一段 JSON，返回 Dictionary / List / string / double / bool / null</summary>
        public static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) throw new ArgumentException("json 为空");
            int pos = 0;
            object root = ParseValue(json, ref pos);
            SkipWs(json, ref pos);
            if (pos < json.Length) throw new FormatException("JSON 结尾有多余内容，位置 " + pos);
            return root;
        }

        /// <summary>
        /// 把字符串转成带引号的 JSON 字面量。
        /// 写出去的内容保证能被本类的 `Parse` 读回来（往返一致）。
        /// </summary>
        public static string Quote(string s)
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
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
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
        // 递归下降
        // ------------------------------------------------------------------

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
    }
}
