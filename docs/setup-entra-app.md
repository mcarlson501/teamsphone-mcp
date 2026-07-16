# Set up an Entra app for TeamsPhone MCP (app-only certificate auth)

TeamsPhone MCP connects to a tenant with an **app-only** Microsoft Entra application that
authenticates with a **certificate** (never a client secret). This guide walks through:

1. Registering the Entra app
2. Granting the Microsoft Teams admin permissions/role it needs
3. Creating a self-signed certificate **for local testing** (Windows *and* macOS/Linux)
4. Uploading the public certificate to the app
5. Mapping a `credentialRef` in local configuration
6. Running the server and calling a tool

> These steps use a **self-signed** certificate because the tenant is ready but no app
> certificate exists yet. Self-signed certs are fine for development and testing. For
> production, use a certificate issued by your organization's CA.

---

## 1. Register the Entra application

1. Sign in to the [Microsoft Entra admin center](https://entra.microsoft.com) as an
   administrator.
2. Go to **Identity → Applications → App registrations → New registration**.
3. Name it (for example, `teamsphone-mcp-dev`).
4. Under **Supported account types**, choose **Accounts in this organizational directory only**.
5. Leave **Redirect URI** empty (app-only flow needs none).
6. Select **Register**.
7. From the app **Overview**, copy the **Application (client) ID** and **Directory (tenant) ID** —
   you will need both for the `credentialRef` config.

---

## 2. Grant Microsoft Teams admin access

TeamsPhone MCP uses the `MicrosoftTeams` PowerShell module with `Connect-MicrosoftTeams`
in **application** mode. That requires both an API permission and a directory role.

### 2a. API permission

1. In the app, open **API permissions → Add a permission**.
2. Select **Microsoft Graph → Application permissions**.
3. Add the permissions your tools require. For read-only voice configuration
   (`get-user-voice-config`) the minimum is typically:
   - `User.Read.All`
   - `Organization.Read.All`
4. Select **Add permissions**, then **Grant admin consent for &lt;tenant&gt;**.

### 2b. Teams admin role

App-only Teams cmdlets require the app's service principal to hold a Teams admin role.

1. Go to **Identity → Roles & admins → Roles & admins**.
2. Open **Teams Communications Administrator** (or **Teams Administrator** for broader access).
3. Select **Add assignments**, search for your app by name, and assign it.

> Grant the least-privileged role that lets your tools run. Read-only voice tooling works
> with **Teams Communications Administrator**.

---

## 3. Create a self-signed certificate (for testing)

Create the certificate on the machine that will **run** the MCP server. The private key
stays local; only the public certificate is uploaded to Entra.

### Option A — Windows (PowerShell `New-SelfSignedCertificate`)

Run in an elevated PowerShell session:

```powershell
$cert = New-SelfSignedCertificate `
    -Subject "CN=teamsphone-mcp-dev" `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -KeyExportPolicy Exportable `
    -KeySpec Signature `
    -KeyLength 2048 `
    -KeyAlgorithm RSA `
    -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddYears(1)

# Note the thumbprint — this is your CertificateThumbprint config value.
$cert.Thumbprint

# Export the PUBLIC certificate (.cer) to upload to Entra.
Export-Certificate -Cert $cert -FilePath "$HOME\teamsphone-mcp-dev.cer" | Out-Null
```

The private key is now in the `CurrentUser\My` store. Use the printed **thumbprint** with
`CertificateStoreLocation: "CurrentUser"` in configuration (see step 5).

### Option B — macOS / Linux (`openssl`)

```bash
# 1. Generate a private key and a self-signed public certificate (valid 1 year).
openssl req -x509 -newkey rsa:2048 -sha256 -days 365 -nodes \
  -keyout teamsphone-mcp-dev.key \
  -out teamsphone-mcp-dev.cer \
  -subj "/CN=teamsphone-mcp-dev"

# 2. Bundle the key + cert into a password-protected PKCS#12 (.pfx) that the server loads.
#    You will be prompted for an export password — remember it for step 5.
openssl pkcs12 -export \
  -inkey teamsphone-mcp-dev.key \
  -in teamsphone-mcp-dev.cer \
  -out teamsphone-mcp-dev.pfx
```

- `teamsphone-mcp-dev.cer` — the **public** certificate to upload to Entra (step 4).
- `teamsphone-mcp-dev.pfx` — the private key bundle the server loads via `CertificatePath`.

> Store the `.pfx` outside source control and pass its password through an environment
> variable (never commit it). Delete the standalone `.key` once the `.pfx` is created.

---

## 4. Upload the public certificate to the app

1. In the app, open **Certificates & secrets → Certificates → Upload certificate**.
2. Select the `.cer` file from step 3 (Windows: exported file; macOS/Linux: `teamsphone-mcp-dev.cer`).
3. Confirm the uploaded certificate's thumbprint matches the value from step 3.

---

## 5. Map a `credentialRef` in local configuration

The server resolves a named `credentialRef` from the `Credentials` configuration section.
Add an entry to `appsettings.Development.json` (or user secrets / environment). Use **one**
of the two certificate sources below.

### 5a. Certificate from the OS store, by thumbprint (Windows, Option A)

```jsonc
{
  "Credentials": {
    "dev-tenant": {
      "TenantId": "<DIRECTORY_TENANT_ID>",
      "ClientId": "<APPLICATION_CLIENT_ID>",
      "CertificateThumbprint": "<CERT_THUMBPRINT>",
      "CertificateStoreLocation": "CurrentUser"
    }
  }
}
```

### 5b. Certificate from a `.pfx` file (macOS/Linux, Option B)

```jsonc
{
  "Credentials": {
    "dev-tenant": {
      "TenantId": "<DIRECTORY_TENANT_ID>",
      "ClientId": "<APPLICATION_CLIENT_ID>",
      "CertificatePath": "/absolute/path/to/teamsphone-mcp-dev.pfx",
      "CertificatePasswordEnvVar": "TEAMSPHONE_MCP_DEV_PFX_PASSWORD"
    }
  }
}
```

Then set the password environment variable (the value is read from the environment, never
from config):

```bash
export TEAMSPHONE_MCP_DEV_PFX_PASSWORD='<pfx-export-password>'
```

`dev-tenant` is the `credentialRef` you pass as a tool argument.

---

## 6. Run the server and call a tool

```bash
# stdio transport for local single-tenant use:
dotnet run --project src/TeamsPhoneMcp.Host -- --stdio
```

Call `get-user-voice-config` with the tenant id, the `credentialRef`, and a user UPN:

```jsonc
{
  "tenantId": "<DIRECTORY_TENANT_ID>",
  "credentialRef": "dev-tenant",
  "userUpn": "user@yourtenant.com"
}
```

A successful call returns a result envelope with the user's voice configuration; a
credential/auth problem fails closed with an `authenticationFailed` error and no secret or
credential reference in the client-facing message.

> For the full set of ways to test the server — automated tests, a local smoke test, and a
> gated live end-to-end call — see [testing.md](testing.md).

---

## Notes and troubleshooting

- **App-only, certificate-only.** The server never uses client secrets or interactive login.
- **Certificate expiry.** Self-signed test certs above are valid for one year. Recreate and
  re-upload before expiry.
- **Least privilege.** Grant only the Graph permissions and Teams role your tools need.
- **`credential does not belong to the requested tenant`.** The `TenantId` in the
  `credentialRef` entry must match the `tenantId` argument you pass to the tool.
- **`The tenant credential could not be resolved`.** Check the thumbprint/`.pfx` path, that
  the private key is present, and that the password environment variable is set.
