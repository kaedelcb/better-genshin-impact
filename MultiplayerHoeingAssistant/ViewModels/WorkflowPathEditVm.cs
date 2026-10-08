using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.ViewModels;

public sealed partial class WorkflowEditVm
{
    public void EnablePaths()
    {
        foreach(var node in Nodes)
            if(!node.Model.Strategies.Any(s=>s.Kind=="flow.route")) node.Model.Strategies.Add(new(){Kind="flow.route"});
    }
    public NodeEditVm AddControl(bool end)
    {
        RememberSchedule();EnablePaths();
        var node=new WorkflowNode{NodeId=NewNodeId(),Kind=end?"control.end":"control.condition",Path=end?null:new(){Yes="$end",No="$end",Condition=new(){Kind="weekdays",Days=["周一","周二","周三","周四","周五"]}}};
        node.Strategies.Add(new(){Kind="flow.route"});Draft.Nodes.Add(node);
        var vm=new NodeEditVm(node);Nodes.Add(vm);Renumber();SelectedNode=vm;return vm;
    }
    public void Connect(NodeEditVm source,NodeEditVm? target,string side="next")
    {
        if(!Nodes.Contains(source) || target is not null && !Nodes.Contains(target)) return;
        if(source.Model.Kind=="control.end") throw new InvalidOperationException("结束流程没有后续去向");
        SetTarget(source,target?.Model.NodeId ?? "$end",side);
    }
    public void SetTarget(NodeEditVm source,string? target,string side)
    {
        RememberSchedule();_scheduleChanging=true;
        try{EnablePaths();if(side=="yes")source.YesTarget=target ?? "lane:"+source.LaneIndex;else if(side=="no")source.NoTarget=target ?? "lane:"+source.LaneIndex;else source.NextTarget=target ?? "";}
        finally{_scheduleChanging=false;}
    }
    public void ApplyInspectorSchedule()
    {
        if(SelectedNode is not {} node)return;
        ScheduleNode(node,node.ScheduleMinute,node.LaneIndex);
    }
}

