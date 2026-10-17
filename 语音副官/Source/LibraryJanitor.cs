using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace VoiceDeputy
{
    // 名单变化（01 §6.5）：本轮"新名字"交给 WarmupScheduler 预热；消失名单只在清理器内部用，不外传。
    public sealed class RosterDelta
    {
        public static readonly RosterDelta Empty = new RosterDelta(null);

        public readonly string[] NewNames;

        public RosterDelta(string[] newNames)
        {
            NewNames = newNames == null || newNames.Length == 0 ? EmptyNames : (string[])newNames.Clone();
        }

        private static readonly string[] EmptyNames = new string[0];
    }

    // 全 mod 唯一的等级判定处与删除者（04 §4）：四步 Tick + 两阶段名单 + 超限淘汰 + 孤儿对账 + 索引落盘。
    // 等级不进条目（每轮现算），别的模块只读写不删。
    public static class LibraryJanitor
    {
        public const int L1SoftLimit = 500;

        private const int LevelL1 = 1;
        private const int LevelL2 = 2;
        private const int LevelL3 = 3;

        // 坏文件待删名单（04 §4.1 ①）：ClipAssembler 只登记，删除在下一轮 Tick 里做。
        private static readonly List<LibraryKey> brokenKeys = new List<LibraryKey>();

        // 上一轮名单（"存活"口径只有本类这一处）；消失名字隔一轮才删（04 §4.3）。
        private static HashSet<string> roster = new HashSet<string>(StringComparer.Ordinal);
        private static string[] vanished = new string[0];
        private static bool l1Warned;

        // UI「清缓存」（04 §4.6）：删目录内全部 .wav + 清内存索引 + 落索引；不动 Settings 与 VoiceFingerprint。
        public static void ClearAll()
        {
            try
            {
                LibraryEntry[] snapshot = SpeechLibrary.Snapshot();
                foreach (LibraryEntry entry in snapshot)
                {
                    SpeechLibrary.RemoveEntry(entry.Value);
                }

                int files = 0;
                DiskFile[] diskFiles = SpeechLibrary.ListFiles();
                if (diskFiles != null)
                {
                    foreach (DiskFile file in diskFiles)
                    {
                        SpeechLibrary.RemoveFile(file.Value);
                        files++;
                    }
                }

                // 待删名单指向的都是刚清掉的文件；留着会在重新合成后误删新文件。
                brokenKeys.Clear();
                SaveIndexOrTrace();
                Trace.Step("janitor", "clear entries=" + snapshot.Length + " files=" + files);
            }
            catch (Exception exception)
            {
                NoteError("clear", exception);
            }
        }

        // 04 §4.4：判据只有一条 Usage.Entries > Settings.LibraryLimit；每次 Put 之后与每轮 Tick 第 ③ 步都跑它。
        public static void CheckLimit()
        {
            try
            {
                LibraryUsage usage = SpeechLibrary.Usage;
                int limit = Limit();
                if (usage.Entries > limit)
                {
                    Evict(usage.Entries - limit);
                }

                WarnL1IfNeeded();
            }
            catch (Exception exception)
            {
                NoteError("limit", exception);
            }
        }

        // 04 §4.1：①②③④ 固定顺序；任一步抛异常只跳过该步。
        public static void Tick()
        {
            try
            {
                Reconcile();
            }
            catch (Exception exception)
            {
                NoteError("reconcile", exception);
            }

            RosterDelta delta = null;
            try
            {
                delta = UpdateRoster();
            }
            catch (Exception exception)
            {
                NoteError("roster", exception);
            }

            CheckLimit();

            SaveIndexOrTrace();

            if (delta != null && delta.NewNames.Length > 0)
            {
                WarmupScheduler.EnqueueRoster(delta);
            }
        }

        // 04 §4.6：ClipAssembler 唯一的登记入口（不删，进待删名单，下一轮 ① 里删）。
        public static void ReportBroken(LibraryKey key)
        {
            brokenKeys.Add(key);
            Trace.Step("janitor", "broken-register " + key.Text);
        }

        // ① 对账 + 坏文件（04 §4.1 / §4.5）：磁盘有索引无 ⇒ 删文件；索引有磁盘无 ⇒ 删条目。
        private static void Reconcile()
        {
            LibraryEntry[] snapshot = SpeechLibrary.Snapshot();
            DiskFile[] files = SpeechLibrary.ListFiles();

            if (files == null)
            {
                Trace.Step("janitor", "reconcile-skip");
            }
            else
            {
                HashSet<string> indexed = new HashSet<string>(StringComparer.Ordinal);
                foreach (LibraryEntry entry in snapshot)
                {
                    indexed.Add(entry.Value);
                }

                HashSet<string> onDisk = new HashSet<string>(StringComparer.Ordinal);
                foreach (DiskFile file in files)
                {
                    onDisk.Add(file.Value);
                }

                int orphans = 0;
                foreach (DiskFile file in files)
                {
                    if (indexed.Contains(file.Value))
                    {
                        continue;
                    }

                    SpeechLibrary.RemoveFile(file.Value);
                    orphans++;
                }

                int stale = 0;
                foreach (LibraryEntry entry in snapshot)
                {
                    if (onDisk.Contains(entry.Value))
                    {
                        continue;
                    }

                    SpeechLibrary.RemoveEntry(entry.Value);
                    stale++;
                }

                if (orphans > 0 || stale > 0)
                {
                    Trace.Step("janitor", "reconcile orphan=" + orphans + " stale=" + stale);
                }
            }

            RemoveBroken();
        }

        // 坏文件删不掉 ⇒ 留在名单里等下一轮（04 §4.1 ①）。
        private static void RemoveBroken()
        {
            for (int i = brokenKeys.Count - 1; i >= 0; i--)
            {
                LibraryKey key = brokenKeys[i];
                bool removed = SpeechLibrary.RemoveFile(key.Value);
                SpeechLibrary.RemoveEntry(key.Value);
                Trace.Step("janitor", "broken " + key.Text);
                if (removed)
                {
                    brokenKeys.RemoveAt(i);
                }
            }
        }

        // ② 两阶段名单（04 §4.3）：先删上一轮消失的名字，再扫本轮名单，把本轮消失的记进名单。
        // 扫名单失败 ⇒ 本轮整体跳过、保留上轮名单（04 §4.1 ②）。
        private static RosterDelta UpdateRoster()
        {
            HashSet<string> current;
            if (!TryScanRoster(out current))
            {
                Trace.Step("janitor", "roster-skip");
                return null;
            }

            RemoveVanished();

            string[] newNames = Difference(current, roster);
            vanished = Difference(roster, current);
            roster = current;

            if (newNames.Length > 0 || vanished.Length > 0)
            {
                Trace.Step("janitor", "roster new=" + newNames.Length + " gone=" + vanished.Length);
            }

            return new RosterDelta(newNames);
        }

        private static bool TryScanRoster(out HashSet<string> names)
        {
            names = null;

            try
            {
                HashSet<string> scanned = new HashSet<string>(StringComparer.Ordinal);
                Faction player = Faction.OfPlayer;

                List<Map> maps = Find.Maps;
                for (int i = 0; i < maps.Count; i++)
                {
                    Map map = maps[i];
                    if (map == null || map.mapPawns == null)
                    {
                        continue;
                    }

                    foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
                    {
                        if (pawn == null || player == null || pawn.Faction != player)
                        {
                            continue;
                        }

                        AddRosterName(scanned, pawn.LabelShort);
                    }
                }

                if (Find.FactionManager != null)
                {
                    foreach (Faction faction in Find.FactionManager.AllFactions)
                    {
                        if (faction != null)
                        {
                            AddRosterName(scanned, faction.Name);
                        }
                    }
                }

                names = scanned;
                return true;
            }
            catch
            {
                return false;
            }
        }

        // 条目里存的是清洗后的 PersonName 文本，所以名单也存清洗后的形态；清洗只有 SegmentPlanner 一处。
        private static void AddRosterName(HashSet<string> names, string raw)
        {
            string cleaned = SegmentPlanner.PersonNameText(raw);
            if (cleaned.Length > 0)
            {
                names.Add(cleaned);
            }
        }

        // 上一轮消失的名字：PersonName 条目按名字删，Sentence 条目按子串删（中文误伤接受）。
        private static void RemoveVanished()
        {
            if (vanished.Length == 0)
            {
                return;
            }

            LibraryEntry[] snapshot = SpeechLibrary.Snapshot();
            foreach (string name in vanished)
            {
                foreach (LibraryEntry entry in snapshot)
                {
                    if (entry.SegmentKind == SegmentKind.PersonName && string.Equals(entry.Text, name, StringComparison.Ordinal))
                    {
                        Delete(entry.Value, "vanish " + name);
                        continue;
                    }

                    if (entry.SegmentKind == SegmentKind.Sentence && entry.Text != null && entry.Text.IndexOf(name, StringComparison.Ordinal) >= 0)
                    {
                        Delete(entry.Value, "vanish-in-sentence " + name);
                    }
                }
            }

            Trace.Step("janitor", "vanish " + vanished.Length);
            vanished = new string[0];
        }

        // ③ 超限淘汰（04 §4.4）：排序键 (等级降序, LastUsedTick 升序)，L1 永不删。
        private static void Evict(int over)
        {
            LibraryEntry[] snapshot = SpeechLibrary.Snapshot();
            List<Candidate> pool = new List<Candidate>(snapshot.Length);
            foreach (LibraryEntry entry in snapshot)
            {
                int level = LevelOf(entry);
                if (level == LevelL1)
                {
                    continue;
                }

                pool.Add(new Candidate(entry, level));
            }

            pool.Sort(CompareCandidates);

            int removed = 0;
            for (int i = 0; i < pool.Count && removed < over; i++)
            {
                Delete(pool[i].Entry.Value, "evict L" + pool[i].Level + " " + pool[i].Entry.Text);
                removed++;
            }

            if (removed < over)
            {
                // L3 + L2 清空后仍超限 ⇒ 停止淘汰（宁可超限，不删骨架）并告警一次。
                Trace.Step("janitor", "l1-protect over=" + (over - removed));
            }
        }

        private static int CompareCandidates(Candidate a, Candidate b)
        {
            if (a.Level != b.Level)
            {
                return b.Level - a.Level;
            }

            return a.Entry.LastUsedTick.CompareTo(b.Entry.LastUsedTick);
        }

        // 04 §4.2 的等级表：只由 SegmentKind 与"名字是否在名单里"推，每轮现算。
        private static int LevelOf(LibraryEntry entry)
        {
            switch (entry.SegmentKind)
            {
                case SegmentKind.Const:
                case SegmentKind.Enum:
                case SegmentKind.Number:
                    return LevelL1;
                case SegmentKind.PersonName:
                    return roster.Contains(entry.Text) ? LevelL2 : LevelL3;
                default:
                    return LevelL3;
            }
        }

        private static void WarnL1IfNeeded()
        {
            LibraryEntry[] snapshot = SpeechLibrary.Snapshot();
            int count = 0;
            foreach (LibraryEntry entry in snapshot)
            {
                if (LevelOf(entry) == LevelL1)
                {
                    count++;
                }
            }

            if (count <= L1SoftLimit)
            {
                l1Warned = false;
                return;
            }

            if (l1Warned)
            {
                return;
            }

            l1Warned = true;
            Trace.Step("janitor", "l1-soft-limit " + count);
        }

        private static void Delete(string value, string reason)
        {
            SpeechLibrary.RemoveFile(value);
            SpeechLibrary.RemoveEntry(value);
            Trace.Step("janitor", reason);
        }

        private static void SaveIndexOrTrace()
        {
            if (!SpeechLibrary.SaveIndex())
            {
                Trace.Step("janitor", "index-write-failed");
            }
        }

        private static string[] Difference(HashSet<string> left, HashSet<string> right)
        {
            List<string> only = new List<string>();
            foreach (string name in left)
            {
                if (!right.Contains(name))
                {
                    only.Add(name);
                }
            }

            only.Sort(StringComparer.Ordinal);
            return only.ToArray();
        }

        private static int Limit()
        {
            VoiceDeputySettings settings = VoiceDeputyMod.Settings;
            return settings == null ? 3000 : settings.LibraryLimit;
        }

        private static void NoteError(string stage, Exception exception)
        {
            Trace.Step("janitor", stage + "-err " + exception.GetType().Name);
        }

        private struct Candidate
        {
            public readonly LibraryEntry Entry;
            public readonly int Level;

            public Candidate(LibraryEntry entry, int level)
            {
                Entry = entry;
                Level = level;
            }
        }
    }
}
