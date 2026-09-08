<#
.SYNOPSIS
    Runs the status load test from inside the cluster, without building or pushing an image.

.DESCRIPTION
    Ships the tool's source in a ConfigMap and builds it in a stock SDK pod. That is slower to
    start than a prebuilt image - about a minute of restore - but needs no registry credentials,
    which makes a one-off run against a test environment a single command.

    Running inside the cluster is what makes the poll half of the test meaningful. The polling
    endpoint's rate limiter partitions on the client IP, and it only sees the per-client
    X-Forwarded-For this tool sends when nothing in front of the service overwrites it. Traffic
    arriving over the public hostname passes through Cloudflare, which sets its own
    CF-Connecting-IP and collapses every virtual client into a single partition.

    Check there is somewhere for the pod to run first. If the nodes are already close to fully
    requested, the generator competes with the very services being measured and the numbers
    describe the node rather than the application.

.EXAMPLE
    ./run-in-cluster.ps1 -Context my-cluster -EventId 81 -Clients 200 -Duration 300
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][int]$EventId,
    [int]$Clients = 200,
    [double]$Ramp = 30,
    [double]$Duration = 300,
    # Placeholder: pass -Context with the kubectl context for the target cluster.
    [string]$Context = '<kube-context>',
    [string]$Namespace = 'timing-test',
    [string]$ApiUrl = '',
    [string]$JobName = 'status-load-test',
    # Pins the generator ONTO nodes carrying this label, as key=value. To keep it away from a
    # node instead, label the others and select those - a nodeSelector cannot express exclusion.
    [string]$NodeSelector = '',
    [switch]$NoPoll,
    [switch]$NoWebSocket
)

$ErrorActionPreference = 'Stop'
$kube = @('--context', $Context, '-n', $Namespace)

if (-not $ApiUrl) {
    $service = & kubectl @kube get svc -l 'app.kubernetes.io/name=redmist-status-api' -o jsonpath='{.items[0].metadata.name}'
    if (-not $service) { throw 'Could not find the status API service; pass -ApiUrl explicitly.' }
    $port = & kubectl @kube get svc $service -o jsonpath='{.spec.ports[0].port}'
    $ApiUrl = "http://${service}:${port}"
}
Write-Host "Target:  $ApiUrl"
Write-Host "Clients: $Clients over a ${Ramp}s ramp, holding ${Duration}s"

Write-Host 'Removing any previous run...'
& kubectl @kube delete job $JobName --ignore-not-found | Out-Null
& kubectl @kube delete configmap "$JobName-src" --ignore-not-found | Out-Null

Write-Host 'Uploading source...'
$files = Get-ChildItem -Path $PSScriptRoot -File |
    Where-Object { $_.Extension -in '.cs', '.csproj' }
$fromFile = $files | ForEach-Object { '--from-file'; "$($_.Name)=$($_.FullName)" }
& kubectl @kube create configmap "$JobName-src" @fromFile | Out-Null

$toolArgs = @(
    '--api-url', $ApiUrl
    '--event-id', $EventId
    '--clients', $Clients
    '--ramp', $Ramp
    '--duration', $Duration
)
if ($NoPoll) { $toolArgs += '--no-poll' }
if ($NoWebSocket) { $toolArgs += '--no-websocket' }
$commandLine = ($toolArgs | ForEach-Object { "'$_'" }) -join ' '

$selectorYaml = ''
if ($NodeSelector) {
    $pair = $NodeSelector -split '=', 2
    # Without the '=', the value lands as empty and the selector matches no node at all: the pod
    # sits Pending and the only symptom is the wait below timing out five minutes later.
    if ($pair.Count -ne 2 -or -not $pair[0] -or -not $pair[1]) {
        throw "-NodeSelector must be key=value, e.g. 'kubernetes.io/hostname=node-1'."
    }
    $selectorYaml = "      nodeSelector:`n        $($pair[0]): `"$($pair[1])`"`n"
}

# The completed pod is retained (restartPolicy: Never, no ttlSecondsAfterFinished), so the final
# report is still readable with `kubectl logs` if the follow below is interrupted.
$job = @"
apiVersion: batch/v1
kind: Job
metadata:
  name: $JobName
spec:
  backoffLimit: 0
  template:
    spec:
      restartPolicy: Never
$selectorYaml      volumes:
        - name: src
          configMap:
            name: $JobName-src
        - name: work
          emptyDir: {}
      containers:
        - name: loadtest
          image: mcr.microsoft.com/dotnet/sdk:10.0
          workingDir: /work
          env:
            - name: DOTNET_CLI_TELEMETRY_OPTOUT
              value: "1"
            - name: DOTNET_NOLOGO
              value: "1"
            - name: DOTNET_gcServer
              value: "1"
          command:
            - /bin/sh
            - -c
            - |
              set -e
              cp /src/* /work/
              dotnet publish -c Release -o /work/app --nologo -v q
              exec dotnet /work/app/RedMist.StatusLoadTest.dll $commandLine
          volumeMounts:
            - name: src
              mountPath: /src
            - name: work
              mountPath: /work
          resources:
            requests:
              cpu: "250m"
              memory: "512Mi"
            limits:
              cpu: "1500m"
              memory: "1536Mi"
"@

$manifest = New-TemporaryFile
Set-Content -Path $manifest -Value $job -Encoding utf8
try {
    Write-Host 'Starting job...'
    & kubectl @kube apply -f $manifest | Out-Null
}
finally {
    Remove-Item $manifest -Force
}

Write-Host 'Waiting for the pod (the first run restores packages, so allow a minute)...'
& kubectl @kube wait --for=condition=ready pod -l "job-name=$JobName" --timeout=300s | Out-Null
& kubectl @kube logs -f -l "job-name=$JobName" --tail=-1

Write-Host ''
Write-Host "Clean up with: kubectl --context $Context -n $Namespace delete job $JobName configmap $JobName-src"
