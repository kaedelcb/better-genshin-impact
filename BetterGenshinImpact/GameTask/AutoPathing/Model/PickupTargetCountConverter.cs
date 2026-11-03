using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterGenshinImpact.GameTask.AutoPathing.Model;

/// <summary>拾取目标和达标后要跳过的紧邻点位数。</summary>
public readonly record struct PickupTarget(int Count, int SkipNextWaypoints);

/// <summary>同一字段接受正整数（包括数字字符串）或“拾取数/跳过数”，无效值不阻止线路加载。</summary>
internal sealed class PickupTargetCountConverter : JsonConverter<PickupTarget?>
{
    public override PickupTarget? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var count) && count > 0)
            return new PickupTarget(count, 0);

        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString().AsSpan();
            var slash = text.IndexOf('/');
            if (slash < 0 && TryParseDigits(text, out var quotedCount) && quotedCount > 0)
                return new PickupTarget(quotedCount, 0);
            if (slash > 0 && TryParseDigits(text[..slash], out var target) && target > 0
                && TryParseDigits(text[(slash + 1)..], out var skip))
                return new PickupTarget(target, skip);
        }

        reader.Skip();
        return null;
    }

    private static bool TryParseDigits(ReadOnlySpan<char> text, out int number)
    {
        number = 0;
        return !text.IsEmpty && text.IndexOfAnyExceptInRange('0', '9') < 0
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    public override void Write(Utf8JsonWriter writer, PickupTarget? value, JsonSerializerOptions options)
    {
        if (value is not { Count: > 0, SkipNextWaypoints: >= 0 } target)
            writer.WriteNullValue();
        else if (target.SkipNextWaypoints == 0)
            writer.WriteNumberValue(target.Count);
        else
            writer.WriteStringValue(FormattableString.Invariant($"{target.Count}/{target.SkipNextWaypoints}"));
    }
}
