from pathlib import Path
import json, hashlib, subprocess
root=Path.cwd(); out=Path(__file__).parent
prior=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-legacy-seal-source-20261004-from-01a106d9'
current=root/'_workflow/local-wait-admission-gates-20261004/CURRENT-HANDOFF.md'
original=current.read_bytes()
append='\n\n## 2026-10-04 迁移拒绝与只读幂等观察候选（当前优先）\n\n来源01a106f5，候选31350cadb163a5977fbcea20bae906ea5cb3dbdd。完整事实读auto-relay-legacy-seal-source-20261004-from-01a106d9/MIGRATION-CANDIDATE.md与migration-final原TRX/log/impact-comparison、两PFP/恢复SHA、checkpoint原件。两个产品窄修复（根事实异常结构化拒绝、现代Activated重开只读复核），继承R56依赖保全；5/5和89/89绿，两普通PFP完整。full2075/4/2；target初次608/1/2新增三轮合法移交封印断言失败，同版本16/0/0及同条件repeat609/0/2不覆盖首次失败。保守合集2084/5/2=2091，新增7/删0/三共有变化；274inputs前后同字节，原271只两产品变，声明面同SHA。当前不是认证/独立pass/实机验收；四原R56和新封印失败仍open，生产门关闭。\n\n唯一下一责任是Host驱动退出/异步终局回写/Shutdown取消与释放租约交错的确定原始字节反例、集中安全修复，再完整真实依赖认证/全源域sol-high综合后审/原级闭环与全部功能实际交付。原预算尚未全面核清，新请求0，补读CLI3 intent，不复制无限policy/归零/伪receipt。audit2仍9f85缺receipt、verify无report。最新接班握手只依auto-relay-migration-terminal-seal-20261004-from-01a106f5/HANDOFF.md与relay-prompt.txt及原生old-goal-paused-readback；旧握手仅历史。迁移聚焦候选终态后转Host生命周期责任，按语义边界交接，不按时间/工具数。保护User/JS/.kiro/材料外；总Goal未完成。\n'
current.write_bytes(original+append.encode('utf-8'))
assert current.read_bytes().startswith(original)
(out/'handoff-append-observation.json').write_text(json.dumps(dict(original_sha256=hashlib.sha256(original).hexdigest(),
    new_sha256=hashlib.sha256(current.read_bytes()).hexdigest(),old_bytes=len(original),new_bytes=current.stat().st_size,
    original_prefix_preserved=True),indent=2),encoding='utf-8')
def git(*args):
    return subprocess.run(['git','-c','core.longpaths=true',*args],stdout=subprocess.PIPE,stderr=subprocess.PIPE,check=True)
assert git('diff','--cached','--name-only','-z').stdout==b''
scope=[str(out.relative_to(root)),str(prior.relative_to(root)),str(current.relative_to(root))]
status=git('status','--porcelain=v1','--untracked-files=all','-z','--',*scope).stdout
paths=[row[3:].decode('utf-8') for row in status.split(b'\0') if row]
spec=out/'handoff-metadata-paths.nul'; spec.write_bytes(b''.join(p.encode('utf-8')+b'\0' for p in paths))
git('add','--pathspec-from-file='+str(spec),'--pathspec-file-nul')
commit=git('commit','--only','-m','docs: preserve migration candidate and terminal seal lifecycle handoff',
    '--pathspec-from-file='+str(spec),'--pathspec-file-nul')
(out/'handoff-metadata-commit.log').write_bytes(commit.stdout+commit.stderr)
head=git('rev-parse','HEAD').stdout.decode().strip()
actual=[p.decode('utf-8') for p in git('diff-tree','--no-commit-id','--name-only','-r','-z',head).stdout.split(b'\0') if p]
assert set(actual)==set(paths)
assert git('diff','--cached','--name-only','-z').stdout==b''
(out/'source-status.txt').write_bytes(git('status','--porcelain=v1').stdout)
(out/'source-head.json').write_text(json.dumps(dict(head=head,actual_paths=actual,staging_empty=True,product_candidate='31350cadb163a5977fbcea20bae906ea5cb3dbdd'),indent=2),encoding='utf-8')
print(json.dumps(dict(head=head,files=len(actual),staging_empty=True)))
