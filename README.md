# SesPoc — SES email via EC2 instance IAM role

A minimal .NET 10 Web API that registers users (in-memory DB) and sends a welcome email through **Amazon SES**.
It authenticates to AWS **only through the EC2 instance profile (IAM role)**. There are no access keys in appsettings, environment variables or credential files.

## How credentials work

`Program.cs` builds the SES client explicitly with `InstanceProfileAWSCredentials`:

```csharp
new AmazonSimpleEmailServiceV2Client(new InstanceProfileAWSCredentials(), RegionEndpoint.GetBySystemName(region));
```

This **bypasses the SDK's default credential chain**, which would otherwise look at `AWS_ACCESS_KEY_ID`/`AWS_SECRET_ACCESS_KEY`, `~/.aws/credentials`, profiles and so on.
The SDK requests temporary role credentials from the Instance Metadata Service (IMDSv2 token first, `http://169.254.169.254`) and refreshes them automatically.

**Consequence:** off EC2 (for example on your dev machine), the SES call fails and `register` returns **502** with nothing saved. That is expected, and it proves no local credentials are used.

## API

| Method | Route | Description |
|---|---|---|
| POST | `/api/users/register` | Body `{ "name": "...", "email": "..." }`. Sends the welcome email first and saves the user only if SES accepts it. |
| GET  | `/api/users` | Lists users held in memory. |
| GET  | `/swagger` | Swagger UI, enabled in all environments. `/` redirects here. |

Responses from `register`:
- `201` created, with `emailSent: true`
- `400` validation error
- `409` email already registered
- `502` SES send failed; the user is **not** saved

The in-memory DB is cleared whenever the IIS app pool recycles.

## Configuration (`appsettings.json`)

```json
"Ses": {
  "Region": "us-east-1",
  "FromAddress": "no-reply@example.com"
}
```

These are the only settings, and neither is a secret. The app will not start if either is missing or invalid.

## AWS setup

1. **SES identity**: In the SES console for your region, verify the sender (`FromAddress`) as an email identity or verify its domain.
2. **SES sandbox**: A new account can only send *to verified addresses*. Verify each test recipient, or request production access.
3. **IAM policy**: Create a policy from [`deploy/iam-policy-ses-send.json`](deploy/iam-policy-ses-send.json) after replacing `<REGION>`, `<ACCOUNT_ID>` and the `ses:FromAddress` condition value.
4. **IAM role**: Create a role with the trust policy [`deploy/ec2-trust-policy.json`](deploy/ec2-trust-policy.json) (the console option "AWS service → EC2" does the same) and attach the policy from step 3.
5. **Attach to the instance**: In the EC2 console, open the instance and choose **Actions → Security → Modify IAM role**, then pick the role.
6. **Network**: The instance needs outbound HTTPS (443) to `email.<region>.amazonaws.com` through an internet/NAT gateway or an SES VPC endpoint. IMDS must be enabled. IMDSv2-required works at the default hop limit because IIS runs directly on the host.

## Build and publish (dev machine)

```powershell
.\deploy\publish.ps1          # outputs .\publish
.\deploy\publish.ps1 -Zip     # also creates publish.zip
```

This is a framework-dependent publish. The generated `web.config` uses the ASP.NET Core Module with in-process hosting.

## IIS on Windows EC2

1. Install IIS: `Install-WindowsFeature Web-Server -IncludeManagementTools`
2. Install the **ASP.NET Core 10 Hosting Bundle** from https://dotnet.microsoft.com/download/dotnet/10.0, then run `iisreset` (or `net stop was /y; net start w3svc`).
3. Copy the contents of `publish\` to the server, for example `C:\inetpub\SesPoc`.
4. In IIS Manager:
   - Create an **Application Pool** `SesPoc` with **.NET CLR version = No Managed Code**.
   - Create a **Site** (or an application under Default Web Site) pointing to `C:\inetpub\SesPoc` and using that pool.
5. Edit `C:\inetpub\SesPoc\appsettings.json` and set `Ses:Region` and `Ses:FromAddress`.
6. Open the Security Group / Windows Firewall for the site's port (for example 80).

## Verify

1. From the EC2 box, check that the role is visible to IMDS (IMDSv2):
   ```powershell
   $t = Invoke-RestMethod -Method PUT -Uri http://169.254.169.254/latest/api/token -Headers @{ "X-aws-ec2-metadata-token-ttl-seconds" = "60" }
   Invoke-RestMethod -Uri http://169.254.169.254/latest/meta-data/iam/security-credentials/ -Headers @{ "X-aws-ec2-metadata-token" = $t }
   ```
   This should print the role name.
2. Browse to `http://<ec2-host>/swagger` and `POST /api/users/register` with a verified recipient (in sandbox mode). Expect `201` and the email in the inbox.
3. `GET /api/users` should list the user.
4. Negative test (optional): detach the role or remove the policy, recycle the app pool, and register again. Expect `502` with nothing saved.

## Troubleshooting

- **HTTP 500.19 / 500.31**: The Hosting Bundle is missing or the wrong version. Reinstall it and run `iisreset`.
- **Startup errors**: In `web.config`, set `stdoutLogEnabled="true"` and `stdoutLogFile=".\logs\stdout"`, then create the `logs` folder and give the app pool identity (`IIS AppPool\SesPoc`) write access.
- **502 with a credentials or IMDS error**: No role is attached, IMDS is disabled, or the request is not running on EC2.
- **502 with `MessageRejected` / "Email address is not verified"**: The sender isn't verified, or the account is in the sandbox and the recipient isn't verified.
- **502 with `AccessDenied`**: The role policy is missing `ses:SendEmail`, or the region, account or `FromAddress` condition doesn't match.
