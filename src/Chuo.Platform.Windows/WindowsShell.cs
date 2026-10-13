using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Chuo.Abstractions;

namespace Chuo.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsShell : IShell
{
    public void Open(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();

    public void ShowInFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { $"/select,{path}" } })?.Dispose();

    /// <summary>UAC の確認でユーザーが「いいえ」を選んだ（ERROR_CANCELLED）。</summary>
    private const int ErrorCancelled = 1223;

    public void RunAsAdministrator(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" })?.Dispose();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
        }
    }
}
