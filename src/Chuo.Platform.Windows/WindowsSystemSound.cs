using System.Runtime.Versioning;
using Chuo.Core.Platform;

namespace Chuo.Platform.Windows;

/// <summary>Windows のシステム音で通知音を鳴らす。</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemSound : ISystemSound
{
    public void PlayNotification() => Native.MessageBeep(Native.MB_ICONASTERISK);
}
