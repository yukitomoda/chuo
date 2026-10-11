namespace Chuo.Abstractions;

/// <summary>ホスト API のバージョン。<c>plugin.json</c> の <c>hostApi</c> と照合する。</summary>
public static class HostApi
{
    /// <summary>現在のホスト API のバージョン。互換性のない変更をしたらメジャーを上げる。</summary>
    public static readonly Version Version = new(1, 0);
}
