using System;
using BetterGenshinImpact.GameTask.AutoPathing;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Core.Script.Dependence;

public class Log
{
    private const string PickupPrefix = "交互或拾取：";
    private readonly ILogger<Log> _logger = App.GetLogger<Log>();
    private readonly AutoPathingScript? _pickupLogSource;

    public Log() { }

    internal Log(AutoPathingScript pickupLogSource)
    {
        _pickupLogSource = pickupLogSource;
    }

    public void Debug(string? message, params object?[] args)
    {
        _logger.LogDebug(message, args);
    }

    public void Info(string? message, params object?[] args)
    {
        if (_pickupLogSource != null && args is { Length: 0 }
            && PickupWaypointProgress.Capture(_pickupLogSource) is { } pickup
            && GetPickupItemName(message) is { } itemName)
        {
            pickup.ReportHere(itemName);
        }
        _logger.LogInformation(message, args);
    }

    internal static string? GetPickupItemName(string? message)
    {
        if (message == null || !message.StartsWith(PickupPrefix, StringComparison.Ordinal)
                            || message.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            return null;
        }

        var name = message.AsSpan(PickupPrefix.Length).Trim();
        if (name.Length >= 2 && (name[0] == '"' && name[^1] == '"' || name[0] == '“' && name[^1] == '”'))
        {
            name = name[1..^1].Trim();
        }
        return name.IsEmpty ? null : name.ToString();
    }

    public void Warn(string? message, params object?[] args)
    {
        _logger.LogWarning(message, args);
    }

    public void Error(string? message, params object?[] args)
    {
        _logger.LogError(message, args);
    }
}
