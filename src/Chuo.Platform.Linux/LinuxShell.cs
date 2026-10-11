using System.Diagnostics;
using Chuo.Abstractions;

namespace Chuo.Platform.Linux;

public sealed class LinuxShell : IShell
{
    public void Open(string target) =>
        Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { target }, UseShellExecute = false })?.Dispose();

    // TODO: org.freedesktop.FileManager1.ShowItems で、ファイルを選択した状態で開く
    public void ShowInFolder(string path) => Open(Path.GetDirectoryName(path) ?? path);
}
