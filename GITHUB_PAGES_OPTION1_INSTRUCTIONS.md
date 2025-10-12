# GitHub Pages Configuration - Option 1 (GitHub Actions)

## ? Step-by-Step Instructions

### Step 1: Change Pages Settings on GitHub

1. **Navigate to Repository Settings**
   - Go to: https://github.com/bgriggs/redmist-timing-scoring-backend
   - Click on **Settings** tab (top right)

2. **Go to Pages Settings**
   - In the left sidebar, click **Pages**
   - You'll see current configuration

3. **Change Source to GitHub Actions**
   - Under **Build and deployment** section
   - Find **Source** dropdown
   - **Current setting:** "Deploy from a branch" with `gh-pages`
   - **Change to:** "GitHub Actions" ? Select this option
   
   ![Pages Settings](https://docs.github.com/assets/cb-47267/mw-1440/images/help/pages/pages-source-github-actions.webp)

4. **Save (Automatic)**
   - The change saves automatically
   - You should see a message confirming GitHub Actions deployment

### Step 2: Verify Workflow

The workflow has been updated to use GitHub Actions deployment:

```yaml
permissions:
  contents: read
  pages: write      # ? Allows writing to Pages
  id-token: write   # ? Allows OIDC authentication

jobs:
  build:
    # Builds documentation
    
  deploy:
    environment:
      name: github-pages
    # Deploys using actions/deploy-pages@v4
```

### Step 3: Push and Test

```bash
# Commit the workflow changes
git add .github/workflows/docs.yml
git commit -m "Configure GitHub Pages for Actions deployment"
git push origin main
```

### Step 4: Monitor Deployment

1. **Watch Workflow**
   - Go to **Actions** tab
   - Click on "Deploy Documentation" workflow
   - Watch it complete both `build` and `deploy` jobs

2. **Check Deployment**
   - Go to **Settings** ? **Pages**
   - You'll see deployment status
   - URL: `https://bgriggs.github.io/redmist-timing-scoring-backend/`

## ?? What Changed

### Before (gh-pages branch)
```
Source: Deploy from a branch
Branch: gh-pages / (root)
```

### After (GitHub Actions)
```
Source: GitHub Actions
Custom workflow: .github/workflows/docs.yml
```

## ? Benefits of GitHub Actions Deployment

1. **Direct Deployment** - No intermediate `gh-pages` branch needed
2. **Faster** - Deploys immediately after build
3. **Modern Approach** - GitHub's recommended method
4. **Better Control** - Full control over deployment process
5. **Environment Protection** - Can add approval gates if needed

## ?? Verification

After deployment succeeds, verify:

### Check Pages Deployment
```
Settings ? Pages ? "Your site is live at..."
URL: https://bgriggs.github.io/redmist-timing-scoring-backend/
```

### Check Actions
```
Actions ? Deploy Documentation ? ? Success
- build job: ?
- deploy job: ?
```

### Check Branches
The `gh-pages` branch is no longer needed and can be deleted:
```bash
# Optional: Delete old gh-pages branch
git push origin --delete gh-pages
```

## ?? Current Workflow Flow

1. **Trigger:** Push to `main` or manual dispatch
2. **Build Job:**
   - Checkout code
   - Setup .NET 9
   - Install DocFX
   - Build solution
   - Generate API metadata
   - Build documentation
   - Upload `_site/` as artifact
3. **Deploy Job:**
   - Download artifact
   - Deploy to GitHub Pages using OIDC
   - Update Pages environment

## ?? Expected Result

? Workflow completes successfully  
? Documentation deploys to GitHub Pages  
? Site available at: https://bgriggs.github.io/redmist-timing-scoring-backend/  
? Auto-updates on every push to main  

## ?? Troubleshooting

### If Deployment Still Fails

**Check Pages Settings Again:**
- Ensure "GitHub Actions" is selected (not "Deploy from a branch")
- Refresh the page to confirm

**Check Workflow Permissions:**
- `pages: write` permission is set ?
- `id-token: write` permission is set ?

**Check Environment:**
- Go to Settings ? Environments ? github-pages
- Ensure no restrictive deployment rules
- Or configure `main` branch as allowed

### If You Don't See "GitHub Actions" Option

This means GitHub Pages is not enabled for Actions yet:
1. Try selecting any branch first
2. Save
3. Then you should see "GitHub Actions" option

## ?? Summary

**Action Required:**
1. Go to **Settings** ? **Pages**
2. Change **Source** from "Deploy from a branch" to **"GitHub Actions"**
3. Push the updated workflow
4. Watch it deploy successfully!

**No Code Changes Needed** - The workflow is already configured correctly for GitHub Actions deployment.

---

Once you complete Step 1 (change Pages settings), push the changes and your documentation will deploy automatically! ??
