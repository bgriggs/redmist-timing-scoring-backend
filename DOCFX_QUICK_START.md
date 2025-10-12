# DocFX Quick Start

## 1-Minute Setup

### Install DocFX
```bash
dotnet tool install -g docfx
```

### Build Documentation
```bash
.\build-docs.ps1
```

### Preview Documentation
```bash
.\serve-docs.ps1
```

Open browser: http://localhost:8080

## That's It!

Your documentation is now:
- ? Built from code comments
- ? Served locally
- ? Ready to deploy

## Deploy to GitHub Pages

1. Push to GitHub:
```bash
git add .
git commit -m "Add DocFX documentation"
git push origin main
```

2. Enable GitHub Pages:
   - Go to repository **Settings** ? **Pages**
   - Source: **Deploy from a branch**
   - Branch: **gh-pages**
   - Click **Save**

3. Wait for GitHub Actions to complete

4. Visit your documentation:
   ```
   https://[your-username].github.io/redmist-timing-scoring-backend/
   ```

## File Structure

```
??? docfx.json          # Configuration
??? index.md            # Homepage
??? toc.yml             # Navigation
??? api/                # API docs (auto-generated)
??? articles/           # Guides and tutorials
??? build-docs.ps1      # Build script
??? serve-docs.ps1      # Serve script
```

## Common Commands

```bash
# Build documentation
docfx build

# Serve locally
docfx serve _site

# Build and serve
docfx --serve

# Clean and rebuild
Remove-Item -Recurse -Force _site
docfx build
```

## Next Steps

1. **Add More Articles** - Create markdown files in `articles/`
2. **Add Images** - Place in `images/` directory
3. **Customize Theme** - Edit `docfx.json`
4. **Add Examples** - Update article markdown files

## Need Help?

See full documentation:
- **Setup Guide:** `DOCFX_SETUP_GUIDE.md`
- **Complete Summary:** `DOCFX_COMPLETE_SUMMARY.md`
- **DocFX Docs:** https://dotnet.github.io/docfx/

---

**Quick Commands:**

Build: `.\build-docs.ps1`  
Serve: `.\serve-docs.ps1`  
Deploy: Push to `main` branch
