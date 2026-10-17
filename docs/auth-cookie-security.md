# Browser authentication cookies

Login and Google login set the HttpOnly `refresh_token` cookie. Refresh reads it without a JSON body and rotates it. These APIs return only `accessToken` and `expiresIn`. Send `credentials: "include"` from the frontend.

## Configuration

`Authentication:RefreshCookie:Secure` defaults to `true`. It may be `false` only in Development. `Authentication:RefreshCookie:SameSite` defaults to `Lax`; allowed values are `Lax`, `Strict`, and `None`. `None` requires Secure. Invalid combinations fail application startup.

HttpOnly is always enabled. Path stays `/api/auth`; Domain is omitted (host-only). Creation and deletion use the same options. Max-Age on issuance follows the refresh-token lifetime. Changing deployment cookie paths/domains requires an explicit migration of old cookies.

Production example: FE `https://app.example.com`, BE `https://api.example.com`, `Frontend__Url=https://app.example.com`, `Authentication__RefreshCookie__Secure=true`, `Authentication__RefreshCookie__SameSite=Lax`.

Different-site deployments need `SameSite=None` with HTTPS, but browser third-party-cookie restrictions may still block them. Prefer same-site deployment. For local HTTP testing only, use Development with Secure=false and SameSite=Lax; the existing HTTPS redirection configuration still applies. HTTPS local development can retain the defaults.

## Origin / CSRF contract

POST `/api/auth/login`, `/google`, `/refresh`, and `/logout` require exactly one Origin header matching the origin configured by `Frontend:Url` (also used for CORS). Browsers supply this header automatically. Matching includes scheme, hostname and port; suffixes, paths, multiple origins, missing Origin, and literal `null` are rejected. There is no Referer fallback or missing-Origin bypass.

Rejection returns HTTP 403 ProblemDetails with `code: CSRF_ORIGIN_DENIED` before invoking the endpoint handler, without issuing, rotating, revoking or clearing cookies. This is not a reason for FE to refresh or log out. CORS preflight OPTIONS remains handled by CORS.

Postman, scripts and other non-browser clients must explicitly send `Origin: <configured frontend origin>`, even with a Bearer token. Origin validation protects against browser CSRF; it is not client authentication and non-browser clients can set this header. Access/refresh credentials remain required as before. Swagger requests from the API origin are rejected unless that is the configured frontend origin; use Postman with the explicit header.

Register, email confirmation and authorization rules are unchanged. Refresh-token reuse detection is unchanged: clients must coordinate refresh across requests and tabs. A network failure after rotation is ambiguous; do not blindly retry with the old token.
