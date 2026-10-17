using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Verse;

namespace VoiceDeputy
{
    public readonly struct LibraryUsage
    {
        public readonly int Entries;
        public readonly long Bytes;

        public LibraryUsage(int entries, long bytes)
        {
            Entries = entries;
            Bytes = bytes;
        }

        public string Describe()
        {
            return Entries.ToString("N0", CultureInfo.InvariantCulture) + " 条 / " + DescribeBytes();
        }

        private string DescribeBytes()
        {
            if (Bytes < 1024L)
            {
                return Bytes.ToString(CultureInfo.InvariantCulture) + " B";
            }

            if (Bytes < 1024L * 1024L)
            {
                return (Bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            }

            return (Bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }
    }

    // 库键（01 §6.6 / 04 §2.1）。只由 SpeechLibrary.Key 构造：别的模块只交 (text, kind)。
    // Scope 与 Value 是计算属性，不落库、不进构造参数。
    public readonly struct LibraryKey
    {
        public readonly string VoiceFingerprint;
        public readonly string Text;
        public readonly SegmentKind SegmentKind;

        public LibraryKey(string voiceFingerprint, string text, SegmentKind segmentKind)
        {
            VoiceFingerprint = voiceFingerprint ?? string.Empty;
            Text = text ?? string.Empty;
            SegmentKind = segmentKind;
        }

        public string Scope
        {
            get { return SpeechLibrary.ScopeOf(SegmentKind); }
        }

        public string Value
        {
            get { return SpeechLibrary.ValueOf(this); }
        }
    }

    // 索引条目（01 §6.7）：只存事实，等级每轮由 LibraryJanitor 现算，不进条目。
    public sealed class LibraryEntry
    {
        public readonly string Value;
        public readonly string Text;
        public readonly SegmentKind SegmentKind;
        public readonly long Bytes;
        public int LastUsedTick;

        public LibraryEntry(string value, string text, SegmentKind segmentKind, long bytes, int lastUsedTick)
        {
            Value = value;
            Text = text;
            SegmentKind = segmentKind;
            Bytes = bytes;
            LastUsedTick = lastUsedTick;
        }

        public LibraryEntry Copy()
        {
            return new LibraryEntry(Value, Text, SegmentKind, Bytes, LastUsedTick);
        }
    }

    // 磁盘侧事实（01 §6.7）：只给 LibraryJanitor 对账用。
    public sealed class DiskFile
    {
        public readonly string Value;
        public readonly long Bytes;

        public DiskFile(string value, long bytes)
        {
            Value = value;
            Bytes = bytes;
        }
    }

    // 唯一的磁盘读写点（01 的 I2）：键构造、读、写、占用、快照、磁盘列表、索引存取。
    // 不判上限、不删条目（那是 LibraryJanitor）；只在主线程被访问。
    public static class SpeechLibrary
    {
        public const string FolderName = "Cache/Voice";
        public const string WavExtension = "wav";

        private const string IndexFileName = "index.json";
        private const string SaveScopePrefix = "save:";
        private const string GlobalScope = "global";

        private static Dictionary<string, LibraryEntry> index;
        private static bool indexDirty;
        private static string saveFingerprint = string.Empty;
        private static string voiceFingerprint;
        private static string voiceFingerprintInput;
        private static SHA1 hasher;

        public static string SaveFingerprint
        {
            get { return saveFingerprint; }
        }

        // 04 §2.1：sha1(BaseUrl + "|" + Model + "|" + Voice)，全 mod 唯一派生点。
        public static string VoiceFingerprint
        {
            get
            {
                VoiceDeputySettings settings = VoiceDeputyMod.Settings;
                string input = settings == null
                    ? string.Empty
                    : (settings.BaseUrl ?? string.Empty) + "|" + (settings.Model ?? string.Empty) + "|" + (settings.Voice ?? string.Empty);

                if (voiceFingerprint == null || voiceFingerprintInput != input)
                {
                    voiceFingerprintInput = input;
                    voiceFingerprint = Hash(input);
                }

                return voiceFingerprint;
            }
        }

        public static LibraryUsage Usage
        {
            get
            {
                Dictionary<string, LibraryEntry> entries = Index();
                long bytes = 0L;
                foreach (KeyValuePair<string, LibraryEntry> pair in entries)
                {
                    bytes += pair.Value.Bytes;
                }

                return new LibraryUsage(entries.Count, bytes);
            }
        }

        // 索引改过、还没落盘（04 §4.6）：Put 只标脏，什么时候落盘由 LibraryJanitor 决定
        // （唯一落盘入口 SaveIndex，节流在 LibraryJanitor.SaveIndexIfDue 里）。
        public static bool IndexDirty
        {
            get { return indexDirty; }
        }

        public static LibraryKey Key(string text, SegmentKind segmentKind)
        {
            return new LibraryKey(VoiceFingerprint, text, segmentKind);
        }

        // 04 §2.1：名字与整句随存档，其余全局。
        public static string ScopeOf(SegmentKind segmentKind)
        {
            return segmentKind == SegmentKind.PersonName || segmentKind == SegmentKind.Sentence
                ? SaveScopePrefix + saveFingerprint
                : GlobalScope;
        }

        public static string ValueOf(LibraryKey key)
        {
            return Hash(key.VoiceFingerprint + "|" + key.Scope + "|" + key.Text);
        }

        // 命中刷 LastUsedTick（04 §2.3）；文件没了当未命中，索引留给清理器对账。
        public static bool TryRead(LibraryKey key, out byte[] bytes)
        {
            bytes = null;

            LibraryEntry entry;
            if (!Index().TryGetValue(key.Value, out entry))
            {
                return false;
            }

            try
            {
                string path = FilePath(entry.Value);
                if (path == null || !File.Exists(path))
                {
                    return false;
                }

                bytes = File.ReadAllBytes(path);
            }
            catch
            {
                bytes = null;
                return false;
            }

            if (bytes == null || bytes.Length == 0)
            {
                bytes = null;
                return false;
            }

            entry.LastUsedTick = GenTicks.TicksGame;
            return true;
        }

        // 不刷 LastUsedTick：预热查库不能给 L3 续命（04 §2.3）。
        public static bool Contains(LibraryKey key)
        {
            return Index().ContainsKey(key.Value);
        }

        // 写盘成功才入索引（04 §3.2）；返回写完后的占用（调用方据此决定是否 CheckLimit）。
        public static LibraryUsage Put(LibraryKey key, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return Usage;
            }

            string path = FilePath(key.Value);
            if (path == null)
            {
                return Usage;
            }

            try
            {
                string directory = Path.GetDirectoryName(path);
                if (directory != null && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllBytes(path, bytes);
            }
            catch
            {
                // 写盘失败 ⇒ 不入索引，调用方按 "Contains 仍为假" 判该片段失败。
                return Usage;
            }

            Index()[key.Value] = new LibraryEntry(key.Value, key.Text, key.SegmentKind, bytes.Length, GenTicks.TicksGame);
            indexDirty = true;
            return Usage;
        }

        // 给 LibraryJanitor 的只读副本（04 §2.7）。
        public static LibraryEntry[] Snapshot()
        {
            Dictionary<string, LibraryEntry> entries = Index();
            LibraryEntry[] snapshot = new LibraryEntry[entries.Count];
            int i = 0;
            foreach (KeyValuePair<string, LibraryEntry> pair in entries)
            {
                snapshot[i++] = pair.Value.Copy();
            }

            return snapshot;
        }

        // 目录不存在 ⇒ 空数组（真的没有文件）；读目录失败 ⇒ null（对账那一步本轮跳过）。
        // 04 §2.4 写的是"失败 ⇒ 空数组"，但 §4.1 ① 要求"失败 ⇒ 本轮跳过对账"；
        // 空数组表达不了这两者的区别，会变成"读目录失败就清空全部索引"，所以失败回 null。
        public static DiskFile[] ListFiles()
        {
            string directory = CacheDirectory();
            if (directory == null || !Directory.Exists(directory))
            {
                return EmptyFiles;
            }

            string[] paths;
            try
            {
                paths = Directory.GetFiles(directory, "*." + WavExtension);
            }
            catch
            {
                return null;
            }

            List<DiskFile> files = new List<DiskFile>(paths.Length);
            foreach (string path in paths)
            {
                try
                {
                    FileInfo info = new FileInfo(path);
                    files.Add(new DiskFile(Path.GetFileNameWithoutExtension(path), info.Length));
                }
                catch
                {
                    // 单个文件读不到就跳过，不牵连整轮对账。
                }
            }

            return files.ToArray();
        }

        // 只允许 LibraryJanitor 调（04 §4.6）：删索引条目，不动文件。
        public static void RemoveEntry(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            if (Index().Remove(value))
            {
                indexDirty = true;
            }
        }

        // 只允许 LibraryJanitor 调（04 §4.6）：删磁盘文件，不动索引。
        // 返回 false = 删不掉（被占用 / 权限），调用方按"留待下轮"处理（04 §4.4）。
        public static bool RemoveFile(string value)
        {
            string path = FilePath(value);
            if (path == null)
            {
                return true;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        // 只允许 LibraryJanitor 调（04 §4.6）。返回 false = 落盘失败，由调用方记日志。
        public static bool SaveIndex()
        {
            string path = IndexPath();
            if (path == null)
            {
                return false;
            }

            try
            {
                string directory = Path.GetDirectoryName(path);
                if (directory != null && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(path, SerializeIndex(Index()), new UTF8Encoding(false));
                indexDirty = false;
                return true;
            }
            catch
            {
                // 落盘失败 ⇒ 记日志的事归调用方，这里只保证不抛（04 §4.1 ④）。
                return false;
            }
        }

        // 只允许 DeputyGameComponent 调（01 §7.6）。
        public static void SetSaveFingerprint(string id)
        {
            if (!string.IsNullOrEmpty(id))
            {
                saveFingerprint = id;
            }
        }

        private static readonly DiskFile[] EmptyFiles = new DiskFile[0];

        private static Dictionary<string, LibraryEntry> Index()
        {
            if (index == null)
            {
                index = LoadIndex();
            }

            return index;
        }

        private static Dictionary<string, LibraryEntry> LoadIndex()
        {
            Dictionary<string, LibraryEntry> parsed = null;
            string path = IndexPath();
            if (path != null)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        parsed = ParseIndex(File.ReadAllText(path, Encoding.UTF8));
                    }
                }
                catch
                {
                    parsed = null;
                }
            }

            // 索引缺失/损坏 ⇒ 从空索引开始（04 §4.5）。
            return parsed ?? new Dictionary<string, LibraryEntry>(StringComparer.Ordinal);
        }

        private static string FilePath(string value)
        {
            string directory = CacheDirectory();
            return directory == null || string.IsNullOrEmpty(value)
                ? null
                : Path.Combine(directory, value + "." + WavExtension);
        }

        private static string IndexPath()
        {
            string directory = CacheDirectory();
            return directory == null ? null : Path.Combine(directory, IndexFileName);
        }

        private static string CacheDirectory()
        {
            string root = ModRootDirectory();
            return root == null ? null : Path.Combine(root, FolderName);
        }

        // 缓存目录挂在 mod 根下（04 §1），mod 根只从 Mod 实例取一次。
        private static string ModRootDirectory()
        {
            try
            {
                Mod mod = LoadedModManager.GetMod<VoiceDeputyMod>();
                ModContentPack content = mod == null ? null : mod.Content;
                return content == null ? null : content.RootDir;
            }
            catch
            {
                return null;
            }
        }

        private static string Hash(string input)
        {
            if (hasher == null)
            {
                hasher = SHA1.Create();
            }

            byte[] digest = hasher.ComputeHash(Encoding.UTF8.GetBytes(input ?? string.Empty));
            StringBuilder builder = new StringBuilder(digest.Length * 2);
            foreach (byte b in digest)
            {
                builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private static string SerializeIndex(Dictionary<string, LibraryEntry> entries)
        {
            StringBuilder builder = new StringBuilder(entries.Count * 96 + 2);
            builder.Append('{');
            bool first = true;
            foreach (KeyValuePair<string, LibraryEntry> pair in entries)
            {
                LibraryEntry entry = pair.Value;
                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                builder.Append('"');
                AppendEscaped(builder, entry.Value);
                builder.Append("\":{");
                builder.Append("\"text\":\"");
                AppendEscaped(builder, entry.Text);
                builder.Append("\",\"segmentKind\":\"");
                AppendEscaped(builder, entry.SegmentKind.ToString());
                builder.Append("\",\"lastUsedTick\":");
                builder.Append(entry.LastUsedTick.ToString(CultureInfo.InvariantCulture));
                builder.Append(",\"bytes\":");
                builder.Append(entry.Bytes.ToString(CultureInfo.InvariantCulture));
                builder.Append('}');
            }

            builder.Append('}');
            return builder.ToString();
        }

        private static void AppendEscaped(StringBuilder builder, string text)
        {
            if (text == null)
            {
                return;
            }

            foreach (char c in text)
            {
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }
        }

        // 只为 index.json 写的最小读取器：对象套对象，值是字符串或整数。
        // 任何不认识的字段跳过；整体坏掉就返回 null（调用方从空索引起步）。
        private static Dictionary<string, LibraryEntry> ParseIndex(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            int position = 0;
            Dictionary<string, LibraryEntry> entries = new Dictionary<string, LibraryEntry>(StringComparer.Ordinal);
            SkipWhitespace(json, ref position);
            if (!Consume(json, ref position, '{'))
            {
                return null;
            }

            while (true)
            {
                SkipWhitespace(json, ref position);
                if (Consume(json, ref position, '}'))
                {
                    break;
                }

                string value = ReadString(json, ref position);
                if (value == null)
                {
                    return entries;
                }

                SkipWhitespace(json, ref position);
                if (!Consume(json, ref position, ':'))
                {
                    return entries;
                }

                LibraryEntry entry = ReadEntry(value, json, ref position);
                if (entry == null)
                {
                    return entries;
                }

                entries[entry.Value] = entry;

                SkipWhitespace(json, ref position);
                if (!Consume(json, ref position, ','))
                {
                    break;
                }
            }

            return entries;
        }

        private static LibraryEntry ReadEntry(string value, string json, ref int position)
        {
            SkipWhitespace(json, ref position);
            if (!Consume(json, ref position, '{'))
            {
                return null;
            }

            string text = string.Empty;
            SegmentKind segmentKind = SegmentKind.Const;
            int lastUsedTick = 0;
            long bytes = 0L;

            while (true)
            {
                SkipWhitespace(json, ref position);
                if (Consume(json, ref position, '}'))
                {
                    break;
                }

                string field = ReadString(json, ref position);
                if (field == null)
                {
                    return null;
                }

                SkipWhitespace(json, ref position);
                if (!Consume(json, ref position, ':'))
                {
                    return null;
                }

                SkipWhitespace(json, ref position);
                if (field == "text")
                {
                    text = ReadString(json, ref position) ?? string.Empty;
                }
                else if (field == "segmentKind")
                {
                    string kindName = ReadString(json, ref position);
                    if (!TryParseSegmentKind(kindName, out segmentKind))
                    {
                        return null;
                    }
                }
                else if (field == "lastUsedTick")
                {
                    lastUsedTick = (int)ReadNumber(json, ref position);
                }
                else if (field == "bytes")
                {
                    bytes = ReadNumber(json, ref position);
                }
                else
                {
                    SkipValue(json, ref position);
                }

                SkipWhitespace(json, ref position);
                if (!Consume(json, ref position, ','))
                {
                    SkipWhitespace(json, ref position);
                    if (!Consume(json, ref position, '}'))
                    {
                        return null;
                    }

                    break;
                }
            }

            return new LibraryEntry(value, text, segmentKind, bytes, lastUsedTick);
        }

        private static bool TryParseSegmentKind(string name, out SegmentKind segmentKind)
        {
            segmentKind = SegmentKind.Const;
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            try
            {
                segmentKind = (SegmentKind)Enum.Parse(typeof(SegmentKind), name);
                return Enum.IsDefined(typeof(SegmentKind), segmentKind);
            }
            catch
            {
                return false;
            }
        }

        private static void SkipWhitespace(string json, ref int position)
        {
            while (position < json.Length && char.IsWhiteSpace(json[position]))
            {
                position++;
            }
        }

        private static bool Consume(string json, ref int position, char expected)
        {
            SkipWhitespace(json, ref position);
            if (position >= json.Length || json[position] != expected)
            {
                return false;
            }

            position++;
            return true;
        }

        private static string ReadString(string json, ref int position)
        {
            SkipWhitespace(json, ref position);
            if (position >= json.Length || json[position] != '"')
            {
                return null;
            }

            position++;
            StringBuilder builder = new StringBuilder();
            while (position < json.Length)
            {
                char c = json[position++];
                if (c == '"')
                {
                    return builder.ToString();
                }

                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                if (position >= json.Length)
                {
                    return null;
                }

                char escape = json[position++];
                switch (escape)
                {
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'b':
                        builder.Append('\b');
                        break;
                    case 'f':
                        builder.Append('\f');
                        break;
                    case 'u':
                        if (position + 4 > json.Length)
                        {
                            return null;
                        }

                        int code;
                        if (!int.TryParse(json.Substring(position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                        {
                            return null;
                        }

                        builder.Append((char)code);
                        position += 4;
                        break;
                    default:
                        builder.Append(escape);
                        break;
                }
            }

            return null;
        }

        private static long ReadNumber(string json, ref int position)
        {
            SkipWhitespace(json, ref position);
            bool negative = position < json.Length && json[position] == '-';
            if (negative)
            {
                position++;
            }

            long value = 0L;
            while (position < json.Length && json[position] >= '0' && json[position] <= '9')
            {
                value = value * 10L + (json[position] - '0');
                position++;
            }

            // 小数/指数（本索引不写，但别人改过索引时不至于把整份索引读废）。
            while (position < json.Length && (json[position] == '.' || json[position] == 'e' || json[position] == 'E' || json[position] == '+' || json[position] == '-' || (json[position] >= '0' && json[position] <= '9')))
            {
                position++;
            }

            return negative ? -value : value;
        }

        private static void SkipValue(string json, ref int position)
        {
            SkipWhitespace(json, ref position);
            if (position >= json.Length)
            {
                return;
            }

            if (json[position] == '"')
            {
                ReadString(json, ref position);
                return;
            }

            int depth = 0;
            while (position < json.Length)
            {
                char c = json[position];
                if (c == '{' || c == '[')
                {
                    depth++;
                }
                else if (c == '}' || c == ']')
                {
                    if (depth == 0)
                    {
                        return;
                    }

                    depth--;
                }
                else if (c == ',' && depth == 0)
                {
                    return;
                }

                position++;
            }
        }
    }
}
