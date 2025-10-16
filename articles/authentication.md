# Authentication Guide

RedMist uses OAuth 2.0 for authentication and authorization.

## Authentication Flow

### Client Credentials Flow (Recommended for Services)

The client credentials flow is used for machine-to-machine authentication.

#### Step 1: Request Token

**Endpoint:** `POST https://auth.redmist.racing/realms/redmist/protocol/openid-connect/token`

**Headers:**
```
Content-Type: application/x-www-form-urlencoded
```

**Body Parameters:**
| Parameter | Value | Description |
|-----------|-------|-------------|
| grant_type | client_credentials | OAuth2 grant type |
| client_id | YOUR_CLIENT_ID | Your client identifier |
| client_secret | YOUR_SECRET | Your client secret |

**Example Request:**
```bash
curl -X POST "https://auth.redmist.racing/realms/redmist/protocol/openid-connect/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=relay-myorg" \
  -d "client_secret=abc123xyz"
```

#### Step 2: Receive Token

**Response:**
```json
{
  "access_token": "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9...",
  "expires_in": 300,
  "refresh_expires_in": 0,
  "token_type": "Bearer",
  "not-before-policy": 0,
  "scope": "profile email"
}
```

#### Step 3: Use Token

Include the token in the Authorization header of all API requests:

```http
Authorization: Bearer eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9...
```

## Token Types

### Service Account Tokens
- Used for server-to-server communication
- Obtained via client credentials flow
- No user context
- Typically expires in 5 minutes

### User Tokens
- Obtained via authorization code flow (web applications)
- Contains user identity and claims
- Supports refresh tokens

## Code Examples

### JavaScript/Node.js

```javascript
async function getAccessToken() {
    const params = new URLSearchParams();
    params.append("grant_type", "client_credentials");
    params.append("client_id", process.env.CLIENT_ID);
    params.append("client_secret", process.env.CLIENT_SECRET);

    const response = await fetch(
        "https://auth.redmist.racing/realms/redmist/protocol/openid-connect/token",
        {
            method: "POST",
            headers: {
                "Content-Type": "application/x-www-form-urlencoded"
            },
            body: params
        }
    );

    const data = await response.json();
    return data.access_token;
}

// Use with API
async function getEvents() {
    const token = await getAccessToken();
    
    const response = await fetch(
        "https://api.redmist.racing/Events/LoadLiveEvents",
        {
            headers: {
                "Authorization": `Bearer ${token}`
            }
        }
    );
    
    return await response.json();
}
```

### Python

```python
import requests
import os

def get_access_token():
    """Get access token using client credentials"""
    response = requests.post(
        "https://auth.redmist.racing/realms/redmist/protocol/openid-connect/token",
        data={
            "grant_type": "client_credentials",
            "client_id": os.environ["CLIENT_ID"],
            "client_secret": os.environ["CLIENT_SECRET"]
        }
    )
    response.raise_for_status()
    return response.json()["access_token"]

def get_events():
    """Get live events using authenticated API"""
    token = get_access_token()
    
    response = requests.get(
        "https://api.redmist.racing/Events/LoadLiveEvents",
        headers={"Authorization": f"Bearer {token}"}
    )
    response.raise_for_status()
    return response.json()
```

### C#

```csharp
using System.Net.Http;
using System.Net.Http.Headers;

public class RedMistAuthClient
{
    private readonly HttpClient _httpClient;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private string? _accessToken;
    private DateTime _tokenExpiry;

    public RedMistAuthClient(string clientId, string clientSecret)
    {
        _httpClient = new HttpClient();
        _clientId = clientId;
        _clientSecret = clientSecret;
    }

    public async Task<string> GetAccessTokenAsync()
    {
        // Return cached token if still valid
        if (_accessToken != null && DateTime.UtcNow < _tokenExpiry)
        {
            return _accessToken;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, 
            "https://auth.redmist.racing/realms/redmist/protocol/openid-connect/token");

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _clientId,
            ["client_secret"] = _clientSecret
        };

        request.Content = new FormUrlEncodedContent(formData);

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<TokenResponse>();
        
        _accessToken = result.AccessToken;
        _tokenExpiry = DateTime.UtcNow.AddSeconds(result.ExpiresIn - 30); // Refresh 30s early

        return _accessToken;
    }

    public async Task<List<EventListSummary>> GetLiveEventsAsync()
    {
        var token = await GetAccessTokenAsync();
        
        var request = new HttpRequestMessage(HttpMethod.Get, 
            "https://api.redmist.racing/Events/LoadLiveEvents");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<List<EventListSummary>>();
    }

    private record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn
    );
}
```

