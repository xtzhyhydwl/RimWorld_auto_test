using System;
using System.Collections.Generic;
using Verse;

namespace VoiceDeputy
{
    public static class SpeechCatalog
    {
        private const string ThreatGroup = "威胁";
        private const string PersonGroup = "人身";
        private const string HealthGroup = "健康";
        private const string GuestGroup = "访客";
        private const string EnvironmentGroup = "环境";
        private const string BuildGroup = "建造与资源";
        private const string FallbackGroup = "兜底";

        // 顺序即界面顺序；分组靠相邻项的 Group 相同来体现，省的再维护一张分组表。
        // 优先级、聚合模板键、开发期默认值一列都不在代码里判（02 §5）。
        public static readonly CategoryEntry[] Entries =
        {
            new CategoryEntry(SpokenCategory.ThreatRaid, ThreatGroup, "袭击", "敌对派系、机械族、虫巢来袭", 20, null, true),
            new CategoryEntry(SpokenCategory.ThreatSiege, ThreatGroup, "围攻 / 空投", "围攻、空投、机械集群", 20, null, true),
            new CategoryEntry(SpokenCategory.ThreatAnimalInsanity, ThreatGroup, "动物发狂", "野生动物集体发狂", 20, null, true),
            new CategoryEntry(SpokenCategory.PawnDowned, PersonGroup, "殖民者倒地", "殖民者或宠物倒地", 10, null, true),
            new CategoryEntry(SpokenCategory.PawnDied, PersonGroup, "殖民者死亡", "殖民者或宠物死亡", 10, SpeechPhrase.Phrase_PawnDied_Batch, true),
            new CategoryEntry(SpokenCategory.MentalBreak, PersonGroup, "精神崩溃", "殖民者精神崩溃", 10, SpeechPhrase.Phrase_MentalBreak_Batch, true),
            new CategoryEntry(SpokenCategory.Birth, PersonGroup, "分娩 / 出生", "分娩开始、婴儿出生", 10, null, true),
            new CategoryEntry(SpokenCategory.DiseaseOutbreak, HealthGroup, "疾病爆发", "疾病爆发，含感染者名单", 5, SpeechPhrase.Phrase_DiseaseOutbreak_Batch, true),
            new CategoryEntry(SpokenCategory.DiseaseSpread, HealthGroup, "感染 / 瘟疫扩散", "感染与瘟疫扩散", 5, SpeechPhrase.Phrase_DiseaseSpread_Batch, true),
            new CategoryEntry(SpokenCategory.Addiction, HealthGroup, "成瘾与戒断", "成瘾、戒断症状", 5, null, true),
            new CategoryEntry(SpokenCategory.TraderArrival, GuestGroup, "商队抵达", "商队抵达，含派系与商人类型", 5, null, true),
            new CategoryEntry(SpokenCategory.Visitors, GuestGroup, "访客 / 难民", "访客、难民、乞讨者", 5, null, true),
            new CategoryEntry(SpokenCategory.Quests, GuestGroup, "任务与委托", "新任务与委托", 5, null, true),
            new CategoryEntry(SpokenCategory.WeatherExtreme, EnvironmentGroup, "极端天气", "热浪、寒潮、火山冬天", 5, null, true),
            new CategoryEntry(SpokenCategory.EnvHazard, EnvironmentGroup, "环境危害", "太阳耀斑、日食、有毒落尘、辐射", 5, null, true),
            new CategoryEntry(SpokenCategory.Rotten, EnvironmentGroup, "物品腐烂", "库存物品腐烂", 0, SpeechPhrase.Phrase_Rotten_Batch, true),
            new CategoryEntry(SpokenCategory.BuildMissingMaterials, BuildGroup, "蓝图缺材料", "蓝图缺料，播「缺 X 钢」", 0, null, true),
            new CategoryEntry(SpokenCategory.BuildPlaceFailed, BuildGroup, "落点失败", "「这里不能放」", 0, null, true),
            new CategoryEntry(SpokenCategory.BillSuspended, BuildGroup, "工作台缺料", "工作台因缺料挂起", 0, null, true),
            new CategoryEntry(SpokenCategory.OtherIncident, FallbackGroup, "其它事件", "未归类的随机事件", 0, null, true),
            new CategoryEntry(SpokenCategory.OtherLetter, FallbackGroup, "其它信件", "未归类的信件", 0, null, true),
            new CategoryEntry(SpokenCategory.OtherMessage, FallbackGroup, "其它提示", "未归类的角落提示", 0, null, true),
        };

