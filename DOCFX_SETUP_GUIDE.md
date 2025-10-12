# DocFX Setup and Build Guide

This guide explains how to set up and build the RedMist documentation website using DocFX.

## Prerequisites

- .NET 9 SDK installed
- Git (optional, for publishing to GitHub Pages)

## Installation

### Step 1: Install DocFX

Install DocFX as a global .NET tool:

```bash
dotnet tool install -g docfx
```

Or update if already installed:

```bash
dotnet tool update -g docfx
```

### Step 2: Verify Installation

```bash
docfx --version
```

## Building Documentation

### Build API Documentation

Generate API documentation from XML comments:

```bash
docfx metadata
```

This will:
- Extract XML documentation from all projects
- Generate YAML files in the `api/` directory
- Create API reference structure

### Build Complete Site

Build the entire documentation website:

```bash
docfx build
```

This will:
- Process all markdown files
- Generate API reference pages
- Combine articles with API docs
- Output to `_site/` directory

### Serve Locally

Preview the documentation website:

```bash
docfx serve _site
```

Then open your browser to: `http://localhost:8080`

### Build and Serve (Combined)

```bash
docfx build && docfx serve _site
```

Or use the DocFX command:

```bash
docfx --serve
```

## Project Structure

```
RedMist.TimingAndScoringService/
??? docfx.json                 # DocFX configuration
??? index.md                   # Homepage
??? toc.yml                    # Top-level table of contents
??? api/                       # API documentation
?   ??? index.md              # API overview
?   ??? *.yml                 # Generated API files (after build)
??? articles/                  # Conceptual documentation
?   ??? toc.yml               # Articles table of contents
?   ??? getting-started.md    # Getting started guide
?   ??? architecture.md       # Architecture docs
?   ??? authentication.md     # Auth guide
?   ??? signalr-hubs.md      # SignalR docs
?   ??? ...                   # More articles
??? images/                    # Images and assets
??? _site/                     # Generated output (gitignored)
```

## Configuration

### docfx.json

The main configuration file controls:

- **metadata:** Which projects to document
- **build:** What to include in the site
- **template:** Which theme to use
- **globalMetadata:** Site-wide settings

Key settings:

```json
{
  "metadata": [{
    "src": [{ "files": ["**/*.csproj"] }],
    "dest": "api"
  }],
  "build": {
    "content": [
      { "files": ["api/**.yml"] },
      { "files": ["articles/**.md"] }
    ],
    "template": ["default", "modern"],
    "globalMetadata": {
      "_appTitle": "RedMist Documentation"
    }
  }
}
```

## Customization

### Themes

DocFX supports custom themes. Available templates:
- `default` - Standard DocFX theme
- `modern` - Modern responsive theme
- `statictoc` - Static table of contents

Install additional themes:

```bash
docfx template export modern
```

### Custom CSS

Add custom styling:

1. Create `styles/main.css`
2. Reference in `docfx.json`:

```json
{
  "build": {
    "resource": [
      { "files": ["styles/**"] }
    ]
  }
}
```

### Custom Logo

Add your logo:

1. Place image in `images/logo.png`
2. Update `docfx.json`:

```json
{
  "build": {
    "globalMetadata": {
      "_appLogoPath": "images/logo.png"
    }
  }
}
```

## Publishing

### GitHub Pages

#### Option 1: GitHub Actions (Automated)

Create `.github/workflows/docs.yml`:

```yaml
name: Deploy Documentation

on:
  push:
    branches: [ main ]
  workflow_dispatch:

jobs:
  build-and-deploy:
    runs-on: ubuntu-latest
    
    steps:
    - uses: actions/checkout@v3
    
    - name: Setup .NET
      uses: actions/setup-dotnet@v3
      with:
        dotnet-version: 9.0.x
    
    - name: Install DocFX
      run: dotnet tool install -g docfx
    
    - name: Build Documentation
      run: |
        docfx metadata
        docfx build
    
    - name: Deploy to GitHub Pages
      uses: peaceiris/actions-gh-pages@v3
      with:
        github_token: ${{ secrets.GITHUB_TOKEN }}
        publish_dir: ./_site
        publish_branch: gh-pages
```

#### Option 2: Manual Publishing

```bash
# Build documentation
docfx build

# Create gh-pages branch
git checkout --orphan gh-pages
git rm -rf .
cp -r _site/* .
git add .
git commit -m "Deploy documentation"
git push origin gh-pages
```

### Custom Domain

1. Add `CNAME` file to `_site/`:

```
docs.redmist.racing
```

2. Configure in `docfx.json`:

```json
{
  "build": {
    "resource": [
      { "files": ["CNAME"] }
    ]
  }
}
```

3. Configure DNS:

