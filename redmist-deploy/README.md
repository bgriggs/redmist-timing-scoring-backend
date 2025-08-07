# RedMist Kubernetes Deployment

This repository contains Helm charts and setup scripts for deploying RedMist services to Kubernetes with Cloudflare TLS termination.

## Architecture

```
Internet → Cloudflare (HTTPS) → Cloudflare Tunnel → Kubernetes Ingress (HTTP) → Services → Pods
```

## Features

- **Cloudflare TLS**: All TLS termination handled by Cloudflare
- **No Certificate Management**: No cert-manager or Let's Encrypt required
- **Tunnel Support**: Works with Cloudflare tunnels (no external IP needed)
- **Automatic SSL**: Cloudflare handles certificate lifecycle
- **Enhanced Performance**: Cloudflare CDN and edge optimization
- **DDoS Protection**: Built-in Cloudflare security features

## Quick Start

### Deployment

```powershell
# Deploy to development (latest)
.\deploy.ps1 -Environment dev

# Deploy to test with specific version
.\deploy.ps1 -Environment test -Version "1.2.0"

# Deploy to production (requires confirmation)
.\deploy.ps1 -Environment prod -Version "1.1.0"
```

### Environment Setup

```powershell
# Setup test environment
.\setup-test.ps1

# Setup dev environment  
.\setup-dev.ps1

# Setup prod environment
.\setup-prod.ps1
```

### API Testing

```powershell
# Test APIs after deployment
curl https://api-test.redmist.racing/status/Events/LoadLiveEvents  # Test
curl https://api-dev.redmist.racing/status/Events/LoadLiveEvents   # Dev  
curl https://api.redmist.racing/status/Events/LoadLiveEvents       # Prod
```

## Environment Files

- `values-test.yaml` - Test environment configuration
- `values-dev.yaml` - Development environment configuration
- `values-prod.yaml` - Production environment configuration

## Setup Scripts

- `setup-environment.ps1` - Common infrastructure setup (NGINX ingress only)
- `setup-test.ps1` - Test environment with secrets management
- `setup-dev.ps1` - Development environment setup
- `setup-prod.ps1` - Production environment setup

## Configuration

All environments are configured for:
- **TLS**: Disabled (handled by Cloudflare)
- **SSL Redirect**: Disabled to prevent redirect loops
- **Path Rewriting**: Uses regex patterns for proper URL rewriting
- **HTTP Only**: Internal cluster communication via HTTP

## Cloudflare Setup

Ensure your Cloudflare tunnel is configured with:
- **Backend**: Cluster (HTTP, not HTTPS)
- **TLS Origin Server Name**: Your domain (e.g., `api-test.redmist.racing`)

## Troubleshooting

### Redirect Loops
If you see "too many redirects" errors:
1. Verify `nginx.ingress.kubernetes.io/ssl-redirect: "false"` is set
2. Ensure Cloudflare tunnel uses HTTP backend

### 404 Errors  
If you get 404 errors:
1. Check ingress path uses regex: `/status/(.*)`
2. Ensure rewrite-target is: `/$1`
3. Verify pathType is: `ImplementationSpecific`

### Service Connectivity
If services aren't reachable:
1. Check pod status: `kubectl get pods -n timing-test`
2. Verify service endpoints: `kubectl get endpoints -n timing-test`
3. Test direct pod access: `kubectl port-forward -n timing-test <pod-name> 8080:8080`
