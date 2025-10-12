#!/usr/bin/env pwsh
# Build RedMist Documentation

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Building RedMist Documentation" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Check if DocFX is installed
Write-Host "Checking for DocFX..." -ForegroundColor Yellow
$docfxVersion = docfx --version 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "DocFX not found. Installing..." -ForegroundColor Yellow
    dotnet tool install -g docfx
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to install DocFX"
        exit 1
    }
    Write-Host "DocFX installed successfully" -ForegroundColor Green
} else {
    Write-Host "DocFX version: $docfxVersion" -ForegroundColor Green
}

Write-Host ""

# Clean previous build
if (Test-Path "_site") {
    Write-Host "Cleaning previous build..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force "_site"
}

if (Test-Path "api") {
    Write-Host "Cleaning previous API metadata..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force "api/*.yml"
}

Write-Host ""

# Build solution first to generate XML documentation
Write-Host "Building solution..." -ForegroundColor Yellow
dotnet build --configuration Release --no-incremental

if ($LASTEXITCODE -ne 0) {
    Write-Error "Solution build failed"
    exit 1
}

Write-Host "Solution built successfully" -ForegroundColor Green
Write-Host ""

# Generate API metadata from XML documentation
Write-Host "Generating API metadata..." -ForegroundColor Yellow
docfx metadata docfx.json

if ($LASTEXITCODE -ne 0) {
    Write-Error "Metadata generation failed"
    exit 1
}

Write-Host "Metadata generated successfully" -ForegroundColor Green
Write-Host ""

# Build documentation site
Write-Host "Building documentation site..." -ForegroundColor Yellow
docfx build docfx.json

if ($LASTEXITCODE -ne 0) {
    Write-Error "Documentation build failed"
    exit 1
}

Write-Host "Documentation built successfully!" -ForegroundColor Green
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Build Complete!" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor White
Write-Host "  1. Preview locally: " -NoNewline -ForegroundColor White
Write-Host ".\serve-docs.ps1" -ForegroundColor Yellow
Write-Host "  2. Or run: " -NoNewline -ForegroundColor White
Write-Host "docfx serve _site" -ForegroundColor Yellow
Write-Host "  3. Open browser to: " -NoNewline -ForegroundColor White
Write-Host "http://localhost:8080" -ForegroundColor Cyan
Write-Host ""
