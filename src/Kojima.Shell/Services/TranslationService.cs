using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Kojima.Shell.Services;

/// <summary>
/// 翻译引擎：自动 / 免费通道（MyMemory → Google）/ DeepSeek。
/// 与界面无关，便于单独测试与复用。
/// </summary>
public static class TranslationService
{
    public const string DeepSeekEndpoint = "https://api.deepseek.com/chat/completions";
    public const string SystemPrompt = "你是专业翻译，只输出译文";
    public const string NoKeyText = "未配置 DeepSeek API Key：设置 → 用量 → 来源密钥 里填入后即可翻译";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

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
    public static async Task<string> FreeAsync(
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

    private static object Payload(string text, string target, bool reversed)
    {
        var language = target == "日" ? "日文" : "英文";
        var prompt = reversed
            ? $"把下面的{language}翻译成中文，只输出译文：\n{text}"
            : $"把下面的中文翻译成{language}，只输出译文：\n{text}";
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
