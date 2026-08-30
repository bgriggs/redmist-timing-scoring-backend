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

## Keycloak login theme

The `redmist` login theme lives in `infrastructure/keycloak/theme/`. The chart turns
it into a ConfigMap, an init container copies it onto a writable volume, and Keycloak
picks it up at `/opt/keycloak/themes/redmist`.

The theme's colors are duplicated from `src/styles/_variables.scss` in
`redmist-landing-ui`, and `img/favicon.ico` and `img/hero.jpg` are copies of that
repo's `public/` assets (`hero.jpg` downscaled from `shutterstock_748716091.jpg`,
which is far too large to serve on a login page). None of that is imported at build
time, so changing the app's branding does not change the login page -- update both.

### Working on the theme

Stand up a throwaway instance in its own namespace. It uses an in-memory database,
so it needs no Postgres and cannot touch production:

```powershell
kubectl create namespace keycloak-theme
kubectl create secret generic keycloak-admin-secret `
    --from-literal=username=admin --from-literal=password=<pick one> -n keycloak-theme

# Optional: seed it with the real realm so the login flow behaves like production.
kubectl get cm redmist-realm-data -n keycloak -o yaml | ... | kubectl apply -f -

helm upgrade --install keycloak-theme ./infrastructure/keycloak `
  --namespace keycloak-theme --set namespace=keycloak-theme `
  --set keycloak.devMode=true `
  --set keycloak.themeVolume.enabled=true --set keycloak.themeVolume.fromChart=true `
  --set ingress.enabled=false --wait

kubectl port-forward -n keycloak-theme svc/keycloak 8081:80
```

`devMode` turns Keycloak's theme caching off, so an edit shows on the next page load
without a redeploy:

```powershell
$pod = kubectl get pod -n keycloak-theme -l app=keycloak -o jsonpath='{.items[0].metadata.name}'
Get-Content ./infrastructure/keycloak/theme/login/resources/css/redmist.css `
  | kubectl exec -i -n keycloak-theme $pod -c keycloak -- `
      bash -c 'cat > /opt/keycloak/themes/redmist/login/resources/css/redmist.css'
```

That only edits the running pod. Re-run `helm upgrade` to persist it, or the next
restart reverts to whatever is in the ConfigMap.

Tear down with `helm uninstall keycloak-theme -n keycloak-theme; kubectl delete ns keycloak-theme`.

### Shipping it to production

Do **not** use `deploy-infrastructure.ps1` or `setup-keycloak.ps1` for this. The
first installs under the release name `keycloak-<env>` while the live release is
named `keycloak`, and the second points at a `charts/keycloak` path that no longer
exists. Upgrade the release directly, re-passing `database.host` -- `helm upgrade`
resets user-supplied values otherwise, and the chart default is empty:

```powershell
helm upgrade keycloak ./infrastructure/keycloak --namespace keycloak `
  --set database.host=<the managed Postgres host> `
  --set keycloak.themeVolume.enabled=true `
  --set keycloak.themeVolume.fromChart=true --wait
```

Then point the realm at the theme -- shipping the files changes nothing on its own.
In the admin console: realm `redmist` -> Realm settings -> Themes -> Login theme ->
`redmist`. Do this **after** the upgrade, or the realm names a theme that is not yet
on disk. Prod keeps this in Postgres, so it survives restarts and is not a chart
value.

Expect a short outage on every one of these upgrades: the deployment is
`replicas: 1` with `strategy: Recreate`, so the old pod is terminated before the new
one starts and logins fail while Keycloak boots. Production runs `start`, which
caches themes for the life of the process, so the `checksum/theme` annotation
deliberately rolls the pod whenever the theme changes.

To roll back the look without a restart, set Login theme back to blank in the admin
console; that takes effect immediately. `helm rollback keycloak -n keycloak` reverts
the deployment but costs another restart.

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
