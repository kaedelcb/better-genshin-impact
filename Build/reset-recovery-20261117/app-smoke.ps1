param([Parameter(Mandatory=$true)][string]$Executable, [Parameter(Mandatory=$true)][string]$DataRoot)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -ReferencedAssemblies UIAutomationClient,UIAutomationTypes -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Automation;
public static class IsolatedWindowDriver {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    private delegate bool EnumWindowProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr h, int id);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    public static IntPtr FindExitConfirmation(int ownedPid) {
        IntPtr result = IntPtr.Zero;
        EnumWindows((h,p) => {
            GetWindowThreadProcessId(h, out uint pid);
            if (pid != ownedPid) return true;
            var text = new System.Text.StringBuilder(256);
            GetWindowText(h, text, text.Capacity);
            if (text.ToString() == "退出确认" && GetDlgItem(h, 6) != IntPtr.Zero) { result = h; return false; }
            return true;
        }, IntPtr.Zero);
        return result;
    }
    public static void ConfirmExit(IntPtr dialog, int ownedPid) {
        GetWindowThreadProcessId(dialog, out uint pid);
        var yes = GetDlgItem(dialog, 6);
        if (pid != ownedPid || yes == IntPtr.Zero) throw new InvalidOperationException("Not the owned exit dialog.");
        if (!PostMessage(yes, 0x00F5, IntPtr.Zero, IntPtr.Zero)) throw new InvalidOperationException("Could not click Yes.");
    }
    public static Task Invoke(AutomationElement element) {
        return Task.Run(() => ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke());
    }
}
'@
function Capture-Window([IntPtr]$Handle, [string]$Path) {
    $rect=[IsolatedWindowDriver+Rect]::new()
    if(-not [IsolatedWindowDriver]::GetWindowRect($Handle,[ref]$rect)){throw 'GetWindowRect failed.'}
    $bitmap=[System.Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
    try {
        $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $dc=$graphics.GetHdc()
            try {if(-not [IsolatedWindowDriver]::PrintWindow($Handle,$dc,2)){throw 'PrintWindow failed.'}}
            finally {$graphics.ReleaseHdc($dc)}
        } finally {$graphics.Dispose()}
        $bitmap.Save($Path,[System.Drawing.Imaging.ImageFormat]::Png)
    } finally {$bitmap.Dispose()}
}
if (Test-Path $DataRoot) { throw 'Smoke data root must be new; refusing to overwrite.' }
[IO.Directory]::CreateDirectory($DataRoot) | Out-Null
$env:NEXUSBGI_DATA_ROOT = $DataRoot
$configPath = Join-Path $DataRoot 'assistant-config.json'
@{
    StandaloneMode=$true; ServerUrl=''; PlayerUid='199999999'; PlayerName='恢复链隔离运行'; TeamUids=@('199999999');
    DisclaimerAccepted=$true; BgiPath=(Join-Path $DataRoot 'nonexistent/BetterGI.exe');
    AutoLaunchOnBoot=$false; AutoLaunchWithBgi=$false; GuardBgi=$false; CheckBgiUpdatesOnStartup=$false;
    ClientInstanceId=('isolated-smoke-' + [Guid]::NewGuid().ToString('N'))
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $configPath -Encoding UTF8
function Find-Id($Root,[string]$Id) {
    $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))
}
function Wait-For([scriptblock]$Read,[int]$Seconds=30) {
    $end=[DateTime]::UtcNow.AddSeconds($Seconds)
    do { $value=& $Read; if($value){return $value}; Start-Sleep -Milliseconds 150 } while([DateTime]::UtcNow -lt $end)
    throw "Timed out waiting for isolated UI: $($Read.ToString())"
}
$rounds=@()
for($round=1; $round -le 2; $round++) {
    $process=Start-Process -FilePath $Executable -ArgumentList '--no-auto-launch' -PassThru
    $closed=$false
    try {
        $window=Wait-For {
            $process.Refresh()
            if($process.HasExited){throw "Isolated app exited early: $($process.ExitCode)"}
            $processWindows=[System.Windows.Automation.AutomationElement]::RootElement.FindAll(
                [System.Windows.Automation.TreeScope]::Children,
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id))
            foreach($candidate in $processWindows) {
                if(Find-Id $candidate 'HeaderSettingsButton'){return $candidate}
            }
        }
        $settings=Find-Id $window 'HeaderSettingsButton'
        $click=[IsolatedWindowDriver]::Invoke($settings)
        $null=Wait-For {
            $all=$window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
            foreach($item in $all){if($item.Current.Name -like '*配置保存于 assistant-config.json*'){return $true}}
        }
        if(-not $click.Wait(3000)){throw 'Settings invocation did not return.'}
        $snapshot=Join-Path $DataRoot ("app-settings-round-$round.png")
        Capture-Window ([IntPtr]$window.Current.NativeWindowHandle) $snapshot
        $all=$window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
        $names=@($all | ForEach-Object { $_.Current.Name } | Where-Object { $_ })
        $names | ConvertTo-Json | Set-Content (Join-Path $DataRoot ("ui-round-$round.json")) -Encoding UTF8
        $exit=Find-Id $window 'HeaderExitButton'
        if(-not $exit){throw 'Production exit button missing.'}
        $exitClick=[IsolatedWindowDriver]::Invoke($exit)
        $dialog=Wait-For {
            if($exitClick.IsFaulted){throw $exitClick.Exception}
            $handle=[IsolatedWindowDriver]::FindExitConfirmation($process.Id)
            if($handle -ne [IntPtr]::Zero){return $handle}
        } 10
        Capture-Window $dialog (Join-Path $DataRoot ("exit-confirmation-round-$round.png"))
        [IsolatedWindowDriver]::ConfirmExit($dialog,$process.Id)
        if(-not $process.WaitForExit(20000)){throw 'Isolated app did not exit after confirming exit.'}
        if($process.ExitCode -ne 0){throw "Isolated app exited with code $($process.ExitCode)."}
        $closed=$true
        $saved=Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
        if($saved.PlayerName -ne '恢复链隔离运行' -or -not $saved.StandaloneMode -or $saved.AutoLaunchOnBoot -or $saved.AutoLaunchWithBgi){throw 'Isolated configuration did not survive shutdown.'}
        $rounds+=@{round=$round;pid=$process.Id;exitCode=$process.ExitCode;screenshot=$snapshot;settingsInteracted=$true;dataRetained=$true}
    } finally {
        # Never kill another process or the user's BGI. A failed isolated process is
        # left identifiable in the result instead of concealing a shutdown failure.
        if(-not $closed -and -not $process.HasExited){Write-Warning "Isolated process still running: pid=$($process.Id), exe=$Executable, root=$DataRoot"}
        $process.Dispose()
    }
}
$rounds | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $DataRoot 'smoke-result.json') -Encoding UTF8
Write-Output 'ISOLATED_APP_START_INTERACT_EXIT_RESTART_PASSED'
