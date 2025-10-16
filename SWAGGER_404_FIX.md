# Swagger 404 Fix for Production Deployment

## Problem
The Swagger UI at `https://api.redmist.racing/status/swagger` was returning 404 errors in production (Kubernetes), despite working fine in local development.

## Root Cause
The issue was caused by the ingress path rewriting configuration. The Kubernetes ingress was configured with:
- Path: `/status/(.*)`
- Rewrite target: `/$1`

This means when accessing `https://api.redmist.racing/status/swagger`, the ingress would capture `swagger` and rewrite the request to `/swagger` before sending it to the pod.

However, Swagger UI generates its internal URLs and asset references (JavaScript, CSS) relative to its current location. When Swagger is accessed through a path-based proxy like `/status/swagger`, it needs to be aware of this base path to generate correct URLs for its assets.

Without this awareness, Swagger would try to load assets from `/swagger/swagger.json` instead of the correct `/status/swagger/swagger.json`, causing 404 errors.

## Solution
The fix involves two changes:

### 1. Update Program.cs (StatusApi)
Added support for running behind a path-based proxy by:
- Reading a `PathBase` configuration value
- Calling `app.UsePathBase(pathBase)` to tell ASP.NET Core about the base path
- Updating Swagger configuration to include the path base in server URLs

```csharp
// Support for running behind a path-based proxy (e.g., /status)
var pathBase = app.Configuration["PathBase"];
if (!string.IsNullOrEmpty(pathBase))
{
    app.UsePathBase(pathBase);
}

app.UseSwagger(c =>
{
    c.PreSerializeFilters.Add((swagger, httpReq) =>
    {
        if (!string.IsNullOrEmpty(pathBase))
        {
            swagger.Servers = new List<Microsoft.OpenApi.Models.OpenApiServer>
            {
                new() { Url = $"{httpReq.Scheme}://{httpReq.Host.Value}{pathBase}" }
            };
        }
    });
});
```

### 2. Update Helm Chart (values.yaml)
Added the `PathBase` environment variable to configure the application:

```yaml
env:
  PathBase: "/status"
```

## How It Works
1. When the application starts, it reads the `PathBase` configuration
2. The `UsePathBase` middleware strips the `/status` prefix from incoming requests
3. Controllers and endpoints see clean paths without the prefix
4. Swagger UI is configured to generate URLs with the `/status` prefix included
5. All asset references and API calls work correctly

## Deployment Steps

### 1. Build and Push Docker Image
```powershell
cd C:\Code\RedMist.TimingAndScoringService
docker build -f "RedMist.StatusApi\Dockerfile" --force-rm -t bigmission/redmist-status-api "."
docker push bigmission/redmist-status-api
```

### 2. Deploy to Kubernetes
```powershell
cd redmist-deploy
.\deploy.ps1 -Environment prod
```

### 3. Verify Deployment
```powershell
# Check pod status
kubectl get pods -n timing | Select-String "status-api"

# Check logs
kubectl logs -f <pod-name> -n timing

# Test Swagger endpoint
curl https://api.redmist.racing/status/swagger/index.html
```

## Testing in Development
To test this locally with the path base:

1. Add to `appsettings.Development.json`:
```json
{
  "PathBase": "/status"
}
```

2. Run the application
3. Access Swagger at: `http://localhost:5000/status/swagger`

## Alternative Solutions Considered

### Option 1: Change Ingress Path
Instead of `/status/(.*)`, use `/status` with no rewrite. 
- **Rejected**: Would require changing all API consumer configurations

### Option 2: Disable Path Rewriting
Remove the `nginx.ingress.kubernetes.io/rewrite-target` annotation.
- **Rejected**: Would expose internal routing structure

### Option 3: Move Swagger to Root
Configure Swagger at the root path instead of `/swagger`.
- **Rejected**: Less conventional and could conflict with other routes

## Impact
- ? No changes required for API consumers
- ? Swagger UI now works in production
- ? All API endpoints continue to work as before
- ? Works in both development and production environments
- ? Compatible with Cloudflare TLS termination

## References
- [ASP.NET Core PathBase Documentation](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer)
- [Swagger Behind a Proxy](https://github.com/domaindrivendev/Swashbuckle.AspNetCore#change-relative-path-to-the-ui)
- [Kubernetes Ingress Path Rewriting](https://kubernetes.github.io/ingress-nginx/examples/rewrite/)
