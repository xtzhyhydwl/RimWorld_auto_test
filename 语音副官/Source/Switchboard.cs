namespace VoiceDeputy
{
    public static class Switchboard
    {
        // Settings 为 null 只出现在 Mod 构造完成前的读档期；此时一律放行，免得把已发生的事件全丢掉。
        public static bool Enabled(SpokenCategory category)
        {
            VoiceDeputySettings settings = VoiceDeputyMod.Settings;
            return settings == null || settings.IsCategoryEnabled(category);
        }
    }
}
