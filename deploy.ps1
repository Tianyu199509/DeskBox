param([string]$zipFile)

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "C:\Windows\System32\OpenSSH\ssh.exe"
$psi.Arguments = "-o StrictHostKeyChecking=no -o ConnectTimeout=15 -tt root@8.148.87.90"
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true

$proc = [System.Diagnostics.Process]::Start($psi)

Start-Sleep -Seconds 2

# Upload
Write-Host "Uploading..."
$upload = Start-Process -FilePath "C:\Windows\System32\OpenSSH\scp.exe" -ArgumentList "-o StrictHostKeyChecking=no `"$zipFile`" root@8.148.87.90:/var/www/deskbox/" -NoNewWindow -Wait -PassThru
if ($upload.ExitCode -ne 0) { Write-Host "Upload failed"; exit 1 }

# Extract + fix permissions
$cmd = @"
cd /var/www/deskbox && \
rm -rf static && mkdir static && \
unzip -o static-site.zip -d static && \
find static -type d -exec chmod 755 {} + && \
find static -type f -exec chmod 644 {} + && \
rm static-site.zip && \
echo '=== Deploy complete ===' && \
curl -sI http://localhost/ | head -3
"@

$proc.StandardInput.WriteLine($cmd)
$proc.StandardInput.Flush()

Start-Sleep -Seconds 15
$proc.StandardInput.WriteLine("exit")
$proc.StandardInput.Flush()

Start-Sleep -Seconds 2

$stdout = $proc.StandardOutput.ReadToEnd()
if (!$proc.HasExited) { $proc.Kill() }

$clean = $stdout -replace '\x1b\[[0-9;]*[a-zA-Z]', ''
$clean = $clean -replace '\[[\?0-9;]*[a-zA-Z]', ''
Write-Host $clean
