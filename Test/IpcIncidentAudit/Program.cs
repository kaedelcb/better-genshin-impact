using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.ExternalInterface;
using Microsoft.Extensions.Logging.Abstractions;
using MultiplayerHoeingAssistant.Services;

// Actual production sources. UI/game/transport boundaries remain inert.
var failed = 0; var total = 0;
async Task Check(string name, Func<Task> test)
{
    total++; PreemptionGate.Disarm(); CancellationContext.Instance.Set();
    RunnerContext.Instance.taskProgress = null; RunnerContext.Instance.IsContinuousRunGroup = false;
    TaskContext.Instance().Config.SuspendedTaskContext = null;
    ExternalInterfaceEventHub.Instance.OnSlotReleased = null;
    try {
        A(!ExecutionScope.HasActive && TaskControl.TaskSemaphore.CurrentCount == 1, "previous ownership leaked");
        await test();
        A(!ExecutionScope.HasActive && TaskControl.TaskSemaphore.CurrentCount == 1, "ownership leaked");
        Console.WriteLine("PASS " + name);
    } catch(Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
static void A(bool value, string why) { if(!value) throw new Exception(why); }
static string Key() => Guid.NewGuid().ToString("N");
static Task Done() => Task.CompletedTask;
static void Reject(Action action) {
    try { action(); } catch(InvalidOperationException) { return; }
    throw new Exception("unexpected admission");
}
static SuspendContextCapture.Snapshot Group(string name, int index=0) =>
    new("group", name, index, "routes", "route", 0, null, null, false);

await Check("idle friendship cannot create checkpoint", async () => {
    RunnerContext.Instance.taskProgress = new() { CurrentScriptGroupName="好感任务" };
    A(SuspendContextCapture.CaptureCurrent()==null,"history treated as live");
    PreemptionGate.Arm(Key()); var ran=false;
    var result=await new TaskRunner().RunCurrentAsync(()=>{ran=true;return Done();});
    A(result==TaskRunResult.RejectedSlotBusy&&!ran,"unowned task ran");
    A(TaskContext.Instance().Config.SuspendedTaskContext==null,"false restore created");
});
await Check("09:00 owned dragon runs UID and both groups", async () => {
    RunnerContext.Instance.taskProgress=new(){CurrentScriptGroupName="好感任务"};
    var ticket=Key();PreemptionGate.Arm(ticket);
    using var root=ExecutionScope.Start(new(JobKind.OneDragon,"联机锄地",JobSource.Ext,JobId:Guid.NewGuid(),TakeoverTicket:ticket));
    var ran=0;
    A(await new TaskRunner().RunCurrentAsync(()=>{ran++;return Done();})==TaskRunResult.Ran,"UID blocked");
    foreach(var name in new[]{"传奇","精英"})
        A(await new TaskRunner().RunThreadAsync(()=>{ran++;return Done();},job:new(JobKind.Group,name,JobSource.OneDragonInternal,ParentJobId:root.Descriptor.JobId))==TaskRunResult.Ran,"child blocked");
    A(ran==3&&root.Result==TaskRunResult.Ran,"missing actions");
    A(TaskContext.Instance().Config.SuspendedTaskContext==null,"friendship resurrected");
});
await Check("unrelated V2 cannot consume ticket",async()=>{
    var ticket=Key();PreemptionGate.Arm(ticket);
    A(await new TaskRunner().RunCurrentAsync(()=>throw new Exception("must not run"),job:new(JobKind.Group,Key(),JobSource.V2))==TaskRunResult.RejectedSlotBusy,"intruder ran");
    A(PreemptionGate.Authorize(ticket),"ticket stolen");
});
await Check("batch retains ownership across roots",async()=>{
    var ticket=Key();PreemptionGate.Arm(ticket);
    for(var i=0;i<2;i++){
        A(await new TaskRunner().RunCurrentAsync(Done,job:new(JobKind.Group,Key(),JobSource.Ext,TakeoverTicket:ticket))==TaskRunResult.Ran,"batch rejected");
        Reject(()=>{using var other=ExecutionScope.Start(new(JobKind.Solo,"local",JobSource.Ui));});
    }
    A(PreemptionGate.Release(ticket),"release failed");
    A(await new TaskRunner().RunCurrentAsync(Done)==TaskRunResult.Ran,"local control not restored");
});
await Check("duplicate suspend and foreign release",()=>{
    var ticket=Key();var epoch=PreemptionGate.Arm(ticket);
    A(PreemptionGate.TryMarkContextSaved(),"capture denied");
    A(PreemptionGate.Arm(ticket)==epoch&&!PreemptionGate.TryMarkContextSaved(),"duplicate reset checkpoint");
    A(!PreemptionGate.Release(Key())&&PreemptionGate.Authorize(ticket),"foreign release succeeded");return Done();
});
await Check("completed and revoked tickets cannot reacquire",()=>{
    var ticket=Key();PreemptionGate.Arm(ticket);PreemptionGate.Release(ticket);Reject(()=>PreemptionGate.Arm(ticket));
    ticket=Key();PreemptionGate.Arm(ticket);PreemptionGate.Disarm();Reject(()=>PreemptionGate.Arm(ticket));return Done();
});
await Check("checkpoint keeps dragon ancestor and node",()=>{
    using var root=ExecutionScope.Start(new(JobKind.OneDragon,"actual-root",JobSource.Ui));
    root.SetDragonNode(4);root.SetCheckpoint(Group("actual-child",7));
    RunnerContext.Instance.taskProgress=new(){CurrentScriptGroupName="other-history"};
    A(SuspendContextCapture.CaptureCurrent() is {TaskType:"onedragon",GroupName:"actual-root",OneDragonTaskIndex:4,SubTaskGroupName:"actual-child",TaskIndex:7},"ancestor lost");return Done();
});
await Check("completed root has no checkpoint",()=>{
    using(var root=ExecutionScope.Start(new(JobKind.Group,"old",JobSource.Ui)))root.SetCheckpoint(Group("old"));
    A(SuspendContextCapture.CaptureCurrent()==null,"completed root recoverable");return Done();
});
await Check("suspend in unlocked gap stops old loop",async()=>{
    using var root=ExecutionScope.Start(new(JobKind.OneDragon,"old",JobSource.Ui));root.SetDragonNode(2);
    var ticket=Key();PreemptionGate.Arm(ticket);var point=ExecutionScope.Suspend();
    A(point?.GroupName=="old"&&root.Token.IsCancellationRequested,"root not stopped");
    A(await new TaskRunner().RunCurrentAsync(()=>throw new Exception("must not run"))==TaskRunResult.Preempted,"old loop continued");
    Reject(()=>{using var next=ExecutionScope.Start(new(JobKind.Group,"online",JobSource.Ext,TakeoverTicket:ticket));});
});
await Check("checkpoint unaffected by post-release progress",()=>{
    SuspendContextCapture.Snapshot? point;
    using(var root=ExecutionScope.Start(new(JobKind.Group,"victim",JobSource.Ui))){root.SetCheckpoint(Group("victim",3));point=ExecutionScope.Suspend();}
    RunnerContext.Instance.taskProgress=new(){CurrentScriptGroupName="intruder"};
    SuspendContextCapture.Save(NullLogger.Instance,point!,"test");
    A(TaskContext.Instance().Config.SuspendedTaskContext?.GroupName=="victim","checkpoint changed");return Done();
});
await Check("thrown failure reaches result and registry",async()=>{
    var id=Guid.NewGuid();
    var result=await new TaskRunner().RunCurrentAsync(()=>throw new InvalidOperationException("injected"),job:new(JobKind.Group,Key(),JobSource.Ui,JobId:id));
    A(result==TaskRunResult.Failed&&JobRegistry.Instance.Query(id)?.State==JobState.Failed,"false success");
});
await Check("cancel exception without global flag is not success",async()=>{
    var id=Guid.NewGuid();
    var result=await new TaskRunner().RunCurrentAsync(()=>throw new TaskCanceledException(),job:new(JobKind.Group,Key(),JobSource.Ui,JobId:id));
    A(result==TaskRunResult.Cancelled&&JobRegistry.Instance.Query(id)?.State==JobState.Cancelled,"cancelled action succeeded");
});
await Check("caught project error survives later successful child",async()=>{
    using var root=ExecutionScope.Start(new(JobKind.OneDragon,"root",JobSource.Ui));
    A(await new TaskRunner().RunCurrentAsync(()=>{root.Observe(TaskRunResult.Failed);return Done();})==TaskRunResult.Failed,"caught error hidden");
    A(await new TaskRunner().RunCurrentAsync(Done)==TaskRunResult.Ran&&root.Result==TaskRunResult.Failed,"later child erased root failure");
});
await Check("disposed-gap manual stop survives Set",()=>{
    using var root=ExecutionScope.Start(new(JobKind.OneDragon,"root",JobSource.Ui));
    PreemptionGate.Arm(Key());CancellationContext.Instance.Clear();CancellationContext.Instance.ManualCancel();CancellationContext.Instance.Set();
    A(root.Token.IsCancellationRequested&&!PreemptionGate.IsArmed,"gap stop ignored");
    A(CancellationContext.Instance.IsInManualStopCooldown(TimeSpan.FromSeconds(30),out _),"cooldown erased");return Done();
});
await Check("busy semaphore never executes action",async()=>{
    await TaskControl.TaskSemaphore.WaitAsync();
    try{A(await new TaskRunner().RunCurrentAsync(()=>throw new Exception("must not run"))==TaskRunResult.RejectedSlotBusy,"busy admission wrong");}
    finally{TaskControl.TaskSemaphore.Release();}
});
await Check("explicit keys do not collapse by name",()=>{
    var r=new JobRegistry(false);var a=r.Submit(JobKind.Group,"same",JobSource.Ext,3,"a");var b=r.Submit(JobKind.Group,"same",JobSource.Ext,3,"b");
    A(!b.Adopted&&a.Job.JobId!=b.Job.JobId,"different keys merged");
    A(r.Submit(JobKind.Group,"same",JobSource.Ext,3,"a").Job.JobId==a.Job.JobId,"same key duplicated");return Done();
});
await Check("parent and repeated same-name children stay distinct",()=>{
    var r=new JobRegistry(false);var p=r.Submit(JobKind.OneDragon,"same",JobSource.Ui).Job;
    var a=r.Submit(JobKind.Group,"same",JobSource.OneDragonInternal,parentJobId:p.JobId).Job;
    var b=r.Submit(JobKind.Group,"same",JobSource.OneDragonInternal,parentJobId:p.JobId).Job;
    A(p.JobId!=a.JobId&&a.JobId!=b.JobId,"nodes merged");return Done();
});
await Check("old terminal cannot delete newer active index",()=>{
    var r=new JobRegistry(false);var a=r.Submit(JobKind.Group,"same",JobSource.Ui,3).Job;
    var b=r.Submit(JobKind.Group,"same",JobSource.Ext,3,jobId:Guid.NewGuid()).Job;
    r.TryMarkTerminal(a.JobId,JobState.Succeeded);
    A(r.Submit(JobKind.Group,"same",JobSource.Ext,3).Job.JobId==b.JobId,"new index deleted");return Done();
});
await Check("preemption is not user cancellation",()=>{
    var actions=BatchReconcileDecider.Decide([new("task",true){State=BatchItemState.Submitted,JobId="id"}],
        [new("id","task",3,"cancelled",true,"preempted")],true,3,DateTime.UtcNow);
    A(!actions.Any(a=>a is BatchReconcileAction.AbortUserCancelled),"preempt called F11");
    A(actions.OfType<BatchReconcileAction.ConfirmTerminal>().Single().ErrorCode=="preempted","reason dropped");return Done();
});
await Check("actual user cancellation aborts batch",()=>{
    var actions=BatchReconcileDecider.Decide([new("task",true){State=BatchItemState.Submitted,JobId="id"}],
        [new("id","task",3,"cancelled",true,"cancelled_user")],true,3,DateTime.UtcNow);
    A(actions.Any(a=>a is BatchReconcileAction.AbortUserCancelled),"stop ignored");return Done();
});
await Check("failed terminal without code retains failure",()=>{
    var actions=BatchReconcileDecider.Decide([new("task",true){State=BatchItemState.Submitted,JobId="id"}],
        [new("id","task",3,"failed",false,null)],true,3,DateTime.UtcNow);
    A(actions.OfType<BatchReconcileAction.ConfirmTerminal>().Single().ErrorCode!=null,"failed displayed success");return Done();
});
await Check("lost response attaches by request key not same-name child",()=>{
    var item=new BatchExpectedItem("same",true){State=BatchItemState.Submitted,RequestKey="request"};
    var actions=BatchReconcileDecider.Decide([item],
        [new("child","same",3,"succeeded",false,null,"other","Group","parent"),
         new("root","same",3,"running",false,null,"request","OneDragon")],true,3,DateTime.UtcNow);
    A(actions.OfType<BatchReconcileAction.Attach>().Single().JobId=="root","wrong attachment");return Done();
});
await Check("admission callback failure cannot leak ownership",()=>{
    Reject(()=>ExecutionScope.Start(new(JobKind.Group,Key(),JobSource.Ui,OnAdmitted:()=>throw new InvalidOperationException("admission failed"))));
    A(!ExecutionScope.HasActive && ExecutionScope.Current==null,"failed callback leaked root");
    using var root=ExecutionScope.Start(new(JobKind.Group,Key(),JobSource.Ui));return Done();
});
await Check("hotkey authority flows only through designated callback",async()=>{
    var ticket=Key();PreemptionGate.Arm(ticket);
    using (ExecutionScope.UseTakeoverTicket(ticket)) {
        await Task.Run(()=> { using var root=ExecutionScope.Start(new(JobKind.Solo,"hotkey",JobSource.Hotkey));
            A(root.Descriptor.TakeoverTicket==ticket,"ticket not inherited"); });
    }
    Reject(()=>ExecutionScope.Start(new(JobKind.Solo,"unrelated UI",JobSource.Ui)));
});
await Check("revoked hotkey authority cannot start delayed work",()=>{
    var ticket=Key();PreemptionGate.Arm(ticket);
    using var authority=ExecutionScope.UseTakeoverTicket(ticket);
    PreemptionGate.Release(ticket);
    Reject(()=>ExecutionScope.Start(new(JobKind.Solo,"late callback",JobSource.Hotkey)));return Done();
});
await Check("disposed root cancellation never affects successor",()=>{
    var old=ExecutionScope.Start(new(JobKind.Solo,"old",JobSource.Ui));old.Dispose();
    using var current=ExecutionScope.Start(new(JobKind.Solo,"current",JobSource.Ui));
    old.Cancel(); A(current.IsCurrentOwner&&!current.Token.IsCancellationRequested,"successor cancelled");return Done();
});
await Check("duplicate running job cannot overwrite real outcome",async()=>{
    var name=Key();var job=JobRegistry.Instance.Submit(JobKind.Group,name,JobSource.Ext).Job;
    JobRegistry.Instance.TryMarkRunning(job.JobId);
    var result=await new TaskRunner().RunCurrentAsync(()=>throw new Exception("duplicate executed"),
        job:new(JobKind.Group,name,JobSource.Ext,JobId:job.JobId));
    A(result==TaskRunResult.RejectedSlotBusy&&job.State==JobState.Running,"duplicate changed running attempt");
    JobRegistry.Instance.TryMarkTerminal(job.JobId,JobState.Succeeded);
});
await Check("accepted coordinator root can adopt running job",async()=>{
    var name=Key();var job=JobRegistry.Instance.Submit(JobKind.Group,name,JobSource.Ext).Job;
    JobRegistry.Instance.TryMarkRunning(job.JobId);
    var descriptor=new JobDescriptor(JobKind.Group,name,JobSource.Ext,JobId:job.JobId);
    using var root=ExecutionScope.Start(descriptor);
    A(await new TaskRunner().RunCurrentAsync(Done,job:descriptor)==TaskRunResult.Ran,"valid adoption rejected");
    A(job.State==JobState.Succeeded,"coordinator root not terminal");
});
await Check("wrong parent identity is never admitted",async()=>{
    using var root=ExecutionScope.Start(new(JobKind.OneDragon,Key(),JobSource.Ui,JobId:Guid.NewGuid()));
    A(await new TaskRunner().RunCurrentAsync(()=>throw new Exception("foreign child executed"),
        job:new(JobKind.Group,Key(),JobSource.OneDragonInternal,ParentJobId:Guid.NewGuid()))==TaskRunResult.RejectedSlotBusy,"wrong parent admitted");
});
await Check("missing screenshot initialization is failure not success",async()=>{
    TaskContext.Instance().IsInitialized=false;
    try { A(await new TaskRunner().RunCurrentAsync(()=>throw new Exception("must not execute"))==TaskRunResult.Failed,"initialization reported success"); }
    finally { TaskContext.Instance().IsInitialized=true; }
});
await Check("user stopped root cannot become recovery victim",()=>{
    using var root=ExecutionScope.Start(new(JobKind.Group,Key(),JobSource.Ui));
    root.SetCheckpoint(Group("actual"));ExecutionScope.StopActive(true);
    A(SuspendContextCapture.CaptureCurrent()==null && ExecutionScope.Suspend()==null,"stopped task saved");return Done();
});
await Check("accepted missing handle is unknown and never replayed",()=>{
    var item=new BatchExpectedItem("task",true){State=BatchItemState.Submitted,JobId="accepted",SubmitAttempts=1};
    var actions=BatchReconcileDecider.Decide([item],[],true,3,DateTime.UtcNow);
    A(!actions.Any(a=>a is BatchReconcileAction.Resubmit),"unknown execution replayed");
    A(actions.OfType<BatchReconcileAction.ConfirmTerminal>().Single().ErrorCode=="lost_job","unknown became success");return Done();
});
await Check("concurrent roots admit exactly one owner",async()=>{
    const int contenders=32;
    for (var round=0;round<25;round++) {
        var attempted=0;var accepted=0;
        var start=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allAttempted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks=Enumerable.Range(0,contenders).Select(_=>Task.Run(async()=>{
            await start.Task;
            ExecutionScope? root=null;
            try {
                try { root=ExecutionScope.Start(new(JobKind.Group,Key(),JobSource.Ui));
                    Interlocked.Increment(ref accepted); }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith("task_busy:")) { }
                finally { if (Interlocked.Increment(ref attempted)==contenders) allAttempted.TrySetResult(); }
                if (root!=null) await allAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { root?.Dispose(); }
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15));
        A(accepted==1,$"round {round}: admitted {accepted} roots");
        A(!ExecutionScope.HasActive,"concurrent owner leaked");
    }
});
await Check("expired lease cancels owner without admitting overlap",async()=>{
    var ticket=Key();PreemptionGate.Arm(ticket);
    using var root=ExecutionScope.Start(new(JobKind.OneDragon,Key(),JobSource.Ext,TakeoverTicket:ticket));
    const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static;
    var gateSync=typeof(PreemptionGate).GetField("Sync",flags)!.GetValue(null)!;
    // Only this standalone process's in-memory clock state is changed; no product IPC is used.
    lock (gateSync) typeof(PreemptionGate).GetField("_armedAtUtc",flags)!
        .SetValue(null,DateTime.UtcNow-PreemptionGate.IntentTtl-TimeSpan.FromSeconds(1));
    var cancelled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var registration=root.Token.Register(()=>cancelled.TrySetResult());
    await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    A(root.Result==TaskRunResult.Cancelled&&root.StopReason=="lease_expired","expiry not propagated");
    A(!root.IsCurrentOwner&&!PreemptionGate.Authorize(ticket),"expired authority retained");
    A(!PreemptionGate.Renew(ticket),"expired ticket renewed");
    Reject(()=>PreemptionGate.Arm(ticket));
    Reject(()=>ExecutionScope.Start(new(JobKind.Group,"must wait for disposal",JobSource.Ui)));
});
await Check("lost acknowledgement retry is bounded and retains request key",()=>{
    var key=Key();
    var item=new BatchExpectedItem("task",true){State=BatchItemState.Submitted,RequestKey=key,SubmitAttempts=1};
    for (var attempt=1;attempt<BatchReconcileDecider.MaxSubmitAttempts;attempt++) {
        item.SubmitAttempts=attempt;
        var actions=BatchReconcileDecider.Decide([item],[],true,3,DateTime.UtcNow);
        A(actions.OfType<BatchReconcileAction.Resubmit>().Single().Index==0,"retry missing");
        A(!actions.Any(a=>a is BatchReconcileAction.ConfirmTerminal),"premature terminal");
        A(item.RequestKey==key&&item.JobId==null,"retry changed request identity");
    }
    item.SubmitAttempts=BatchReconcileDecider.MaxSubmitAttempts;
    var exhausted=BatchReconcileDecider.Decide([item],[],true,3,DateTime.UtcNow);
    A(!exhausted.Any(a=>a is BatchReconcileAction.Resubmit),"unbounded retries");
    A(exhausted.OfType<BatchReconcileAction.ConfirmTerminal>().Single().ErrorCode=="result_unknown","unknown result called success");
    A(item.RequestKey==key,"exhaustion changed request identity");return Done();
});
Console.WriteLine($"Regression total={total}; passed={total-failed}; failed={failed}. UI/game/transport not exercised.");
Environment.ExitCode=failed==0?0:1;
