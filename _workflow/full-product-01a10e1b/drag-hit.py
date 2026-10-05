from pathlib import Path
import sys,json
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s
base=Path(__file__).resolve().parent
with s.Session(root,'full-product-drag-hit-surfaces') as budget:
    budget.track(base);p=root/'MultiplayerHoeingAssistant/Views/MistletoePage.xaml';raw=p.read_bytes();s.write(base/'before-hit/MistletoePage.xaml',raw)
    t=raw.decode('utf-8-sig')
    old='Padding="10,8" Margin="0,0,0,6" AllowDrop="True" DragOver="TaskNode_DragOver"';assert t.count(old)==1
    t=t.replace(old,'Padding="10,8" Margin="0,0,0,6" Background="Transparent" AllowDrop="True" DragOver="TaskNode_DragOver"')
    old='TextAlignment="Center" PreviewMouseLeftButtonDown="TaskNode_MouseDown"';assert t.count(old)==1
    t=t.replace(old,'TextAlignment="Center" Background="Transparent" Height="22" VerticalAlignment="Center" PreviewMouseLeftButtonDown="TaskNode_MouseDown"')
    data=t.encode();data=(b'\xef\xbb\xbf'+data) if raw.startswith(b'\xef\xbb\xbf') else data;s.write(p,data,mode='wb')
    s.write(base/'hit-admission.json',json.dumps(dict(function='C02 drag hit surfaces',evidence='live native gesture did not reorder; handle and border had no hit-test background, blank drop areas fell through',minimum='transparent hit surfaces on explicit handle and card; no algorithm or execution change'),indent=2).encode())
