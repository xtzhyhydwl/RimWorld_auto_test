using Verse;

namespace VoiceDeputy
{
    // 路径日志（01 §5.2 的 id 表）：一行 [VD] <id> <msg>，只在 TraceEnabled 为真时打。
    public static class Trace
    {
        public static void Step(string id, string msg = null)
        {
            try
            {
                VoiceDeputySettings settings = VoiceDeputyMod.Settings;
                if (settings == null || !settings.TraceEnabled)
                {
                    return;
                }

                Log.Message(msg == null || msg.Length == 0 ? "[VD] " + id : "[VD] " + id + " " + msg);
            }
            catch
            {
                // 埋点是旁路：日志打不出来也不能影响播报（01 §5.2 失败模式）。
            }
        }
    }
}