        private static readonly Dictionary<SpokenCategory, CategoryEntry> EntriesByCategory = IndexEntries();

        // 03 §3 的 28 条：单事件 22 + 聚合 5 + 裸文本 1。模板键与 SpeechPhrase 一一对应。
        // 聚合形态固定两槽 {0}=Count、{1}=Names（03 §3.2）；裸文本不切分（03 §3.3）。
        public static readonly PhraseEntry[] Phrases =
        {
            new PhraseEntry(SpeechPhrase.Phrase_ThreatRaid, SpokenCategory.ThreatRaid, PhraseForm.SingleEvent, "侦测到袭击，{0}，{1}。", new[] { SegmentKind.PersonName, SegmentKind.Enum }),
            new PhraseEntry(SpeechPhrase.Phrase_ThreatSiege, SpokenCategory.ThreatSiege, PhraseForm.SingleEvent, "侦测到围攻，{0}，{1}。", new[] { SegmentKind.PersonName, SegmentKind.Enum }),
            new PhraseEntry(SpeechPhrase.Phrase_ThreatAnimalInsanity, SpokenCategory.ThreatAnimalInsanity, PhraseForm.SingleEvent, "动物发狂，{0}。", new[] { SegmentKind.Enum }),
            new PhraseEntry(SpeechPhrase.Phrase_PawnDowned, SpokenCategory.PawnDowned, PhraseForm.SingleEvent, "{0}倒地。", new[] { SegmentKind.PersonName }),
            new PhraseEntry(SpeechPhrase.Phrase_PawnDied, SpokenCategory.PawnDied, PhraseForm.SingleEvent, "{0}阵亡。{1}", new[] { SegmentKind.PersonName, SegmentKind.RawText }),
            new PhraseEntry(SpeechPhrase.Phrase_MentalBreak, SpokenCategory.MentalBreak, PhraseForm.SingleEvent, "{0}精神崩溃，{1}。", new[] { SegmentKind.PersonName, SegmentKind.Enum }),
            new PhraseEntry(SpeechPhrase.Phrase_Birth, SpokenCategory.Birth, PhraseForm.SingleEvent, "{0}开始分娩。", new[] { SegmentKind.PersonName }),
            new PhraseEntry(SpeechPhrase.Phrase_DiseaseOutbreak, SpokenCategory.DiseaseOutbreak, PhraseForm.SingleEvent, "疾病爆发，{0}，{1}人感染。", new[] { SegmentKind.Enum, SegmentKind.Number }),
            new PhraseEntry(SpeechPhrase.Phrase_DiseaseSpread, SpokenCategory.DiseaseSpread, PhraseForm.SingleEvent, "感染扩散，{0}人。", new[] { SegmentKind.Number }),
            new PhraseEntry(SpeechPhrase.Phrase_Addiction, SpokenCategory.Addiction, PhraseForm.SingleEvent, "{0}出现戒断。", new[] { SegmentKind.PersonName }),
            new PhraseEntry(SpeechPhrase.Phrase_TraderArrival, SpokenCategory.TraderArrival, PhraseForm.SingleEvent, "商队抵达，{0}，{1}。", new[] { SegmentKind.PersonName, SegmentKind.Enum }),
            new PhraseEntry(SpeechPhrase.Phrase_Visitors, SpokenCategory.Visitors, PhraseForm.SingleEvent, "访客抵达，{0}。", new[] { SegmentKind.PersonName }),
            new PhraseEntry(SpeechPhrase.Phrase_Quests, SpokenCategory.Quests, PhraseForm.SingleEvent, "新任务：{0}。", new[] { SegmentKind.RawText }),
            new PhraseEntry(SpeechPhrase.Phrase_WeatherExtreme, SpokenCategory.WeatherExtreme, PhraseForm.SingleEvent, "天气异常，{0}。", new[] { SegmentKind.Enum }),
            new PhraseEntry(SpeechPhrase.Phrase_EnvHazard, SpokenCategory.EnvHazard, PhraseForm.SingleEvent, "环境危害，{0}。", new[] { SegmentKind.Enum }),
            new PhraseEntry(SpeechPhrase.Phrase_Rotten, SpokenCategory.Rotten, PhraseForm.SingleEvent, "库存物品腐烂。", new SegmentKind[0]),
            new PhraseEntry(SpeechPhrase.Phrase_BuildMissingMaterials, SpokenCategory.BuildMissingMaterials, PhraseForm.SingleEvent, "材料不足，缺 {0} 个{1}。", new[] { SegmentKind.Number, SegmentKind.Enum }),
            new PhraseEntry(SpeechPhrase.Phrase_BuildPlaceFailed, SpokenCategory.BuildPlaceFailed, PhraseForm.SingleEvent, "这里不能放：{0}", new[] { SegmentKind.RawText }),
            new PhraseEntry(SpeechPhrase.Phrase_BillSuspended, SpokenCategory.BillSuspended, PhraseForm.SingleEvent, "工作台因缺料停摆。", new SegmentKind[0]),
            new PhraseEntry(SpeechPhrase.Phrase_OtherIncident, SpokenCategory.OtherIncident, PhraseForm.SingleEvent, "有事件：{0}。", new[] { SegmentKind.RawText }),
            new PhraseEntry(SpeechPhrase.Phrase_OtherLetter, SpokenCategory.OtherLetter, PhraseForm.SingleEvent, "有信件：{0}。", new[] { SegmentKind.RawText }),
            new PhraseEntry(SpeechPhrase.Phrase_OtherMessage, SpokenCategory.OtherMessage, PhraseForm.SingleEvent, "{0}", new[] { SegmentKind.RawText }),
            new PhraseEntry(SpeechPhrase.Phrase_PawnDied_Batch, SpokenCategory.PawnDied, PhraseForm.Batch, "{0} 人阵亡：{1}。", new[] { SegmentKind.Number, SegmentKind.PersonName }),
            new PhraseEntry(SpeechPhrase.Phrase_MentalBreak_Batch, SpokenCategory.MentalBreak, PhraseForm.Batch, "{0} 人精神崩溃：{1}。", new[] { SegmentKind.Number, SegmentKind.PersonName }),
            new PhraseEntry(SpeechPhrase.Phrase_DiseaseOutbreak_Batch, SpokenCategory.DiseaseOutbreak, PhraseForm.Batch, "疾病爆发，{0} 人感染。", new[] { SegmentKind.Number, SegmentKind.PersonName }),
            new PhraseEntry(SpeechPhrase.Phrase_DiseaseSpread_Batch, SpokenCategory.DiseaseSpread, PhraseForm.Batch, "感染扩散，{0} 人。", new[] { SegmentKind.Number, SegmentKind.PersonName }),
            new PhraseEntry(SpeechPhrase.Phrase_Rotten_Batch, SpokenCategory.Rotten, PhraseForm.Batch, "有 {0} 件物品腐烂。", new[] { SegmentKind.Number, SegmentKind.PersonName }),
            new PhraseEntry(SpeechPhrase.Phrase_RawText, null, PhraseForm.RawText, string.Empty, new[] { SegmentKind.RawText }),
        };

