import pathlib
p=pathlib.Path("MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs")
s=p.read_text(encoding="utf-8")
def rep(old,new,cnt=1):
    global s
    assert s.count(old)==cnt,(s.count(old),old[:110])
    s=s.replace(old,new,cnt)

# 修正 try/finally 与既有 catch 的顺序（apply 路径）
rep('''            finally
            {
                _effectCallInProgress = false;
            }
            if (CurrentStageOrNone() != stageBeforeEffect)
                return MarkBlocked("concurrent_state_change_after_effect:" + stageBeforeEffect + "->" + CurrentStageOrNone());
            catch (Exception ex)      // 副作用可能已发生 ⇒ fail-closed，绝不重复执行（会诊 IMPORTANT-8）
            {
                return MarkBlocked("reference_update_exception_unknown:" + ex.GetType().Name);
            }''',
'''            catch (Exception ex)      // 副作用可能已发生 ⇒ fail-closed，绝不重复执行（会诊 IMPORTANT-8）
            {
                return MarkBlocked("reference_update_exception_unknown:" + ex.GetType().Name);
            }
            finally
            {
                _effectCallInProgress = false;
            }
            if (CurrentStageOrNone() != stageBeforeEffect)
                return MarkBlocked("concurrent_state_change_after_effect:" + stageBeforeEffect + "->" + CurrentStageOrNone());''')
p.write_text(s,encoding="utf-8")
print("order fixed")
