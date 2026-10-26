// DSH_GameLink · 一条事件的字段拼装器。
//
// 只负责 `"key":value` 这些片段；ts / seq / kind 的前缀与写盘在 LiveChannel.Write 里（同一把锁里分配）。
// 用 string 手拼而不是序列化器：这里没有 Newtonsoft（不引额外 dll），
// 而这些字段一共就十来个，手拼最快也最少依赖。

using System.Globalization;
using System.Text;

namespace DSH_GameLink
{
    /// <summary>
    /// 一条事件的字段拼装器。**字段名不要用 `ts` / `seq` / `kind`** —— 这三个键由 LiveChannel.BuildLine 写，
    /// 重复会造出同名键（JSON.parse 取后一个，等于把前一个静默丢掉）。
    /// </summary>
    internal sealed class EventLine
    {
        private readonly string kind;
        private readonly StringBuilder fields = new StringBuilder(96);

        internal EventLine(string kind)
        {
            this.kind = kind;
        }

        internal EventLine Num(string key, int value)
        {
            fields.Append(",\"").Append(key).Append("\":")
                .Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        internal EventLine Text(string key, string value)
        {
            fields.Append(",\"").Append(key).Append("\":");
            AppendQuoted(fields, value);
            return this;
        }

        internal EventLine Flag(string key, bool value)
        {
            fields.Append(",\"").Append(key).Append("\":").Append(value ? "true" : "false");
            return this;
        }

        internal void Emit()
        {
            LiveChannel.Write(kind, fields);
        }

        private static void AppendQuoted(StringBuilder sb, string raw)
        {
            Json.Quote(sb, raw);
        }
    }
}
