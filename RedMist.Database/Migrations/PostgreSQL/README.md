# RedMist PostgreSQL Database Migrations

This directory contains PostgreSQL-specific database migrations for the RedMist Timing & Scoring system.

## Overview

The PostgreSQL migrations use a separate `TsContextPostgreSQL` DbContext to avoid conflicts with existing SQL Server migrations. This allows us to maintain both SQL Server and PostgreSQL support without migration collisions.

## Creating Migrations

To create a new PostgreSQL migration:

```sh
cd RedMist.Database
dotnet ef migrations add <MigrationName> --context TsContextPostgreSQL --output-dir Migrations/PostgreSQL
```

## Applying Migrations

To apply migrations to a PostgreSQL database:

```sh
dotnet ef database update --context TsContextPostgreSQL --connection "Host=your-server;Database=redmist-timing;Username=username;Password=password"
```

## Initial Setup

1. **Create the database schema:**
   ```sh
   dotnet ef database update --context TsContextPostgreSQL --connection "Host=localhost;Database=redmist-timing-dev;Username=postgres;Password=yourpassword"
   ```

2. **Create the OrganizationExtView:**
   ```sh
psql -h localhost -U postgres -d redmist-timing-dev -f Migrations/PostgreSQL/CreateOrganizationExtView.sql
 ```

## Important Notes

### OrganizationExtView

The `OrganizationExtView` is a database view that provides organizations with a fallback to a default logo. This view is **NOT** managed by EF Core migrations and must be created manually using the SQL script.

**Why?**
- The view uses complex SQL (CROSS JOIN with subquery, COALESCE) that doesn't translate well to EF Core's migration system
- Keeping it as a manual SQL script gives us full control over the view definition
- Different databases may require slightly different view syntax

### Accessing the View in Code

The view is accessed through an extension method:

```csharp
using RedMist.Database.Extensions;

using var context = await tsContext.CreateDbContextAsync();
var orgs = await context.OrganizationExtView()
    .Where(o => o.Id == id)
    .ToListAsync();
```

## PostgreSQL vs SQL Server

### Key Differences

| Feature | PostgreSQL | SQL Server |
|---------|-----------|------------|
| **Schema** | `public` (default) | `dbo` (default) |
| **JSON Type** | `jsonb` (binary, indexed) | `nvarchar(max)` |
| **Identifiers** | Case-sensitive in quotes | Case-insensitive |
| **Limit Clause** | `LIMIT n` | `TOP n` |

### Configuration Differences

The PostgreSQL context (`TsContextPostgreSQL`) is configured to:
- Use `jsonb` column type for all JSON properties (better performance)
- Use PostgreSQL-specific conventions
- Not include the `OrganizationExtView` in the model (manual SQL)

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

### "Unable to create DbContext"
Make sure you're using `--context TsContextPostgreSQL` when running EF Core commands.

### "View does not exist"
Run the `CreateOrganizationExtView.sql` script manually:
```sh
psql -h localhost -U postgres -d redmist-timing-dev -f Migrations/PostgreSQL/CreateOrganizationExtView.sql
```

### "Column type mismatch"
PostgreSQL uses `jsonb` for JSON columns. Make sure your PostgreSQL version supports JSONB (9.4+).

## Migration History

| Migration | Description |
|-----------|-------------|
| `InitialPostgreSQL` | Initial schema creation for PostgreSQL |
| _Manual_ | OrganizationExtView (see CreateOrganizationExtView.sql) |

## See Also

- [PostgreSQL Migration Guide](../../../POSTGRESQL_MIGRATION_GUIDE.md)
- [EF Core PostgreSQL Provider Documentation](https://www.npgsql.org/efcore/)
