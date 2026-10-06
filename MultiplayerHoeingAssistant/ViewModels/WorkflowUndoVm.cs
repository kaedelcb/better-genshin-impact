using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.ViewModels;

public sealed partial class WorkflowEditVm
{
    private bool _scheduleReady,_restoringUndo,_scheduleChanging;
    internal bool IsRestoringSchedule => _restoringUndo;
    private void InitializeUndo()
    {
        _scheduleReady=true;
        void Attach(){foreach(var n in Nodes)n.BeforeEdit=()=>{if(!_restoringUndo && !_scheduleChanging)RememberSchedule();};}
        Attach();Nodes.CollectionChanged+=(_,_)=>Attach();
    }
    protected new bool SetProperty<T>(ref T field,T value,[CallerMemberName]string? name=null)
    {
        if(!EqualityComparer<T>.Default.Equals(field,value) && _scheduleReady && !_restoringUndo && !_scheduleChanging
            && name is not ("SelectedNode" or "AppendSourceIndex" or "AppendStatusText"))RememberSchedule();
        return base.SetProperty(ref field,value,name);
    }
    private static Dictionary<string,object?> Fields(object vm)=>vm.GetType().GetProperties(BindingFlags.Public|BindingFlags.Instance)
        .Where(p=>p.CanWrite && (p.PropertyType==typeof(string) || p.PropertyType==typeof(int) || p.PropertyType==typeof(bool)))
        .ToDictionary(p=>p.Name,p=>p.GetValue(vm));
    private static void RestoreFields(object vm,Dictionary<string,object?> fields)
    {foreach(var (name,value) in fields)vm.GetType().GetProperty(name)!.SetValue(vm,value);}
    private ScheduleUndo CaptureUndo()=>new(Nodes.ToArray(),Nodes.Select(n=>Fields(n)).ToArray(),
        Nodes.Select(n=>JsonSerializer.Serialize(n.Model)).ToArray(),Fields(this),Lanes.ToArray());
    private bool RestoreUndo(ScheduleUndo undo)
    {
        _restoringUndo=true;
        try
        {
            Nodes.Clear();Draft.Nodes.Clear();
            for(var i=0;i<undo.Nodes.Length;i++)
            {
                var node=undo.Nodes[i];var original=JsonSerializer.Deserialize<WorkflowNode>(undo.Models[i])!;
                node.Model.Kind=original.Kind;node.Model.Ref=original.Ref;node.Model.Strategies=original.Strategies;node.Model.Path=original.Path;node.Model.ExtensionData=original.ExtensionData;
                RestoreFields(node,undo.NodeFields[i]);Nodes.Add(node);Draft.Nodes.Add(node.Model);node.NotifyReferenceChanged();
            }
            RestoreFields(this,undo.FlowFields);Lanes.Clear();foreach(var lane in undo.Lanes)Lanes.Add(lane);
            StoreLanes();Renumber();SelectedNode=null;return true;
        }
        finally{_restoringUndo=false;OnPropertyChanged(nameof(IsRestoringSchedule));}
    }
    private sealed record ScheduleUndo(NodeEditVm[] Nodes,Dictionary<string,object?>[] NodeFields,string[] Models,Dictionary<string,object?> FlowFields,string[] Lanes);
}

public sealed partial class NodeEditVm
{
    internal Action? BeforeEdit {get;set;}
    protected new bool SetProperty<T>(ref T field,T value,[CallerMemberName]string? name=null)
    {
        if(name!="Index" && !EqualityComparer<T>.Default.Equals(field,value))BeforeEdit?.Invoke();
        return base.SetProperty(ref field,value,name);
    }
    private void SetWeekday(int day,bool value,string property)
    {if(_weekdays[day]!=value)BeforeEdit?.Invoke();_weekdays[day]=value;OnPropertyChanged(property);}
}
