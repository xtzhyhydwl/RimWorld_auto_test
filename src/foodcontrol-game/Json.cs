using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FoodControl.Game
{
    /// <summary>
    /// **mod 里唯一的 JSON 原语出处**。
    ///
    /// ## 为什么单独提出来（DRY 审计的结论）
    ///
    /// 在此之前，这套原语（`Escape` / `Q` / `N` / `KV`）只作为 `Telemetry` 的
    /// **私有**方法存在，于是任何别的模块要写 JSON 就只有两条路：
    ///   ① 手拼字符串（本项目里出现过 4 套转义实现，**语义还各不相同**）
    ///   ② 把 `Telemetry` 的私有方法抄一份
    /// 两条都是错的。而本项目**已经**被这类重复咬过：`SimpleJson.Quote` 那次
    /// 就是为了避免"两份手写转义"才提出来共用的。
    ///
    /// → 现在它们是 `Json` 的公开原语；`Telemetry` 改为委托（行为**逐字不变**，
    ///   因为转义/数字格式这两件事一改就会让下游解析脚本炸）。
    ///
    /// ⚠ 与 Core 里的 `SimpleJson` 分工：
    ///   - `Core.SimpleJson`：**解析 + 转义**（离线可测，Core 不许引用游戏类型）
    ///   - `Game.Json`（本类）：**只有写出**（游戏层的遥测/意图流用）
    ///   两者都存在的理由是 Core 不能引用 Unity/游戏程序集，而游戏层不该为了写一行 JSON
    ///   把 Core 的解析器拖进来。**但转义规则必须一致**（都是 RFC 8259 的最小集）。
    /// </summary>
    public static class Json
    {
        /// <summary>字符串 → JSON 字面量（含引号）。null 变成 `""`（不是 `null`）</summary>
        public static string Q(string s)
        {
            if (s == null) return "\"\"";
            return "\"" + Escape(s) + "\"";
        }

        /// <summary>转义串内容（**不含**首尾引号）</summary>
        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
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
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 数字格式化。**必须用 InvariantCulture** —— 中文/欧洲区域设置会把小数点写成逗号，
        /// 那样 jsonl 就不是合法 JSON 了（下游解析脚本会炸）。
        /// NaN/Infinity → `null`（JSON 没有这两个字面量）。
        /// </summary>
        public static string N(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            double r = Math.Round(v, 4);
            if (r == 0) r = 0;   // 消掉 -0
            return r.ToString("0.####", CultureInfo.InvariantCulture);
        }

        public static string N(int v)
        {
            return v.ToString(CultureInfo.InvariantCulture);
        }

        public static string B(bool v)
        {
            return v ? "true" : "false";
        }

        /// <summary>往对象里写一个键值对（`rawValue` 必须是**已经序列化好**的 JSON 片段）</summary>
        public static void KV(StringBuilder sb, ref bool first, string key, string rawValue)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(Escape(key)).Append("\":").Append(rawValue);
        }

        /// <summary>简写：写一个字符串字段</summary>
        public static void KVs(StringBuilder sb, ref bool first, string key, string value)
        {
            KV(sb, ref first, key, Q(value));
        }

        /// <summary>简写：写一个整数字段</summary>
        public static void KVi(StringBuilder sb, ref bool first, string key, int value)
        {
            KV(sb, ref first, key, N(value));
        }

        public static string DoubleArray(double[] arr)
        {
            if (arr == null || arr.Length == 0) return "[]";
            var sb = new StringBuilder(arr.Length * 8);
            sb.Append('[');
            for (int i = 0; i < arr.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(N(arr[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>字符串数组</summary>
        public static string StringArray(IList<string> items)
        {
            if (items == null || items.Count == 0) return "[]";
            var sb = new StringBuilder(items.Count * 16);
            sb.Append('[');
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Q(items[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>用 `a | b` 拼两段非空文本（遥测/摘要里到处都是这个模式）</summary>
        public static string Join(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b != null ? b : "";
            if (string.IsNullOrEmpty(b)) return a;
            return a + " | " + b;
        }
    }
}
