#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BetterGenshinImpact.GameTask.AutoHoeing.Models;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.Shared.CooperativeRerun;
namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Rerun;
public sealed class CooperativeRoutePlan
{
 private readonly string _snapshot,_fileName,_fullPath;
 public RerunRouteManifest Manifest { get; }
 public RouteInfo Route { get; private set; } = new();
 public Dictionary<int,string> SyncPointIds { get; }=new();
 public Dictionary<int,string> FightPointIds { get; }=new();
 private CooperativeRoutePlan(PathingTask t,int i,double d){_fileName=t.FileName;_fullPath=t.FullPath;_snapshot=JsonSerializer.Serialize(t,PathRecorder.JsonOptions);Manifest=new(){OriginalIndex=i,RouteId=$"{i}:{(string.IsNullOrEmpty(t.LogicalRouteId)?RouteVariantNaming.StripBaseNameAnyVariant(t.FileName):t.LogicalRouteId)}"};var ss=ConvertWaypointsForTrack(t.Positions,t);var m=PathingTaskHelper.IsManualMode(t);var c=new List<RerunCheckpoint>();var f=0;for(var s=0;s<ss.Count;s++){if(m){for(var w=0;w<ss[s].Count;w++){var p=ss[s][w];var k=s*10000+w;if(!string.IsNullOrEmpty(p.SyncPointId))AddSync(k,$"s:{s}:sync:{p.SyncPointId}",s,"sync",c);else if(p.Type=="teleport")AddSync(k,$"s:{s}:tp",s,"teleport",c);if(p.Action=="fight")AddFight(k,s,f++,c);}}else{var a=0;foreach(var x in new SyncPointResolver().ResolveWithIndex(ss[s],d)){if(x.syncPoint!=null&&x.syncPointIdx>=0)AddSync(s*10000+x.syncPointIdx,$"s:{s}:auto:{a++}",s,"sync",c);if(x.fightIdx>=0)AddFight(s*10000+x.fightIdx,s,f++,c);}for(var w=0;w<ss[s].Count;w++)if(ss[s][w].Type=="teleport")AddSync(s*10000+w,$"s:{s}:tp",s,"teleport",c);}}Manifest.Checkpoints=c;}
 private void AddSync(int k,string id,int s,string kind,List<RerunCheckpoint> c){if(!SyncPointIds.ContainsKey(k)){SyncPointIds[k]=id;c.Add(new(){Id=id,Segment=s,Kind=kind});}}
 private void AddFight(int k,int s,int o,List<RerunCheckpoint> c){if(!FightPointIds.ContainsKey(k)){var id=$"s:{s}:fight:{o}";FightPointIds[k]=id;c.Add(new(){Id=id,Segment=s,Kind="fight"});}}
 public static CooperativeRoutePlan Build(RouteInfo r,int i,double d)=>FromTask(r,PathingTask.BuildFromFilePath(r.FullPath)??throw new InvalidOperationException(),i,d);
 public static CooperativeRoutePlan FromTask(RouteInfo r,PathingTask t,int i,double d)
 {
  ArgumentNullException.ThrowIfNull(r);
  ArgumentNullException.ThrowIfNull(t);
  var plan = new CooperativeRoutePlan(t,i,d);
  plan.Route = JsonSerializer.Deserialize<RouteInfo>(JsonSerializer.Serialize(r)) ?? throw new InvalidOperationException("Failed to freeze route metadata.");
  return plan;
 }
 public PathingTask CreateTask(){var t=JsonSerializer.Deserialize<PathingTask>(_snapshot,PathRecorder.JsonOptions)??throw new InvalidOperationException();t.FileName=_fileName;t.FullPath=_fullPath;return t;}
 public static List<List<WaypointForTrack>> ConvertWaypointsForTrack(List<Waypoint> ps,PathingTask t){var a=ps.Select(p=>{var w=new WaypointForTrack(p,t.Info.MapName,t.Info.MapMatchMethod){Misidentification=p.PointExtParams.Misidentification,MonsterTag=p.PointExtParams.MonsterTag,EnableMonsterLootSplit=p.PointExtParams.EnableMonsterLootSplit,AutoFight=p.PointExtParams.AutoFight};return w;}).ToList();var r=new List<List<WaypointForTrack>>();var c=new List<WaypointForTrack>();foreach(var p in a){if(p.Type=="teleport"&&c.Count>0){r.Add(c);c=new();}c.Add(p);}r.Add(c);return r;}
}