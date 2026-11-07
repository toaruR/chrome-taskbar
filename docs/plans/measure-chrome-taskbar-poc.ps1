[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Urls,
    [ValidateSet('visible','background','minimized','off-screen','maximized')][string]$WindowState = 'visible',
    [switch]$Diagnostics
)

$ErrorActionPreference = 'Stop'
$chrome = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
if (-not (Test-Path -LiteralPath $chrome)) { throw "Chrome not found: $chrome" }

Add-Type -AssemblyName UIAutomationClient
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ChromeTaskbarPocWin32 {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
}
'@

$profile = Join-Path $env:TEMP ("ChromeTaskbarPoc-" + [Guid]::NewGuid())
New-Item -ItemType Directory -Path $profile | Out-Null
$escapedProfile = [regex]::Escape($profile)
$start = [Diagnostics.Stopwatch]::StartNew()
try {
    # Start-Process joins its argument array. Quote every URL so Chrome receives
    # separate navigations rather than one whitespace-delimited argument.
    $quotedUrls = @($Urls | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' })
    Start-Process -FilePath $chrome -ArgumentList (@("--user-data-dir=$profile", '--no-first-run', '--no-default-browser-check', '--new-window') + $quotedUrls) | Out-Null
    $fixture = @()
    while ($start.ElapsedMilliseconds -lt 20000) {
        # Command-line profile matching prevents touching an unrelated Chrome instance.
        $fixture = @(Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" |
            Where-Object { $_.CommandLine -match $escapedProfile } |
            ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue } |
            Where-Object { $_.MainWindowHandle -ne 0 })
        if ($fixture.Count -eq 1) { break }
        Start-Sleep -Milliseconds 200
    }
    if ($fixture.Count -ne 1) { throw "Expected one profile-matched Chrome window; found $($fixture.Count)." }

    $window = $fixture[0]
    $hwnd = $window.MainWindowHandle
    switch ($WindowState) {
        'background' { [void][ChromeTaskbarPocWin32]::ShowWindow($hwnd, 9) }
        'minimized' { [void][ChromeTaskbarPocWin32]::ShowWindow($hwnd, 6) }
        'off-screen' { [void][ChromeTaskbarPocWin32]::ShowWindow($hwnd, 9) }
        'maximized' { [void][ChromeTaskbarPocWin32]::ShowWindow($hwnd, 3) }
    }
    Start-Sleep -Milliseconds 400
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)
    $tabs = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    $output = [ordered]@{
        profile = $profile; pid = $window.Id; hwnd = $hwnd.ToInt64(); observed_tabs = $tabs.Count
        minimized = [ChromeTaskbarPocWin32]::IsIconic($hwnd); duration_ms = [int]$start.ElapsedMilliseconds
    }
    if ($Diagnostics) {
        $processes = @(Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" |
            Where-Object { $_.CommandLine -match $escapedProfile })
        $output.supplied_urls = @($Urls)
        $output.fixture_window_count = $fixture.Count
        $output.command_lines = @($processes | ForEach-Object CommandLine)
        $output.tab_names = @($tabs | ForEach-Object Current.Name)
    }
    [pscustomobject]$output | ConvertTo-Json -Compress
}
finally {
    @(Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match $escapedProfile } |
        ForEach-Object { $_.ProcessId }) | ForEach-Object { Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 500
    Remove-Item -LiteralPath $profile -Recurse -Force -ErrorAction SilentlyContinue
}
