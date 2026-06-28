# Deploy RedMist to Kubernetes
param(
    [Parameter(Mandatory=$true)]
    [ValidateSet("dev", "test", "prod")]
    [string]$Environment,

    [string]$Version = "latest",

    # Optional extra Helm values file layered last (e.g. an external timing-source overlay). Kept generic
    # so deployment-specific overlays can be supplied at call time without naming them in this script.
    [string]$ExternalValues = ""
)

# Environment-specific configuration
$config = switch ($Environment) {
    "dev" { 
        @{
            namespace = "timing-dev"
            releaseName = "redmist-dev"
            valuesFile = "values-dev.yaml"
            color = "Green"
        }
    }
    "test" { 
        @{
            namespace = "timing-test"
            releaseName = "redmist-test"
            valuesFile = "values-test.yaml"
            color = "Yellow"
        }
    }
    "prod" { 
        @{
            namespace = "timing-prod"
            releaseName = "redmist-prod"
            valuesFile = "values-prod.yaml"
            color = "Red"
        }
    }
}

Write-Host "Deploying RedMist to $($Environment.ToUpper()) environment..." -ForegroundColor $config.color
Write-Host "Container version: $Version" -ForegroundColor Cyan
Write-Host "Namespace: $($config.namespace)" -ForegroundColor Cyan

# Kubernetes context check
$k8sContext = kubectl config current-context 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ Failed to get Kubernetes context. Is kubectl configured?" -ForegroundColor Red
    exit 1
}
Write-Host "`nCurrent Kubernetes context: " -ForegroundColor Cyan -NoNewline
Write-Host $k8sContext -ForegroundColor White
$contextConfirm = Read-Host "Is this the correct context for '$Environment'? (yes/no)"
if ($contextConfirm -ne "yes") {
    Write-Host "Deployment cancelled. Switch context with: kubectl config use-context <context-name>" -ForegroundColor Yellow
    exit 1
}

# Production safety check
if ($Environment -eq "prod") {
    $confirm = Read-Host "Are you sure you want to deploy to PRODUCTION? (yes/no)"
    if ($confirm -ne "yes") {
        Write-Host "Production deployment cancelled." -ForegroundColor Yellow
        exit 1
    }
}

# Ensure chart dependencies are present
Write-Host "Fetching chart dependencies..." -ForegroundColor Cyan
helm dependency build .
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ helm dependency build FAILED!" -ForegroundColor Red
    exit 1
}

# Optional extra values overlay, layered last so it wins over the environment file.
$extraValues = @()
if ($ExternalValues) {
    if (-not (Test-Path $ExternalValues)) {
        Write-Host "❌ ExternalValues file not found: $ExternalValues" -ForegroundColor Red
        exit 1
    }
    Write-Host "Including extra values overlay: $ExternalValues" -ForegroundColor Cyan
    $extraValues = @('-f', $ExternalValues)
}

# Deploy with Helm
helm upgrade --install $config.releaseName . `
  --force-conflicts `
  -f values.yaml `
  -f $config.valuesFile `
  @extraValues `
  --set redmist-status-api.environment=$Environment `
  --set redmist-user-management.environment=$Environment `
  --set redmist-event-management.environment=$Environment `
  --set redmist-event-orchestration.environment=$Environment `
  --set redmist-relay-api.environment=$Environment `
  --set redmist-status-api.image.tag=$Version `
  --set redmist-relay-api.image.tag=$Version `
  --set redmist-event-management.image.tag=$Version `
  --set redmist-event-orchestration.image.tag=$Version `
  --set redmist-user-management.image.tag=$Version `
  --set redmist-external-data-collection.image.tag=$Version `
  --set redmist-sponsor-data-rollup.image.tag=$Version `
  --set redmist-sponsor-reports.image.tag=$Version `
  --namespace $config.namespace `
  --wait

if ($LASTEXITCODE -eq 0) {
    Write-Host "✅ $($Environment.ToUpper()) deployment complete!" -ForegroundColor $config.color
    
    # Show next steps
    Write-Host "`nNext steps:" -ForegroundColor Yellow
    if ($Environment -eq "test") {
        Write-Host "Test API: curl https://api-test.redmist.racing/status/Events/LoadLiveEvents" -ForegroundColor White
    } elseif ($Environment -eq "dev") {
        Write-Host "Test API: curl https://api-dev.redmist.racing/status/Events/LoadLiveEvents" -ForegroundColor White
    } else {
        Write-Host "Test API: curl https://api.redmist.racing/status/Events/LoadLiveEvents" -ForegroundColor White
    }
} else {
    Write-Host "❌ $($Environment.ToUpper()) deployment FAILED!" -ForegroundColor Red
    Write-Host "`nPossible causes:" -ForegroundColor Yellow
    Write-Host "- Database migration failed (check migration container logs)" -ForegroundColor Yellow
    Write-Host "- Resource constraints or timeout" -ForegroundColor Yellow
    Write-Host "- Configuration issues" -ForegroundColor Yellow
    Write-Host "`nTo debug migration issues:" -ForegroundColor Cyan
    Write-Host "kubectl get jobs -n $($config.namespace) -l app.kubernetes.io/component=migration" -ForegroundColor White
    Write-Host "kubectl logs -n $($config.namespace) job/<migration-job-name>" -ForegroundColor White
    exit 1
}
