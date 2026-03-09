# Deploy RedMist Infrastructure to Kubernetes
param(
    [Parameter(Mandatory=$true)]
    [ValidateSet("dev", "test", "prod")]
    [string]$Environment,

    # KEDA helm chart version. Pin this to avoid unexpected upgrades.
    [string]$KedaVersion = "2.16.0",

    # kube-prometheus-stack helm chart version.
    [string]$PrometheusStackVersion = "70.4.2",

    # Helm release name for Prometheus. The server address used in status-api values
    # is derived from this: http://<name>-kube-prometheus-prometheus.monitoring:9090
    [string]$PrometheusReleaseName = "prometheus",

    # Skip Prometheus installation if you already have a Prometheus instance in the cluster.
    [switch]$SkipPrometheus,

    # Grafana is bundled with kube-prometheus-stack but is NOT needed for KEDA.
    # By default it is disabled to reduce the attack surface.
    # Pass a strong password here to enable it (min 12 chars recommended).
    [string]$GrafanaAdminPassword = "",

    # Skip Keycloak installation if it is already deployed on this cluster.
    [switch]$SkipKeycloak,

    # Skip Cloudflare Tunnel installation if it is already deployed on this cluster.
    [switch]$SkipCloudflareTunnel
)

# Environment-specific configuration
$config = switch ($Environment) {
    "dev" { 
        @{
            namespace = "timing-dev"
            color = "Green"
        }
    }
    "test" { 
        @{
            namespace = "timing-test"
            color = "Yellow"
        }
    }
    "prod" { 
        @{
            namespace = "timing-prod"
            color = "Red"
        }
    }
}

Write-Host "Deploying RedMist Infrastructure to $($Environment.ToUpper()) environment..." -ForegroundColor $config.color
Write-Host "Namespace: $($config.namespace)" -ForegroundColor Cyan

# Production safety check
if ($Environment -eq "prod") {
    $confirm = Read-Host "Are you sure you want to deploy infrastructure to PRODUCTION? (yes/no)"
    if ($confirm -ne "yes") {
        Write-Host "Production infrastructure deployment cancelled." -ForegroundColor Yellow
        exit 1
    }
}

# ---------------------------------------------------------------------------
# Helm repos
# ---------------------------------------------------------------------------

Write-Host "`nUpdating Helm repositories..." -ForegroundColor Cyan
helm repo add kedacore https://kedacore.github.io/charts 2>$null
helm repo add prometheus-community https://prometheus-community.github.io/helm-charts 2>$null
helm repo update

if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ Failed to update Helm repositories!" -ForegroundColor Red
    exit 1
}
Write-Host "✅ Helm repositories updated" -ForegroundColor Green

$deploymentFailed = $false

# ---------------------------------------------------------------------------
# KEDA
# ---------------------------------------------------------------------------

