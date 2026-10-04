from pathlib import Path
p = Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs')
b = p.read_bytes().replace(b'(TaskCenterHost.AdmissionTestSeams)',b'(TaskCenterAdmissionSeams)').replace(b'GetValue<string>() == "nodeExecution"',b'GetValue<int>() == (int)OperationType.NodeExecution')
p.write_bytes(b)
