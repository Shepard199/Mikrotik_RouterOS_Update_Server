# IP Whitelist Security Guide

## Overview

The IP Whitelist feature provides a security layer that restricts API access to specific IP addresses or ranges. This is essential for production deployments where you want to limit access to known clients only.

## Features

✅ **Exact IP Matching** - Allow specific IP addresses
✅ **CIDR Range Support** - Allow entire network ranges (10.0.0.0/8, etc.)
✅ **Localhost Bypass** - Automatic localhost (127.0.0.1, ::1) support
✅ **Private Network Support** - Automatic private network ranges (10.x, 172.16-31.x, 192.168.x)
✅ **Endpoint Exclusion** - Bypass whitelist for specific endpoints (/health, /metrics)
✅ **Reverse Proxy Support** - X-Forwarded-For and X-Real-IP header support
✅ **IPv4 & IPv6** - Full support for both address families
✅ **Logging** - Detailed access denied logging

## Configuration

### appsettings.json

```json
{
  "IpWhitelist": {
    "Enabled": true,
    "AllowLocalhost": true,
    "AllowPrivateNetworks": true,
    "AllowedIps": [
      "203.0.113.10",
      "203.0.113.20"
    ],
    "AllowedRanges": [
      "10.0.0.0/8",
      "172.16.0.0/12",
      "192.168.0.0/16"
    ],
    "ExcludedEndpoints": [
      "/health",
      "/metrics",
      "/swagger"
    ]
  }
}
```

### Configuration Options

| Setting | Type | Description |
|---------|------|-------------|
| `Enabled` | bool | Enable/disable IP whitelist (default: true) |
| `AllowLocalhost` | bool | Auto-allow 127.0.0.1 and ::1 (default: true) |
| `AllowPrivateNetworks` | bool | Auto-allow private ranges (default: true) |
| `AllowedIps` | string[] | List of exact IPs to allow |
| `AllowedRanges` | string[] | List of CIDR ranges to allow |
| `ExcludedEndpoints` | string[] | Endpoints to bypass whitelist check |

## Use Cases

### 1. Production API Access Control

Allow only your application servers:

```json
{
  "IpWhitelist": {
    "Enabled": true,
    "AllowLocalhost": false,
    "AllowPrivateNetworks": false,
    "AllowedIps": [
      "203.0.113.10",
      "203.0.113.20",
      "203.0.113.30"
    ]
  }
}
```

### 2. Enterprise Network with Private IP Range

Allow entire corporate network:

```json
{
  "IpWhitelist": {
    "Enabled": true,
    "AllowPrivateNetworks": true,
    "AllowedRanges": [
      "10.0.0.0/8"
    ],
    "ExcludedEndpoints": [
      "/health"
    ]
  }
}
```

### 3. Kubernetes Cluster

Allow all pod IPs:

```json
{
  "IpWhitelist": {
    "Enabled": true,
    "AllowedRanges": [
      "10.244.0.0/16"
    ],
    "ExcludedEndpoints": [
      "/health",
      "/health/detailed"
    ]
  }
}
```

### 4. Development Environment

Allow everything (no whitelist):

```json
{
  "IpWhitelist": {
    "Enabled": false
  }
}
```

## CIDR Notation Examples

### Common Networks

```
10.0.0.0/8           - Entire 10.x.x.x network (16,777,216 IPs)
172.16.0.0/12        - Entire 172.16-31.x.x network (1,048,576 IPs)
192.168.0.0/16       - Entire 192.168.x.x network (65,536 IPs)

10.0.0.0/24          - 10.0.0.0-10.0.0.255 (256 IPs)
10.0.0.0/25          - 10.0.0.0-10.0.0.127 (128 IPs)
10.0.0.0/30          - 10.0.0.0-10.0.0.3 (4 IPs)
10.0.0.0/31          - 10.0.0.0-10.0.0.1 (2 IPs - /31 is point-to-point)
```

### Subnet Cheat Sheet

