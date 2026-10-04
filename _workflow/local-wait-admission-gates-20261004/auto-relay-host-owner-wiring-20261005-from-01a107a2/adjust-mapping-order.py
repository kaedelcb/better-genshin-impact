from pathlib import Path
p=Path('MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs')
raw=p.read_bytes(); t=raw.decode('utf-8').replace('\r\n','\n')
block='''        try
        {
            if (!_runs.BindOriginalAdmissionMapping(runId, op)) return new SendOutcome.Unknown("original_mapping_not_persisted");
        }
        catch (Exception ex) { return new SendOutcome.Unknown("original_mapping_publish_" + ex.GetType().Name); }

'''
for name in ['DispatchViaHostAsync','DispatchResumeViaHostAsync']:
    start=t.index('    private async Task<SendOutcome> '+name)
    end=t.index('    private ',start+10)
    part=t[start:end]
    assert part.count(block)==1
    part=part.replace(block,'')
    marker='        WorkflowRunner runner;'
    assert part.count(marker)==1
    adjusted=block.replace('return new SendOutcome.Unknown("original_mapping_not_persisted");','throw new RunRecordConflictException("original_mapping_not_persisted");')
    adjusted=adjusted.replace('catch (Exception ex) { return new SendOutcome.Unknown("original_mapping_publish_" + ex.GetType().Name); }','catch (Exception ex)\n        {\n            lock (_gate) _reservedWorkflows.Remove(workflowId);\n            return new SendOutcome.Unknown("original_mapping_publish_" + ex.GetType().Name);\n        }')
    part=part.replace(marker,adjusted+marker)
    t=t[:start]+part+t[end:]
p.write_bytes(t.replace('\n','\r\n').encode('utf-8'))
