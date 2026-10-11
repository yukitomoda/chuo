using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Chuo.Abstractions;
using Chuo.Core.Platform;
using Microsoft.Extensions.Logging;

namespace Chuo.App;

/// <summary>
/// 項目のアイコンを読み込み、使い回す。UI スレッドから使う。
/// 読み込みは UI スレッドの外で行うので、入力や一覧の描画を待たせない。
/// </summary>
public sealed class IconCache(IFileIconProvider fileIcons, ILogger<IconCache> logger)
{
    /// <summary>表示は 32px なので、高 DPI の画面でもぼけないよう 64px で持つ。</summary>
    private const int IconPixels = 64;

    private const int MaxEntries = 512;

    private readonly Dictionary<IconSource, Bitmap?> _cache = [];
    private readonly Dictionary<IconSource, Task<Bitmap?>> _loading = [];

    /// <summary>読み込み済みのアイコンを返す。読み込み済みでなければ false。</summary>
    public bool TryGet(IconSource source, out Bitmap? bitmap) => _cache.TryGetValue(source, out bitmap);

    /// <summary>アイコンを読み込む。読み込めなければ null を返す（例外は投げない）。</summary>
    public Task<Bitmap?> LoadAsync(IconSource source)
    {
        if (_cache.TryGetValue(source, out var cached))
        {
            return Task.FromResult(cached);
        }
        if (!_loading.TryGetValue(source, out var loading))
        {
            loading = LoadAndStoreAsync(source);
            if (!loading.IsCompleted)
            {
                _loading[source] = loading;
            }
        }
        return loading;
    }

    private async Task<Bitmap?> LoadAndStoreAsync(IconSource source)
    {
        Bitmap? bitmap;
        try
        {
            bitmap = await LoadCoreAsync(source);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "アイコンを読み込めなかった: {Source}", source);
            bitmap = null;
        }

        // UI スレッドに戻ってから更新する
        _loading.Remove(source);
        if (_cache.Count >= MaxEntries)
        {
            // TODO: 使われていないものから捨てる。Bitmap は表示中かもしれないので、ここでは破棄しない
            _cache.Clear();
        }
        _cache[source] = bitmap;
        return bitmap;
    }

    private async Task<Bitmap?> LoadCoreAsync(IconSource source)
    {
        switch (source)
        {
            case ImageFileIconSource image:
                return await Task.Run(() =>
                {
                    using var stream = File.OpenRead(image.Path);
                    return Bitmap.DecodeToWidth(stream, IconPixels);
                });
            case ImageBytesIconSource bytes:
                return await Task.Run(() =>
                {
                    using var stream = new MemoryStream(bytes.Data.ToArray());
                    return Bitmap.DecodeToWidth(stream, IconPixels);
                });
            case FileIconSource file:
                var pixels = await fileIcons.GetIconAsync(file.Path, IconPixels);
                return pixels is null ? null : CreateBitmap(pixels);
            default:
                return null;
        }
    }

    private static Bitmap CreateBitmap(IconPixels pixels)
    {
        var handle = GCHandle.Alloc(pixels.Bgra, GCHandleType.Pinned);
        try
        {
            // Bitmap は画素をコピーして持つので、固定はこの間だけでよい
            return new Bitmap(
                PixelFormat.Bgra8888, AlphaFormat.Premul, handle.AddrOfPinnedObject(),
                new PixelSize(pixels.Width, pixels.Height), new Vector(96, 96), pixels.Width * 4);
        }
        finally
        {
            handle.Free();
        }
    }
}