| Prefix | IPs | Network Class |
|--------|-----|---|
| /8 | 16,777,216 | Class A |
| /16 | 65,536 | Class B |
| /24 | 256 | Class C |
| /25 | 128 | Half C |
| /26 | 64 | Quarter C |
| /27 | 32 | Eighth C |
| /28 | 16 | Sixteenth C |
| /29 | 8 | Thirty-second C |
| /30 | 4 | Used for p-to-p links |
| /31 | 2 | Point-to-point |
| /32 | 1 | Single host |

## Reverse Proxy Headers

When running behind a reverse proxy (nginx, Apache, load balancer), ensure your proxy sets the correct headers:

### Nginx Example

```nginx
location / {
    proxy_pass http://app:5000;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-Proto $scheme;
}
```

### Apache Example

```apache
<VirtualHost *:80>
    ProxyPreserveHost On
    ProxyPass / http://app:5000/

    RequestHeader set X-Forwarded-For "%{REMOTE_ADDR}s"
    RequestHeader set X-Real-IP "%{REMOTE_ADDR}s"
</VirtualHost>
```

### Azure Application Gateway

X-Forwarded-For is automatically set by Azure App Gateway.

### AWS ALB/NLB

```
X-Forwarded-For header is automatically populated with the client IP
```

## Response Codes

### Access Granted
- **200 OK** - Request processed normally

### Access Denied
- **403 Forbidden** - IP address not in whitelist

```json
{
  "code": "access_denied",
  "message": "Access to this resource is not allowed",
  "error": "IP address is not in whitelist"
}
```

## Logging

All access control events are logged with appropriate levels:

### Info Level
- Whitelist initialization
- Localhost allowed
- Private networks allowed

### Debug Level
- Allowed IPs/ranges
- Successful access

### Warning Level
- Access denied
- IP not in whitelist

### Error Level
- Invalid configuration
- Parse errors

Example logs:

```
[INFO] IP Whitelist enabled with 2 IPs and 3 ranges
[INFO] Localhost (127.0.0.1, ::1) is allowed
[DEBUG] IP allowed (exact match): 203.0.113.10
[DEBUG] IP allowed (CIDR range): 10.0.1.5 in 10.0.0.0/8
[WARN] IP access denied: 203.0.113.99
```

## Endpoint Exclusion

Some endpoints should bypass IP whitelist checks (e.g., health checks for monitoring):

```json
{
  "IpWhitelist": {
    "ExcludedEndpoints": [
      "/health",
      "/health/detailed",
      "/metrics",
      "/swagger"
    ]
  }
}
```

These endpoints will be accessible from any IP address, regardless of whitelist settings.

## Docker Environment Variables

To override settings via environment variables:

```bash
docker run \
  -e "IpWhitelist__Enabled=true" \
  -e "IpWhitelist__AllowedIps__0=10.0.0.10" \
  -e "IpWhitelist__AllowedRanges__0=10.0.0.0/8" \
  mikrotik-updateserver
```

### Environment Variable Format

```
IpWhitelist__Enabled=true
IpWhitelist__AllowLocalhost=true
IpWhitelist__AllowPrivateNetworks=true
IpWhitelist__AllowedIps__0=203.0.113.10
IpWhitelist__AllowedIps__1=203.0.113.20
IpWhitelist__AllowedRanges__0=10.0.0.0/8
IpWhitelist__ExcludedEndpoints__0=/health
```

## Kubernetes ConfigMap Example

```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: app-config
data:
  appsettings.json: |
    {
      "IpWhitelist": {
        "Enabled": true,
        "AllowedRanges": [
          "10.244.0.0/16"
        ],
        "ExcludedEndpoints": [
          "/health",
          "/health/detailed"
        ]
      }
    }
---
apiVersion: v1
kind: Pod
metadata:
  name: app
spec:
  containers:
  - name: app
    image: mikrotik-updateserver:latest
    volumeMounts:
    - name: config
      mountPath: /app/config
  volumes:
  - name: config
    configMap:
      name: app-config
```

