param(
    [Parameter(Mandatory=$true)]
    [string]$DatabaseHost,
    
    [Parameter(Mandatory=$true)]
    [string]$DatabasePassword,
    
    [Parameter(Mandatory=$true)]
    [string]$AdminPassword,
    
    [string]$DatabasePort = "25060",
    [string]$DatabaseUsername = "doadmin"
)

$namespace = "keycloak"

Write-Host "Setting up Keycloak on DigitalOcean Kubernetes..." -ForegroundColor Cyan
Write-Host "Database Host: $DatabaseHost" -ForegroundColor Yellow
Write-Host "Database Port: $DatabasePort" -ForegroundColor Yellow
Write-Host "Database Username: $DatabaseUsername" -ForegroundColor Yellow
Write-Host ""

# Create database secret
Write-Host "Creating database credentials secret..." -ForegroundColor Yellow
kubectl create secret generic keycloak-db-secret `
    --from-literal=username=$DatabaseUsername `
    --from-literal=password=$DatabasePassword `
    --namespace=$namespace `
    --dry-run=client -o yaml | kubectl apply -f -

# Create admin secret
Write-Host "Creating admin credentials secret..." -ForegroundColor Yellow
kubectl create secret generic keycloak-admin-secret `
    --from-literal=username=admin `
    --from-literal=password=$AdminPassword `
    --namespace=$namespace `
    --dry-run=client -o yaml | kubectl apply -f -

# Deploy Keycloak
Write-Host "Deploying Keycloak..." -ForegroundColor Yellow
helm upgrade --install keycloak ./charts/keycloak `
    --namespace=$namespace `
    --set database.host=$DatabaseHost `
    --set database.port=$DatabasePort `
    --wait `
    --timeout=10m

Write-Host ""
Write-Host "Verifying deployment..." -ForegroundColor Yellow
kubectl get pods -n $namespace
kubectl get ingress -n $namespace

Write-Host ""
Write-Host "Waiting for Keycloak to be ready..." -ForegroundColor Yellow
kubectl wait --for=condition=ready pod -l app=keycloak -n $namespace --timeout=5m

Write-Host ""
Write-Host "✓ Keycloak deployed successfully!" -ForegroundColor Green
Write-Host ""
Write-Host "Access Keycloak at: https://auth.redmist.racing" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next Steps:" -ForegroundColor Yellow
Write-Host "1. Test access to Keycloak admin console"
Write-Host "2. Verify database connection"
Write-Host "3. Proceed with data migration from SQL Server"
