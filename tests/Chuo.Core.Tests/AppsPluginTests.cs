using System.Buffers.Binary;
using System.Text;
using Chuo.Plugins.Apps;

namespace Chuo.Core.Tests;

public class ShellLinkTests
{
    [Fact]
    public void LinkInfo_のUnicodeのパスと引数を読む()
    {
        var data = new ShellLinkBuilder { UnicodePath = @"C:\Program Files\アプリ\app.exe", Arguments = "--flag" }.Build();

        var info = ShellLink.Parse(data);

        Assert.Equal(@"C:\Program Files\アプリ\app.exe", info?.TargetPath);
        Assert.Equal("--flag", info?.Arguments);
    }

    [Fact]
    public void LinkInfo_のANSIのパスを読む()
    {
        var data = new ShellLinkBuilder { AnsiPath = @"C:\Windows\notepad.exe", WithIdList = true }.Build();

        Assert.Equal(@"C:\Windows\notepad.exe", ShellLink.Parse(data)?.TargetPath);
    }

    [Fact]
    public void LinkInfo_がなければ環境変数のブロックのパスを使う()
    {
        Environment.SetEnvironmentVariable("CHUO_TEST_DIR", "/opt/test");
        var data = new ShellLinkBuilder { EnvironmentPath = "%CHUO_TEST_DIR%/tool.exe", Arguments = "-x" }.Build();

        Assert.Equal("/opt/test/tool.exe", ShellLink.Parse(data)?.TargetPath);
    }

    [Fact]
    public void パスがなければ_TargetPath_は_null()
    {
        var info = ShellLink.Parse(new ShellLinkBuilder().Build());

        Assert.NotNull(info);
        Assert.Null(info.TargetPath);
    }

    [Fact]
    public void 壊れたファイルは_null()
    {
        Assert.Null(ShellLink.Parse([1, 2, 3]));
        var truncated = new ShellLinkBuilder { UnicodePath = @"C:\a.exe" }.Build()[..100];
        Assert.Null(ShellLink.Parse(truncated));
    }

    /// <summary>テスト用に MS-SHLLINK の形式のバイト列を作る。</summary>
    private sealed class ShellLinkBuilder
    {
        public bool WithIdList { get; init; }
        public string? UnicodePath { get; init; }
        public string? AnsiPath { get; init; }
        public string? Arguments { get; init; }
        public string? EnvironmentPath { get; init; }

        public byte[] Build()
        {
            var flags = 0x80u; // IsUnicode
            var body = new List<byte>();
            if (WithIdList)
            {
                flags |= 0x1;
                body.AddRange(U16(4));
                body.AddRange(new byte[] { 2, 0, 0, 0 });
            }
            if (UnicodePath is not null || AnsiPath is not null)
            {
                flags |= 0x2;
                body.AddRange(LinkInfo());
            }
            body.AddRange(U16(0)); // 使わないが、StringData の前の位置がずれないことを確かめるため Name を空で入れる
            flags |= 0x4;
            if (Arguments is not null)
            {
                flags |= 0x20;
                body.AddRange(U16(Arguments.Length));
                body.AddRange(Encoding.Unicode.GetBytes(Arguments));
            }
            if (EnvironmentPath is not null)
            {
                var block = new byte[0x314];
                BinaryPrimitives.WriteUInt32LittleEndian(block, 0x314);
                BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), 0xA0000001);
                Encoding.Unicode.GetBytes(EnvironmentPath).CopyTo(block, 8 + 260);
                body.AddRange(block);
            }
            body.AddRange(U32(0)); // TerminalBlock

