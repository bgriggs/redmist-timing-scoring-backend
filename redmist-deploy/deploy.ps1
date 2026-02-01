# Deploy RedMist to Kubernetes
param(
    [Parameter(Mandatory=$true)]
    [ValidateSet("dev", "test", "prod")]
    [string]$Environment,
    
    [string]$Version = "latest"
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

# Production safety check
if ($Environment -eq "prod") {
    $confirm = Read-Host "Are you sure you want to deploy to PRODUCTION? (yes/no)"
    if ($confirm -ne "yes") {
        Write-Host "Production deployment cancelled." -ForegroundColor Yellow
        exit 1
    }
}

# Deploy with Helm
helm upgrade --install $config.releaseName . `
  -f values.yaml `
  -f $config.valuesFile `
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