public sealed partial class NodeEditVm
{
    private string _nextTarget="",_yesTarget="$end",_noTarget="$end",_conditionDays="周一,周二,周三,周四,周五",_conditionFrom="00:00",_conditionUntil="24:00";
    private int _conditionKindIndex,_span=1;
    private bool _constantAnswer=true;
    private int _originalConditionKind;
    private string _originalDays="",_originalFrom="",_originalUntil="";
    private bool _originalAnswer;
    private string _observationKeyword="",_observationSourceNode="",_originalObservationKeyword="",_originalObservationSourceNode="";
    public string ObservationKeyword{get=>_observationKeyword;set=>SetProperty(ref _observationKeyword,value);}
    public string ObservationSourceNode{get=>_observationSourceNode;set=>SetProperty(ref _observationSourceNode,value);}
    public bool IsResource=>Model.Kind.StartsWith("resource.",StringComparison.Ordinal);
    public string NodeIdentity=>Model.NodeId;
    public bool IsCondition=>Model.Kind=="control.condition";
    public string NextTarget{get=>_nextTarget;set=>SetProperty(ref _nextTarget,value ?? "");}
    public string YesTarget{get=>_yesTarget;set=>SetProperty(ref _yesTarget,value ?? "$end");}
    public string NoTarget{get=>_noTarget;set=>SetProperty(ref _noTarget,value ?? "$end");}
    public int ConditionKindIndex{get=>_conditionKindIndex;set=>SetProperty(ref _conditionKindIndex,value);}
    public string ConditionDays{get=>_conditionDays;set=>SetProperty(ref _conditionDays,value);}
    public string ConditionFrom{get=>_conditionFrom;set=>SetProperty(ref _conditionFrom,value);}
    public string ConditionUntil{get=>_conditionUntil;set=>SetProperty(ref _conditionUntil,value);}
    public bool ConstantAnswer{get=>_constantAnswer;set=>SetProperty(ref _constantAnswer,value);}
    public int LaneSpan{get=>_span;set=>SetProperty(ref _span,Math.Max(1,value));}
    private string _originalPath="";
    private string PathKey=>JsonSerializer.Serialize(new{NextTarget,YesTarget,NoTarget,ConditionKindIndex,ConditionDays,ConditionFrom,ConditionUntil,ConstantAnswer,LaneSpan,ObservationKeyword,ObservationSourceNode});
    internal void InitializePath()
    {
        _nextTarget=Model.Path?.Next ?? "";_yesTarget=Model.Path?.Yes ?? "$end";_noTarget=Model.Path?.No ?? "$end";
        var c=Model.Path?.Condition;
        _conditionKindIndex=c?.Kind switch{"timeWindow"=>1,"constant"=>2,"observation"=>4,"weekdays" or null=>0,_=>3};
        _observationKeyword=Model.Strategies.LastOrDefault(s=>s.Kind=="observer.log")?.GetString("keyword") ?? "";
        _observationSourceNode=c?.SourceNodeId ?? "";
        _originalObservationKeyword=_observationKeyword;_originalObservationSourceNode=_observationSourceNode;
        if(c?.Days is {} days)_conditionDays=string.Join(',',days);
        _conditionFrom=c?.From ?? "00:00";_conditionUntil=c?.Until ?? "23:59";_constantAnswer=c?.Value ?? true;
        _originalConditionKind=_conditionKindIndex;_originalDays=_conditionDays;_originalFrom=_conditionFrom;_originalUntil=_conditionUntil;_originalAnswer=_constantAnswer;
        if(Model.ExtensionData?.TryGetValue("scheduleSpan",out var span)==true && span.TryGetInt32(out var count))_span=Math.Max(1,count);
        _originalPath=PathKey;
    }
    internal void ApplyPath(WorkflowNode target)
    {
        if(PathKey==_originalPath)return;
        if(ObservationKeyword!=_originalObservationKeyword)
        {
            if(string.IsNullOrWhiteSpace(ObservationKeyword))target.Strategies.RemoveAll(s=>s.Kind=="observer.log");
            else
            {
                var observer=target.Strategies.LastOrDefault(s=>s.Kind=="observer.log");
                if(observer is null){observer=new(){Kind="observer.log"};target.Strategies.Add(observer);}
                observer.Params ??=new();observer.Params["keyword"]=JsonSerializer.SerializeToElement(ObservationKeyword.Trim());
            }
        }
        target.ExtensionData ??=new();target.ExtensionData["scheduleSpan"]=JsonSerializer.SerializeToElement(LaneSpan);
        if(!target.Strategies.Any(s=>s.Kind=="flow.route"))target.Strategies.Add(new(){Kind="flow.route"});
        target.Path ??=new();target.Path.Next=string.IsNullOrWhiteSpace(NextTarget)?null:NextTarget.Trim();
        if(IsCondition)
        {
            target.Path.Yes=YesTarget.Trim();target.Path.No=NoTarget.Trim();
            if(ConditionKindIndex==3)return; // 布局/去向编辑不把未知条件转换为默认星期。
            target.Path.Condition ??=new();var c=target.Path.Condition;
            var kindChanged=ConditionKindIndex!=_originalConditionKind;
            if(kindChanged)c.Kind=ConditionKindIndex switch{1=>"timeWindow",2=>"constant",4=>"observation",_=>"weekdays"};
            if(ConditionKindIndex==4 && (kindChanged || ObservationSourceNode!=_originalObservationSourceNode))c.SourceNodeId=ObservationSourceNode;
            if(ConditionKindIndex==0 && (kindChanged || ConditionDays!=_originalDays))
                c.Days=ConditionDays.Split([',','，',' '],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).ToList();
            if(ConditionKindIndex==1)
            {
                if(kindChanged || ConditionFrom!=_originalFrom)c.From=ConditionFrom;
                if(kindChanged || ConditionUntil!=_originalUntil)c.Until=ConditionUntil;
            }
            if(ConditionKindIndex==2 && (kindChanged || ConstantAnswer!=_originalAnswer))c.Value=ConstantAnswer;
        }
    }
}