        // 02 §4.1：两个通道的排他声明处（incident 没有丢弃表，判定不中就兜底）。
        private static readonly string[] LetterDiscardDefNames =
        {
            "Death", "BabyBirth", "ThreatBig", "ThreatSmall", "NegativeEvent", "PositiveEvent", "NeutralEvent",
        };

        private static readonly string[] MessageDiscardDefNames =
        {
            "ThreatBig", "ThreatSmall", "PawnDeath",
        };

        // 02 §3.1 的判定顺序：具体判据排在宽判据之前，顺序即优先级，调用方不重排。
        private static readonly BindingKey[] IncidentJudgmentOrder =
        {
            BindingKey.RaidStrategyPrefix,
            BindingKey.ArrivalModeContains,
            BindingKey.DefNameContains,
            BindingKey.DefName,
            BindingKey.WorkerClass,
            BindingKey.Category,
        };

        // letter / message 只有一个判据种类；表内顺序就是 SilentInput/RejectInput 先于 NegativeEvent。
        private static readonly BindingKey[] DefNameJudgmentOrder = { BindingKey.DefName };

        private const string RottenTextKey = "MessageRottedAwayInStorage";

        private static SourceBinding[] bindings;
        private static string[] constantTexts;

        // 绑定表延迟到第一次查询才建：TextSuffix 要从原版翻译串派生（02 §3.3），
        // 而静态字段初始化会撞上 Mod 构造期（语言包还没读）。
        public static SourceBinding[] Bindings
        {
            get { return bindings ?? (bindings = CreateBindings()); }
        }