## Troubleshooting

### Issue: "IP address is not in whitelist" but IP is configured

**Causes:**
1. IP format mismatch (check for spaces, typos)
2. Port included in IP (should be stripped automatically)
3. Different IP address due to reverse proxy

**Solution:**
- Check logs for actual IP being checked
- Verify X-Forwarded-For header from proxy
- Use `/health` endpoint with curl -v to see request details

### Issue: Cannot access health check endpoint

**Cause:**
- ExcludedEndpoints may have different paths than expected

**Solution:**
- Check exact endpoint path
- Verify ExcludedEndpoints configuration
- Use leading slash: `/health` not `health`

### Issue: Whitelist not working (all IPs blocked)

**Causes:**
1. AllowLocalhost = false and no IPs configured
2. All IPs accidentally excluded

**Solution:**
- Enable AllowLocalhost for development: `"AllowLocalhost": true`
- Check AllowedIps and AllowedRanges configuration
- Verify Enabled = true

### Issue: IPv6 not working

**Solution:**
- Add IPv6 addresses in CIDR notation: `"::1/128"`
- Use full IPv6 address format
- Test with curl -6 for IPv6

## Performance Considerations

- IP checks are very fast (sub-millisecond)
- CIDR parsing is done once at startup
- No external calls or I/O during request processing
- Minimal memory overhead

## Security Best Practices

1. **Enable in Production**
   ```json
   "IpWhitelist": {
     "Enabled": true,
     "AllowPrivateNetworks": false,
     "AllowLocalhost": false
   }
   ```

2. **Disable Unnecessary Services**
   - Remove health endpoints from ExcludedEndpoints if not needed
   - Only include endpoints that require monitoring

3. **Audit Logging**
   - Monitor warning level logs for denied access attempts
   - Set up alerts for repeated access denials from unknown IPs

4. **Use CIDR Ranges**
   - Better than listing individual IPs
   - Easier to maintain
   - Supports dynamic IP allocation

5. **Document Access Requirements**
   ```json
   "IpWhitelist": {
     "AllowedIps": [
       "203.0.113.10",  // Production API Server
       "203.0.113.20"   // Backup API Server
     ],
     "AllowedRanges": [
       "10.0.0.0/8"     // Corporate Network
     ]
   }
   ```

## Testing

### Test with curl

```bash
# Test with allowed IP
curl -H "X-Forwarded-For: 10.0.0.10" http://localhost:5000/api/status

# Test with denied IP
curl -H "X-Forwarded-For: 203.0.113.99" http://localhost:5000/api/status
# Should return 403 Forbidden

# Test excluded endpoint (always allowed)
curl -H "X-Forwarded-For: 203.0.113.99" http://localhost:5000/health
# Should return 200 OK
```

### Test with PowerShell

```powershell
# Test with custom IP header
$headers = @{"X-Forwarded-For"="10.0.0.10"}
Invoke-WebRequest -Uri "http://localhost:5000/api/status" -Headers $headers

# Test denied access
$headers = @{"X-Forwarded-For"="203.0.113.99"}
Invoke-WebRequest -Uri "http://localhost:5000/api/status" -Headers $headers
# Should throw error with 403 status
```

## Integration with Other Security Features

IP Whitelist works alongside:
- **Rate Limiting**: Separate requests per endpoint limit
- **CORS**: Cross-origin requests must also pass IP check
- **Security Headers**: Applied to all responses (even 403)
- **Logging**: All denials are logged

## Disabling IP Whitelist

To disable whitelist temporarily or for development:

```json
{
  "IpWhitelist": {
    "Enabled": false
  }
}
```

Or via environment variable:

```bash
export IpWhitelist__Enabled=false
```

## Support

For issues or questions:
1. Check logs for specific IP being denied
2. Verify configuration matches your network setup
3. Test with `/health` endpoint (which can be excluded)
4. Check reverse proxy header forwarding
