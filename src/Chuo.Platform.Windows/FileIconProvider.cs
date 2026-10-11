using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Chuo.Core.Platform;
using Microsoft.Extensions.Logging;
using static Chuo.Platform.Windows.Native;

namespace Chuo.Platform.Windows;

/// <summary>
/// IShellItemImageFactory でファイルのアイコンを取得する。
/// シェルの COM オブジェクトは STA で使うのが安全なので、STA の専用スレッドで1件ずつ処理する。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FileIconProvider : IFileIconProvider, IDisposable
{
    private readonly BlockingCollection<Request> _requests = [];
    private readonly ILogger<FileIconProvider> _logger;

    public FileIconProvider(ILogger<FileIconProvider> logger)
    {
        _logger = logger;
        var thread = new Thread(Run) { IsBackground = true, Name = "FileIcon" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public Task<IconPixels?> GetIconAsync(string path, int size, CancellationToken cancellationToken = default)
    {
        var request = new Request(path, size, cancellationToken);
        _requests.Add(request, cancellationToken);
        return request.Completion.Task;
    }

    public void Dispose() => _requests.CompleteAdding();

    private void Run()
    {
        foreach (var request in _requests.GetConsumingEnumerable())
        {
            if (request.CancellationToken.IsCancellationRequested)
            {
                request.Completion.TrySetCanceled(request.CancellationToken);
                continue;
            }
            try
            {
                request.Completion.TrySetResult(Load(request.Path, request.Size));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "アイコンを取得できなかった: {Path}", request.Path);
                request.Completion.TrySetResult(null);
            }
        }
    }

    private static unsafe IconPixels? Load(string path, int size)
    {
        if (SHCreateItemFromParsingName(path, 0, IID_IShellItemImageFactory, out var factory) < 0)
        {
            return null;
        }

        nint bitmap;
        try
        {
            if (ShellItemImageFactoryGetImage(factory, new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY, out bitmap) < 0)
            {
                return null;
            }
        }
        finally
        {
            Marshal.Release(factory);
        }

        try
        {
            if (GetObject(bitmap, sizeof(BITMAP), out var info) == 0)
            {
                return null;
            }
            var width = info.bmWidth;
            var height = Math.Abs(info.bmHeight);
            var pixels = new byte[width * height * 4];
            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = width,
                // 負の高さを指定すると、上の行から順に受け取れる
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
            };

            var dc = GetDC(0);
            try
            {
                fixed (byte* p = pixels)
                {
                    if (GetDIBits(dc, bitmap, 0, (uint)height, p, ref header, DIB_RGB_COLORS) == 0)
                    {
                        return null;
                    }
                }
            }
            finally
            {
                _ = ReleaseDC(0, dc);
            }

            MakeOpaqueIfNoAlpha(pixels);
            return new IconPixels(width, height, pixels);
        }
        finally
        {
            DeleteObject(bitmap);
        }
    }

    /// <summary>アルファチャネルを持たない画像は、アルファがすべて 0 で返るので不透明にする。</summary>
    private static void MakeOpaqueIfNoAlpha(byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                return;
            }
        }
        for (var i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 0xFF;
        }
    }

    private sealed record Request(string Path, int Size, CancellationToken CancellationToken)
    {
        public TaskCompletionSource<IconPixels?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