        public static CategoryEntry EntryOf(SpokenCategory category)
        {
            CategoryEntry entry;
            return EntriesByCategory.TryGetValue(category, out entry) ? entry : null;
        }

        // 02 §3.1：特例（Siege）→ 具体白名单 → workerClass → 类别（ThreatBig）→ 兜底。
        // workerClassName 是 02 §3.1 判定第 3 步需要的第 5 份事实（文档签名只写了前 4 个）。
        public static SpokenCategory? ForIncident(string defName, string categoryDefName, string raidStrategyDefName, string arrivalModeDefName, string workerClassName = null)
        {
            MatchFacts facts = new MatchFacts(defName, categoryDefName, raidStrategyDefName, arrivalModeDefName, workerClassName, null);
            SpokenCategory? hit = Match(SpeechChannel.Incident, facts);
            return hit ?? SpokenCategory.OtherIncident;
        }

        // 02 §3.2：先查丢弃表，再看 NewQuest，其余兜底。
        public static SpokenCategory? ForLetter(string letterDefName)
        {
            if (Contains(LetterDiscardDefNames, letterDefName))
            {
                return null;
            }

            MatchFacts facts = new MatchFacts(letterDefName, null, null, null, null, null);
            SpokenCategory? hit = Match(SpeechChannel.Letter, facts);
            return hit ?? SpokenCategory.OtherLetter;
        }

        // 02 §3.3：先查丢弃表，再看两个落点 def，再看腐烂后缀，其余兜底。
        public static SpokenCategory? ForMessage(string messageDefName, string text)
        {
            if (Contains(MessageDiscardDefNames, messageDefName))
            {
                return null;
            }

            MatchFacts facts = new MatchFacts(messageDefName, null, null, null, null, text);
            SpokenCategory? hit = Match(SpeechChannel.Message, facts);
            return hit ?? SpokenCategory.OtherMessage;
        }

        // 03 §7.1：预热第一批 = 模板里切出来的全部字面量；派生，不另维护词表。
        public static string[] ConstantTexts()
        {
            if (constantTexts != null)
            {
                return constantTexts;
            }

            List<string> texts = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (PhraseEntry phrase in Phrases)
            {
                foreach (string literal in TemplateLiterals(phrase.Template))
                {
                    if (seen.Add(literal))
                    {
                        texts.Add(literal);
                    }
                }
            }

            constantTexts = texts.ToArray();
            return constantTexts;
        }

        private static Dictionary<SpokenCategory, CategoryEntry> IndexEntries()
        {
            Dictionary<SpokenCategory, CategoryEntry> index = new Dictionary<SpokenCategory, CategoryEntry>(Entries.Length);
            foreach (CategoryEntry entry in Entries)
            {
                index[entry.Category] = entry;
            }

            return index;
        }

        private static SpokenCategory? Match(SpeechChannel channel, MatchFacts facts)
        {
            foreach (BindingKey kind in JudgmentOrder(channel))
            {
                foreach (SourceBinding binding in Bindings)
                {
                    if (binding.Channel != channel || binding.Kind != kind)
                    {
                        continue;
                    }

                    if (BindingMatches(binding, facts))
                    {
                        return binding.Category;
                    }
                }
            }

            return null;
        }

        private static BindingKey[] JudgmentOrder(SpeechChannel channel)
        {
            return channel == SpeechChannel.Incident ? IncidentJudgmentOrder : DefNameJudgmentOrder;
        }

        private static bool BindingMatches(SourceBinding binding, MatchFacts facts)
        {
            if (binding.TextSuffix != null && !EndsWith(facts.Text, binding.TextSuffix))
            {
                return false;
            }

            switch (binding.Kind)
            {
                case BindingKey.DefName:
                    return TextEquals(facts.DefName, binding.Value);
                case BindingKey.Category:
                    return TextEquals(facts.CategoryDefName, binding.Value);
                case BindingKey.RaidStrategyPrefix:
                    return StartsWith(facts.RaidStrategyDefName, binding.Value);
                case BindingKey.ArrivalModeContains:
                    return ContainsText(facts.ArrivalModeDefName, binding.Value);
                case BindingKey.DefNameContains:
                    return ContainsText(facts.DefName, binding.Value);
                case BindingKey.WorkerClass:
                    return ContainsText(facts.WorkerClassName, binding.Value);
                default:
                    return false;
            }
        }

