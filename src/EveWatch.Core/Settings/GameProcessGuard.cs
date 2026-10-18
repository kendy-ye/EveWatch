using System.Diagnostics;

namespace EveWatch.Core.Settings;

/// <summary>
/// 检测 EVE 客户端 / 启动器是否在运行。
/// 克隆设置必须在客户端关闭时进行，否则退出时客户端会回写覆盖改动。
/// </summary>
public static class GameProcessGuard
{
    private static readonly string[] ClientProcessNames = { "exefile", "evelauncher" };

    /// <summary>返回当前正在运行的客户端进程名（空表示可以安全写入）。</summary>
    public static IReadOnlyList<string> GetRunningProcesses()
    {
        var running = new List<string>();

        foreach (string name in ClientProcessNames)
        {
            try
            {
                if (Process.GetProcessesByName(name).Length > 0)
                {
                    running.Add(name);
                }
            }
            catch (Exception)
            {
                // 进程枚举失败按“未运行”处理，避免误阻塞。
            }
        }

        return running;
    }

    /// <summary>客户端或启动器是否在运行。</summary>
    public static bool IsClientRunning() => GetRunningProcesses().Count > 0;
}
