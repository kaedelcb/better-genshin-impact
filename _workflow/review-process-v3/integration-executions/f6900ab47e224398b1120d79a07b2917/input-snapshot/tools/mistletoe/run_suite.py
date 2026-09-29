"""Run the tooling suite and emit actual unittest outcomes as TRX (no synthetic pass)."""
from pathlib import Path
import sys
import unittest
import uuid
import xml.etree.ElementTree as ET

class Result(unittest.TextTestResult):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.rows = []

    def addSuccess(self, test):
        super().addSuccess(test); self.rows.append((test.id(), 'Passed', ''))

    def addFailure(self, test, err):
        super().addFailure(test, err); self.rows.append((test.id(), 'Failed', self._exc_info_to_string(err, test)))

    def addError(self, test, err):
        super().addError(test, err); self.rows.append((test.id(), 'Failed', self._exc_info_to_string(err, test)))

    def addSkip(self, test, reason):
        super().addSkip(test, reason); self.rows.append((test.id(), 'NotExecuted', reason))

    def addSubTest(self, test, subtest, err):
        super().addSubTest(test, subtest, err)
        if err is not None:
            self.rows.append((subtest.id(), 'Failed', self._exc_info_to_string(err, test)))

def main():
    suite = unittest.defaultTestLoader.discover(str(Path(__file__).parent), pattern='test_*.py')
    result = unittest.TextTestRunner(verbosity=1, resultclass=Result).run(suite)
    root = ET.Element('TestRun', xmlns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    rows, defs = ET.SubElement(root, 'Results'), ET.SubElement(root, 'TestDefinitions')
    for name, outcome, message in result.rows:
        ident = str(uuid.uuid5(uuid.NAMESPACE_URL, name))
        row = ET.SubElement(rows, 'UnitTestResult', testId=ident, testName=name, executionId=str(uuid.uuid4()), outcome=outcome)
        if message:
            info = ET.SubElement(ET.SubElement(row, 'Output'), 'ErrorInfo')
            ET.SubElement(info, 'Message').text = message
            ET.SubElement(info, 'StackTrace').text = message
        definition = ET.SubElement(defs, 'UnitTest', id=ident)
        ET.SubElement(definition, 'TestMethod', className=name.rpartition('.')[0], name=name.rpartition('.')[2])
    passed = sum(r[1] == 'Passed' for r in result.rows)
    failed = sum(r[1] == 'Failed' for r in result.rows)
    skipped = sum(r[1] == 'NotExecuted' for r in result.rows)
    summary = ET.SubElement(root, 'ResultSummary', outcome='Completed' if result.wasSuccessful() else 'Failed')
    ET.SubElement(summary, 'Counters', total=str(len(result.rows)), passed=str(passed), failed=str(failed),
                  executed=str(passed + failed), notExecuted=str(skipped), error='0', aborted='0')
    Path(sys.argv[1]).write_bytes(ET.tostring(root, encoding='utf-8'))
    return 0 if result.wasSuccessful() else 1

if __name__ == '__main__':
    sys.exit(main())
