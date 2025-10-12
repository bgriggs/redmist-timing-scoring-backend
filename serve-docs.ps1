#!/usr/bin/env pwsh
# Serve RedMist Documentation Locally

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  RedMist Documentation Server" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Check if _site directory exists
if (!(Test-Path "_site")) {
    Write-Host "Documentation not built yet. Building now..." -ForegroundColor Yellow
    Write-Host ""
    & .\build-docs.ps1
    
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed. Cannot serve documentation."
        exit 1
    }
}

Write-Host ""
Write-Host "Starting documentation server..." -ForegroundColor Green
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Server Information" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  URL: " -NoNewline -ForegroundColor White
Write-Host "http://localhost:8080" -ForegroundColor Cyan
Write-Host "  Press " -NoNewline -ForegroundColor White
Write-Host "Ctrl+C" -NoNewline -ForegroundColor Yellow
Write-Host " to stop" -ForegroundColor White
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Serve documentation
docfx serve _site
