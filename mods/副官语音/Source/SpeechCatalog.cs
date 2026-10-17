namespace VoiceDeputy
{
    public sealed class CategoryEntry
    {
        public readonly SpokenCategory Category;
        public readonly string Group;
        public readonly string Label;
        public readonly string Description;

        public CategoryEntry(SpokenCategory category, string group, string label, string description)
        {
            Category = category;
            Group = group;
            Label = label;
            Description = description;
        }
    }

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
        public static readonly CategoryEntry[] Entries =
        {
            new CategoryEntry(SpokenCategory.ThreatRaid, ThreatGroup, "袭击", "敌对派系、机械族、虫巢来袭"),
            new CategoryEntry(SpokenCategory.ThreatSiege, ThreatGroup, "围攻 / 空投", "围攻、空投、机械集群"),
            new CategoryEntry(SpokenCategory.ThreatAnimalInsanity, ThreatGroup, "动物发狂", "野生动物集体发狂"),
            new CategoryEntry(SpokenCategory.PawnDowned, PersonGroup, "殖民者倒地", "殖民者或宠物倒地"),
            new CategoryEntry(SpokenCategory.PawnDied, PersonGroup, "殖民者死亡", "殖民者或宠物死亡"),
            new CategoryEntry(SpokenCategory.MentalBreak, PersonGroup, "精神崩溃", "殖民者精神崩溃"),
            new CategoryEntry(SpokenCategory.Birth, PersonGroup, "分娩 / 出生", "分娩开始、婴儿出生"),
            new CategoryEntry(SpokenCategory.DiseaseOutbreak, HealthGroup, "疾病爆发", "疾病爆发，含感染者名单"),
            new CategoryEntry(SpokenCategory.DiseaseSpread, HealthGroup, "感染 / 瘟疫扩散", "感染与瘟疫扩散"),
            new CategoryEntry(SpokenCategory.Addiction, HealthGroup, "成瘾与戒断", "成瘾、戒断症状"),
            new CategoryEntry(SpokenCategory.TraderArrival, GuestGroup, "商队抵达", "商队抵达，含派系与商人类型"),
            new CategoryEntry(SpokenCategory.Visitors, GuestGroup, "访客 / 难民", "访客、难民、乞讨者"),
            new CategoryEntry(SpokenCategory.Quests, GuestGroup, "任务与委托", "新任务与委托"),
            new CategoryEntry(SpokenCategory.WeatherExtreme, EnvironmentGroup, "极端天气", "热浪、寒潮、火山冬天"),
            new CategoryEntry(SpokenCategory.EnvHazard, EnvironmentGroup, "环境危害", "太阳耀斑、日食、有毒落尘、辐射"),
            new CategoryEntry(SpokenCategory.Rotten, EnvironmentGroup, "物品腐烂", "库存物品腐烂"),
            new CategoryEntry(SpokenCategory.BuildMissingMaterials, BuildGroup, "蓝图缺材料", "蓝图缺料，播「缺 X 钢」"),
            new CategoryEntry(SpokenCategory.BuildPlaceFailed, BuildGroup, "落点失败", "「这里不能放」"),
            new CategoryEntry(SpokenCategory.BillSuspended, BuildGroup, "工作台缺料", "工作台因缺料挂起"),
            new CategoryEntry(SpokenCategory.OtherIncident, FallbackGroup, "其它事件", "未归类的随机事件"),
            new CategoryEntry(SpokenCategory.OtherLetter, FallbackGroup, "其它信件", "未归类的信件"),
            new CategoryEntry(SpokenCategory.OtherMessage, FallbackGroup, "其它提示", "未归类的角落提示"),
        };
    }
}