## Token Management

### Token Expiration
Tokens expire after **5 minutes** (300 seconds). Implement token caching and refresh:

```javascript
class TokenManager {
    constructor(clientId, clientSecret) {
        this.clientId = clientId;
        this.clientSecret = clientSecret;
        this.token = null;
        this.expiry = null;
    }

    async getToken() {
        // Check if token is still valid
        if (this.token && this.expiry > Date.now()) {
            return this.token;
        }

        // Request new token
        const response = await fetch(
            "https://auth.redmist.racing/realms/redmist/protocol/openid-connect/token",
            {
                method: "POST",
                headers: { "Content-Type": "application/x-www-form-urlencoded" },
                body: new URLSearchParams({
                    grant_type: "client_credentials",
                    client_id: this.clientId,
                    client_secret: this.clientSecret
                })
            }
        );

        const data = await response.json();
        this.token = data.access_token;
        this.expiry = Date.now() + (data.expires_in - 30) * 1000; // Refresh 30s early

        return this.token;
    }
}
```

### SignalR Authentication

For SignalR connections, provide a token factory:

```javascript
const connection = new signalR.HubConnectionBuilder()
    .withUrl("https://api.redmist.racing/status/event-status", {
        accessTokenFactory: async () => await tokenManager.getToken()
    })
    .withAutomaticReconnect()
    .build();
```

## Authorization

### Roles and Claims

Tokens include role claims that determine access:

**Service Account Roles:**
- `relay-svc` - Relay client permissions
- `org-admin` - Organization administration

**User Roles:**
- `user` - Basic user access
- `admin` - Administrative access

### Endpoint Authorization

Different endpoints require different permissions:

| Endpoint | Permission | Role Required |
|----------|-----------|---------------|
| GET /Events/LoadLiveEvents | Public | None |
| GET /Events/LoadEvent | Authenticated | Any |
| POST /Event/SaveNewEvent | Organization Owner | org-admin |
| GET /Organization/LoadRelayConnection | Organization Member | Any in org |

## Security Best Practices

### 1. Secure Credential Storage
- Never commit credentials to source control
- Use environment variables or secure vaults
- Rotate secrets regularly

### 2. Token Security
- Always use HTTPS
- Don't log tokens
- Clear tokens on logout
- Implement token refresh

### 3. Error Handling
```javascript
async function apiCall() {
    try {
        const response = await fetch(url, {
            headers: { Authorization: `Bearer ${token}` }
        });

        if (response.status === 401) {
            // Token expired, refresh and retry
            token = await getNewToken();
            return apiCall();
        }

        if (!response.ok) {
            throw new Error(`API error: ${response.status}`);
        }

        return await response.json();
    } catch (error) {
        console.error("API call failed:", error);
        throw error;
    }
}
```

## Troubleshooting

### Invalid Credentials
**Error:** `401 Unauthorized`
- Verify client_id and client_secret are correct
- Check if client is enabled in Keycloak
- Ensure using correct realm

### Token Expired
**Error:** `401 Unauthorized` on API call
- Implement token refresh logic
- Check token expiration time
- Verify system clock is accurate

### CORS Issues
**Error:** CORS policy blocking request
- Ensure using HTTPS
- Check if origin is allowed
- Use server-side proxy if needed

## Related Documentation

- [Getting Started](getting-started.md)
- [REST API Guide](rest-api-guide.md)
- [Code Examples](code-examples.md)
