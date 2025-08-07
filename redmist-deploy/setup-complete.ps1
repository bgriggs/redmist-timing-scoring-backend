# RedMist Master Setup Script
# This script sets up a complete RedMist environment from scratch

param(
    [Parameter(Mandatory=$true)]
    [ValidateSet("dev", "test", "prod")]
    [string]$Environment,
    
    [string]$RedisPassword = "",
    [string]$DatabaseConnectionString = "",
    [string]$KeycloakClientSecret = ""
)

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "   RedMist Complete Environment Setup   " -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Environment: $Environment" -ForegroundColor Yellow
Write-Host "TLS: Handled by Cloudflare" -ForegroundColor Yellow
Write-Host ""

# Validate prerequisites
Write-Host "Checking prerequisites..." -ForegroundColor Green

$prerequisites = @()

# Check kubectl
if (Get-Command kubectl -ErrorAction SilentlyContinue) {
    Write-Host "✓ kubectl is installed" -ForegroundColor Green
    $prerequisites += $true
} else {
    Write-Host "✗ kubectl is not installed" -ForegroundColor Red
    $prerequisites += $false
}

# Check helm
if (Get-Command helm -ErrorAction SilentlyContinue) {
    Write-Host "✓ helm is installed" -ForegroundColor Green
    $prerequisites += $true
} else {
    Write-Host "✗ helm is not installed" -ForegroundColor Red
    $prerequisites += $false
}

# Check cluster connectivity
try {
    $clusterInfo = kubectl cluster-info 2>$null
    if ($clusterInfo) {
        Write-Host "✓ Connected to Kubernetes cluster" -ForegroundColor Green
        $prerequisites += $true
    } else {
        Write-Host "✗ Cannot connect to Kubernetes cluster" -ForegroundColor Red
        $prerequisites += $false
    }
} catch {
    Write-Host "✗ Cannot connect to Kubernetes cluster" -ForegroundColor Red
    $prerequisites += $false
}

if ($prerequisites -contains $false) {
    Write-Host "Prerequisites not met. Please install missing components and try again." -ForegroundColor Red
    exit 1
}

Write-Host ""

# Step 1: Common Infrastructure Setup
Write-Host "Step 1: Setting up common infrastructure..." -ForegroundColor Green
& ".\setup-environment.ps1"

if ($LASTEXITCODE -ne 0) {
    Write-Host "Common infrastructure setup failed!" -ForegroundColor Red
    exit 1
}

Write-Host ""

# Step 2: Environment-specific setup
Write-Host "Step 2: Setting up $Environment-specific resources..." -ForegroundColor Green

# Collect secrets if not provided
if ([string]::IsNullOrEmpty($RedisPassword)) {
    Write-Host "Redis password is required for environment setup." -ForegroundColor Yellow
    $secureRedisPassword = Read-Host -Prompt "Enter Redis password" -AsSecureString
    $RedisPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto([System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureRedisPassword))
}

if ([string]::IsNullOrEmpty($DatabaseConnectionString)) {
    Write-Host "Database connection string is required for environment setup." -ForegroundColor Yellow
    $DatabaseConnectionString = Read-Host -Prompt "Enter Database connection string"
}

if ([string]::IsNullOrEmpty($KeycloakClientSecret)) {
    Write-Host "Keycloak client secret is required for environment setup." -ForegroundColor Yellow
    $secureKeycloakSecret = Read-Host -Prompt "Enter Keycloak client secret" -AsSecureString
    $KeycloakClientSecret = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto([System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKeycloakSecret))
}

# Run environment-specific setup
Write-Host "Using Cloudflare tunnel-compatible setup..." -ForegroundColor Cyan
& ".\setup-$Environment.ps1" -RedisPassword $RedisPassword -DatabaseConnectionString $DatabaseConnectionString -KeycloakClientSecret $KeycloakClientSecret

if ($LASTEXITCODE -ne 0) {
    Write-Host "Environment-specific setup failed!" -ForegroundColor Red
    exit 1
}

Write-Host ""

# Step 3: Final verification
Write-Host "Step 3: Verifying environment setup..." -ForegroundColor Green
& ".\verify-environment.ps1" -Environment $Environment

Write-Host ""

# Summary
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "        Setup Complete Summary          " -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$namespace = if ($Environment -eq "prod") { "timing" } else { "timing-$Environment" }
$envSuffix = if ($Environment -eq "prod") { "" } else { "-$Environment" }

Write-Host "Environment: $Environment" -ForegroundColor Green
Write-Host "Namespace: $namespace" -ForegroundColor Green
Write-Host ""
Write-Host "Domains configured:" -ForegroundColor Yellow
Write-Host "  API: api$envSuffix.redmist.racing" -ForegroundColor White
Write-Host ""
Write-Host "Note: UI (timingapp) is hosted separately and not included in this deployment" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "1. Configure Cloudflare tunnel to point to the cluster" -ForegroundColor White
Write-Host "2. Deploy the application:" -ForegroundColor White
Write-Host "   .\deploy-$Environment.ps1" -ForegroundColor Cyan
Write-Host ""
Write-Host "Troubleshooting:" -ForegroundColor Yellow
Write-Host "  Check setup: .\verify-environment.ps1 -Environment $Environment" -ForegroundColor White
Write-Host "  View resources: kubectl get all -n $namespace" -ForegroundColor White
