using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using Kojima.Core.Config;

namespace Kojima.Core.Services;

/// <summary>
/// 翻译引擎：自动 / 免费通道（MyMemory → Google）/ DeepSeek。
/// 与界面无关，便于单独测试与复用。
/// </summary>
[SupportedOSPlatform("windows")]
public static class TranslationService
{
    public const string DeepSeekEndpoint = "https://api.deepseek.com/chat/completions";
    public const string SystemPrompt = "你是专业翻译，只输出译文";
    public const string NoKeyText = "未配置 DeepSeek API Key：设置 → 用量 → 来源密钥 里填入后即可翻译";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>按设置里的引擎翻译：auto / google / mymemory / deepseek / baidu / microsoft / custom。</summary>
    public static Task<string> TranslateAsync(
        string text,
        Kojima.Core.Config.TranslateSettings settings,
        CancellationToken ct = default)
    {
        var deepSeekKey = SecretStore.Get(SecretNames.DeepSeek);
        var target = settings.Target == "en" ? "英" : "日";
        var mode = (settings.Engine ?? "auto").Trim().ToLowerInvariant();
        return mode switch
        {
            "baidu" => BaiduAsync(text, target, settings, ct),
            "microsoft" => MicrosoftAsync(text, target, settings, ct),
            "custom" => CustomAsync(text, target, settings, ct),
            "deepseek" => DeepSeekAsync(text, target, settings.Reversed, deepSeekKey, ct),
            "google" => GoogleAsync(text, target, settings.Reversed, ct),
            "mymemory" => MyMemoryAsync(text, target, settings.Reversed, ct),
            "free" => FreeAsync(text, target, settings.Reversed, ct),
            _ => deepSeekKey.Length > 0
                ? DeepSeekAsync(text, target, settings.Reversed, deepSeekKey, ct)
                : FreeAsync(text, target, settings.Reversed, ct),
        };
    }

