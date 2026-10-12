using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Chuo.Plugins.Shared;

/// <summary>ショートカット（.lnk）から読み取った情報。</summary>
/// <param name="TargetPath">リンク先のパス。MSI でインストールしたアプリのショートカットなど、パスを持たないものは null。</param>
/// <param name="Arguments">コマンドライン引数。</param>
internal sealed record ShellLinkInfo(string? TargetPath, string? Arguments);

/// <summary>
/// ショートカット（.lnk）を読む。COM を使わずにファイルの形式（MS-SHLLINK）を直接読むので、どのスレッドからでも速く読める。
/// </summary>
internal static class ShellLink
{
    private const int HeaderSize = 0x4C;
    private const uint EnvironmentVariableDataBlockSignature = 0xA0000001;

    [Flags]
    private enum LinkFlags : uint
    {
        HasLinkTargetIdList = 0x1,
        HasLinkInfo = 0x2,
        HasName = 0x4,
        HasRelativePath = 0x8,
        HasWorkingDir = 0x10,
        HasArguments = 0x20,
        HasIconLocation = 0x40,
        IsUnicode = 0x80,
    }

    /// <summary>StringData の各文字列。ファイル内ではこの順に並ぶ。</summary>
    private static readonly LinkFlags[] StringDataFlags =
        [LinkFlags.HasName, LinkFlags.HasRelativePath, LinkFlags.HasWorkingDir, LinkFlags.HasArguments, LinkFlags.HasIconLocation];

    static ShellLink() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static ShellLinkInfo? TryRead(string path)
    {
        try
        {
            return Parse(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>ショートカットの内容を読む。形式が正しくなければ null を返す。</summary>
    public static ShellLinkInfo? Parse(ReadOnlySpan<byte> data)
    {
        try
        {
            return ParseCore(data);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static ShellLinkInfo? ParseCore(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || ReadUInt32(data, 0) != HeaderSize)
        {
            return null;
        }
        var flags = (LinkFlags)ReadUInt32(data, 0x14);
        var offset = HeaderSize;

        if (flags.HasFlag(LinkFlags.HasLinkTargetIdList))
        {
            offset += 2 + ReadUInt16(data, offset);
        }

        string? target = null;
        if (flags.HasFlag(LinkFlags.HasLinkInfo))
        {
            var size = (int)ReadUInt32(data, offset);
            target = ReadLinkInfo(data.Slice(offset, size));
            offset += size;
        }

        var unicode = flags.HasFlag(LinkFlags.IsUnicode);
        string? arguments = null;
        foreach (var flag in StringDataFlags)
        {
            if (!flags.HasFlag(flag))
            {
                continue;
            }
            var length = ReadUInt16(data, offset) * (unicode ? 2 : 1);
            var bytes = data.Slice(offset + 2, length);
            if (flag == LinkFlags.HasArguments)
            {
                arguments = unicode ? Encoding.Unicode.GetString(bytes) : AnsiEncoding.GetString(bytes);
            }
            offset += 2 + length;
        }

        // パスを LinkInfo に持たないショートカットは、環境変数を含むパスを ExtraData に持つことがある
        target ??= ReadEnvironmentTarget(data[offset..]);
        return new ShellLinkInfo(string.IsNullOrEmpty(target) ? null : target, string.IsNullOrEmpty(arguments) ? null : arguments);
    }

    private static string? ReadLinkInfo(ReadOnlySpan<byte> info)
    {
        var headerSize = ReadUInt32(info, 4);
        var infoFlags = ReadUInt32(info, 8);
        // VolumeIDAndLocalBasePath がなければ、ネットワーク上のパスなので扱わない
        if ((infoFlags & 0x1) == 0)
        {
            return null;
        }

        if (headerSize >= 0x24)
        {
            return ReadUtf16Z(At(info, 0x1C)) + ReadUtf16Z(At(info, 0x20));
        }
        return ReadAnsiZ(At(info, 0x10)) + ReadAnsiZ(At(info, 0x18));
    }

    private static string? ReadEnvironmentTarget(ReadOnlySpan<byte> extraData)
    {
        var offset = 0;
        while (offset + 8 <= extraData.Length)
        {
            var size = (int)ReadUInt32(extraData, offset);
            if (size < 8)
            {
                break;
            }
            if (ReadUInt32(extraData, offset + 4) == EnvironmentVariableDataBlockSignature && size >= 0x314)
            {
                var block = extraData.Slice(offset, size);
                var target = ReadUtf16Z(block.Slice(8 + 260, 520));
                if (target.Length == 0)
                {
                    target = ReadAnsiZ(block.Slice(8, 260));
                }
                return Environment.ExpandEnvironmentVariables(target);
            }
            offset += size;
        }
        return null;
    }

    /// <summary>ANSI の文字列はシステムのコードページ（日本語の Windows では CP932）で書かれている。</summary>
    private static Encoding AnsiEncoding =>
        field ??= Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);

    /// <summary>LinkInfo の中で、指定した位置にあるオフセットが指す先。オフセットが 0（文字列がない）なら空。</summary>
    private static ReadOnlySpan<byte> At(ReadOnlySpan<byte> info, int offsetField)
    {
        var offset = (int)ReadUInt32(info, offsetField);
        return offset == 0 ? [] : info[offset..];
    }

    private static string ReadUtf16Z(ReadOnlySpan<byte> span)
    {
        var length = 0;
        while (length + 1 < span.Length && (span[length] != 0 || span[length + 1] != 0))
        {
            length += 2;
        }
        return Encoding.Unicode.GetString(span[..length]);
    }

    private static string ReadAnsiZ(ReadOnlySpan<byte> span)
    {
        var length = span.IndexOf((byte)0);
        return AnsiEncoding.GetString(length < 0 ? span : span[..length]);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
}
