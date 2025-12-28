# Red Mist Backend Services

[![Build](https://github.com/bgriggs/redmist-timing-scoring-backend/actions/workflows/build.yml/badge.svg)](https://github.com/bgriggs/redmist-timing-scoring-backend/actions/workflows/build.yml)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/download)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Red Mist provides race timing and scoring services for motorsport events. The backend services are designed to handle real-time data processing, event management, and user authentication.
These service make up the backend of the Red Mist system. They are responsible for managing the timing and scoring of events, as well as providing APIs for user management and event orchestration.

# Documentation
Please visit https://docs.redmist.racing/ for the full documentation.

## Quick Reference
- Status API OpenAPI Spec: [Swagger UI](https://api.redmist.racing/status/swagger/index.html) | [swagger.json](https://api.redmist.racing/status/swagger/v1/swagger.json)
- Complete API documentation: [API Reference](https://docs.redmist.racing/api/index.html)
- Event Status Model [SessionState](https://docs.redmist.racing/api/RedMist.TimingCommon.Models.SessionState.html). This is the data included for a real-time event/session.
- Car Status Model [CarPosition](https://docs.redmist.racing/api/RedMist.TimingCommon.Models.CarPosition.html). This is the data available for a car in an event/session.

# Sample Projects
Sample projects demonstrating how to interact with the Red Mist backend services can be found in [samples](https://github.com/bgriggs/redmist-timing-scoring-backend/tree/main/samples).

# Upgrading to V2 API
As of January 1, 2026, Version 1 of the Red Mist API has been officially deprecated due to performance reasons.

Please refer to the [Upgrading to API Version 2](upgrading-to-version2-api.md) guide for details on migrating from Version 1 to Version 2 of the Red Mist API.