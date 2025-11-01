# Deploy RedMist Infrastructure to Kubernetes
param(
    [Parameter(Mandatory=$true)]
    [ValidateSet("dev", "test", "prod")]
    [string]$Environment
)

# Environment-specific configuration
$config = switch ($Environment) {
    "dev" { 
        @{
            namespace = "timing-dev"
            color = "Green"
        }
    }
    "test" { 
        @{
            namespace = "timing-test"
            color = "Yellow"
        }
    }
    "prod" { 
        @{
            namespace = "timing-prod"
            color = "Red"
        }
    }
}

Write-Host "Deploying RedMist Infrastructure to $($Environment.ToUpper()) environment..." -ForegroundColor $config.color
Write-Host "Namespace: $($config.namespace)" -ForegroundColor Cyan

# Production safety check
if ($Environment -eq "prod") {
    $confirm = Read-Host "Are you sure you want to deploy infrastructure to PRODUCTION? (yes/no)"
    if ($confirm -ne "yes") {
        Write-Host "Production infrastructure deployment cancelled." -ForegroundColor Yellow
        exit 1
    }
}

$deploymentFailed = $false

# Deploy Keycloak
Write-Host "`nDeploying Keycloak..." -ForegroundColor Cyan
helm upgrade --install keycloak-$Environment ./infrastructure/keycloak `
  --namespace keycloak `
  --create-namespace `
  --wait

if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ Keycloak deployment FAILED!" -ForegroundColor Red
    $deploymentFailed = $true
} else {
    Write-Host "✅ Keycloak deployed successfully" -ForegroundColor Green
}

# Deploy Cloudflare Tunnel
Write-Host "`nDeploying Cloudflare Tunnel..." -ForegroundColor Cyan
helm upgrade --install cloudflare-tunnel ./infrastructure/cloudflare-tunnel `
  --namespace cloudflare-tunnel `
  --create-namespace `
  --wait

if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ Cloudflare Tunnel deployment FAILED!" -ForegroundColor Red
    $deploymentFailed = $true
} else {
    Write-Host "✅ Cloudflare Tunnel deployed successfully" -ForegroundColor Green
}

# Summary
Write-Host "`n========================================" -ForegroundColor $config.color
if ($deploymentFailed) {
    Write-Host "❌ Infrastructure deployment completed with ERRORS!" -ForegroundColor Red
    exit 1
} else {
    Write-Host "✅ All infrastructure components deployed successfully!" -ForegroundColor $config.color
    Write-Host "`nNext step: Deploy the application with:" -ForegroundColor Yellow
    Write-Host ".\deploy.ps1 -Environment $Environment -Version <version>" -ForegroundColor White
}
