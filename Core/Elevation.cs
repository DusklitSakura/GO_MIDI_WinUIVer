using System.ComponentModel;
using System.Diagnostics;

namespace GoMidi.Core;

/// <summary>
/// Answers "am I elevated?" and can relaunch the app at the other privilege level.
///
/// This matters because of Windows UIPI: a process running at medium integrity
/// cannot inject input into one running elevated, and the failure is silent —
/// <c>SendInput</c> and <c>PostMessage</c> simply do nothing.
/// </summary>
public static class Elevation
{
    private static readonly Lazy<bool> CurrentElevated = new(DetectCurrentProcessElevation);

    /// <summary>True when this process is running with an elevated (administrator) token.</summary>
    public static bool IsCurrentProcessElevated => CurrentElevated.Value;

    private static bool DetectCurrentProcessElevation() => IsTokenElevated(NativeMethods.GetCurrentProcess());

    private static bool IsTokenElevated(nint process)
    {
        if (!NativeMethods.OpenProcessToken(process, NativeMethods.TOKEN_QUERY, out nint token))
        {
            return false;
        }

        try
        {
            return NativeMethods.GetTokenInformation(token, NativeMethods.TokenElevation, out uint elevated, sizeof(uint), out _)
                   && elevated != 0;
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }

    /// <summary>
    /// Relaunches this executable with the requested privilege level.
    ///
    /// Elevating uses the <c>runas</c> verb, which raises the UAC prompt.
    /// Dropping back down goes through <c>explorer.exe</c>: Explorer always runs
    /// unelevated, so the process it starts inherits a normal token. There is no
    /// direct API for de-elevating, and this is the supported workaround.
    /// </summary>
    /// <returns><c>true</c> when a new instance was started.</returns>
    public static bool TryRestart(bool elevated, out string error)
    {
        error = string.Empty;

        string? executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            error = "无法确定程序自身的路径。";
            return false;
        }

        try
        {
            ProcessStartInfo startInfo = elevated
                ? new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = true,
                    Verb = "runas",
                }
                : new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{executable}\"",
                    UseShellExecute = true,
                };

            Process.Start(startInfo);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "已取消管理员授权，程序仍在原权限下运行。";
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("重启进程失败", ex);
            error = ex.Message;
            return false;
        }
    }
}
