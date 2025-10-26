param(
    [Parameter(Mandatory=$true)]
    [string]$TunnelId,
    
    [Parameter(Mandatory=$true)]
    [string]$AccountId,
    
    [Parameter(Mandatory=$true)]
    [string]$CredentialsFile
)

$namespace = "cloudflare-tunnel"

Write-Host "Setting up Cloudflare Tunnel in DigitalOcean Kubernetes..." -ForegroundColor Cyan
Write-Host "Tunnel ID: $TunnelId" -ForegroundColor Yellow
Write-Host "Account ID: $AccountId" -ForegroundColor Yellow
Write-Host "Credentials File: $CredentialsFile" -ForegroundColor Yellow
Write-Host ""

# Verify credentials file exists
if (-not (Test-Path $CredentialsFile)) {
    Write-Error "Credentials file not found: $CredentialsFile"
    exit 1
}

# Create namespace
Write-Host "Creating namespace..." -ForegroundColor Yellow
kubectl create namespace $namespace --dry-run=client -o yaml | kubectl apply -f -

# Create secret from credentials file
Write-Host "Creating tunnel credentials secret..." -ForegroundColor Yellow
kubectl create secret generic cloudflared-credentials `
    --from-file=credentials.json=$CredentialsFile `
    --namespace=$namespace `
    --dry-run=client -o yaml | kubectl apply -f -

# Deploy cloudflared
Write-Host "Deploying Cloudflare Tunnel..." -ForegroundColor Yellow
helm upgrade --install cloudflare-tunnel ./charts/cloudflare-tunnel `
    --namespace=$namespace `
    --set cloudflare.tunnelId=$TunnelId `
    --set cloudflare.accountId=$AccountId `
    --wait

Write-Host ""
Write-Host "Verifying deployment..." -ForegroundColor Yellow
kubectl get pods -n $namespace
Write-Host ""

# Wait a moment for logs to generate
Start-Sleep -Seconds 5

Write-Host "Recent logs:" -ForegroundColor Yellow
kubectl logs -n $namespace -l app=cloudflared --tail=20

Write-Host ""
Write-Host "✓ Cloudflare Tunnel deployed successfully!" -ForegroundColor Green
Write-Host ""

