using Avalonia.Media.Imaging;
using Chuo.Abstractions;
using Microsoft.Extensions.Logging;

namespace Chuo.App;

/// <summary>項目のアイコンを読み込み、使い回す。UI スレッドから使う。</summary>
public sealed class IconCache(ILogger<IconCache> logger)
{
    /// <summary>表示は 32px なので、高 DPI の画面でもぼけないよう 64px で持つ。</summary>
    private const int DecodeWidth = 64;

    private const int MaxEntries = 512;

    private readonly Dictionary<IconSource, Bitmap?> _cache = [];

    public Bitmap? Get(IconSource? source)
    {
        if (source is null)
        {
            return null;
        }
        if (_cache.TryGetValue(source, out var cached))
        {
            return cached;
        }

        if (_cache.Count >= MaxEntries)
        {
            // TODO: 使われていないものから捨てる。Bitmap は表示中かもしれないので、ここでは破棄しない
            _cache.Clear();
        }
        var bitmap = Load(source);
        _cache[source] = bitmap;
        return bitmap;
    }

    private Bitmap? Load(IconSource source)
    {
        try
        {
            switch (source)
            {
                case ImageFileIconSource image:
                    using (var stream = File.OpenRead(image.Path))
                    {
                        return Bitmap.DecodeToWidth(stream, DecodeWidth);
                    }
                case ImageBytesIconSource bytes:
                    using (var stream = new MemoryStream(bytes.Data.ToArray()))
                    {
                        return Bitmap.DecodeToWidth(stream, DecodeWidth);
                    }
                default:
                    // TODO: FileIconSource は OS ごとの実装でファイルのアイコンを取得する（アプリ起動のプラグインで必要）
                    return null;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "アイコンを読み込めなかった: {Source}", source);
            return null;
        }
    }
}
