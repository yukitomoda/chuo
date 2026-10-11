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
}
