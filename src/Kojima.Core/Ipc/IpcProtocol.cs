using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kojima.Core.Ipc;

/// <summary>
/// 主进程 ↔ 子进程的 JSON 行协议。
/// 请求：{"id":1,"method":"sample"}；响应：{"id":1,"result":{...}}；
/// 推送：{"event":"sample","data":{...}}。
/// </summary>
public sealed class IpcEnvelope
{
    public long? Id { get; set; }
    public string? Method { get; set; }
    public JsonElement? Params { get; set; }
    public JsonElement? Result { get; set; }
    public string? Error { get; set; }
    public string? Event { get; set; }
    public JsonElement? Data { get; set; }
}

public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string PipeName(string feature, int shellPid) => $"kojima-{feature}-{shellPid}";
}
