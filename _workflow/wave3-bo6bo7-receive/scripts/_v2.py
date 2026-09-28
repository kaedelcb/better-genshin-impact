import subprocess, json, io, os, hashlib
d='_workflow/wave3-bo6bo7-receive'; v=d+'/verification'
def git(*a):
    p=subprocess.run(['git']+list(a),cwd='.',capture_output=True); return p.returncode,p.stdout
def sha(b): return hashlib.sha256(b).hexdigest()
def lf(b): return b.replace(b'\r\n',b'\n')
F8=['Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md',
 'MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs',
 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt',
 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs',
 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs',
 '_batch21/b21_plan.md','_batch21/sb21-4-handoff-2026-09-28.md','槲寄生调度器总计划.md']
BASE='e2613a851bd45c28fdd56b84dfc10784e1d9c9b8'; DEL='e009068e22b18d89f4eb9f8947fa937dfa8c925c'; OPEN='6fd6207e58ec93dcc38645c12131a9a512f5fc69'
eq=json.load(io.open(v+'/intake-equivalence.json',encoding='utf-8'))
per={}
for f in F8:
    _,blob_del=git('show',DEL+':'+f)
    wt=open(f,'rb').read()
    _,idx=git('show',':'+f)
    rc_unstaged,_=git('diff','--quiet','--no-ext-diff','--',f)
    bl_base=git('rev-parse',BASE+':'+f)[1].decode().strip()
    bl_del=git('rev-parse',DEL+':'+f)[1].decode().strip()
    per[f]={
     'blob_at_source_base':bl_base,'blob_at_delivery':bl_del,
     'blob_at_opening_head':git('rev-parse',OPEN+':'+f)[1].decode().strip(),
     'blob_staged_index':git('rev-parse',':'+f)[1].decode().strip(),
     'changed_by_source_delta':bl_base!=bl_del,
     'imported_index_equals_delivery_blob':git('rev-parse',':'+f)[1].decode().strip()==bl_del,
     'imported_index_bytes_lf_normalized_sha256':sha(lf(idx)),
     'delivery_blob_lf_normalized_sha256':sha(lf(blob_del)),
     'final_worktree_sha256':sha(wt),
     'final_worktree_lf_normalized_sha256':sha(lf(wt)),
     'worktree_differs_from_imported_index':rc_unstaged!=0,
    }
eq['per_file']=per
eq['verification_notes']=[
 'imported_index_equals_delivery_blob is the per-file import-fidelity check (index after `git checkout e009068e2 -- <path>` equals the delivery blob).',
 'worktree_differs_from_imported_index is the post-import-modification check; false means the file still byte-matches the import (modulo the repository-wide CRLF checkout convention, which affects the index blob and the worktree identically per side).',
 'Only ClaimSurfaceManifest.txt (mandated regeneration) and the four status documents (this batch registration) are marked modified after import; the product source and both fixture files are not.',
]
eq['post_import_modified_files']=[f for f in F8 if per[f]['worktree_differs_from_imported_index']]
eq['post_import_unmodified_files']=[f for f in F8 if not per[f]['worktree_differs_from_imported_index']]
io.open(v+'/intake-equivalence.json','w',encoding='utf-8',newline='\n').write(json.dumps(eq,ensure_ascii=False,indent=2)+"\n")
print('delta_byte_identical:',eq['delta_byte_identical'])
print('post-import modified :',[f.split("/")[-1] for f in eq['post_import_modified_files']])
print('post-import untouched:',[f.split("/")[-1] for f in eq['post_import_unmodified_files']])