            var header = new byte[0x4C];
            BinaryPrimitives.WriteUInt32LittleEndian(header, 0x4C);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x14), flags);
            return [.. header, .. body];
        }

        private byte[] LinkInfo()
        {
            var unicode = UnicodePath is not null;
            var headerSize = unicode ? 0x24 : 0x1C;
            var volumeId = new byte[] { 0x10, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0x10, 0, 0, 0 };
            var ansi = Encoding.ASCII.GetBytes((AnsiPath ?? "") + "\0");
            var unicodeBytes = unicode ? Encoding.Unicode.GetBytes(UnicodePath + "\0") : [];
            var suffix = new byte[] { 0 };
            var suffixUnicode = unicode ? new byte[] { 0, 0 } : [];

            var volumeIdOffset = headerSize;
            var localBasePathOffset = volumeIdOffset + volumeId.Length;
            var suffixOffset = localBasePathOffset + ansi.Length;
            var unicodeOffset = suffixOffset + suffix.Length;
            var suffixUnicodeOffset = unicodeOffset + unicodeBytes.Length;
            var size = suffixUnicodeOffset + suffixUnicode.Length;

            var result = new List<byte>();
            result.AddRange(U32(size));
            result.AddRange(U32(headerSize));
            result.AddRange(U32(1)); // VolumeIDAndLocalBasePath
            result.AddRange(U32(volumeIdOffset));
            result.AddRange(U32(localBasePathOffset));
            result.AddRange(U32(0));
            result.AddRange(U32(suffixOffset));
            if (unicode)
            {
                result.AddRange(U32(unicodeOffset));
                result.AddRange(U32(suffixUnicodeOffset));
            }
            result.AddRange(volumeId);
            result.AddRange(ansi);
            result.AddRange(suffix);
            result.AddRange(unicodeBytes);
            result.AddRange(suffixUnicode);
            return [.. result];
        }

        private static byte[] U16(int value)
        {
            var bytes = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)value);
            return bytes;
        }

        private static byte[] U32(int value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)value);
            return bytes;
        }
    }
}

public class DesktopEntryTests
{
    private const string Sample = """
        [Desktop Entry]
        Type=Application
        Name=Text Editor
        Name[ja]=テキストエディター
        Name[ja_JP]=テキストエディター（日本）
        Comment=Edit text files\sfast
        Keywords=text;plain\;editor;;
        Exec=gnome-text-editor %U
        NoDisplay=false

        [Desktop Action new-window]
        Name=New Window
        Exec=gnome-text-editor --new-window
        """;

    private static DesktopEntry Parse() => DesktopEntry.Parse(new StringReader(Sample));

    [Fact]
    public void メインのグループの値だけを読む()
    {
        var entry = Parse();

        Assert.Equal("gnome-text-editor %U", entry.Get("Exec"));
        Assert.Equal("Application", entry.Get("Type"));
        Assert.False(entry.GetBool("NoDisplay"));
    }

    [Theory]
    [InlineData("ja_JP.UTF-8", "テキストエディター（日本）")]
    [InlineData("ja_JP.UTF-8@modifier", "テキストエディター（日本）")]
    [InlineData("ja", "テキストエディター")]
    [InlineData("ja_XX.UTF-8", "テキストエディター")]
    [InlineData("en_US.UTF-8", "Text Editor")]
    [InlineData("C", "Text Editor")]
    [InlineData(null, "Text Editor")]
    public void ロケールに合わせた値を選ぶ(string? locale, string expected) =>
        Assert.Equal(expected, Parse().GetLocalized("Name", locale));

    [Fact]
    public void エスケープを解く() => Assert.Equal("Edit text files fast", Parse().Get("Comment"));

    [Fact]
    public void リストを区切る() =>
        Assert.Equal(["text", "plain;editor"], DesktopEntry.SplitList(Parse().Get("Keywords")));
}

public class DesktopExecTests
{
    [Theory]
    [InlineData("firefox %u", new[] { "firefox" })]
    [InlineData("code --new-window %F", new[] { "code", "--new-window" })]
    [InlineData("\"/opt/My App/app\" --name=%c", new[] { "/opt/My App/app", "--name=App" })]
    [InlineData("sh -c \"echo \\\"hi\\\" \\$HOME\"", new[] { "sh", "-c", "echo \"hi\" $HOME" })]
    [InlineData("app %i", new[] { "app", "--icon", "app-icon" })]
    [InlineData("app 100%%", new[] { "app", "100%" })]
    [InlineData("app  --a   --b", new[] { "app", "--a", "--b" })]
    [InlineData("app \"\"", new[] { "app", "" })]
    public void 引数を区切りフィールドコードを展開する(string exec, string[] expected) =>
        Assert.Equal(expected, DesktopExec.Parse(exec, "App", "app-icon", "/usr/share/applications/app.desktop"));

    [Fact]
    public void デスクトップファイルのパスを展開する() =>
        Assert.Equal(["app", "/a/app.desktop"], DesktopExec.Parse("app %k", "App", null, "/a/app.desktop"));

    [Fact]
    public void アイコンがなければ_i_は取り除く() =>
        Assert.Equal(["app"], DesktopExec.Parse("app %i", "App", null, "/a/app.desktop"));

    [Theory]
    [InlineData("app \"unterminated")]
    [InlineData("%f")]
    [InlineData("")]
    public void 実行できない値は_null(string exec) =>
        Assert.Null(DesktopExec.Parse(exec, "App", null, "/a/app.desktop"));
}
