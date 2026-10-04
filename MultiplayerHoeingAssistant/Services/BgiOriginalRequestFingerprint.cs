using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MultiplayerHoeingAssistant.Services;

// Version 1 mirrors ExecutionRequestContract.Fingerprint on the parsed wire request.
// The legacy 24hex local fingerprint retains its original meaning.
internal static class BgiOriginalRequestFingerprint
{
    internal const int Version = 1;

    internal static string Compute(string operation, string wirePayloadJson)
    {
        var data = JObject.Parse(wirePayloadJson);
        data.Remove("idempotencyKey");
        var canonical = operation + "\n" + Canonical(data).ToString(Formatting.None);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static JToken Canonical(JToken value) => value switch
    {
        JObject obj => new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new JProperty(p.Name, Canonical(p.Value)))),
        JArray array => new JArray(array.Select(Canonical)),
        _ => value.DeepClone(),
    };
}
