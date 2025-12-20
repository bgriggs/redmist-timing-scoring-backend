# RedMist.MoveLogosToCdn

A utility tool for propagating organization logos from the database to the CDN.

## Purpose

This console application reads organization logos stored in the database and uploads them to the CDN storage. It handles both custom organization logos and applies a default logo for organizations that don't have one configured.

## Features

- Retrieves all organizations from the database
- Uploads existing organization logos to CDN
- Applies default logo for organizations without custom logos
- Purges CDN cache after uploads to ensure fresh content delivery
- Provides detailed logging of the migration process

## Configuration

The application requires the following configuration settings:

### Connection Strings
- `ConnectionStrings:Default` - PostgreSQL database connection string

### Assets Configuration
- `Assets:StorageZoneName` - Bunny CDN storage zone name
- `Assets:StorageAccessKey` - Bunny CDN storage access key
- `Assets:MainReplicationRegion` - Primary CDN replication region
- `Assets:ApiAccessKey` - Bunny CDN API access key
- `Assets:CdnId` - CDN identifier for cache purging

Configuration can be provided through:
- `appsettings.json`
- `appsettings.Development.json`
- User Secrets (recommended for development)
- Environment variables
- Command-line arguments

## Usage

```bash
dotnet run --project RedMist.MoveLogosToCdn
```

## Requirements

- .NET 10
- PostgreSQL database with Organizations and DefaultOrgImages tables
- Bunny CDN account with configured storage zone
