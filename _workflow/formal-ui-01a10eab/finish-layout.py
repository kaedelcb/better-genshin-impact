from pathlib import Path
import sys
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'))
import storage_limits as storage
with storage.Session(root,'formal-ui-finish-layout') as budget:
    p=root/'MultiplayerHoeingAssistant/Views/MistletoePage.xaml';data=p.read_bytes();t=data.decode('utf-8-sig').replace('\r\n','\n')
    start=t.index('                        <!-- ===== 运行状态卡')
    end=t.index('                    </StackPanel>\n                </ScrollViewer>',start)
    history_start=t.index('                                <ItemsControl ItemsSource="{Binding TaskCenter.HistoryRuns}">',start)
    history_end=t.index('</ItemsControl>',history_start)+len('</ItemsControl>')
    history=t[history_start:history_end]
    t=t[:start]+'                        <Expander Header="历史运行（最近20条 · 只读）" Foreground="{DynamicResource GoldText}" Margin="0,10" IsExpanded="False">\n'+history+'\n                        </Expander>\n'+t[end:]
    storage.write(p,(b'\xef\xbb\xbf' if data.startswith(b'\xef\xbb\xbf') else b'')+t.encode(),mode='wb')
    print('thin controls and readonly history attached')
