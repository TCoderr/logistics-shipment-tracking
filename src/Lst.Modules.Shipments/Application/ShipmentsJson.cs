using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lst.Modules.Shipments.Application;

public static class ShipmentsJson
{
    /// <summary>
    /// Gönderi oluşturma isteği için sıkı okuma: büyük/küçük harf duyarlı camelCase, tanımsız alan yok,
    /// yinelenen alan yok, string'den sayı okuma yok, yorum/sondaki virgül yok.
    /// </summary>
    public static readonly JsonSerializerOptions StrictRequest = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 16,
    };

    /// <summary>Yanıt gövdesi bir kez bu ayarlarla UTF-8 byte dizisine serialize edilir; replay aynı byte'ları döner.</summary>
    public static readonly JsonSerializerOptions Response = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
}
