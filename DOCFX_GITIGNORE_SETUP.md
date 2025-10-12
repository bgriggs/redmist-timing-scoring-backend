# DocFX .gitignore Configuration

## ? Added to .gitignore

The following DocFX-generated files and folders have been added to `.gitignore`:

```gitignore
# DocFX Generated Files
_site/
api/*.yml
api/.manifest
.manifest
obj/docfx/
```

## ?? What Gets Ignored

### `_site/` 
- **What:** Complete generated documentation website
- **Why:** Build output, regenerated on every build
- **Size:** Can be large (hundreds of files)

### `api/*.yml`
- **What:** Auto-generated API metadata from XML comments
- **Why:** Generated from source code during build
- **Note:** `api/index.md` is NOT ignored (it's manually created)

### `api/.manifest` & `.manifest`
- **What:** DocFX build manifest files
- **Why:** Internal DocFX metadata, regenerated each build

### `obj/docfx/`
- **What:** DocFX intermediate build files
- **Why:** Temporary build artifacts

## ?? What Stays in Git

These files ARE committed to source control:

? **Configuration:**
- `docfx.json` - DocFX configuration
- `toc.yml` - Main table of contents
- `index.md` - Homepage

? **Articles:**
- `articles/*.md` - All article markdown files
- `articles/toc.yml` - Articles navigation

? **Manual API Docs:**
- `api/index.md` - API documentation overview

? **Build Scripts:**
- `build-docs.ps1` - Build script
- `serve-docs.ps1` - Serve script
- `.github/workflows/docs.yml` - CI/CD workflow

? **Images/Assets:**
- `images/**` - Any images you add

## ?? How It Works

### Local Development
```bash
# Generate files (ignored by git)
.\build-docs.ps1

# Files created in _site/ and api/*.yml
# Git ignores these automatically
```

### GitHub Actions
```yaml
# In .github/workflows/docs.yml
- name: Build Documentation
  run: docfx build docfx.json

# Generates files in CI environment
# Never committed to repo
# Deployed directly to GitHub Pages
```

## ? Benefits

1. **Clean Repository** - No build artifacts in version control
2. **No Merge Conflicts** - Generated files don't cause conflicts
3. **Smaller Repo Size** - Hundreds of files not tracked
4. **Automatic Updates** - CI builds fresh docs every time
5. **Source of Truth** - Only source files in git

## ?? Verify .gitignore

Check what's ignored:
```bash
# See what would be ignored
git status --ignored

# List ignored files
git ls-files --others --ignored --exclude-standard
```

## ?? Current Status

| File/Folder | Status | Reason |
|------------|--------|--------|
| `_site/` | ? Ignored | Generated output |
| `api/*.yml` | ? Ignored | Auto-generated from code |
| `api/index.md` | ? Tracked | Manual documentation |
| `articles/*.md` | ? Tracked | Source articles |
| `docfx.json` | ? Tracked | Configuration |
| `*.md` (root) | ? Tracked | Documentation source |

## ?? Best Practices

### ? DO Commit:
- Markdown articles
- Configuration files
- Manual documentation
- Build scripts
- Images and assets

### ? DON'T Commit:
- `_site/` directory
- `api/*.yml` files
- Build outputs
- Generated metadata

## ?? Workflow

1. **Write documentation** in `.md` files ? Commit
2. **Update XML comments** in code ? Commit
3. **Run build** locally ? Files generated (ignored)
4. **Push to GitHub** ? CI builds and deploys
5. **Documentation live** on GitHub Pages!

---

**Your `.gitignore` is now properly configured for DocFX!** ??

The generated documentation files will be ignored locally and in CI, keeping your repository clean while ensuring documentation is always up-to-date.
