# RedMist Database Migrations

This directory contains database migrations for the RedMist Timing & Scoring system using PostgreSQL.

## Overview

RedMist uses PostgreSQL as its primary database. All migrations are managed through Entity Framework Core.

## Creating Migrations

To create a new migration:

```sh
cd RedMist.Database
dotnet ef migrations add <MigrationName>
```

## Applying Migrations

To apply migrations to a PostgreSQL database:

```sh
dotnet ef database update --connection "Host=your-server;Database=redmist-timing;Username=username;Password=password"
```

## Initial Setup

1. **Create the database schema:**
   ```sh
   dotnet ef database update --connection "Host=localhost;Database=redmist-timing-dev;Username=postgres;Password=yourpassword"
   ```

2. **Create the OrganizationExtView:**
   ```sh
   psql -h localhost -U postgres -d redmist-timing-dev -f Migrations/CreateOrganizationExtView.sql
   ```

## Important Notes

### OrganizationExtView

The `OrganizationExtView` is a database view that provides organizations with a fallback to a default logo. This view is **NOT** managed by EF Core migrations and must be created manually using the SQL script.

**Why?**
- The view uses complex SQL (CROSS JOIN with subquery, COALESCE) that doesn't translate well to EF Core's migration system
- Keeping it as a manual SQL script gives us full control over the view definition

### Accessing the View in Code

The view is accessed through an extension method:

```csharp
using RedMist.Database.Extensions;

using var context = await tsContext.CreateDbContextAsync();
var orgs = await context.OrganizationExtView()
    .Where(o => o.Id == id)
    .ToListAsync();
```

## PostgreSQL Configuration

The database context is configured to:
- Use `jsonb` column type for all JSON properties (better performance)
- Use PostgreSQL-specific conventions and features
- Enable legacy timestamp behavior for compatibility

## Connection Strings

### Development (Local)
```
Host=localhost;Database=redmist-timing-dev;Username=postgres;Password=yourpassword
```

### Production (Example)
```
Host=your-pg-server.postgres.database.azure.com;Database=redmist-timing;Username=adminuser@your-pg-server;Password=securepassword;SSL Mode=Require
```

## Troubleshooting

### "View does not exist"
Run the `CreateOrganizationExtView.sql` script manually:
```sh
psql -h localhost -U postgres -d redmist-timing-dev -f Migrations/CreateOrganizationExtView.sql
```

### Legacy Timestamp Behavior
PostgreSQL migrations require the legacy timestamp behavior to be enabled. This is configured in the application startup:
```csharp
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
