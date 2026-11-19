using System.Diagnostics;
using Chuo.Core.Platform;

namespace Chuo.Platform.Linux;

/// <summary>Linux のデスクトップの通知音を鳴らす。音を出せない環境では何もしない。</summary>
public sealed class LinuxSystemSound : ISystemSound
{
    private static readonly string[] SoundFiles =
    [
        "/usr/share/sounds/freedesktop/stereo/message-new-instant.oga",
        "/usr/share/sounds/freedesktop/stereo/bell.oga",
    ];

    public void PlayNotification()
    {
        if (TryStart("canberra-gtk-play", "-i", "message-new-instant"))
        {
            return;
        }
        foreach (var file in SoundFiles)
        {
            if (File.Exists(file) && TryStart("paplay", file))
            {
                return;
            }
        }
    }

    private static bool TryStart(string fileName, params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo(fileName) { UseShellExecute = false, RedirectStandardError = true };
            foreach (var arg in arguments)
            {
                info.ArgumentList.Add(arg);
            }
            using var process = Process.Start(info);
            return process is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
