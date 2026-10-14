using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public sealed partial class TaskCenterPanelViewModel
{
    public RelayCommand ImportScheduleCommand=>new(async _=>
    {
        if(!GuardNoOpenDraft())return;
        var dialog=new Microsoft.Win32.OpenFileDialog{Title="导入整份调度列表",Filter="调度列表 (*.json)|*.json"};
        if(dialog.ShowDialog()!=true)return;
        if (_remoteTarget is not null)
        {
            await ImportRemoteScheduleAsync(dialog.FileName);
            return;
        }
        try{ImportScheduleFromFile(dialog.FileName);}catch(Exception ex){SetStatus("调度列表导入失败："+ex.Message,true);Refresh();}
    });
    public RelayCommand ExportScheduleCommand=>new(async _=>
    {
        var dialog=new Microsoft.Win32.SaveFileDialog{Title="导出整份调度列表",Filter="调度列表 (*.json)|*.json",FileName="调度列表.json"};
        if(dialog.ShowDialog()!=true)return;
        if (_remoteTarget is not null)
        {
            await ExportRemoteScheduleAsync(dialog.FileName);
            return;
        }
        try{ExportScheduleToFile(dialog.FileName);}catch(Exception ex){SetStatus("调度列表导出失败："+ex.Message,true);}
    });
    internal void ImportScheduleFromFile(string path)
    {
        if (_remoteTarget is not null) throw new InvalidOperationException("远程导入调度列表须等待执行端回复");
        if(!GuardNoOpenDraft())return;
        var ids=WorkflowScheduleTransfer.Import(_host.Workflows,path);
        SetStatus($"已导入 {ids.Count} 个只读候选；原列表保持。预览确认后激活并重新绑定账号/资源。",false);Refresh();
    }
    internal void ExportScheduleToFile(string path)
    {
        if (_remoteTarget is not null) throw new InvalidOperationException("远程导出调度列表须等待执行端回复");
        WorkflowScheduleTransfer.Export(_host.Workflows,path);
        SetStatus("已导出整份调度列表；账号值已移除，导入后重新绑定。",false);
    }
}
