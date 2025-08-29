# RedMist Database Migration Runner

This is a console application that runs Entity Framework Core database migrations for the RedMist system.

## Key Features

- **Lightweight Console App**: Uses `Microsoft.NET.Sdk` instead of Web SDK to minimize dependencies
- **Base .NET Runtime**: Only requires `mcr.microsoft.com/dotnet/runtime:9.0`, not ASP.NET Core
- **Kubernetes-Optimized**: Designed to run as a Kubernetes Job with proper logging and error handling
- **Connection Testing**: Verifies database connectivity before attempting migrations
- **Migration Status**: Logs pending migrations and completion status

## Changes Made

### Fixed Container Runtime Issues
- Removed dependency on `Microsoft.AspNetCore.App` framework
- Changed from Web SDK to Console SDK
- Removed `RedMist.Backend.Shared` dependency to avoid ASP.NET Core transitive dependencies
- Updated Dockerfile to use runtime image instead of aspnet image

### Enhanced Logging
- Added detailed migration status logging
- Added connection string masking for security
- Added Entity Framework specific logging configuration
- Increased delay before container exit to ensure Kubernetes captures logs

### Kubernetes Integration
- Designed for use as Helm pre-install/pre-upgrade hook
- Returns proper exit codes (0 for success, 1 for failure)
- Includes example Kubernetes Job configuration

## Usage

### Local Development
```bash
# Set connection string
export ConnectionStrings__Default="Server=localhost;Database=RedMist;Integrated Security=true;TrustServerCertificate=true;"

# Run migrations
dotnet run --project RedMist.DatabaseMigrationRunner
```

### Kubernetes Deployment
See `kubernetes-example.yaml` for example Helm Job configuration.

## Troubleshooting

### Container Logs
```bash
kubectl logs job/redmist-db-migration -n timing-dev
```

### Common Issues
1. **Connection String**: Ensure the connection string secret is properly configured
2. **Network Access**: Verify the migration job can reach the database
3. **Permissions**: Ensure the database user has sufficient permissions to run migrations
4. **Timeouts**: Check if the database is responding within reasonable time limits

## Dependencies

- .NET 9.0 Runtime
- Entity Framework Core 9.0.6
- SQL Server provider
- NLog for logging