    /// <summary>百度翻译：需要 AppID + 密钥。</summary>
    public static async Task<string> BaiduAsync(
        string text,
        string target,
        Kojima.Core.Config.TranslateSettings settings,
        CancellationToken ct = default)
    {
        var appId = (settings.BaiduAppId ?? "").Trim();
        var key = SecretStore.Get(SecretNames.TranslateBaiduKey).Trim();
        if (appId.Length == 0 || key.Length == 0)
        {
            throw new InvalidOperationException("未配置百度翻译 AppID / 密钥：设置 → 功能 → 翻译");
        }

        var salt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var to = target == "英" ? "en" : "jp";
        var from = to == "en" ? "zh" : "zh";
        var sign = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
            Encoding.UTF8.GetBytes(appId + text + salt + key))).ToLowerInvariant();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["q"] = text,
            ["from"] = from,
            ["to"] = to,
            ["appid"] = appId,
            ["salt"] = salt,
            ["sign"] = sign,
        });
        using var response = await Http.PostAsync("https://fanyi-api.baidu.com/api/trans/vip/translate", form, ct)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.TryGetProperty("error_code", out var code) && code.GetString() is { Length: > 0 } error && error != "52000")
        {
            throw new InvalidOperationException($"百度翻译错误 {error}" +
                (root.TryGetProperty("error_msg", out var message) ? "：" + message.GetString() : ""));
        }

        if (!root.TryGetProperty("trans_result", out var results) || results.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("百度翻译返回为空");
        }

        var builder = new StringBuilder();
        foreach (var item in results.EnumerateArray())
        {
            if (item.TryGetProperty("dst", out var dst) && dst.GetString() is { } line)
            {
                if (builder.Length > 0) { builder.Append('\n'); }
                builder.Append(line);
            }
        }

        return builder.ToString();
    }

    /// <summary>微软翻译（Azure Translator）。</summary>
    public static async Task<string> MicrosoftAsync(
        string text,
        string target,
        Kojima.Core.Config.TranslateSettings settings,
        CancellationToken ct = default)
    {
        var key = SecretStore.Get(SecretNames.TranslateMicrosoftKey).Trim();
        if (key.Length == 0)
        {
            throw new InvalidOperationException("未配置微软翻译密钥：设置 → 功能 → 翻译");
        }

        var to = target == "英" ? "en" : "ja";
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=" + to);
        request.Headers.Add("Ocp-Apim-Subscription-Key", key);
        var region = (settings.MicrosoftRegion ?? "").Trim();
        if (region.Length > 0)
        {
            request.Headers.Add("Ocp-Apim-Subscription-Region", region);
        }

        request.Content = new StringContent(
            JsonSerializer.Serialize(new[] { new { Text = text } }), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement[0].GetProperty("translations")[0].GetProperty("text").GetString() ?? "";
    }

    /// <summary>自定义大模型（OpenAI 兼容的 /chat/completions）。</summary>
    public static async Task<string> CustomAsync(
        string text,
        string target,
        Kojima.Core.Config.TranslateSettings settings,
        CancellationToken ct = default)
    {
        var endpoint = (settings.CustomEndpoint ?? "").Trim();
        if (endpoint.Length == 0)
        {
            throw new InvalidOperationException("未配置自定义大模型接口：设置 → 功能 → 翻译");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        var key = SecretStore.Get(SecretNames.TranslateCustomKey).Trim();
        if (key.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        var body = new
        {
            model = string.IsNullOrWhiteSpace(settings.CustomModel) ? "gpt-4o-mini" : settings.CustomModel.Trim(),
            temperature = 0,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = BuildInstruction(text, target, settings.Reversed) },
            },
        };
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");
        }

        return ExtractDeepSeek(responseBody);
    }

    /// <summary>按目标语言（"日" / "英"）与方向翻译一段文本。</summary>
    public static async Task<string> TranslateAsync(
        string text,
        string target,
        bool reversed,
        string? engine,
        string? key,
        CancellationToken ct = default)
    {
        var apiKey = (key ?? "").Trim();
        var mode = (engine ?? "auto").Trim().ToLowerInvariant();
        var useDeepSeek = mode == "deepseek" || (mode == "auto" && apiKey.Length > 0);

        return useDeepSeek
            ? await DeepSeekAsync(text, target, reversed, apiKey, ct).ConfigureAwait(false)
            : await FreeAsync(text, target, reversed, ct).ConfigureAwait(false);
    }

    /// <summary>DeepSeek 通道：需要 API Key。</summary>
    public static async Task<string> DeepSeekAsync(
        string text,
        string target,
        bool reversed,
        string key,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(NoKeyText);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, DeepSeekEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(
            JsonSerializer.Serialize(Payload(text, target, reversed)), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ErrorText(body, response.StatusCode));
        }

        return ExtractDeepSeek(body);
    }

    /// <summary>免费通道：MyMemory 优先，失败回退 Google 公共端点。</summary>
    /// <summary>免费通道：先 MyMemory，失败再 Google。</summary>
    public static async Task<string> FreeAsync(
        string text,
        string target,
        bool reversed,
        CancellationToken ct = default)
    {
        try
        {
            return await MyMemoryAsync(text, target, reversed, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn("MyMemory 失败，改用 Google：" + ex.Message);
            return await GoogleAsync(text, target, reversed, ct).ConfigureAwait(false);
        }
    }

    /// <summary>MyMemory 免费接口。</summary>
    public static async Task<string> MyMemoryAsync(
        string text,
        string target,
        bool reversed,
        CancellationToken ct = default)
    {
        var (source, destination) = reversed
            ? (target == "日" ? "ja-JP" : "en-US", "zh-CN")
            : ("zh-CN", target == "日" ? "ja-JP" : "en-US");

        try
        {
            var url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(text)
                + "&langpair=" + Uri.EscapeDataString(source + "|" + destination);
            using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("responseData", out var data)
                    && data.TryGetProperty("translatedText", out var translated)
                    && translated.GetString() is { Length: > 0 } result
                    && !result.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
                {
                    return WebUtility.HtmlDecode(result);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 免费通道失败就换下一个，不向用户暴露中间错误
        }

        // MyMemory 不可用时由 FreeAsync 负责兜底；显式选 MyMemory 也回落到 Google，避免整页不可用
        return await GoogleAsync(text, target, reversed, ct).ConfigureAwait(false);
    }

    /// <summary>Google 免费接口（translate_a/single）。</summary>
    public static async Task<string> GoogleAsync(
        string text,
        string target,
        bool reversed,
        CancellationToken ct = default)
    {
        var google = "https://translate.googleapis.com/translate_a/single?client=gtx&sl="
            + (reversed ? (target == "日" ? "ja" : "en") : "auto")
            + "&tl=" + (reversed ? "zh-CN" : (target == "日" ? "ja" : "en"))
            + "&dt=t&q=" + Uri.EscapeDataString(text);
        using var googleResponse = await Http.GetAsync(google, ct).ConfigureAwait(false);
        var googleBody = await googleResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!googleResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ErrorText(googleBody, googleResponse.StatusCode));
        }

        using var parsed = JsonDocument.Parse(googleBody);
        var builder = new StringBuilder();
        if (parsed.RootElement.ValueKind == JsonValueKind.Array
            && parsed.RootElement.GetArrayLength() > 0
            && parsed.RootElement[0] is { ValueKind: JsonValueKind.Array } sentences)
        {
            foreach (var sentence in sentences.EnumerateArray())
            {
                if (sentence.ValueKind == JsonValueKind.Array && sentence.GetArrayLength() > 0
                    && sentence[0].GetString() is { } piece)
                {
                    builder.Append(piece);
                }
            }
        }

        return builder.ToString();
    }

    private static string BuildInstruction(string text, string target, bool reversed)
    {
        var language = target == "日" ? "日文" : "英文";
        return reversed
            ? $"把下面的{language}翻译成中文，只输出译文：\n{text}"
            : $"把下面的中文翻译成{language}，只输出译文：\n{text}";
    }

    private static object Payload(string text, string target, bool reversed)
    {
        var prompt = BuildInstruction(text, target, reversed);
        return new
        {
            model = "deepseek-chat",
            temperature = 0.0,
            messages = new[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = prompt },
            },
        };
    }

    private static string ExtractDeepSeek(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            return "";
        }

        if (!choices[0].TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content))
        {
            return "";
        }

        return content.GetString() ?? "";
    }

    public static string ErrorText(string body, HttpStatusCode status)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message)
                && message.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }
        catch (JsonException)
        {
        }

        return $"HTTP {(int)status}";
    }
}
