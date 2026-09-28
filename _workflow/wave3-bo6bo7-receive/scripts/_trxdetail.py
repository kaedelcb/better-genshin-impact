import xml.etree.ElementTree as ET, collections, json, io
NS={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def detail(p):
    root=ET.parse(p).getroot()
    c=root.find('t:ResultSummary/t:Counters',NS); s=root.find('t:ResultSummary',NS)
    rows=[]
    for r in root.find('t:Results',NS):
        rows.append((r.get('outcome'), r.get('testName')))
    oc=collections.Counter(o for o,_ in rows)
    return {'path':p.replace('\\','/'),'counters':dict(c.attrib),'summary_outcome':s.get('outcome'),
            'row_outcomes':dict(oc),
            'not_executed_names':[n for o,n in rows if o=='NotExecuted'],
            'failed_names':[n for o,n in rows if o=='Failed']}
for p in ['_workflow/wave3-bo6bo7-receive/baseline/assistant-full-baseline.trx',
          '_workflow/wave3-bo6bo7-receive/regression/final/assistant-full-1564.trx',
          '_workflow/wave3-bo6bo7-receive/baseline/targeted-baseline.trx',
          '_workflow/wave3-bo6bo7-receive/regression/final/targeted-93.trx']:
    d=detail(p)
    print(d['path'].split('/')[-1])
    print('   counters:',d['counters'])
    print('   row_outcomes:',d['row_outcomes'],' summary:',d['summary_outcome'])
    for n in d['not_executed_names']: print('   NotExecuted:',n)
    print()
