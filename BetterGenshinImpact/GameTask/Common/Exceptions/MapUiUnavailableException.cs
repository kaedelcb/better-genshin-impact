using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;

namespace BetterGenshinImpact.GameTask.Common.Exceptions;

/// <summary>快速拖动传送中地图意外消失；与未激活点位、坐标识别失败等普通错误区分。</summary>
public sealed class MapUiUnavailableException(string message) : RetryException(message);
