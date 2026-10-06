from pathlib import Path
import sys
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'))
import storage_limits as storage
with storage.Session(root,'formal-ui-attach-and-format') as budget:
    path=root/'MultiplayerHoeingAssistant/Views/MistletoePage.xaml'
    original=path.read_bytes(); text=original.decode('utf-8-sig')
    start=text.index('                        <!-- BGI 任务状态判断卡片')
    end=text.index('                        <!-- ===== 流程预览卡',start)
    text=text[:start]+'                        <views:ScheduleListView DataContext="{Binding TaskCenter}" Margin="0,0,0,10"/>\r\n'+text[end:]
    # Keep all existing detailed fields accessible while the default face remains the schedule.
    start=text.index('                        <!-- ===== 流程编辑卡')
    end=text.index('                        <!-- ===== 运行状态卡',start)
    text=text[:start]+'                        <Expander Header="流程设置与完整策略" Foreground="{DynamicResource GoldText}" IsExpanded="False">\r\n'+text[start:end]+'                        </Expander>\r\n'+text[end:]
    storage.write(path,(b'\xef\xbb\xbf' if original.startswith(b'\xef\xbb\xbf') else b'')+text.encode('utf-8'),mode='wb')
    for rel in ['ViewModels/TaskCenterPanelViewModel.EditParts.cs','Models/TaskCenter/WorkflowModels.cs','Services/TaskCenter/WorkflowRunner.cs','Services/TaskCenter/WorkflowPlanner.cs']:
        p=root/'MultiplayerHoeingAssistant'/rel;data=p.read_bytes();body=data.decode('utf-8-sig').replace('\r\n','\n').replace('\n','\r\n')
        storage.write(p,(b'\xef\xbb\xbf' if data.startswith(b'\xef\xbb\xbf') else b'')+body.encode('utf-8'),mode='wb')
    print('attached; existing format retained')
