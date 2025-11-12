#!/usr/bin/env pwsh
# RedMist Test Environment Setup
# This script sets up the test environment with Cloudflare TLS termination

param(
    [string]$RedisPassword = "",
    [string]$DatabaseConnectionString = "",
    [string]$KeycloakClientSecret = ""
)

Write-Host "Setting up RedMist test environment..." -ForegroundColor Green

# First run the common infrastructure setup
Write-Host "Running common infrastructure setup..." -ForegroundColor Yellow
& ".\setup-environment.ps1" -Environment "test"

# Check if the common setup succeeded
if ($LASTEXITCODE -ne 0) {
    Write-Error "Common infrastructure setup failed"
    exit 1
}

$namespace = "timing-test"

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
$secretExists = kubectl get secret rmkeys-test -n $namespace --ignore-not-found 2>$null
if ([string]::IsNullOrEmpty($secretExists)) {
    kubectl create secret generic rmkeys-test `
        --from-literal=redis="$RedisPassword" `
        --from-literal=db="$DatabaseConnectionString" `
        --from-literal=usermansecret="$KeycloakClientSecret" `
        --namespace=$namespace
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Secret 'rmkeys-test' created successfully." -ForegroundColor Green
    } else {
        Write-Error "Failed to create secret 'rmkeys-test'"
        exit 1
    }
} else {
    Write-Host "Secret 'rmkeys-test' already exists." -ForegroundColor Green
}

# Create Redis operator auth secret
Write-Host "Creating Redis operator auth secret..." -ForegroundColor Yellow
$redisSecretExists = kubectl get secret redis-auth -n $namespace --ignore-not-found 2>$null
if ([string]::IsNullOrEmpty($redisSecretExists)) {
    kubectl create secret generic redis-auth `
        --from-literal=password="$RedisPassword" `
        --namespace=$namespace
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Secret 'redis-auth' created successfully." -ForegroundColor Green
    } else {
        Write-Error "Failed to create secret 'redis-auth'"
        exit 1
    }
} else {
    Write-Host "Secret 'redis-auth' already exists." -ForegroundColor Green
}

Write-Host ""
Write-Host "Test environment setup complete!" -ForegroundColor Green
Write-Host "Configuration:" -ForegroundColor Cyan
Write-Host "  - Namespace: $namespace" -ForegroundColor White
Write-Host "  - TLS: Handled by Cloudflare" -ForegroundColor White
Write-Host "  - Certificates: Not required (Cloudflare terminates TLS)" -ForegroundColor White
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "1. Deploy the application: helm install redmist-test . -f values-test.yaml -n $namespace" -ForegroundColor White
Write-Host "2. Ensure your Cloudflare tunnel points to the cluster" -ForegroundColor White
Write-Host "3. Test the API: curl https://api-test.redmist.racing/status/Events/LoadLiveEvents" -ForegroundColor White
