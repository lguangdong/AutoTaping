# Requires: PowerShell 5.1 (.NET Framework 4.x), UI Automation
# Purpose: launch AutoTaping.App, log in, walk through every page and save PNG screenshots.
# All strings are ASCII on purpose: PS 5.1 parses non-BOM scripts as ANSI, so any Chinese
# literal in the source would arrive mangled. Chinese UI text is therefore built from
# code points (see below) and matched by exact string comparison.
param(
    [string]$OutDir  = '',
    [string]$ExePath = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutDir)  { $OutDir  = Join-Path $repoRoot 'docs\images' }
if (-not $ExePath) { $ExePath = Join-Path $repoRoot 'src\AutoTaping.App\bin\Debug\net8.0-windows\AutoTaping.App.exe' }

$log = Join-Path $PSScriptRoot 'capture-screenshots.log'
if (Test-Path $log) { Remove-Item $log -Force }

function Log([string]$message) {
    $line = '{0} {1}' -f (Get-Date -Format 'HH:mm:ss.fff'), $message
    Write-Host $line
    Add-Content -Path $log -Value $line -Encoding UTF8
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class Win32 {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWnd, EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
'@

$script:AE = [System.Windows.Automation.AutomationElement]
$script:TS = [System.Windows.Automation.TreeScope]
$script:AppPid = 0

function ToText([int[]]$codePoints) { -join ($codePoints | ForEach-Object { [char]$_ }) }

$script:OkText     = ToText @(0x786E, 0x5B9A)                   # OK
$script:SetOnText  = (ToText @(0x7F6E)) + ' ON'                # set DI ON
$script:AckAllText = ToText @(0x5168, 0x90E8, 0x786E, 0x8BA4)  # ack all alarms

# ---------- helpers ----------

function Get-AppWindows {
    param([int]$ProcessId)
    $cond = New-Object System.Windows.Automation.PropertyCondition($script:AE::ProcessIdProperty, $ProcessId)
    $all = $script:AE::RootElement.FindAll($script:TS::Children, $cond)
    $result = @()
    foreach ($w in $all) {
        try {
            if ($w.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) { $result += $w }
        } catch { }
    }
    ,$result
}

function Wait-AppWindow {
    param([int]$ProcessId, [int]$TimeoutSec = 30)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $wins = Get-AppWindows -ProcessId $ProcessId
        if ($wins.Count -gt 0) { return $wins[0] }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Wait-NewWindow {
    param([int]$ProcessId, [IntPtr[]]$Exclude, [int]$TimeoutSec = 20)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        foreach ($w in (Get-AppWindows -ProcessId $ProcessId)) {
            if ($Exclude -notcontains ([IntPtr]$w.Current.NativeWindowHandle)) { return $w }
        }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Find-ById {
    param($Root, [string]$AutomationId)
    $cond = New-Object System.Windows.Automation.PropertyCondition($script:AE::AutomationIdProperty, $AutomationId)
    return $Root.FindFirst($script:TS::Descendants, $cond)
}

function Find-ByType {
    param($Root, $ControlType)
    $cond = New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $ControlType)
    $found = $Root.FindAll($script:TS::Descendants, $cond)
    foreach ($e in $found) { $e }
}

function Find-ButtonByName {
    param($Root, [string]$Name)
    $found = Find-ByType -Root $Root -ControlType ([System.Windows.Automation.ControlType]::Button)
    @($found | Where-Object { $_.Current.Name -eq $Name })
}

function Invoke-Element {
    param($Element)
    try {
        $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    } catch [System.Windows.Automation.ElementNotAvailableException] {
        Log '  (element closed while invoking)'
    } catch [System.InvalidOperationException] {
        Log ('  (invoke rejected: ' + $_.Exception.Message + ')')
    }
}

function Get-Rect {
    param($Element)
    $r = New-Object Win32+RECT
    [void][Win32]::GetWindowRect([IntPtr]$Element.Current.NativeWindowHandle, [ref]$r)
    return $r
}

# --- modal dialogs: the WPF MessageBox is a top level window of class #32770 ---

function Get-DialogHandles {
    $script:foundHandles = @()
    $callback = [Win32+EnumWindowsProc]{
        param([IntPtr]$hWnd, [IntPtr]$lParam)
        $procId = [uint32]0
        [void][Win32]::GetWindowThreadProcessId($hWnd, [ref]$procId)
        if ($procId -eq $script:AppPid -and [Win32]::IsWindowVisible($hWnd)) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Win32]::GetClassName($hWnd, $sb, 256)
            if ($sb.ToString() -eq '#32770') { $script:foundHandles += $hWnd }
        }
        return $true
    }
    [void][Win32]::EnumWindows($callback, [IntPtr]::Zero)
    ,$script:foundHandles
}

function Get-DialogButtons {
    param([IntPtr]$Dialog)
    $script:dialogButtons = @()
    $callback = [Win32+EnumWindowsProc]{
        param([IntPtr]$hWnd, [IntPtr]$lParam)
        $sb = New-Object System.Text.StringBuilder 256
        [void][Win32]::GetClassName($hWnd, $sb, 256)
        if ($sb.ToString() -eq 'Button') { $script:dialogButtons += $hWnd }
        return $true
    }
    [void][Win32]::EnumChildWindows($Dialog, $callback, [IntPtr]::Zero)
    ,$script:dialogButtons
}

function Dismiss-Dialogs {
    param([int]$Rounds = 10)
    for ($i = 0; $i -lt $Rounds; $i++) {
        $handles = Get-DialogHandles
        if ($handles.Count -eq 0) { return }

        foreach ($h in $handles) {
            if (-not [Win32]::IsWindow($h)) { continue }

            $title = ''
            try { $title = $script:AE::FromHandle($h).Current.Name } catch { }

            # Native message boxes do not expose their buttons through UI Automation
            # (verified: 0 buttons), so the keyboard is the only reliable way in:
            #   one button  -> ENTER hits the default OK button
            #   two buttons -> the default is Cancel, TAB moves to OK, SPACE presses it
            $kids = Get-DialogButtons -Dialog $h
            Log ('  dismissing dialog: ' + $title + ' (buttons=' + $kids.Count + ')')

            [void][Win32]::SetForegroundWindow($h)
            Start-Sleep -Milliseconds 300

            if ($kids.Count -ge 2) {
                [System.Windows.Forms.SendKeys]::SendWait('{TAB}')
                Start-Sleep -Milliseconds 250
                [System.Windows.Forms.SendKeys]::SendWait(' ')
            } else {
                [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
            }

            Start-Sleep -Milliseconds 600
        }
    }
}

function Save-WindowShot {
    param($Element, [string]$FileName)
    Dismiss-Dialogs
    [void][Win32]::SetForegroundWindow([IntPtr]$Element.Current.NativeWindowHandle)
    Start-Sleep -Milliseconds 500

    $r = Get-Rect -Element $Element
    $screen = [System.Windows.Forms.SystemInformation]::VirtualScreen

    $left = [Math]::Max($r.Left, $screen.Left)
    $top = [Math]::Max($r.Top, $screen.Top)
    $right = [Math]::Min($r.Right, $screen.Right)
    $bottom = [Math]::Min($r.Bottom, $screen.Bottom)
    $width = $right - $left
    $height = $bottom - $top

    if ($width -le 0 -or $height -le 0) { Log "  skip $FileName (window outside screen)"; return }

    $bmp = New-Object System.Drawing.Bitmap $width, $height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($left, $top, 0, 0, (New-Object System.Drawing.Size $width, $height))
    $g.Dispose()

    $path = Join-Path $OutDir $FileName
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Log "  saved $FileName ($width x $height)"
}

# ---------- start app ----------

if (-not (Test-Path $ExePath)) { throw "exe not found: $ExePath" }
Get-Process -Name 'AutoTaping.App' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Log "starting $ExePath"
$proc = Start-Process -FilePath $ExePath -WorkingDirectory (Split-Path $ExePath -Parent) -PassThru
$script:AppPid = [int]$proc.Id
Log "pid=$($proc.Id)"

$login = Wait-AppWindow -ProcessId $proc.Id -TimeoutSec 40
if (-not $login) { throw 'login window not found' }
Log ('login window: ' + $login.Current.Name)
[void][Win32]::SetForegroundWindow([IntPtr]$login.Current.NativeWindowHandle)
Start-Sleep -Milliseconds 800
Save-WindowShot -Element $login -FileName '01-login.png'

# ---------- login ----------

$userBox = Find-ById -Root $login -AutomationId 'UserNameInput'
$pwdBox = Find-ById -Root $login -AutomationId 'PasswordInput'
if (-not $userBox) { throw 'UserNameInput not found' }
if (-not $pwdBox) { throw 'PasswordInput not found' }

$userBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('admin')
[void][Win32]::SetForegroundWindow([IntPtr]$login.Current.NativeWindowHandle)
Start-Sleep -Milliseconds 400
$pwdBox.SetFocus()
Start-Sleep -Milliseconds 400
$focused = $script:AE::FocusedElement
Log ('focused element: ' + $focused.Current.ControlType.ProgrammaticName + ' id=' + $focused.Current.AutomationId)
[System.Windows.Forms.SendKeys]::SendWait('123456')
Start-Sleep -Milliseconds 400

$loginButtons = @(Find-ByType -Root $login -ControlType ([System.Windows.Automation.ControlType]::Button))
Log "login buttons: $($loginButtons.Count)"
if ($loginButtons.Count -gt 1) {
    Invoke-Element -Element $loginButtons[$loginButtons.Count - 1]
} else {
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
}

Log 'credentials entered, waiting for main window'
$main = Wait-NewWindow -ProcessId $proc.Id -Exclude @([IntPtr]$login.Current.NativeWindowHandle) -TimeoutSec 30
if (-not $main) {
    foreach ($w in (Get-AppWindows -ProcessId $proc.Id)) {
        Log ('  window still open: ' + $w.Current.Name)
        foreach ($t in @(Find-ByType -Root $w -ControlType ([System.Windows.Automation.ControlType]::Text))) {
            Log ('    text: ' + $t.Current.Name)
        }
    }
    throw 'main window not found'
}

Start-Sleep -Seconds 3
Log ('main window: ' + $main.Current.Name)

$mainHwnd = [IntPtr]$main.Current.NativeWindowHandle
[void][Win32]::SetForegroundWindow($mainHwnd)
Start-Sleep -Milliseconds 500
Dismiss-Dialogs

# ---------- classify buttons ----------

$winRect = Get-Rect -Element $main
$buttons = @(Find-ByType -Root $main -ControlType ([System.Windows.Automation.ControlType]::Button))
$nav = @($buttons | Where-Object { ([int]$_.Current.BoundingRectangle.X - $winRect.Left) -lt 20 } |
         Sort-Object { [int]$_.Current.BoundingRectangle.Y })
$opbar = @($buttons | Where-Object { ([int]$_.Current.BoundingRectangle.Y - $winRect.Top) -gt ($winRect.Bottom - $winRect.Top - 140) } |
           Sort-Object { [int]$_.Current.BoundingRectangle.X })
Log "buttons=$($buttons.Count) nav=$($nav.Count) opbar=$($opbar.Count)"

function Open-Page {
    param([int]$Index, [int]$SettleSeconds = 2)
    Log "opening nav index $Index"
    Invoke-Element -Element $nav[$Index]
    Start-Sleep -Seconds $SettleSeconds
    Dismiss-Dialogs
}

# ---------- acknowledge leftovers so the status bar starts clean ----------

if ($nav.Count -ge 8) {
    Open-Page -Index 4
    $ackAll = @(Find-ButtonByName -Root $main -Name $script:AckAllText)
    if ($ackAll.Count -gt 0) {
        Log 'acknowledging previous alarms'
        Invoke-Element -Element $ackAll[0]
        Start-Sleep -Seconds 1
        Dismiss-Dialogs
    } else {
        Log 'WARN: ack-all button not found'
    }
}

# ---------- 02: auto page, idle ----------

Open-Page -Index 0
Start-Sleep -Seconds 2
Save-WindowShot -Element $main -FileName '02-auto-idle.png'

# ---------- 09: IO page, simulate sensor signals ----------

Open-Page -Index 6
Start-Sleep -Seconds 1

$setOn = @(Find-ButtonByName -Root $main -Name $script:SetOnText)
$combos = @(Find-ByType -Root $main -ControlType ([System.Windows.Automation.ControlType]::ComboBox))
Log "IO page: setOn=$($setOn.Count) combos=$($combos.Count)"
$diCombo = $combos[$combos.Count - 1]

function Simulate-DiPoint {
    param([int]$DownPresses)
    if ($DownPresses -gt 0) {
        $diCombo.SetFocus()
        Start-Sleep -Milliseconds 250
        for ($k = 0; $k -lt $DownPresses; $k++) {
            [System.Windows.Forms.SendKeys]::SendWait('{DOWN}')
            Start-Sleep -Milliseconds 150
        }
        Start-Sleep -Milliseconds 250
    }
    $btn = @(Find-ButtonByName -Root $main -Name $script:SetOnText)
    if ($btn.Count -gt 0) { Invoke-Element -Element $btn[0]; Log '  DI set ON' }
    Start-Sleep -Milliseconds 700
}

# default selection is X00 (carrier in position); then walk down the DI combo
Simulate-DiPoint -DownPresses 0    # X00 carrier in position
Simulate-DiPoint -DownPresses 1    # X01 outfeed has part
Simulate-DiPoint -DownPresses 2    # X03 vacuum attached
Simulate-DiPoint -DownPresses 1    # X04 tape remaining

Start-Sleep -Seconds 1
Save-WindowShot -Element $main -FileName '09-io.png'

# ---------- 03: back to auto, reset + start ----------

Open-Page -Index 0
Start-Sleep -Seconds 1

if ($opbar.Count -ge 6) {
    Log 'pressing reset'
    Invoke-Element -Element $opbar[3]
    Start-Sleep -Seconds 5
    Dismiss-Dialogs

    Log 'pressing start'
    Invoke-Element -Element $opbar[0]
    Start-Sleep -Seconds 32
    Save-WindowShot -Element $main -FileName '03-auto-running.png'

    Log 'pressing stop'
    Invoke-Element -Element $opbar[2]
    Start-Sleep -Seconds 3
    Dismiss-Dialogs
} else {
    Log 'WARN: operation bar not recognized'
}

# ---------- 04..08, 10: remaining pages ----------

$pages = @(
    @{ Index = 1; File = '04-manual.png' },
    @{ Index = 2; File = '05-recipe.png' },
    @{ Index = 3; File = '06-vision.png' },
    @{ Index = 5; File = '08-output.png' },
    @{ Index = 7; File = '10-system.png' }
)

foreach ($page in $pages) {
    if ($page.Index -ge $nav.Count) { Log "WARN: nav index $($page.Index) out of range"; continue }
    Open-Page -Index $page.Index
    Start-Sleep -Seconds 2
    Save-WindowShot -Element $main -FileName $page.File
}

# ---------- 07: emergency stop first, then the alarm page ----------

if ($opbar.Count -ge 5) {
    Log 'pressing emergency stop to raise a real alarm'
    Invoke-Element -Element $opbar[4]
    Start-Sleep -Seconds 3
    Dismiss-Dialogs
}

Open-Page -Index 4
Start-Sleep -Seconds 2
Save-WindowShot -Element $main -FileName '07-alarm.png'

Log 'done'

# graceful shutdown so the next launch does not report a power loss (AL503)
[void][Win32]::PostMessage($mainHwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
$exited = $false
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Milliseconds 500
    if (-not (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue)) { $exited = $true; break }
}
if (-not $exited) { Get-Process -Id $proc.Id -ErrorAction SilentlyContinue | Stop-Process -Force }
Log ('app closed (graceful=' + $exited + ')')
