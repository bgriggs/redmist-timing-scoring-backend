# RedMist Deployment Structure

This repository contains Helm charts for deploying RedMist timing and scoring services to Kubernetes.

## Deployment Components

The deployment is split into two parts:

### 1. Infrastructure Components
- **Keycloak**: Authentication and user management
- **Cloudflare Tunnel**: Secure ingress tunneling

### 2. Application Components
- **RedMist Status API**
- **RedMist Relay API**
- **RedMist Event Management**
- **RedMist Event Orchestration**
- **RedMist User Management**
- **Redis**: Shared cache (deployed with application)

## Deployment Order

### Initial Setup

1. **Deploy Infrastructure** (one-time or when infrastructure changes):
   ```powershell
   .\deploy-infrastructure.ps1 -Environment <dev|test|prod>
   ```

2. **Deploy Application**:
   ```powershell
   .\deploy.ps1 -Environment <dev|test|prod> -Version "x.x.x.x"
   ```

### Regular Updates

For application updates, you only need to run:
```powershell
.\deploy.ps1 -Environment <dev|test|prod> -Version "x.x.x.x"
```

## Environments

- **dev**: Development environment (`timing-dev` namespace)
- **test**: Testing environment (`timing-test` namespace)
- **prod**: Production environment (`timing` namespace)

## Database Migrations

Database migrations are **not** run automatically during deployment. You must run migrations manually before or after deploying a new version.

## Directory Structure

```
.
├── charts/                          # Application Helm charts
│   ├── redmist-event-management/
│   ├── redmist-event-orchestration/
│   ├── redmist-relay-api/
│   ├── redmist-status-api/
│   ├── redmist-ui-browser/
│   └── redmist-user-management/
├── infrastructure/                  # Infrastructure Helm charts
│   ├── cloudflare-tunnel/
│   └── keycloak/
├── deploy.ps1                      # Application deployment script
├── deploy-infrastructure.ps1       # Infrastructure deployment script
├── values.yaml                     # Base values
├── values-dev.yaml                 # Dev environment values
├── values-test.yaml                # Test environment values
└── values-prod.yaml                # Production environment values
```

## Troubleshooting

### Check deployment status
```powershell
helm list -n timing-test
kubectl get pods -n timing-test
```

### Check infrastructure
```powershell
helm list -n keycloak
helm list -n cloudflare-tunnel
```

### View logs
```powershell
kubectl logs -n timing-test deployment/redmist-test-redmist-status-api
```

### Stuck deployment
If a deployment gets stuck, you may need to uninstall and reinstall:
```powershell
helm uninstall redmist-test -n timing-test
.\deploy.ps1 -Environment test -Version "x.x.x.x"
```
