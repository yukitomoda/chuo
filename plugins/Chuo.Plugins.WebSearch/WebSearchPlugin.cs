using System.ComponentModel.DataAnnotations;
using Chuo.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Chuo.Plugins.WebSearch;

public sealed class WebSearchOptions
{
    [Display(Name = "検索エンジンの名前")]
    public string EngineName { get; set; } = "Google";

    [Display(Name = "検索の URL", Description = "{0} が検索の文字列に置き換わる")]
    public string UrlTemplate { get; set; } = "https://www.google.com/search?q={0}";
}

public sealed class WebSearchPlugin : IChuoPlugin
{
    public void ConfigureServices(IServiceCollection services, IPluginContext context) =>
        services.Configure<WebSearchOptions>(context.Configuration);
}

/// <summary>キーワードの後の文字列で Web 検索する（例: <c>g rust</c>）。</summary>
[LiveQueryProvider(Keyword = "g")]
public sealed class WebSearchProvider(IOptionsMonitor<WebSearchOptions> options) : ILiveQueryProvider
{
    public ValueTask<IReadOnlyList<ResultItem>> QueryAsync(Query query, CancellationToken cancellationToken)
    {
        var text = query.Text.Trim();
        if (text.Length == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<ResultItem>>([]);
        }

        var current = options.CurrentValue;
        var url = string.Format(current.UrlTemplate, Uri.EscapeDataString(text));
        ResultItem item = new()
        {
            Id = "search",
            Title = $"{current.EngineName} で「{text}」を検索",
            Subtitle = url,
            Actions = [ItemAction.Open(url), ItemAction.CopyText(url, "URL をコピー")],
        };
        return ValueTask.FromResult<IReadOnlyList<ResultItem>>([item]);
    }
}
