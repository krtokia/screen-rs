using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Monitor.Sender;

/// <summary>
/// REQUIREMENTS.md §9, §13.7. The sender installs itself: run the exe once and it copies itself into
/// %LOCALAPPDATA%\ScreenSender, registers a per-user logon auto-start, and relaunches from there.
/// No script, no admin rights. The security-sensitive machine settings (auto-login, no sleep, no lock)
/// are the operator's job and cannot be automated — we only remind them.
/// </summary>
public static class SelfInstall
{
    private static readonly string InstallDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenSender");
    private static readonly string InstalledExe = Path.Combine(InstallDir, "ScreenSender.exe");

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ScreenSender";

    /// <summary>
    /// Returns true if this process should keep running as the sender. Returns false if it acted as an
    /// installer and handed off to the installed copy (the caller must then exit).
    /// </summary>
    public static bool EnsureInstalledAndRunningFromTarget()
    {
        var current = Environment.ProcessPath ?? Application.ExecutablePath;

        // Already the installed copy — this is the normal steady-state launch. Just make sure the
        // auto-start entry still points here (survives a manual move or a stale entry).
        if (PathsEqual(current, InstalledExe))
        {
            TryRegisterAutoStart();
            return true;
        }

        // Running from wherever the operator dropped the exe: install, then hand off.
        try
        {
            Directory.CreateDirectory(InstallDir);
            KillInstalledCopy();
            File.Copy(current, InstalledExe, overwrite: true);
            TryRegisterAutoStart();

            Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true });

            ShowPostInstallHelp();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"설치에 실패했습니다.\n\n{ex.Message}\n\n대상 폴더: {InstallDir}",
                "Screen Sender", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        return false;
    }

    private static void TryRegisterAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            // Quoted so a path with spaces is launched as one argument.
            key.SetValue(RunValue, $"\"{InstalledExe}\"");
        }
        catch
        {
            // A missing auto-start is not fatal to a session already running; log and move on.
            Log.Warn("could not register auto-start under HKCU\\...\\Run");
        }
    }

    private static void KillInstalledCopy()
    {
        foreach (var p in Process.GetProcessesByName("ScreenSender"))
        {
            try
            {
                if (p.Id != Environment.ProcessId && PathsEqual(p.MainModule?.FileName, InstalledExe))
                {
                    p.Kill();
                    p.WaitForExit(3000);
                }
            }
            catch
            {
                // Access denied or already gone — the File.Copy below will surface a real problem.
            }
        }
    }

    private static void ShowPostInstallHelp()
    {
        MessageBox.Show(
            "설치 완료. 로그인하면 자동으로 실행됩니다.\n\n" +
            "이 PC를 '항상 로그인 + 항상 깨어있는' 상태로 두어야 원격에서 화면이 보입니다. " +
            "아래 3가지를 직접 설정해 주세요:\n\n" +
            "1. 자동 로그인 — 실행(Win+R)에 netplwiz 입력 → 계정 선택 → " +
            "'사용자 이름과 암호를 입력해야...' 체크 해제.\n" +
            "   (재부팅 시 암호 화면이 아니라 바탕화면까지 저절로 가야 함)\n\n" +
            "2. 절전 끄기 — 설정 > 시스템 > 전원 > 절전 모드 '안 함'.\n" +
            "   (잠든 PC는 앱이 통째로 멈춥니다. 모니터만 꺼지는 건 괜찮음)\n\n" +
            "3. 잠금 끄기 — 설정 > 계정 > 로그인 옵션 > '다시 로그인하도록 요구' 안 함.\n" +
            "   (잠긴 세션은 검은 화면만 캡처됩니다)\n\n" +
            $"로그: {Path.Combine(InstallDir, "sender.log")}",
            "Screen Sender — 설치 완료",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static bool PathsEqual(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
