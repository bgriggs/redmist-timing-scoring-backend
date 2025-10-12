# Production Swagger URLs - Quick Reference

## ?? Live API Documentation Endpoints

### RedMist Status API
**URL:** `https://api.redmist.racing/swagger`

**Available APIs:**
- V1 - Legacy Payload format
- V2 - Enhanced SessionState format

**Endpoints Include:**
- Event status and timing data
- Live event listings (public)
- Session results
- Car lap data
- Control logs
- Flags history
- Competitor metadata

---

### RedMist Event Management API
**URL:** `https://[management-service-url]/swagger`

**Endpoints Include:**
- Event CRUD operations
- Event activation/deactivation
- Organization settings
- Control log configuration
- Event scheduling

**Authentication:** Required (Bearer token)

---

### RedMist User Management API
**URL:** `https://[user-service-url]/swagger`

**Endpoints Include:**
- User organization management
- Organization creation
- Relay client provisioning
- Keycloak integration
- User roles and permissions

**Authentication:** Required (Bearer token)

---

### RedMist Timing & Scoring Service
**URL:** `https://[timing-service-url]/swagger`

**Endpoints Include:**
- Internal session state (MessagePack)
- Real-time timing calculations

**Authentication:** Internal service-to-service

---

## ?? Quick Authentication Guide

### Get Your Token
```bash
curl -X POST "https://auth.redmist.racing/realms/redmist/protocol/openid-connect/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=YOUR_CLIENT_ID" \
  -d "client_secret=YOUR_SECRET"
```

### Use in Swagger
1. Click **"Authorize"** button (?? icon)
2. Enter: `Bearer YOUR_ACCESS_TOKEN`
3. Click **"Authorize"**
4. Click **"Close"**
5. All authenticated endpoints now work!

---

## ?? Direct Access to OpenAPI Specs

### Status API
- **V1 Spec:** `https://api.redmist.racing/swagger/v1/swagger.json`
- **V2 Spec:** `https://api.redmist.racing/swagger/v2/swagger.json`

### Event Management API
- **V1 Spec:** `https://[management-service-url]/swagger/v1/swagger.json`

### User Management API
- **V1 Spec:** `https://[user-service-url]/swagger/v1/swagger.json`

### Timing & Scoring Service
- **V1 Spec:** `https://[timing-service-url]/swagger/v1/swagger.json`

**Use these specs with:**
- Postman (Import ? Link)
- Insomnia (Import ? From URL)
- OpenAPI Generator (for client SDKs)
- Any OpenAPI-compatible tool

---

## ?? Pro Tips

### 1. Bookmark Your Favorites
Save these Swagger URLs in your browser for quick access.

### 2. Use Browser Extensions
- **JSON Viewer** - Better formatting of spec files
- **OpenAPI (Swagger) Editor** - Validate and edit specs

### 3. Generate Client Code
```bash
# Using OpenAPI Generator
openapi-generator-cli generate \
  -i https://api.redmist.racing/swagger/v1/swagger.json \
  -g typescript-axios \
  -o ./generated-client
```

### 4. Download for Offline Use
Right-click on spec URL ? Save As ? Import to local tool

### 5. Share with Team
Send the Swagger URL instead of writing documentation - it's always up-to-date!

---

## ?? Common Use Cases

### Test an Endpoint
1. Go to Swagger UI
2. Authenticate if needed
3. Find your endpoint
4. Click "Try it out"
5. Enter parameters
6. Click "Execute"
7. See real response

### Debug an Issue
1. Open Swagger UI
2. Test the endpoint with known-good data
3. Compare response to expected
4. Verify request format

### Integrate a New Client
1. Download OpenAPI spec
2. Generate client SDK in your language
3. Use generated client in your app
4. Reference Swagger UI for examples

### Validate API Contract
1. Review endpoint in Swagger
2. Check request/response models
3. Verify authentication requirements
4. Confirm data types and constraints

---

## ?? Security Notes

- Swagger UI is **public** but endpoints still require authentication
- Bearer tokens are **never** stored by Swagger UI
- Use HTTPS only - never HTTP
- Tokens expire - refresh as needed
- Don't share tokens in screenshots or documentation

---

## ?? Support

**Documentation Issues?**
- Check XML comments in source code
- Verify build includes documentation files
- See `DOCUMENTATION_SETUP_SUMMARY.md`

**API Questions?**
- Test in Swagger UI first
- Review request/response models
- Check authentication requirements
- See `API_DOCUMENTATION_QUICK_REFERENCE.md`

**Access Issues?**
- Verify bearer token is valid
- Check token hasn't expired
- Ensure you have correct permissions
- See authentication guide above

---

## ?? Additional Resources

- **Main README:** Project overview and architecture
- **API Versioning:** `API_VERSIONING_QUICK_REFERENCE.md`
- **Setup Guide:** `DOCUMENTATION_SETUP_SUMMARY.md`
- **Production Config:** `SWAGGER_PRODUCTION_ENABLED.md`

---

**Last Updated:** After enabling Swagger in production

**Status:** ? All services have Swagger UI enabled in production
