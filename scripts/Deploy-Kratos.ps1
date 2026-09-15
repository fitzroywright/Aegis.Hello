param(
    [string]$KratosHost = "root@kratos",
    [string]$RemoteStage = "/tmp/aegis-hello-publish"
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$PublishDir = Join-Path $RepoRoot "artifacts\kratos-publish"
$StageDir = Join-Path $RepoRoot "artifacts\kratos-stage"

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host " AEGIS HELLO -> KRATOS DEPLOYMENT" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

Push-Location $RepoRoot
try {
    Write-Host "Verifying authoritative main..." -ForegroundColor Yellow
    $branch = (git branch --show-current).Trim()
    if ($branch -ne "main") {
        throw "Aegis.Hello must be deployed from main. Current branch: $branch"
    }

    $status = git status --porcelain
    if ($status) {
        throw "Repository has uncommitted changes. Deployment stopped.`n$status"
    }

    git pull --ff-only origin main
    if ($LASTEXITCODE -ne 0) { throw "git pull failed." }

    if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
    if (Test-Path $StageDir) { Remove-Item $StageDir -Recurse -Force }
    New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null
    New-Item -ItemType Directory -Path $StageDir -Force | Out-Null

    Write-Host "Publishing Aegis.Hello..." -ForegroundColor Yellow
    dotnet restore .\Aegis.Hello.csproj
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed." }

    dotnet build .\Aegis.Hello.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }

    dotnet publish .\Aegis.Hello.csproj -c Release --no-build -o $PublishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

    Copy-Item (Join-Path $PublishDir "*") $StageDir -Recurse -Force
    New-Item -ItemType Directory -Path (Join-Path $StageDir "deploy\kratos") -Force | Out-Null
    Copy-Item (Join-Path $RepoRoot "deploy\kratos\aegis-hello.service") (Join-Path $StageDir "deploy\kratos\aegis-hello.service") -Force
    Copy-Item (Join-Path $RepoRoot "deploy\kratos\install-hello.sh") (Join-Path $StageDir "deploy\kratos\install-hello.sh") -Force

    Write-Host "Uploading publish to $KratosHost..." -ForegroundColor Yellow
    ssh $KratosHost "rm -rf '$RemoteStage' && mkdir -p '$RemoteStage'"
    if ($LASTEXITCODE -ne 0) { throw "Unable to create remote staging directory." }

    scp -r "$StageDir\*" "${KratosHost}:$RemoteStage/"
    if ($LASTEXITCODE -ne 0) { throw "scp upload failed." }

    Write-Host "Installing on Kratos..." -ForegroundColor Yellow
    ssh $KratosHost "chmod +x '$RemoteStage/deploy/kratos/install-hello.sh' && '$RemoteStage/deploy/kratos/install-hello.sh' '$RemoteStage'"
    if ($LASTEXITCODE -ne 0) { throw "Kratos installer failed." }

    Write-Host "`nDeployment files installed successfully." -ForegroundColor Green
    Write-Host "If /etc/aegis/hello.env already contains the registration key, Hello was restarted." -ForegroundColor Green
    Write-Host "Otherwise add the key on Kratos and run: systemctl restart aegis-hello" -ForegroundColor Yellow
}
finally {
    Pop-Location
}
