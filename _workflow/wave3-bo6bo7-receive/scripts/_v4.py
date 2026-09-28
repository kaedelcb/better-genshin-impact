import json, io, os, hashlib, xml.etree.ElementTree as ET
d='_workflow/wave3-bo6bo7-receive'; v=d+'/verification'
NS={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def sha(p): return hashlib.sha256(open(p,'rb').read()).hexdigest()
def tres(p, name):
    root=ET.parse(p).getroot()
    for r in root.find('t:Results',NS):
        if r.get('testName')==name:
            ei=r.find('t:Output/t:ErrorInfo',NS)
            return {'outcome':r.get('outcome'),'testId':r.get('testId'),
                    'message':(ei.findtext('t:Message',default='',namespaces=NS) if ei is not None else ''),
                    'assertion':''}
    return None
def trx_one(p):
    root=ET.parse(p).getroot()
    rows=list(root.find('t:Results',NS)); c=dict(root.find('t:ResultSummary/t:Counters',NS).attrib)
    r=rows[0]; ei=r.find('t:Output/t:ErrorInfo',NS)
    import re
    stack=(ei.findtext('t:StackTrace',default='',namespaces=NS) if ei is not None else '')
    msg=(ei.findtext('t:Message',default='',namespaces=NS) if ei is not None else '')
    return {'path':p.replace('\\','/'),'file_sha256':sha(p),'coverage':{'total':c.get('total'),'passed':c.get('passed'),'failed':c.get('failed')},
            'outcome':r.get('outcome'),'testId':r.get('testId'),'testName':r.get('testName'),
            'assertion_line':(re.search(r'WorkflowRunnerTests\.cs:line \d+',stack) or [None])[0] if re.search(r'WorkflowRunnerTests\.cs:line \d+',stack) else None,
            'message':msg}
recs=json.load(io.open(d+'/mutation-records.json',encoding='utf-8-sig'))
deliv=json.load(io.open(r'C:\Users\Administrator\.codex\worktrees\wave3-bo6-bo7\better-genshin-impact-LCB\_workflow\wave3-bo6-bo7\manifest.json',encoding='utf-8'))
delm={m['id']:m for m in deliv['mutations']}
out={'meaning':'Compact, independently checkable verification record for the eight reverse mutations re-executed on the integrated mainline bytes. Raw TRX XML and build logs remain under _workflow/wave3-bo6bo7-receive/mutations/<id>/; this record carries the outcome, target identity, assertion location, digests and the source-vs-receive hash comparison so the claim can be checked without shipping ~24 TRX files.',
 'source_of_recorded_values':'The source delivery accepted-state values are read from the source batch manifest (_workflow/wave3-bo6-bo7/manifest.json at commit e009068e2); the live per-mutation experiment records in the source worktree are untracked and are not re-read here.',
 'original_sha256_worktree_form':sha(d+'/mutations/bo6-completed-filter-v3/source-original.cs'),
 'note_on_hash_forms':'original/restored are the same working-tree bytes for every mutation (CRLF form, 5470cfcb...). The source batch manifest recorded 181aa93e... for original/restored (the LF repository-blob form of the same content) and recorded mutant hashes in the CRLF form; this batch records every hash in the CRLF working-tree form, so original/restored differ from the source manifest text while all eight mutant hashes match it exactly.',
 'mutations':[]}
allmatch=True
for r in recs:
    mid=r['id']; m=delm[mid]
    mans=r['mutant_sha256']==m['mutant_sha256']
    allmatch=allmatch and mans
    out['mutations'].append({
     'id':mid,'description':r['description'],'mutation_label':r['mutation_label'],
     'target_test_id':r['target_test_id'],'target_name':r['target_name'],
     'baseline':trx_one(r['baseline_trx']),'mutant':trx_one(r['mutant_trx']),'restored':trx_one(r['restored_trx']),
     'build_exit':r['build_exit'],'baseline_exit':r['baseline_exit'],'mutant_exit':r['mutant_exit'],'restored_exit':r['restored_exit'],
     'original_sha256':r['original_sha256'],'mutant_sha256':r['mutant_sha256'],'restored_sha256':r['restored_sha256'],
     'source_manifest_original_sha256':m['original_sha256'],'source_manifest_mutant_sha256':m['mutant_sha256'],
     'mutant_sha256_matches_source_record':mans,
     'source_original_sha256_is_LF_form_of_same_content':m['original_sha256']!=r['original_sha256'] and m['restored_sha256']==m['original_sha256'],
     'source_original_cs_file_sha256':sha(d+'/mutations/'+mid+'/source-original.cs'),
     'mutation_patch_sha256':sha(d+'/mutations/'+mid+'/mutation.patch'),
     'build_log_sha256':sha(d+'/mutations/'+mid+'/mutant/build.log'),
     'restored_equals_original':r['restored_sha256']==r['original_sha256'],
     'target_passed_failed_passed':[trx_one(r['baseline_trx'])['outcome'],trx_one(r['mutant_trx'])['outcome'],trx_one(r['restored_trx'])['outcome']]==['Passed','Failed','Passed']})
out['all_eight_mutant_hashes_match_source_records']=allmatch
io.open(v+'/mutation-verification.json','w',encoding='utf-8',newline='\n').write(json.dumps(out,ensure_ascii=False,indent=2)+"\n")
print('mutant hashes all match source records:',allmatch)
for m in out['mutations']:
    print('  %-30s %s -> %s  target=%s %s' % (m['id'], m['target_passed_failed_passed'], m['mutant_sha256'][:12], m['target_test_id'][:8], m['mutant']['assertion_line']))
