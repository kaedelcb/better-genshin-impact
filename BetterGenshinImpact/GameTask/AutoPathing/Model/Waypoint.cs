using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;

namespace BetterGenshinImpact.GameTask.AutoPathing.Model;

[Serializable]
public class Waypoint
{




    //异常识别处理
    public class Misidentification
    {

        //何时处理   pathTooFar  路径过远  Unrecognized 未识别
        public List<string> Type { get; set; }= ["unrecognized"];
        //处理方式  previousDetectedPoint  取上一个识别到的点位置  ,  mapRecognition 大地图识别 , ScheduledArrival  特定时间到达
        public string HandlingMode { get; set; } = "previousDetectedPoint";
        //自行预估的时间
        public int ArrivalTime { get; set; } = 0;
    }
    public class ExtParams
    {
        public Misidentification Misidentification { get; set; } = new();
        public string Description { get; set; } = "";
        //normal 小怪,elite 精英,legendary 传奇
        public string MonsterTag { get; set; }
        public bool EnableMonsterLootSplit { get; set; } = false;
        // 当前战斗点的自动战斗扩展参数。
        public FightExtParams? AutoFight { get; set; }
    }

    public class FightExtParams
    {
        /// <summary>奖励结束检测类型：experience/exp/经验值 或 mora/摩拉。</summary>
        public string? RewardType { get; set; }

        /// <summary>仅摩拉模式使用；null 表示匹配全部已加载的摩拉模板。</summary>
        public List<int>? MoraValues { get; set; }
    }



    public double X { get; set; }
    public double Y { get; set; }
    
    public ExtParams PointExtParams { get; set; }=new ExtParams();

    /// <summary>
    /// <see cref="WaypointType"/>
    /// </summary>
    public string Type { get; set; } = WaypointType.Path.Code;

    /// <summary>
    /// <see cref="MoveModeEnum"/>
    /// </summary>
    public string MoveMode { get; set; } = MoveModeEnum.Walk.Code;

    /// <summary>
    /// <see cref="ActionEnum"/>
    /// </summary>
    public string? Action { get; set; }
    
    public string? ActionParams { get; set; }
    
    /// <summary>
    /// 怪物、特产
    /// </summary>
    public List<MaterialInfo> Items { get; set; } = [];

    /// <summary>
    /// 逻辑同步点标记（route-variant-sync-by-logical-id spec / R1.2）。
    /// 非空时表示该 waypoint 是一个逻辑同步点，syncId 拼为 {LogicalRouteId}_{SyncPointId}
    /// （或 fallback {FileName}_{SyncPointId}，见 R3.6）。
    /// 建议作者使用字母前缀（如 fight_1, pickup_2），避免与自动模式
    /// {FileName}_{listIdx}_{fightIdx} 拼法的纯数字段命名空间冲突（D-5）。
    /// 老 JSON 保持 null，行为完全不变。
    /// </summary>
    public string? SyncPointId { get; set; }
}
