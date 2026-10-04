from pathlib import Path
import json,hashlib,locale
d=Path('_workflow/local-wait-admission-gates-20261004/auto-relay-four-entry-recovery-20261004-from-01a1061f');changes=[]
# Derived JSON only. Preserve exact original bytes; raw TRX/log/independent/source evidence is never edited.
for p in sorted(d.rglob('*.json')):
 if 'original-encoding' in p.parts:continue
 b=p.read_bytes()
 try:b.decode('utf-8-sig')
 except UnicodeDecodeError:
  text=b.decode(locale.getencoding());json.loads(text)
  backup=d/'original-encoding'/p.relative_to(d);backup=backup.with_suffix(backup.suffix+'.bin');backup.parent.mkdir(parents=True,exist_ok=True);assert not backup.exists();backup.write_bytes(b)
  new=text.encode('utf-8');p.write_bytes(new);changes.append(dict(path=str(p),original_encoding=locale.getencoding(),original_sha256=hashlib.sha256(b).hexdigest(),backup=str(backup),utf8_sha256=hashlib.sha256(new).hexdigest()))
(d/'encoding-observation.json').write_text(json.dumps(changes,ensure_ascii=False,indent=2),encoding='utf-8');print('derived JSON normalized with original bytes retained:',len(changes),locale.getencoding())
