using System.Globalization;

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

    // 播报库（缓存）还没接（施工图第 5 步）。界面只认 Usage 这一个只读入口，
    // 接上真实缓存时改这里就够了，UI 不用动。缓存是全存档共用的单一目录 Cache/Voice。
    public static class SpeechLibrary
    {
        public const string FolderName = "Cache/Voice";

        public static LibraryUsage Usage
        {
            get { return new LibraryUsage(0, 0L); }
        }
    }
}
