# RedMist Environment Verification Script
# This script verifies that the environment is properly set up

param(
    [Parameter(Mandatory=$true)]
    [ValidateSet("dev", "test", "prod")]
    [string]$Environment
)

# Environment-specific variables
$namespace = switch ($Environment) {
    "dev" { "timing-dev" }
    "test" { "timing-test" }
    "prod" { "timing-prod" }
}

$secretName = switch ($Environment) {
    "dev" { "rmkeys-dev" }
    "test" { "rmkeys-test" }
    "prod" { "rmkeys" }
}

Write-Host "Verifying RedMist $Environment environment setup..." -ForegroundColor Green
Write-Host "Note: Using Cloudflare for TLS termination" -ForegroundColor Cyan
Write-Host "Namespace: $namespace" -ForegroundColor Cyan
Write-Host "=================================" -ForegroundColor Gray

$allChecks = @()

# Check 1: Namespace exists
Write-Host "1. Checking namespace..." -ForegroundColor Yellow
$namespaceExists = kubectl get namespace $namespace --ignore-not-found 2>$null
if ($namespaceExists) {
    Write-Host "   ✓ Namespace '$namespace' exists" -ForegroundColor Green
    $allChecks += $true
} else {
    Write-Host "   ✗ Namespace '$namespace' not found" -ForegroundColor Red
    $allChecks += $false
}

# Check 2: Secrets exist
Write-Host "2. Checking secrets..." -ForegroundColor Yellow
$secretExists = kubectl get secret $secretName -n $namespace --ignore-not-found 2>$null
if ($secretExists) {
    Write-Host "   ✓ Secret '$secretName' exists" -ForegroundColor Green
    
    # Check secret keys
    $redisKey = kubectl get secret $secretName -n $namespace -o jsonpath='{.data.redis}' 2>$null
    $dbKey = kubectl get secret $secretName -n $namespace -o jsonpath='{.data.db}' 2>$null
    $keycloakKey = kubectl get secret $secretName -n $namespace -o jsonpath='{.data.usermansecret}' 2>$null
    
    if ($redisKey) { Write-Host "     ✓ Redis password key exists" -ForegroundColor Green } else { Write-Host "     ✗ Redis password key missing" -ForegroundColor Red }
    if ($dbKey) { Write-Host "     ✓ Database connection string key exists" -ForegroundColor Green } else { Write-Host "     ✗ Database connection string key missing" -ForegroundColor Red }
    if ($keycloakKey) { Write-Host "     ✓ Keycloak client secret key exists" -ForegroundColor Green } else { Write-Host "     ✗ Keycloak client secret key missing" -ForegroundColor Red }
    
    $allChecks += ($redisKey -and $dbKey -and $keycloakKey)
} else {
    Write-Host "   ✗ Secret '$secretName' not found" -ForegroundColor Red
    $allChecks += $false
}

# Check 3: Ingress Controller (TLS handled by Cloudflare)
Write-Host "3. Checking NGINX Ingress Controller..." -ForegroundColor Yellow
$ingressNs = kubectl get namespace ingress-nginx --ignore-not-found 2>$null
if ($ingressNs) {
    Write-Host "   ✓ ingress-nginx namespace exists" -ForegroundColor Green
    
    # Check ingress controller pods
    $ingressPods = kubectl get pods -n ingress-nginx -l app.kubernetes.io/name=ingress-nginx --no-headers 2>$null | Measure-Object | Select-Object -ExpandProperty Count
    $readyIngressPods = kubectl get pods -n ingress-nginx -l app.kubernetes.io/name=ingress-nginx --no-headers 2>$null | Where-Object { $_ -match "1/1.*Running" } | Measure-Object | Select-Object -ExpandProperty Count
    
    Write-Host "     Pods: $readyIngressPods/$ingressPods ready" -ForegroundColor $(if ($readyIngressPods -eq $ingressPods -and $ingressPods -gt 0) { "Green" } else { "Red" })
    
    # Check external IP
    $externalIp = kubectl get svc ingress-nginx-controller -n ingress-nginx -o jsonpath='{.status.loadBalancer.ingress[0].ip}' 2>$null
    if ($externalIp) {
        Write-Host "     ✓ External IP: $externalIp" -ForegroundColor Green
        $allChecks += $true
    } else {
        Write-Host "     ⚠ External IP not assigned yet (may take a few minutes)" -ForegroundColor Yellow
        $allChecks += $true  # Don't fail for this, it might just be pending
    }
} else {
    Write-Host "   ✗ NGINX Ingress Controller not installed" -ForegroundColor Red
    $allChecks += $false
}

# Summary
Write-Host "`n=================================" -ForegroundColor Gray
$passedChecks = ($allChecks | Where-Object { $_ -eq $true }).Count
$totalChecks = $allChecks.Count

if ($passedChecks -eq $totalChecks) {
    Write-Host "✓ Environment verification PASSED ($passedChecks/$totalChecks)" -ForegroundColor Green
    Write-Host "Environment is ready for deployment!" -ForegroundColor Green
} else {
    Write-Host "✗ Environment verification FAILED ($passedChecks/$totalChecks)" -ForegroundColor Red
    Write-Host "Please fix the issues above before deploying." -ForegroundColor Red
}

Write-Host "`nNext steps:" -ForegroundColor Yellow
if ($passedChecks -eq $totalChecks) {
    Write-Host "Run: .\deploy-$Environment.ps1" -ForegroundColor White
} else {
    Write-Host "1. Fix the failed checks above" -ForegroundColor White
    Write-Host "2. Re-run: .\verify-environment.ps1 -Environment $Environment" -ForegroundColor White
    Write-Host "3. When all checks pass, run: .\deploy-$Environment.ps1" -ForegroundColor White
}

Write-Host "`nUseful commands:" -ForegroundColor Yellow
Write-Host "Check namespace: kubectl get all -n $namespace" -ForegroundColor White
Write-Host "Check secrets: kubectl get secrets -n $namespace" -ForegroundColor White
Write-Host "Check ingress: kubectl get ingress -n $namespace" -ForegroundColor White
