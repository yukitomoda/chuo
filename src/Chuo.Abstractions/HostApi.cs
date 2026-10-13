namespace Chuo.Abstractions;

/// <summary>ホスト API のバージョン。<c>plugin.json</c> の <c>hostApi</c> と照合する。</summary>
public static class HostApi
{
    /// <summary>現在のホスト API のバージョン。互換性のない変更をしたらメジャーを、API を足したらマイナーを上げる。
    /// 1.1：<see cref="IShell.RunAsAdministrator"/>、<see cref="ItemAction.Kind"/>。</summary>
    public static readonly Version Version = new(1, 1);
}
