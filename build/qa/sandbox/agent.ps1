# Runs inside Windows Sandbox, started by the configuration's LogonCommand, so it runs in the signed-in
# session - which is where Install.cmd must run, since installing an app is per user. `wsb exec
# --run-as ExistingLogin` would be the obvious alternative and finds no session to run in (measured
# 22 Nov 2026), and SYSTEM cannot install a per-user package.
#
# Runs each job in C:\qa\jobs once, in name order, under Windows PowerShell 5.1 - what Install.cmd
# uses - and writes its output and exit code to C:\qa\out, which Invoke-SandboxQa.ps1 watches.
$jobs = 'C:\qa\jobs'; $out = 'C:\qa\out'
"agent started $(Get-Date -Format o) as $env:USERDOMAIN\$env:USERNAME" | Set-Content "$out\agent.txt"
while ($true) {
    foreach ($job in @(Get-ChildItem $jobs -Filter *.ps1 -ErrorAction SilentlyContinue | Sort-Object Name)) {
        $done = Join-Path $out "$($job.BaseName).done"
        if (Test-Path $done) { continue }
        $log = Join-Path $out "$($job.BaseName).txt"
        $p = Start-Process powershell.exe -Wait -PassThru -WindowStyle Hidden `
            -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$($job.FullName)`"" `
            -RedirectStandardOutput $log -RedirectStandardError "$log.err"
        "$($p.ExitCode)" | Set-Content $done
    }
    Start-Sleep -Seconds 2
}
