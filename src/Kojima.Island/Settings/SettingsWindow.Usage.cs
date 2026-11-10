using System.Windows;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using Kojima.Core.Config;
using Kojima.Core.Models;

namespace Kojima.Island.Settings;

/// <summary>AI 用量的来源卡片：每个来源一张卡，密钥/站点就地填写。</summary>
public partial class SettingsWindow
{
    private sealed record UsageCard(string Id, CheckBox Toggle, PasswordBox? Key, TextBox? Site);

    private readonly List<UsageCard> _usageCards = new();

    private void BuildUsageSources(KojimaConfig cfg)
    {
        UsageSourceList.Children.Clear();
        _usageCards.Clear();

        var selected = new HashSet<string>(
            cfg.Usage.Sources ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var info in UsageCatalog.All)
        {
            var card = new Border
            {
                Style = (Style)FindResource("Card"),
                Padding = new Thickness(16, 14, 16, 14),
                Margin = new Thickness(0, 0, 0, 10),
            };
            var body = new StackPanel();

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var badge = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(0, 0, 12, 0),
                Child = new TextBlock
                {
                    Text = info.Name.Length > 0 ? info.Name[..1] : "K",
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            badge.SetResourceReference(Border.BackgroundProperty, "FillBrush");
            ((TextBlock)badge.Child).SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            head.Children.Add(badge);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = info.Name, FontSize = 13.5, FontWeight = FontWeights.SemiBold };
            var desc = new TextBlock { Text = Describe(info), FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0) };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush");
            text.Children.Add(name);
            text.Children.Add(desc);
            Grid.SetColumn(text, 1);
            head.Children.Add(text);

            var toggle = new CheckBox
            {
                Style = (Style)FindResource("Switch"),
                IsChecked = selected.Contains(info.Id),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(toggle, 2);
            head.Children.Add(toggle);
            body.Children.Add(head);

            PasswordBox? key = null;
            if (info.NeedsKey)
            {
                var stored = SecretStore.Get(SecretNames.Usage(info.Id)).Length > 0;
                var hint = new TextBlock
                {
                    Text = stored ? "API Key（已加密保存；留空不改，输入可替换）" : "API Key（不填也能读取本机记录）",
                    Style = (Style)FindResource("Hint"),
                    Margin = new Thickness(0, 12, 0, 6),
                };
                body.Children.Add(hint);
                key = new PasswordBox { Style = (Style)FindResource("PwdField") };
                SetSecret(key, SecretNames.Usage(info.Id));
                body.Children.Add(key);
            }

            TextBox? site = null;
            if (info.NeedsSite)
            {
                var hint = new TextBlock
                {
                    Text = "站点地址（例如 https://api.example.com）",
                    Style = (Style)FindResource("Hint"),
                    Margin = new Thickness(0, 10, 0, 6),
                };
                body.Children.Add(hint);
                site = new TextBox
                {
                    Style = (Style)FindResource("Field"),
                    Text = cfg.Usage.Sites.TryGetValue(info.Id, out var address) ? address ?? "" : "",
                };
                body.Children.Add(site);
            }

            card.Child = body;
            UsageSourceList.Children.Add(card);
            _usageCards.Add(new UsageCard(info.Id, toggle, key, site));
        }
    }

    private static string Describe(UsageSourceInfo info)
    {
        var where = info.Id switch
        {
            "claude" => "读取 ~/.claude 会话日志，并用本机登录换取 5 小时 / 每周额度",
            "codex" => "读取 ~/.codex 会话日志，并读取账户额度",
            "cursor" => "从 Cursor 登录态读取账期额度",
            "antigravity" => "读取 Antigravity 会话数据库里的模型调用记录",
            "dsh" => "读取 ~/.dsh 会话日志里的 token 用量",
            "kimi" => "读取 Kimi Code 会话日志，并读取账户额度",
            "opencode" => "读取 OpenCode 本地数据库里的 token 与费用",
            "zcode" => "读取 ZCode 本地数据库里的模型用量",
            "deepseek" => "读取 API Key 对应账户的余额",
            "openrouter" => "读取 API Key 的额度与今日/本月花费",
            "glm" => "读取智谱开放平台的额度窗口",
            "newapi" => "读取 NewAPI 站点的令牌用量",
            "sub2api" => "读取 Sub2API 站点的额度与订阅用量",
            _ => info.Description,
        };
        return where;
    }

    private void ApplyUsageSettings(KojimaConfig cfg, List<KeyValuePair<string, string?>> secretUpdates)
    {
        var known = UsageCatalog.All.Select(info => info.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = _usageCards.Where(card => card.Toggle.IsChecked == true).Select(card => card.Id).ToList();
        foreach (var extra in cfg.Usage.Sources ?? new List<string>())
        {
            if (!known.Contains(extra) && !sources.Contains(extra, StringComparer.OrdinalIgnoreCase))
            {
                sources.Add(extra);
            }
        }

        cfg.Usage.Sources = sources;
        foreach (var card in _usageCards)
        {
            if (card.Key is not null)
            {
                CollectSecret(secretUpdates, card.Key, SecretNames.Usage(card.Id));
            }

            if (card.Site is not null)
            {
                cfg.Usage.Sites[card.Id] = (card.Site.Text ?? "").Trim();
            }
        }
    }
}
