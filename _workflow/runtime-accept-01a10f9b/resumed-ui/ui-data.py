from pathlib import Path
template=Path(__file__).parents[1]/'ui-data.py'
code=template.read_text(encoding='utf-8').replace('01a10f9b','01a10f9b-r2')
# Same physical test namespace, fresh preservation/archive identities. Original
# data and both prior test archives stay independent of this resumed run.
code=code.replace("before=inventory(live);prior_before=inventory(prior)","before=inventory(live);prior_before=inventory(prior);first_archive=parent/'NexusBGI-tested-01a10f9b';assert first_archive.is_dir();first_before=inventory(first_archive)")
code=code.replace('original=before,prior_archive=prior_before,','original=before,prior_archive=prior_before,first_test_archive=first_before,')
code=code.replace("assert inventory(parent/'NexusBGI-tested-01a10e1b')==record['prior_archive']", "assert inventory(parent/'NexusBGI-tested-01a10e1b')==record['prior_archive'];assert inventory(parent/'NexusBGI-tested-01a10f9b')==record['first_test_archive']")
exec(compile(code,str(template)+'[fresh-resumed-ui]','exec'))
