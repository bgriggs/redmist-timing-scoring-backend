#!/usr/bin/env pwsh
# RedMist Production Environment Setup
# This script sets up the production environment with Cloudflare TLS termination

param(
    [string]$RedisPassword = "",
    [string]$DatabaseConnectionString = "",
    [string]$KeycloakClientSecret = ""
)

Write-Host "Setting up RedMist production environment..." -ForegroundColor Red
Write-Host "WARNING: This will create production resources!" -ForegroundColor Yellow

# Production safety check
$confirm = Read-Host "Are you sure you want to set up PRODUCTION environment? (yes/no)"
if ($confirm -ne "yes") {
    Write-Host "Production setup cancelled." -ForegroundColor Yellow
    exit 1
}

# First run the common infrastructure setup
Write-Host "Running common infrastructure setup..." -ForegroundColor Yellow
& ".\setup-environment.ps1" -Environment "prod"

# Check if the common setup succeeded
if ($LASTEXITCODE -ne 0) {
    Write-Error "Common infrastructure setup failed"
    exit 1
}

$namespace = "timing"

# Prompt for secrets if not provided
if ([string]::IsNullOrEmpty($RedisPassword)) {
    $RedisPassword = Read-Host -Prompt "Enter Redis password" -AsSecureString
    $RedisPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto([System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($RedisPassword))
}

if ([string]::IsNullOrEmpty($DatabaseConnectionString)) {
    $DatabaseConnectionString = Read-Host -Prompt "Enter database connection string" -AsSecureString
    $DatabaseConnectionString = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto([System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($DatabaseConnectionString))
}

if ([string]::IsNullOrEmpty($KeycloakClientSecret)) {
    $KeycloakClientSecret = Read-Host -Prompt "Enter Keycloak client secret" -AsSecureString  
    $KeycloakClientSecret = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto([System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($KeycloakClientSecret))
}

# Create secrets
Write-Host "Creating application secrets..." -ForegroundColor Yellow

# Check if secret already exists
$secretExists = kubectl get secret rmkeys -n $namespace --ignore-not-found 2>$null
if ([string]::IsNullOrEmpty($secretExists)) {
    kubectl create secret generic rmkeys `
        --from-literal=redis="$RedisPassword" `
        --from-literal=db="$DatabaseConnectionString" `
        --from-literal=usermansecret="$KeycloakClientSecret" `
        --namespace=$namespace
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Secret 'rmkeys' created successfully." -ForegroundColor Green
    } else {
        Write-Error "Failed to create secret 'rmkeys'"
        exit 1
    }
} else {
    Write-Host "Secret 'rmkeys' already exists." -ForegroundColor Green
}

Write-Host ""
Write-Host "Production environment setup complete!" -ForegroundColor Green
Write-Host "Configuration:" -ForegroundColor Cyan
Write-Host "  - Namespace: $namespace" -ForegroundColor White
Write-Host "  - TLS: Handled by Cloudflare" -ForegroundColor White
Write-Host "  - Certificates: Not required (Cloudflare terminates TLS)" -ForegroundColor White
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "1. Deploy the application: helm install redmist-prod . -f values-prod.yaml -n $namespace" -ForegroundColor White
Write-Host "2. Ensure your Cloudflare tunnel points to: http://192.168.13.3" -ForegroundColor White
Write-Host "3. Test the API: curl https://api.redmist.racing/status/Events/LoadLiveEvents" -ForegroundColor White