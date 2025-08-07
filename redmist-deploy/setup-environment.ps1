# RedMist Environment Setup Script
# This script sets up common infrastructure components needed by all environments

param(
    [Parameter(Mandatory=$true)]
    [ValidateSet("dev", "test", "prod")]
    [string]$Environment,

    [string]$IngressClass = "nginx"
)

Write-Host "Setting up RedMist common infrastructure for $Environment environment..." -ForegroundColor Green
Write-Host "TLS will be handled by Cloudflare (no certificate provisioning needed)" -ForegroundColor Cyan

# Environment-specific variables
$namespace = switch ($Environment) {
    "dev" { "timing-dev" }
    "test" { "timing-test" }
    "prod" { "timing" }
}

# Check if kubectl is available
if (-not (Get-Command kubectl -ErrorAction SilentlyContinue)) {
    Write-Error "kubectl is not installed or not in PATH"
    exit 1
}

# Check if helm is available
if (-not (Get-Command helm -ErrorAction SilentlyContinue)) {
    Write-Error "helm is not installed or not in PATH"
    exit 1
}

# Create namespace
Write-Host "Creating namespace '$namespace'..." -ForegroundColor Yellow
kubectl create namespace $namespace --dry-run=client -o yaml | kubectl apply -f -

Write-Host "Skipping cert-manager installation (Cloudflare handles TLS)." -ForegroundColor Cyan

# Install NGINX Ingress Controller if not present
Write-Host "Checking NGINX Ingress Controller..." -ForegroundColor Yellow
$ingressInstalled = kubectl get namespace ingress-nginx --ignore-not-found 2>$null
if ([string]::IsNullOrEmpty($ingressInstalled)) {
    Write-Host "Installing NGINX Ingress Controller..." -ForegroundColor Yellow
    kubectl apply -f https://raw.githubusercontent.com/kubernetes/ingress-nginx/controller-v1.10.0/deploy/static/provider/cloud/deploy.yaml
    
    Write-Host "Waiting for NGINX Ingress Controller to be ready..." -ForegroundColor Yellow
    kubectl wait --for=condition=available --timeout=300s deployment/ingress-nginx-controller -n ingress-nginx
    
    Write-Host "NGINX Ingress Controller installed successfully!" -ForegroundColor Green
} else {
    Write-Host "NGINX Ingress Controller is already installed." -ForegroundColor Green
}

Write-Host "Common infrastructure setup complete for $Environment environment!" -ForegroundColor Green
Write-Host "Namespace: $namespace" -ForegroundColor Cyan
Write-Host "TLS: Handled by Cloudflare" -ForegroundColor Cyan
Write-Host "Ingress Class: $IngressClass" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "1. Run the environment-specific setup script: .\setup-$Environment.ps1" -ForegroundColor White
Write-Host "2. Ensure Cloudflare tunnel is configured to point to your Kubernetes cluster" -ForegroundColor White
Write-Host "3. Ingress will handle HTTP traffic internally (TLS terminated by Cloudflare)" -ForegroundColor White