// DSH_GameLink · 手写 JSON（写侧）。
//
// 为什么不用 Newtonsoft：这个 mod 刻意零第三方 dll（见 EventLine 头注释）。
// 为什么读侧不用 JSON：命令是**上位机写、游戏读**，游戏侧不该带 JSON 解析器；
//   所以入向是 `key=value` 行（见 CommandChannel），出向才用 JSON（本文件）。
//
// 一个概念一个名字：这里也是 EventLine 的字符串转义实现（EventLine 调 Json.Quote），
// 免得两处各自写一份 JSON 转义。

using System;
using System.Globalization;
using System.Text;

namespace DSH_GameLink
{
    internal static class Json
    {
        /// <summary>NDJSON / JSON 的字符串转义：只处理 JSON 必须处理的字符。</summary>
        internal static void Quote(StringBuilder sb, string raw)
        {
            if (raw == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append('"');
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < 32)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            sb.Append('"');
        }

        internal static string Quote(string raw)
        {
            StringBuilder sb = new StringBuilder(raw == null ? 4 : raw.Length + 2);
            Quote(sb, raw);
            return sb.ToString();
        }
    }

    /// <summary>一次性的 JSON 对象拼装器：`new JsonBuf().Str("a","b").Num("n",1).ToString()`。</summary>
    internal sealed class JsonBuf
    {
        private readonly StringBuilder sb = new StringBuilder(256);
        private bool first = true;

        internal JsonBuf()
        {
            sb.Append('{');
        }

        internal JsonBuf Str(string key, string value)
        {
            Sep();
            Json.Quote(sb, key);
            sb.Append(':');
            Json.Quote(sb, value);
            return this;
        }

        internal JsonBuf Num(string key, long value)
        {
            Sep();
            Json.Quote(sb, key);
            sb.Append(':').Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        internal JsonBuf Num(string key, double value, int decimals)
        {
            Sep();
            Json.Quote(sb, key);
            sb.Append(':').Append(Math.Round(value, decimals).ToString(CultureInfo.InvariantCulture));
            return this;
        }

        internal JsonBuf Bool(string key, bool value)
        {
            Sep();
            Json.Quote(sb, key);
            sb.Append(':').Append(value ? "true" : "false");
            return this;
        }

        /// <summary>已经是合法 JSON 的片段（嵌套对象/数组/字符串字面量）。</summary>
        internal JsonBuf Raw(string key, string rawJson)
        {
            Sep();
            Json.Quote(sb, key);
            sb.Append(':').Append(rawJson ?? "null");
            return this;
        }

        /// <summary>字符串数组。</summary>
        internal JsonBuf StrArray(string key, System.Collections.Generic.IEnumerable<string> values)
        {
            Sep();
            Json.Quote(sb, key);
            sb.Append(":[");
            bool f = true;
            if (values != null)
            {
                foreach (string v in values)
                {
                    if (!f) sb.Append(',');
                    f = false;
                    Json.Quote(sb, v);
                }
            }

            sb.Append(']');
            return this;
        }

        private void Sep()
        {
            if (!first) sb.Append(',');
            first = false;
        }

        public override string ToString()
        {
            return sb.ToString() + "}";
        }
    }
}
