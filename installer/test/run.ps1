# Runs an installer exe with no window and prints its exit code, stdout and stderr.
param([string]$Exe, [string[]]$Args2)
$out = [IO.Path]::GetTempFileName(); $err = [IO.Path]::GetTempFileName()
$p = Start-Process -FilePath $Exe -ArgumentList $Args2 -Wait -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError $err
"exit $($p.ExitCode)"
Get-Content $out | ForEach-Object { "  out: $_" }
Get-Content $err | ForEach-Object { "  err: $_" }
Remove-Item $out, $err
