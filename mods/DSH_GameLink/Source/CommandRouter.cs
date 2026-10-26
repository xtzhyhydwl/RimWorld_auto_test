// DSH_GameLink · 命令分派（一个名字 → 一个处理器）。
//
// 只做查表，不认识传输（文件/kv），也不认识 HTTP/TCP：换传输时这个文件不用动。
// 加一条命令 = 在 Handler 里加一个 case + 在 Protocol.AllCommands 里登记（两处，刻意保持显式）。

using System;

namespace DSH_GameLink
{
    internal static class CommandRouter
    {
        internal static void Dispatch(string command, KvArgs args, out string payload, out bool ok, out string code, out string message)
        {
            payload = "{}";
            ok = false;
            code = null;
            message = null;

            try
            {
                if (string.IsNullOrEmpty(command))
                {
                    code = Protocol.Errors.BadArgs;
                    message = "命令文件里没有 cmd=";
                    return;
                }

                switch (command.Trim().ToLowerInvariant())
                {
                    case Protocol.Commands.Advance:
                        payload = TickDriver.Start(args, out ok, out code, out message);
                        return;

                    case Protocol.Commands.Status:
                        payload = TickDriver.Status(out ok, out code, out message);
                        return;

                    case Protocol.Commands.Cancel:
                        payload = TickDriver.Cancel(out ok, out code, out message);
                        return;

                    case Protocol.Commands.Probe:
                        payload = TickDriver.ProbeJson();
                        ok = true;
                        return;

                    default:
                        code = Protocol.Errors.UnknownCommand;
                        message = "不认识的命令「" + command + "」；支持：" + string.Join(", ", Protocol.AllCommands);
                        return;
                }
            }
            catch (Exception ex)
            {
                code = Protocol.Errors.Internal;
                message = LiveChannel.Describe(ex);
                LiveChannel.Err("执行命令「" + command + "」时抛异常：" + message);
            }
        }
    }
}
