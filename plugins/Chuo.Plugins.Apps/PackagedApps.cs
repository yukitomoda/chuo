using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Chuo.Plugins.Apps;

/// <summary>パッケージのアプリ。</summary>
/// <param name="AppUserModelId">AppUserModelID（<c>&lt;パッケージファミリー名&gt;!&lt;アプリの ID&gt;</c>）。</param>
/// <param name="Name">スタートメニューに出る名前。</param>
/// <param name="PackageName">パッケージの名前（例: <c>Microsoft.WindowsCalculator</c>）。</param>
internal sealed record PackagedApp(string AppUserModelId, string Name, string PackageName);

/// <summary>
/// シェルの「アプリ」フォルダ（<c>shell:AppsFolder</c>）から、パッケージのアプリ（Microsoft Store のアプリなど）を列挙する（設計 4.5）。
/// このフォルダにはショートカットのあるアプリも並ぶので、AppUserModelID がパッケージの形のものだけを使う。
/// </summary>
internal static partial class PackagedApps
{
    /// <summary>パッケージファミリー名は「名前_発行元の ID（13文字）」。</summary>
    [GeneratedRegex(@"^(?<name>[^!\\/_]+)_[0-9a-z]{13}![^!\\/]+$", RegexOptions.IgnoreCase)]
    private static partial Regex PackagedIdPattern();

    /// <summary>ユーザーに登録したパッケージのデータのフォルダ。パッケージごとにフォルダがある。</summary>
    public static string DataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");

    /// <summary>起動とアイコンの取得に使うシェルのパス。</summary>
    public static string ShellPath(string appUserModelId) => @"shell:AppsFolder\" + appUserModelId;

    /// <summary>パッケージのアプリの AppUserModelID なら、パッケージの名前を返す。そうでなければ null。</summary>
    public static string? GetPackageName(string appUserModelId) =>
        PackagedIdPattern().Match(appUserModelId) is { Success: true } match ? match.Groups["name"].Value : null;

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<PackagedApp> Enumerate()
    {
        var apps = new List<PackagedApp>();
        foreach (var (id, name) in Shell.EnumerateAppsFolder())
        {
            if (GetPackageName(id) is { } packageName && name.Length > 0)
            {
                apps.Add(new PackagedApp(id, name, packageName));
            }
        }
        return apps;
    }

    /// <summary>シェルの COM インターフェイスを vtable で直接呼ぶ。</summary>
    [SupportedOSPlatform("windows")]
    private static unsafe partial class Shell
    {
        private static readonly Guid FOLDERID_AppsFolder = new("1e87508d-89c2-42f0-8a7e-645a0f50ca58");
        private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
        private static readonly Guid IID_IEnumShellItems = new("70629033-e363-4a28-a567-0db78006e6d7");
        private static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");

        private const int SIGDN_NORMALDISPLAY = 0;
        private const int SIGDN_PARENTRELATIVEPARSING = unchecked((int)0x80018001);
        private const uint COINIT_MULTITHREADED = 0;

        [LibraryImport("ole32.dll")]
        private static partial int CoInitializeEx(nint reserved, uint coInit);

        [LibraryImport("ole32.dll")]
        private static partial void CoUninitialize();

        [LibraryImport("shell32.dll")]
        private static partial int SHGetKnownFolderItem(in Guid rfid, uint flags, nint token, in Guid riid, out nint ppv);

        /// <summary>フォルダの中の項目の、AppUserModelID と表示名。</summary>
        public static List<(string Id, string Name)> EnumerateAppsFolder()
        {
            var result = new List<(string, string)>();
            // すでに STA で初期化されたスレッドなら失敗するが、そのまま使える
            var initialized = CoInitializeEx(0, COINIT_MULTITHREADED) >= 0;
            try
            {
                Marshal.ThrowExceptionForHR(SHGetKnownFolderItem(FOLDERID_AppsFolder, 0, 0, IID_IShellItem, out var folder));
                nint enumerator;
                try
                {
                    Marshal.ThrowExceptionForHR(BindToHandler(folder, BHID_EnumItems, IID_IEnumShellItems, out enumerator));
                }
                finally
                {
                    Marshal.Release(folder);
                }

                try
                {
                    while (Next(enumerator, out var item) == 0)
                    {
                        try
                        {
                            if (GetDisplayName(item, SIGDN_PARENTRELATIVEPARSING) is { } id
                                && GetDisplayName(item, SIGDN_NORMALDISPLAY) is { } name)
                            {
                                result.Add((id, name));
                            }
                        }
                        finally
                        {
                            Marshal.Release(item);
                        }
                    }
                }
                finally
                {
                    Marshal.Release(enumerator);
                }
            }
            finally
            {
                if (initialized)
                {
                    CoUninitialize();
                }
            }
            return result;
        }

        /// <summary>IShellItem::BindToHandler（vtable の 4 番目）。</summary>
        private static int BindToHandler(nint item, in Guid bhid, in Guid riid, out nint ppv)
        {
            var bindToHandler = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, Guid*, nint*, int>)(*(nint**)item)[3];
            nint result;
            int hr;
            fixed (Guid* pBhid = &bhid, pRiid = &riid)
            {
                hr = bindToHandler(item, 0, pBhid, pRiid, &result);
            }
            ppv = result;
            return hr;
        }

        /// <summary>IShellItem::GetDisplayName（vtable の 6 番目）。</summary>
        private static string? GetDisplayName(nint item, int sigdn)
        {
            var getDisplayName = (delegate* unmanaged[Stdcall]<nint, int, char**, int>)(*(nint**)item)[5];
            char* name;
            if (getDisplayName(item, sigdn, &name) < 0)
            {
                return null;
            }
            try
            {
                return new string(name);
            }
            finally
            {
                Marshal.FreeCoTaskMem((nint)name);
            }
        }

        /// <summary>IEnumShellItems::Next（vtable の 4 番目）で1件ずつ取り出す。終わりなら S_FALSE（1）を返す。</summary>
        private static int Next(nint enumerator, out nint item)
        {
            var next = (delegate* unmanaged[Stdcall]<nint, uint, nint*, uint*, int>)(*(nint**)enumerator)[3];
            nint result = 0;
            uint fetched;
            var hr = next(enumerator, 1, &result, &fetched);
            item = result;
            return hr == 0 && fetched == 1 ? 0 : 1;
        }
    }
}