        // 加一行绑定只改这张表：判定顺序是上面那两组 BindingKey，函数逻辑不认识具体类别。
        private static SourceBinding[] CreateBindings()
        {
            List<SourceBinding> rows = new List<SourceBinding>
            {
                // 02 §3.1 第 1 步：Siege 特例。
                new SourceBinding(SpokenCategory.ThreatSiege, SpeechChannel.Incident, BindingKey.RaidStrategyPrefix, "Siege"),
                new SourceBinding(SpokenCategory.ThreatSiege, SpeechChannel.Incident, BindingKey.ArrivalModeContains, "Drop"),
                new SourceBinding(SpokenCategory.ThreatSiege, SpeechChannel.Incident, BindingKey.DefNameContains, "Siege"),
                new SourceBinding(SpokenCategory.ThreatSiege, SpeechChannel.Incident, BindingKey.DefNameContains, "MechCluster"),
                new SourceBinding(SpokenCategory.ThreatSiege, SpeechChannel.Incident, BindingKey.DefNameContains, "Cluster"),

                // 第 2 步：具体白名单（按 DefName 精确）。
                new SourceBinding(SpokenCategory.ThreatAnimalInsanity, SpeechChannel.Incident, BindingKey.DefName, "AnimalInsanityMass"),
                new SourceBinding(SpokenCategory.ThreatAnimalInsanity, SpeechChannel.Incident, BindingKey.DefName, "AnimalInsanitySingle"),
                new SourceBinding(SpokenCategory.ThreatAnimalInsanity, SpeechChannel.Incident, BindingKey.DefName, "ManhunterPack"),
                new SourceBinding(SpokenCategory.TraderArrival, SpeechChannel.Incident, BindingKey.DefName, "TraderCaravanArrival"),
                new SourceBinding(SpokenCategory.TraderArrival, SpeechChannel.Incident, BindingKey.DefName, "OrbitalTraderArrival"),
                new SourceBinding(SpokenCategory.Visitors, SpeechChannel.Incident, BindingKey.DefName, "VisitorGroup"),
                new SourceBinding(SpokenCategory.WeatherExtreme, SpeechChannel.Incident, BindingKey.DefName, "HeatWave"),
                new SourceBinding(SpokenCategory.WeatherExtreme, SpeechChannel.Incident, BindingKey.DefName, "ColdSnap"),
                new SourceBinding(SpokenCategory.WeatherExtreme, SpeechChannel.Incident, BindingKey.DefName, "VolcanicWinter"),
                new SourceBinding(SpokenCategory.EnvHazard, SpeechChannel.Incident, BindingKey.DefName, "SolarFlare"),
                new SourceBinding(SpokenCategory.EnvHazard, SpeechChannel.Incident, BindingKey.DefName, "Eclipse"),
                new SourceBinding(SpokenCategory.EnvHazard, SpeechChannel.Incident, BindingKey.DefName, "ToxicFallout"),

                // 第 3 步：13 个 Disease_* 共用一个 worker，不逐个列 def 名。
                new SourceBinding(SpokenCategory.DiseaseOutbreak, SpeechChannel.Incident, BindingKey.WorkerClass, "IncidentWorker_Disease"),

                // 第 4 步：类别级判定（宽判据放最后）。
                new SourceBinding(SpokenCategory.ThreatRaid, SpeechChannel.Incident, BindingKey.Category, "ThreatBig"),

                // letter。
                new SourceBinding(SpokenCategory.Quests, SpeechChannel.Letter, BindingKey.DefName, "NewQuest"),

                // message：表内顺序即判定顺序（落点失败先于腐烂）。
                new SourceBinding(SpokenCategory.BuildPlaceFailed, SpeechChannel.Message, BindingKey.DefName, "SilentInput"),
                new SourceBinding(SpokenCategory.BuildPlaceFailed, SpeechChannel.Message, BindingKey.DefName, "RejectInput"),
            };

            string rottenSuffix = RottenTextSuffix();
            if (IsUsableRottenSuffix(rottenSuffix))
            {
                rows.Add(new SourceBinding(SpokenCategory.Rotten, SpeechChannel.Message, BindingKey.DefName, "NegativeEvent", rottenSuffix));
            }

            return rows.ToArray();
        }

