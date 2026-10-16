using System.Runtime.Versioning;
using Chuo.Core.Platform;
using Microsoft.Win32;

namespace Chuo.Platform.Windows;

/// <summary>
/// ユーザーの Run キー（<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>）に登録して、サインイン時に起動する。
/// 管理者の権限は要らない。タスクマネージャーの「スタートアップ アプリ」で無効にした状態（StartupApproved）にも従う。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AutoStart : IAutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    /// <summary>タスクマネージャーでの有効と無効。値の先頭のバイトが奇数なら無効。</summary>
    private const string StartupApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "chuo";

    public bool IsSupported => true;

    public string CurrentPath { get; } = Environment.ProcessPath ?? "";

    public string? GetRegisteredPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(ValueName) is not string command)
        {
            return null;
        }
        using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKey);
        if (approved?.GetValue(ValueName) is byte[] { Length: > 0 } state && (state[0] & 1) == 1)
        {
            return null;
        }
        return RunCommand.ParseExecutable(command);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            // 引数を付けずに起動すると、ランチャーを隠したまま常駐する
            key.SetValue(ValueName, $"\"{CurrentPath}\"");
            // タスクマネージャーで無効にしてあれば、その記録を消して有効に戻す
            using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKey, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

/// <summary>Run キーに書くコマンドライン。</summary>
internal static class RunCommand
{
    /// <summary>コマンドラインから実行ファイルのパスを取り出す（引用符で囲んでいれば、その中）。</summary>
    internal static string ParseExecutable(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end < 0 ? command[1..] : command[1..end];
        }
        var space = command.IndexOf(' ');
        return space < 0 ? command : command[..space];
    }
}
