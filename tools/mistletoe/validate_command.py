"""Run isolated validation with retained logs and automatic generated-binary cleanup.

Example: python -B tools/mistletoe/validate_command.py --root . -- dotnet test
Test/Example.csproj --artifacts-path {artifacts} -p:DeployToBgiTools=false
"""
import argparse
import os
from pathlib import Path
import sys
import storage_limits as storage
import process_runner
from review_support import encode, require


def run(root, argv, timeout=600):
    root=Path(root).resolve()
    require(any('{artifacts}' in a for a in argv), 'command must use isolated {artifacts} output')
    with storage.Session(root, 'isolated-validation') as guard:
        artifacts=guard.temporary_output()
        records=artifacts.parent/'records'
        recovery=records/'recovery'
        guard.entry['recovery_directories']=[str(recovery)]
        guard.save()
        expanded=[a.replace('{artifacts}',str(artifacts)) for a in argv]
        guard.write(records/'command.json', encode({'argv':expanded,'cwd':str(root)}))
        code, stdout, stderr=process_runner.run(expanded, cwd=root, env=os.environ.copy(),
            directory=records,recovery_directory=recovery,phase='validation',timeout=timeout)
        guard.write(records/'result.json', encode({'exit_code':code,
                    'stdout':str(stdout),'stderr':str(stderr)}))
        if code:
            # A nonzero command is a failed validation, even though containment completed normally.
            raise ValidationFailed(code, records)
    return records


class ValidationFailed(Exception):
    def __init__(self, code, records):
        self.code=code; self.records=records
        super().__init__('validation failed: '+str(code))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root',required=True)
    parser.add_argument('--timeout',type=int,default=600)
    parser.add_argument('command',nargs=argparse.REMAINDER)
    args=parser.parse_args()
    command=args.command[1:] if args.command[:1]==['--'] else args.command
    try:
        print(run(args.root,command,args.timeout))
    except ValidationFailed as error:
        print(error.records)
        sys.exit(error.code)