        // 02 §3.3：腐烂的静态尾部从原版翻译串按 { / } 切出来，表里不写死文案。
        private static string RottenTextSuffix()
        {
            string template = RottenTextKey.Translate();
            int lastClosingBrace = template.LastIndexOf('}');
            return lastClosingBrace < 0 ? template : template.Substring(lastClosingBrace + 1);
        }

        // 翻译缺键时 Translate() 原样回键名、后缀可能为空；两种都算不可用（02 §3.3 允许退化成 OtherMessage）。
        private static bool IsUsableRottenSuffix(string suffix)
        {
            return !string.IsNullOrEmpty(suffix) && suffix != RottenTextKey;
        }

        // 只抽字面量（Const 片段）；占位符取什么值归 SegmentPlanner，不在这里解析。
        private static IEnumerable<string> TemplateLiterals(string template)
        {
            if (string.IsNullOrEmpty(template))
            {
                yield break;
            }

            int start = 0;
            int open = template.IndexOf('{');
            while (open >= 0)
            {
                if (open > start)
                {
                    yield return template.Substring(start, open - start);
                }

                int close = template.IndexOf('}', open);
                if (close < 0)
                {
                    yield break;
                }

                start = close + 1;
                open = template.IndexOf('{', start);
            }

            if (start < template.Length)
            {
                yield return template.Substring(start);
            }
        }

