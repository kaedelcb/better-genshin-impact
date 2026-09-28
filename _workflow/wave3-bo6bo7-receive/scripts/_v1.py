import subprocess, json, io, os, hashlib, xml.etree.ElementTree as ET, collections
d='_workflow/wave3-bo6bo7-receive'; v=d+'/verification'; os.makedirs(v, exist_ok=True)
def run(*a): 
    p=subprocess.run(['git']+list(a),cwd='.',capture_output=True)
    return p.stdout
def sha(b): return hashlib.sha256(b).hexdigest()
F8=['Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md',
 'MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs',
 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt',
 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs',
 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs',
 '_batch21/b21_plan.md','_batch21/sb21-4-handoff-2026-09-28.md','槲寄生调度器总计划.md']
BASE='e2613a851bd45c28fdd56b84dfc10784e1d9c9b8'; DEL='e009068e22b18d89f4eb9f8947fa937dfa8c925c'; OPEN='6fd6207e58ec93dcc38645c12131a9a512f5fc69'
# 1. import delta (staged) vs source delta
d_import=run('diff','--cached','--no-ext-diff','--no-textconv','--',*F8)
d_source=run('diff','--no-ext-diff','--no-textconv',BASE,DEL,'--',*F8)
d_post=run('diff','--no-ext-diff','--no-textconv','--',*F8)
io.open(v+'/import-delta-8files.diff','wb').write(d_import)
io.open(v+'/post-import-delta.diff','wb').write(d_post)
per={}
for f in F8:
    per[f]={'blob_at_source_base':run('rev-parse',BASE+':'+f).decode().strip(),
            'blob_at_delivery':run('rev-parse',DEL+':'+f).decode().strip(),
            'blob_at_opening_head':run('rev-parse',OPEN+':'+f).decode().strip(),
            'blob_staged_index':run('rev-parse',':'+f).decode().strip(),
            'final_worktree_sha256':sha(open(f,'rb').read()),
            'changed_by_source_delta':run('rev-parse',BASE+':'+f).decode().strip()!=run('rev-parse',DEL+':'+f).decode().strip(),
            'imported_equals_delivery':run('rev-parse',':'+f).decode().strip()==run('rev-parse',DEL+':'+f).decode().strip(),
            'post_import_modified':bool(run('diff','--no-ext-diff','--quiet','--',f) or run('diff','--cached','--no-ext-diff','--quiet','--',f)) or sha(open(f,'rb').read())!=sha(run('show',DEL+':'+f))}
eq={'batch':os.path.basename(d),
 'statement':'The staged (imported) delta over the eight source files is byte-identical to the source delivery delta base..delivery. The unstaged delta over those files contains this batch\'s own registration appends to four status documents plus the mandated declaration-surface regeneration of ClaimSurfaceManifest.txt; therefore "eight files imported verbatim" is an import-time statement, not a claim about the final tree.',
 'compared_command_imported':'git diff --cached --no-ext-diff --no-textconv -- <8 files>',
 'compared_command_source':'git diff --no-ext-diff --no-textconv e2613a851 e009068e2 -- <8 files>',
 'imported_delta_bytes':len(d_import),'imported_delta_sha256':sha(d_import),
 'source_delta_bytes':len(d_source),'source_delta_sha256':sha(d_source),
 'delta_byte_identical':d_import==d_source,
 'post_import_delta_bytes':len(d_post),'post_import_delta_sha256':sha(d_post),
 'per_file':per,
 'post_import_changes':{
   'status_documents_appended':['Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md','_batch21/b21_plan.md','_batch21/sb21-4-handoff-2026-09-28.md','槲寄生调度器总计划.md'],
   'regenerated_file':'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt',
   'reason':'R5.3 24.126 registration text (required by the batch closeout) and the rule-mandated CLAIM_SURFACE_REGENERATE=1 regeneration; no source, fixture or assertion was edited.',
   'unchanged_after_import':['MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs']}}
io.open(v+'/intake-equivalence.json','w',encoding='utf-8',newline='\n').write(json.dumps(eq,ensure_ascii=False,indent=2)+"\n")
print('delta identical:',d_import==d_source,'| import sha',sha(d_import)[:16],'| source sha',sha(d_source)[:16])
print('post-import delta bytes',len(d_post))
print('per-file imported==delivery:',all(x['imported_equals_delivery'] for x in per.values()))
for f,x in per.items(): print('  %-70s src_changed=%-5s post_import_modified=%s' % (f.split('/')[-1], x['changed_by_source_delta'], x['post_import_modified']))
