# Starts a hidden grandchild that creates $Marker after 5 s, then waits: stopping the run must end both.
param([string]$Marker)
$ps = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
Start-Process -FilePath $ps -ArgumentList '-NoProfile', '-Command', "Start-Sleep 5; New-Item -ItemType File -Path '$Marker'" -WindowStyle Hidden
Write-Output 'started'
Start-Sleep 60
