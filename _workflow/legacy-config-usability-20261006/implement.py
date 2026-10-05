from pathlib import Path
import hashlib,json
root=Path.cwd(); base=Path(__file__).resolve().parent
vm=root/'BetterGenshinImpact/ViewModel/Pages/OneDragonFlowViewModel.cs'
view=root/'BetterGenshinImpact/View/Pages/OneDragonFlowPage.xaml'
paths=[vm,view]
before={str(p.relative_to(root)):{'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'bytes':p.stat().st_size} for p in paths}
(base/'before-product.json').write_text(json.dumps(before,indent=2),encoding='utf-8')
old='迁移能力将在后续版本提供。'
new='旧格式请在助手的“槲寄生 → 任务中心”中选择“准备旧数据迁移”，选择本版 User 目录，再激活候选。迁移后标准配置可在此使用；账号、定时和循环在任务中心保留。损坏或无法识别的配置须先修复。'
data=vm.read_bytes(); assert data.count(old.encode())==1
vm.write_bytes(data.replace(old.encode(),new.encode()))
data=view.read_bytes(); newline=b'\r\n' if b'\r\n' in data else b'\n'
old=b'               <TextBlock Text="{Binding PendingMigrationHint}" TextWrapping="Wrap" />'
new=newline.join([
    b'               <StackPanel>',
    b'                   <TextBlock Text="{Binding PendingMigrationHint}" TextWrapping="Wrap" />',
    '                   <ui:Button Content="打开助手迁移" Margin="0,8,0,0" HorizontalAlignment="Left"'.encode(),
    b'                              Command="{Binding DataContext.LaunchAssistantCommand, RelativeSource={RelativeSource AncestorType=Window}}" />',
    b'               </StackPanel>'
])
assert data.count(old)==1; view.write_bytes(data.replace(old,new))
after={str(p.relative_to(root)):{'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'bytes':p.stat().st_size} for p in paths}
assert all(after[k]['bytes']>=before[k]['bytes'] for k in before)
(base/'after-product.json').write_text(json.dumps(after,indent=2),encoding='utf-8')