```
CNAME  docs  yourusername.github.io
```

### Azure Static Web Apps

Deploy to Azure:

```bash
# Install Azure CLI
az login

# Create static web app
az staticwebapp create \
  --name redmist-docs \
  --resource-group redmist-rg \
  --source ./_site \
  --location eastus2

# Deploy
az staticwebapp deploy \
  --name redmist-docs \
  --resource-group redmist-rg \
  --app-location ./_site
```

## Continuous Integration

### Build Verification

Add to your CI pipeline:

```yaml
# GitHub Actions
- name: Build Docs
  run: |
    dotnet tool install -g docfx
    docfx build
  
- name: Check for Errors
  run: |
    if [ -f _site/404.html ]; then
      echo "Documentation built successfully"
    else
      echo "Build failed"
      exit 1
    fi
```

### Link Validation

Install and run link checker:

```bash
npm install -g broken-link-checker

# Check for broken links
blc http://localhost:8080 -ro
```

## Troubleshooting

### Build Errors

**Error: "Project not found"**
- Verify paths in `docfx.json` are correct
- Ensure projects have `<GenerateDocumentationFile>true</GenerateDocumentationFile>`

**Error: "XML file not found"**
- Build the solution first: `dotnet build`
- Check that XML files are generated in `bin/` directories

**Error: "Template not found"**
- Install template: `docfx template export modern`
- Or use default template

### Performance

**Slow builds:**
- Exclude test projects from metadata
- Use incremental build: `docfx build --incremental`
- Limit file scanning with precise globs

**Large output:**
- Optimize images before adding
- Enable compression in web server
- Use CDN for assets

### Common Issues

**Links not working:**
- Use relative paths in markdown
- Ensure `xref:` references are correct
- Check toc.yml structure

**Styles not applied:**
- Clear browser cache
- Rebuild with `docfx build --force`
- Check resource paths in docfx.json

**API docs missing:**
- Verify XML documentation is enabled
- Check metadata section in docfx.json
- Ensure projects compile successfully

## Advanced Features

### Cross-References

Link to API members:

```markdown
See <xref:RedMist.StatusApi.Controllers.EventsControllerBase> for details.

Method: <xref:RedMist.StatusApi.Controllers.EventsControllerBase.LoadLiveEvents>
```

### Code Snippets

Include code from files:

```markdown
[!code-csharp[Main](../samples/example.cs)]
```

### Tabs

Show platform-specific code:

```markdown
# [JavaScript](#tab/javascript)

const connection = new signalR.HubConnectionBuilder()...

# [Python](#tab/python)

hub = HubConnectionBuilder()...

# [C#](#tab/csharp)

var connection = new HubConnectionBuilder()...
```

### Alerts

Add important notices:

```markdown
> [!NOTE]
> This is a note

> [!WARNING]
> This is a warning

> [!TIP]
> This is a tip
```

## Scripts

### Build Script (build-docs.ps1)

```powershell
#!/usr/bin/env pwsh
# Build documentation

Write-Host "Building RedMist Documentation..." -ForegroundColor Green

# Clean previous build
if (Test-Path "_site") {
    Remove-Item -Recurse -Force "_site"
}

# Build solution first (generates XML docs)
Write-Host "Building solution..." -ForegroundColor Yellow
dotnet build --configuration Release

# Generate metadata
Write-Host "Generating API metadata..." -ForegroundColor Yellow
docfx metadata

# Build documentation
Write-Host "Building documentation site..." -ForegroundColor Yellow
docfx build

Write-Host "Documentation built successfully!" -ForegroundColor Green
Write-Host "Run 'docfx serve _site' to preview" -ForegroundColor Cyan
```

### Serve Script (serve-docs.ps1)

```powershell
#!/usr/bin/env pwsh
# Serve documentation locally

if (!(Test-Path "_site")) {
    Write-Host "Building documentation first..." -ForegroundColor Yellow
    & .\build-docs.ps1
}

Write-Host "Starting documentation server..." -ForegroundColor Green
Write-Host "Open browser to: http://localhost:8080" -ForegroundColor Cyan

docfx serve _site
```

## Resources

- [DocFX Documentation](https://dotnet.github.io/docfx/)
- [Markdown Syntax](https://dotnet.github.io/docfx/spec/docfx_flavored_markdown.html)
- [API Documentation Format](https://dotnet.github.io/docfx/tutorial/intro_to_docfx.html)
- [GitHub Pages Setup](https://pages.github.com/)

## Next Steps

1. ? Install DocFX
2. ? Build documentation locally
3. ? Review and customize
4. ? Set up GitHub Actions
5. ? Deploy to GitHub Pages

Your documentation will be available at:
`https://[your-username].github.io/redmist-timing-scoring-backend/`
