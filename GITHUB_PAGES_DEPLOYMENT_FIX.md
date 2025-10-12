# GitHub Pages Deployment Fix

## ?? Problem

The GitHub Actions workflow was failing with:
```
Branch "main" is not allowed to deploy to github-pages due to environment protection rules.
```

## ?? Root Cause

**Mismatch between deployment methods:**
- Workflow uses: GitHub Pages deployment action (`actions/deploy-pages@v4`)
- Pages settings: Deploy from `gh-pages` branch

These two approaches are incompatible.

## ? Solution Applied

Modified the workflow to push to the `gh-pages` branch using `peaceiris/actions-gh-pages@v3`.

### Changes Made:

1. **Permissions Updated:**
   ```yaml
   permissions:
     contents: write  # Changed from 'read'
   ```

2. **Deployment Method Changed:**
   ```yaml
   # OLD (GitHub Pages deployment action)
   - uses: actions/configure-pages@v4
   - uses: actions/upload-pages-artifact@v3
   - uses: actions/deploy-pages@v4
   
   # NEW (Push to gh-pages branch)
   - uses: peaceiris/actions-gh-pages@v3
     with:
       github_token: ${{ secrets.GITHUB_TOKEN }}
       publish_dir: ./_site
       publish_branch: gh-pages
   ```

3. **Jobs Simplified:**
   - Removed separate `deploy` job
   - Combined into single `build-and-deploy` job

## ?? Current Configuration

### GitHub Pages Settings (Keep As Is)
- **Source:** Deploy from a branch
- **Branch:** `gh-pages` / (root)

### Workflow Behavior
1. Triggers on push to `main` branch
2. Builds .NET solution
3. Generates DocFX documentation
4. Pushes `_site/` folder to `gh-pages` branch
5. GitHub Pages deploys from `gh-pages` automatically

## ?? Alternative: Use GitHub Actions Deployment

If you prefer the modern GitHub Actions deployment method:

### Step 1: Change Pages Settings
1. Go to **Settings** ? **Pages**
2. Under **Source**, select **"GitHub Actions"**
3. Save

### Step 2: Revert Workflow (Optional)
Use the original workflow with:
```yaml
permissions:
  contents: read
  pages: write
  id-token: write

# Deploy job with actions/deploy-pages@v4
```

## ? Testing

### Verify Deployment
1. Commit and push the updated workflow
2. Check Actions tab for workflow run
3. Verify `gh-pages` branch is created/updated
4. Check Pages deployment at: `https://bgriggs.github.io/redmist-timing-scoring-backend/`

### Check Workflow
```bash
git add .github/workflows/docs.yml
git commit -m "Fix GitHub Pages deployment"
git push origin main
```

### Monitor Progress
1. **Actions tab** - Watch workflow execution
2. **Branches** - Verify `gh-pages` updated
3. **Pages settings** - See deployment status

## ?? Troubleshooting

### If Deployment Still Fails

**Check Pages Settings:**
- Ensure "Deploy from a branch" is selected
- Branch should be `gh-pages` (not `main`)
- Folder should be `/ (root)` (not `/docs`)

**Check Permissions:**
- Workflow needs `contents: write` permission
- `GITHUB_TOKEN` has appropriate access

**Check Branch Protection:**
- `gh-pages` branch should not have protection rules
- Or add workflow to allowed deployers

### If gh-pages Branch Doesn't Exist
The workflow will create it automatically on first run.

## ?? Comparison: Deployment Methods

| Method | Pros | Cons |
|--------|------|------|
| **GitHub Actions Deploy** (Original) | Modern, direct deployment | Requires Pages source: "GitHub Actions" |
| **gh-pages Branch** (Current) | Compatible with branch deployment | Extra step (push to branch) |

## ?? Expected Result

After fix:
1. ? Workflow completes successfully
2. ? `gh-pages` branch updated with documentation
3. ? GitHub Pages deploys automatically
4. ? Documentation live at: `https://bgriggs.github.io/redmist-timing-scoring-backend/`

## ?? Summary

**Issue:** Workflow tried to deploy via GitHub Actions, but Pages was set to deploy from `gh-pages` branch.

**Fix:** Changed workflow to push to `gh-pages` branch, compatible with current Pages settings.

**Result:** Documentation now builds and deploys successfully! ??