        private static bool Contains(string[] values, string value)
        {
            if (value == null)
            {
                return false;
            }

            foreach (string candidate in values)
            {
                if (string.Equals(candidate, value, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TextEquals(string actual, string expected)
        {
            return actual != null && string.Equals(actual, expected, StringComparison.Ordinal);
        }

        private static bool StartsWith(string actual, string prefix)
        {
            return actual != null && actual.StartsWith(prefix, StringComparison.Ordinal);
        }

        private static bool EndsWith(string actual, string suffix)
        {
            return actual != null && actual.EndsWith(suffix, StringComparison.Ordinal);
        }

        private static bool ContainsText(string actual, string part)
        {
            return actual != null && actual.IndexOf(part, StringComparison.Ordinal) >= 0;
        }

        // 现场事实的字符串快照：本类只认 string，不认 Verse 类型（02 §3 开头）。
        private readonly struct MatchFacts
        {
            public readonly string DefName;
            public readonly string CategoryDefName;
            public readonly string RaidStrategyDefName;
            public readonly string ArrivalModeDefName;
            public readonly string WorkerClassName;
            public readonly string Text;

            public MatchFacts(string defName, string categoryDefName, string raidStrategyDefName, string arrivalModeDefName, string workerClassName, string text)
            {
                DefName = defName;
                CategoryDefName = categoryDefName;
                RaidStrategyDefName = raidStrategyDefName;
                ArrivalModeDefName = arrivalModeDefName;
                WorkerClassName = workerClassName;
                Text = text;
            }
        }

        #region TEMP 自检：第 4 步进游戏验收后删除（连同 VoiceDeputyMod 里的调用一起删）
        // 第 3 步没有游戏可跑，这段日志代替 06 §3 的 B 类判据；样本一律用 02 里的真实 defName。
        public static void SelfCheck()
        {
            int total = 0;
            int covered = 0;
            foreach (SpokenCategory category in (SpokenCategory[])Enum.GetValues(typeof(SpokenCategory)))
            {
                total++;
                if (EntryOf(category) != null)
                {
                    covered++;
                }
                else
                {
                    Log.Message("[VD] 0 missing-category " + category);
                }
            }

            Log.Message("[VD] 0 self-check phrases=" + Phrases.Length + " bindings=" + Bindings.Length
                + " constants=" + ConstantTexts().Length + " categories=" + covered + "/" + total);

            foreach (SelfCheckSample sample in SelfCheckSamples)
            {
                if (sample.Call == null)
                {
                    Log.Message("[VD] 0 skip-category " + sample.DefName + " (自算/待实现：没有 def 判定)");
                    continue;
                }

                SpokenCategory? actual = sample.Call();
                if (actual == sample.Expected)
                {
                    Log.Message("[VD] 0 sample " + sample.Channel + " " + sample.DefName + " -> " + Describe(actual) + " ok");
                }
                else
                {
                    Log.Message("[VD] 0 sample " + sample.Channel + " " + sample.DefName + " MISMATCH expected="
                        + Describe(sample.Expected) + " actual=" + Describe(actual));
                }
            }
        }

        private static string Describe(SpokenCategory? category)
        {
            return category.HasValue ? category.Value.ToString() : "drop";
        }

        private sealed class SelfCheckSample
        {
            public readonly string Channel;
            public readonly string DefName;
            public readonly SpokenCategory? Expected;
            public readonly Func<SpokenCategory?> Call;

            public SelfCheckSample(string channel, string defName, SpokenCategory? expected, Func<SpokenCategory?> call)
            {
                Channel = channel;
                DefName = defName;
                Expected = expected;
                Call = call;
            }
        }

        private static readonly SelfCheckSample[] SelfCheckSamples =
        {
            // incident：02 §3.1 的 1–5 步各一条。
            new SelfCheckSample("incident", "RaidEnemy", SpokenCategory.ThreatRaid, () => ForIncident("RaidEnemy", "ThreatBig", null, null)),
            new SelfCheckSample("incident", "RaidEnemy/Siege", SpokenCategory.ThreatSiege, () => ForIncident("RaidEnemy", "ThreatBig", "Siege", null)),
            new SelfCheckSample("incident", "ManhunterPack", SpokenCategory.ThreatAnimalInsanity, () => ForIncident("ManhunterPack", "ThreatSmall", null, null)),
            new SelfCheckSample("incident", "Disease_Flu", SpokenCategory.DiseaseOutbreak, () => ForIncident("Disease_Flu", "Disease", null, null, "IncidentWorker_Disease")),
            new SelfCheckSample("incident", "TraderCaravanArrival", SpokenCategory.TraderArrival, () => ForIncident("TraderCaravanArrival", "PositiveEvent", null, null)),
            new SelfCheckSample("incident", "VisitorGroup", SpokenCategory.Visitors, () => ForIncident("VisitorGroup", "NeutralEvent", null, null)),
            new SelfCheckSample("incident", "HeatWave", SpokenCategory.WeatherExtreme, () => ForIncident("HeatWave", "ThreatSmall", null, null)),
            new SelfCheckSample("incident", "SolarFlare", SpokenCategory.EnvHazard, () => ForIncident("SolarFlare", "NegativeEvent", null, null)),
            new SelfCheckSample("incident", "UnknownIncident", SpokenCategory.OtherIncident, () => ForIncident("UnknownIncident", "Misc", null, null)),

            // letter：02 §3.2 的三步。
            new SelfCheckSample("letter", "NewQuest", SpokenCategory.Quests, () => ForLetter("NewQuest")),
            new SelfCheckSample("letter", "UnknownLetter", SpokenCategory.OtherLetter, () => ForLetter("UnknownLetter")),
            new SelfCheckSample("letter", "Death", null, () => ForLetter("Death")),

            // message：02 §3.3 的四步。
            new SelfCheckSample("message", "SilentInput", SpokenCategory.BuildPlaceFailed, () => ForMessage("SilentInput", "这里被占了。")),
            new SelfCheckSample("message", "NegativeEvent", SpokenCategory.Rotten, () => ForMessage("NegativeEvent", "样品" + RottenTextSuffix())),
            new SelfCheckSample("message", "UnknownMessage", SpokenCategory.OtherMessage, () => ForMessage("UnknownMessage", "随便一条小字。")),
            new SelfCheckSample("message", "PawnDeath", null, () => ForMessage("PawnDeath", string.Empty)),
            new SelfCheckSample("message", "ThreatBig", null, () => ForMessage("ThreatBig", string.Empty)),

            // 自算 / 待实现的 8 类没有 For* 可抽，逐个列出防漏。
            new SelfCheckSample("self-computed", "PawnDowned", SpokenCategory.PawnDowned, null),
            new SelfCheckSample("self-computed", "PawnDied", SpokenCategory.PawnDied, null),
            new SelfCheckSample("self-computed", "MentalBreak", SpokenCategory.MentalBreak, null),
            new SelfCheckSample("self-computed", "Birth", SpokenCategory.Birth, null),
            new SelfCheckSample("self-computed", "BuildMissingMaterials", SpokenCategory.BuildMissingMaterials, null),
            new SelfCheckSample("self-computed", "DiseaseSpread", SpokenCategory.DiseaseSpread, null),
            new SelfCheckSample("self-computed", "Addiction", SpokenCategory.Addiction, null),
            new SelfCheckSample("self-computed", "BillSuspended", SpokenCategory.BillSuspended, null),
        };
        #endregion
    }
}