Write-Host "`nDeploying KEDA $KedaVersion..." -ForegroundColor Cyan
helm upgrade --install keda kedacore/keda `
    --namespace keda `
    --create-namespace `
    --version $KedaVersion `
    --wait `
    --timeout 5m

if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ KEDA deployment FAILED!" -ForegroundColor Red
    $deploymentFailed = $true
} else {
    Write-Host "✅ KEDA deployed successfully" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# Prometheus (kube-prometheus-stack)
# ---------------------------------------------------------------------------

if (-not $SkipPrometheus) {
    Write-Host "`nDeploying kube-prometheus-stack $PrometheusStackVersion..." -ForegroundColor Cyan

    # Resolve Grafana configuration before installing.
    # Grafana is disabled by default — it is not required for KEDA and its well-known
    # default password (prom-operator) is a security risk on a shared cluster.
    $grafanaArgs = @("--set", "grafana.enabled=false")
    if (-not [string]::IsNullOrEmpty($GrafanaAdminPassword)) {
        if ($GrafanaAdminPassword.Length -lt 12) {
            Write-Host "❌ GrafanaAdminPassword must be at least 12 characters." -ForegroundColor Red
            exit 1
        }
        $grafanaArgs = @(
            "--set", "grafana.enabled=true",
            "--set", "grafana.adminPassword=$GrafanaAdminPassword"
        )
        Write-Host "  Grafana will be enabled with the supplied admin password." -ForegroundColor Cyan
    } else {
        Write-Host "  Grafana disabled (pass -GrafanaAdminPassword to enable)." -ForegroundColor Cyan
    }

    # kube-prometheus-stack ships very large CRDs that exceed Helm's default patch limit
    # and cause "unexpected EOF" / "context deadline exceeded" errors. The fix is to
    # apply the CRDs separately via kubectl server-side apply (which streams them
    # efficiently), then install the chart with --skip-crds.
    Write-Host "  Applying Prometheus Operator CRDs via server-side apply..." -ForegroundColor Cyan
    helm show crds prometheus-community/kube-prometheus-stack --version $PrometheusStackVersion |
        kubectl apply --server-side --force-conflicts -f - --timeout=120s

    if ($LASTEXITCODE -ne 0) {
        Write-Host "❌ Prometheus CRD installation FAILED!" -ForegroundColor Red
        $deploymentFailed = $true
    } else {
        Write-Host "  ✅ Prometheus Operator CRDs applied" -ForegroundColor Green

        # serviceMonitorSelectorNilUsesHelmValues=false  allows Prometheus to discover
        # ServiceMonitors in ALL namespaces (timing-dev, timing-prod, etc.), not just
        # the monitoring namespace where the stack is installed.
        helm upgrade --install $PrometheusReleaseName prometheus-community/kube-prometheus-stack `
            --namespace monitoring `
            --create-namespace `
            --version $PrometheusStackVersion `
            --skip-crds `
            --set prometheus.prometheusSpec.serviceMonitorSelectorNilUsesHelmValues=false `
            --set prometheus.prometheusSpec.podMonitorSelectorNilUsesHelmValues=false `
            @grafanaArgs `
            --wait `
            --timeout 10m

        if ($LASTEXITCODE -ne 0) {
            Write-Host "❌ kube-prometheus-stack deployment FAILED!" -ForegroundColor Red
            $deploymentFailed = $true
        } else {
            Write-Host "✅ kube-prometheus-stack deployed successfully" -ForegroundColor Green
        }
    }
} else {
    Write-Host "`nSkipping Prometheus installation (--SkipPrometheus specified)." -ForegroundColor Cyan
    Write-Host "  Ensure you set the correct prometheusServerAddress in status-api values." -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# Keycloak
# ---------------------------------------------------------------------------

if (-not $SkipKeycloak) {
    Write-Host "`nDeploying Keycloak..." -ForegroundColor Cyan
    helm upgrade --install keycloak-$Environment ./infrastructure/keycloak `
      --namespace keycloak `
      --create-namespace `
      --wait

    if ($LASTEXITCODE -ne 0) {
        Write-Host "❌ Keycloak deployment FAILED!" -ForegroundColor Red
        $deploymentFailed = $true
    } else {
        Write-Host "✅ Keycloak deployed successfully" -ForegroundColor Green
    }
} else {
    Write-Host "`nSkipping Keycloak installation (--SkipKeycloak specified)." -ForegroundColor Cyan
}

# ---------------------------------------------------------------------------
# Cloudflare Tunnel
# ---------------------------------------------------------------------------

if (-not $SkipCloudflareTunnel) {
    Write-Host "`nDeploying Cloudflare Tunnel..." -ForegroundColor Cyan
    helm upgrade --install cloudflare-tunnel ./infrastructure/cloudflare-tunnel `
      --namespace cloudflare-tunnel `
      --create-namespace `
      --wait

    if ($LASTEXITCODE -ne 0) {
        Write-Host "❌ Cloudflare Tunnel deployment FAILED!" -ForegroundColor Red
        $deploymentFailed = $true
    } else {
        Write-Host "✅ Cloudflare Tunnel deployed successfully" -ForegroundColor Green
    }
} else {
    Write-Host "`nSkipping Cloudflare Tunnel installation (--SkipCloudflareTunnel specified)." -ForegroundColor Cyan
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------

$prometheusAddress = "http://$PrometheusReleaseName-kube-prometheus-prometheus.monitoring:9090"

Write-Host "`n========================================" -ForegroundColor $config.color
if ($deploymentFailed) {
    Write-Host "❌ Infrastructure deployment completed with ERRORS!" -ForegroundColor Red
    exit 1
} else {
    Write-Host "✅ All infrastructure components deployed successfully!" -ForegroundColor $config.color
    Write-Host ""
    if (-not $SkipPrometheus) {
        Write-Host "Prometheus address (needed in status-api values):" -ForegroundColor Yellow
        Write-Host "  $prometheusAddress" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "To enable KEDA scaling, add to values-$Environment.yaml:" -ForegroundColor Yellow
        Write-Host "  redmist-status-api:" -ForegroundColor Cyan
        Write-Host "    autoscaling:" -ForegroundColor Cyan
        Write-Host "      minReplicas: 0" -ForegroundColor Cyan
        Write-Host "      keda:" -ForegroundColor Cyan
        Write-Host "        enabled: true" -ForegroundColor Cyan
        Write-Host "        prometheusServerAddress: $prometheusAddress" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "Note: The event-orchestration chart must include a ServiceMonitor so" -ForegroundColor Yellow
        Write-Host "Prometheus scrapes the live_events_count metric from it." -ForegroundColor Yellow
        Write-Host ""
    }
    Write-Host "Next step: Deploy the application with:" -ForegroundColor Yellow
    Write-Host ".\deploy.ps1 -Environment $Environment -Version <version>" -ForegroundColor White
}
