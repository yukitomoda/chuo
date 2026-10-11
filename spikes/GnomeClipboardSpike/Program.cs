// 技術検証 #4：GNOME Shell 拡張から D-Bus でクリップボードの変化を受け取る。
//   dotnet run                       変化を待ち受け、中身を取得して記録する
//   dotnet run -- get-text            拡張経由でクリップボードのテキストを取得する
//   dotnet run -- set-text <文字列>   拡張経由でクリップボードにテキストを設定する
//   dotnet run -- set-image <PNG>     拡張経由でクリップボードに画像を設定する
using System.Diagnostics;
using System.Text;
using Tmds.DBus.Protocol;

const string BusName = "chuo.Spike";
const string ObjectPath = "/chuo/Spike";
const string Interface = "chuo.Spike.Clipboard";

using var connection = new DBusConnection(DBusAddress.Session!);
await connection.ConnectAsync();

switch (args)
{
    case ["get-text"]:
        Console.WriteLine(await GetTextAsync());
        return;
    case ["set-text", var text]:
        await SetContentAsync("text/plain;charset=utf-8", Encoding.UTF8.GetBytes(text));
        return;
    case ["set-image", var path]:
        await SetContentAsync("image/png", await File.ReadAllBytesAsync(path));
        return;
}

var outDir = Path.Combine(Path.GetTempPath(), "chuo-spike-clipboard");
Directory.CreateDirectory(outDir);
Log($"待ち受けを開始（画像の保存先: {outDir}）");

await connection.WatchSignalAsync(BusName, ObjectPath, Interface, "Changed",
    static (Message m, object? _) =>
    {
        var reader = m.GetBodyReader();
        return (Serial: reader.ReadUInt32(), MimeTypes: reader.ReadArrayOfString(), RealtimeUs: reader.ReadInt64());
    },
    async (Notification<(uint Serial, string[] MimeTypes, long RealtimeUs)> n) =>
    {
        if (!n.HasValue)
        {
            Log($"通知が止まった: {n.Type} {n.Exception?.Message}");
            return;
        }
        // GLib の monotonic time と Stopwatch は起点が異なったので、実時刻（UNIX 時刻の µs）で比べる
        var receivedUs = (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10;
        var (serial, mimeTypes, sentUs) = n.Value;
        Log($"changed #{serial}: delay={(receivedUs - sentUs) / 1000.0:F2}ms mime=[{string.Join(", ", mimeTypes)}]");
        try
        {
            await FetchAsync(serial, mimeTypes);
        }
        catch (Exception ex)
        {
            Log($"  取得に失敗: {ex.Message}");
        }
    },
    ObserverFlags.None, emitOnCapturedContext: false);

await connection.DisconnectedAsync();

async Task FetchAsync(uint serial, string[] mimeTypes)
{
    // パスワードマネージャーが付ける印。履歴に残さない
    if (mimeTypes.Contains("x-kde-passwordManagerHint"))
    {
        Log("  パスワードのため無視");
        return;
    }
    var sw = Stopwatch.StartNew();
    if (mimeTypes.FirstOrDefault(t => t.StartsWith("image/png")) is { } imageType)
    {
        var data = await GetContentAsync(imageType);
        var file = Path.Combine(outDir, $"{serial}.png");
        await File.WriteAllBytesAsync(file, data);
        var isPng = data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']);
        Log($"  image: {data.Length:N0} bytes png={isPng} fetch={sw.Elapsed.TotalMilliseconds:F2}ms -> {file}");
    }
    else if (mimeTypes.Any(t => t.StartsWith("text/plain") || t is "UTF8_STRING" or "STRING"))
    {
        var text = await GetTextAsync();
        var preview = text.Length > 40 ? text[..40] + "…" : text;
        Log($"  text: {text.Length} chars fetch={sw.Elapsed.TotalMilliseconds:F2}ms \"{preview.ReplaceLineEndings("⏎")}\"");
    }
    else
    {
        Log("  対象外の形式");
    }
}

Task<string> GetTextAsync() =>
    connection.CallMethodAsync(CreateCall("GetText", null, null),
        static (Message m, object? _) => m.GetBodyReader().ReadString(), null);

Task<byte[]> GetContentAsync(string mimeType) =>
    connection.CallMethodAsync(CreateCall("GetContent", "s", (ref MessageWriter w) => w.WriteString(mimeType)),
        static (Message m, object? _) => m.GetBodyReader().ReadArrayOfByte(), null);

Task SetContentAsync(string mimeType, byte[] data) =>
    connection.CallMethodAsync(CreateCall("SetContent", "say", (ref MessageWriter w) =>
    {
        w.WriteString(mimeType);
        w.WriteArray(data);
    }));

MessageBuffer CreateCall(string member, string? signature, BodyWriter? writeBody)
{
    var writer = connection.GetMessageWriter();
    try
    {
        writer.WriteMethodCallHeader(BusName, ObjectPath, Interface, member, signature, MessageFlags.None);
        writeBody?.Invoke(ref writer);
        return writer.CreateMessage();
    }
    finally
    {
        writer.Dispose();
    }
}

static void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");

// MessageWriter は構造体なので、Action<MessageWriter> に渡すとコピーに書き込まれ、
// ヘッダーの本文の長さと食い違ってバスに切断される。ref で渡す
delegate void BodyWriter(ref MessageWriter writer);
