# API Documentation

## Overview

The RedMist Timing & Scoring system provides comprehensive APIs for race timing and event management.

## Available APIs

### [Status API](RedMist.StatusApi.md)
Real-time event status, timing data, and race information.

**Base URL:** `https://api.redmist.racing`

**Key Features:**
- Live event listings
- Real-time timing data
- Session results
- Control logs
- Competitor metadata

### [Event Management API](RedMist.EventManagement.md)
Event configuration and organization management.

**Key Features:**
- Event CRUD operations
- Organization settings
- Control log configuration
- Event scheduling

### [User Management API](RedMist.UserManagement.md)
User and organization administration.

**Key Features:**
- User management
- Organization provisioning
- Relay client setup
- Keycloak integration

### [Timing & Scoring Service](RedMist.TimingAndScoringService.md)
Internal real-time event processing service.

**Key Features:**
- Real-time calculations
- Session state management
- MessagePack serialization

## Authentication

All protected endpoints require Bearer token authentication:

```http
Authorization: Bearer YOUR_ACCESS_TOKEN
```

See the [Authentication Guide](../articles/authentication.md) for details.

## Versioning

The APIs support versioning via URL segments:

- V1: `/v1/[controller]/[action]` or `/[controller]/[action]` (legacy)
- V2: `/v2/[controller]/[action]`

See [API Versioning](../articles/api-versioning.md) for more information.

## Quick Links

- [Getting Started](../articles/getting-started.md)
- [Code Examples](../articles/code-examples.md)
- [Data Models](../articles/data-models.md)
