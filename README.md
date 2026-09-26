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

The steps below use the AWS CLI from your dev machine; every one has a console equivalent.
Names used throughout: role `SesPocEc2Role`, policy `SesPocSesSend`, instance profile `SesPocEc2Profile`.

### 1. Verify the SES identity

Verify the sender (`FromAddress`) as an email identity, or verify its domain:

```powershell
aws sesv2 create-email-identity --email-identity no-reply@example.com --region us-east-1
```

AWS emails a confirmation link to that address; click it before sending. For a domain identity, publish the returned DKIM CNAME records instead.

**SES sandbox**: a new account can only send *to verified addresses*, at a low daily rate. Verify each test recipient the same way, or open a production-access request from the SES console (**Account dashboard → Request production access**).

### 2. Create the IAM policy

Edit [`deploy/iam-policy-ses-send.json`](deploy/iam-policy-ses-send.json) and replace `<REGION>`, `<ACCOUNT_ID>` and the `ses:FromAddress` condition value with your own, then:

```powershell
aws iam create-policy --policy-name SesPocSesSend --policy-document file://deploy/iam-policy-ses-send.json
```

The condition pins the policy to one sender address, so a compromised instance cannot send as anyone else.

### 3. Create the role and instance profile

```powershell
aws iam create-role --role-name SesPocEc2Role --assume-role-policy-document file://deploy/ec2-trust-policy.json
aws iam attach-role-policy --role-name SesPocEc2Role --policy-arn arn:aws:iam::<ACCOUNT_ID>:policy/SesPocSesSend
aws iam create-instance-profile --instance-profile-name SesPocEc2Profile
aws iam add-role-to-instance-profile --instance-profile-name SesPocEc2Profile --role-name SesPocEc2Role
```

The console shortcut **Create role → AWS service → EC2** applies the same trust policy and creates the matching instance profile for you; via the CLI both are separate calls.

### 4. Launch (or pick) the Windows EC2 instance

A Windows Server 2022 Base AMI on `t3.small` or larger is enough for IIS plus this API. It needs:

- a **Security Group** allowing inbound RDP (3389) from your IP and HTTP (80) from wherever you will test;
- a subnet with outbound internet — an internet gateway plus public IP, or a NAT gateway;
- **IMDS enabled**. IMDSv2-required is fine at the default hop limit of 1, because IIS runs directly on the host rather than in a container.

### 5. Attach the instance profile

```powershell
aws ec2 associate-iam-instance-profile --instance-id i-0123456789abcdef0 --iam-instance-profile Name=SesPocEc2Profile
```

In the console this is **Actions → Security → Modify IAM role** on the instance. The change takes effect within a minute or so; no reboot is needed, but recycle the IIS app pool so the SDK picks up fresh credentials.

### 6. Confirm network reachability

From the instance, SES must be reachable on HTTPS (443):

```powershell
Test-NetConnection email.us-east-1.amazonaws.com -Port 443
```

If the instance has no route to the internet, add an **SES VPC endpoint** (`com.amazonaws.<region>.email-smtp` for SMTP, or use a NAT gateway for the API endpoint) instead of opening outbound internet access.

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

## Cleaning up AWS resources

Tear down in this order — IAM refuses to delete a role or policy that is still attached to something.

1. **Detach the instance profile from the instance** (find the association id first):
   ```powershell
   aws ec2 describe-iam-instance-profile-associations --filters Name=instance-id,Values=i-0123456789abcdef0
   aws ec2 disassociate-iam-instance-profile --association-id iip-assoc-0123456789abcdef0
   ```
2. **Terminate the instance** (or keep it if you only wanted to remove SES access):
   ```powershell
   aws ec2 terminate-instances --instance-ids i-0123456789abcdef0
   ```
   The root EBS volume goes with it only if **Delete on termination** was left at its default `true`; check **Storage** on the instance beforehand and delete any leftover volume. Release any Elastic IP you allocated (`aws ec2 release-address`), or it keeps billing while unattached.
3. **Delete the instance profile and role**:
   ```powershell
   aws iam remove-role-from-instance-profile --instance-profile-name SesPocEc2Profile --role-name SesPocEc2Role
   aws iam delete-instance-profile --instance-profile-name SesPocEc2Profile
   aws iam detach-role-policy --role-name SesPocEc2Role --policy-arn arn:aws:iam::<ACCOUNT_ID>:policy/SesPocSesSend
   aws iam delete-role --role-name SesPocEc2Role
   ```
4. **Delete the policy**:
   ```powershell
   aws iam delete-policy --policy-arn arn:aws:iam::<ACCOUNT_ID>:policy/SesPocSesSend
   ```
   If this fails with `DeleteConflict`, something is still attached — list it with `aws iam list-entities-for-policy --policy-arn ...`.
5. **Delete the SES identities** you verified for the POC:
   ```powershell
   aws sesv2 list-email-identities --region us-east-1
   aws sesv2 delete-email-identity --email-identity no-reply@example.com --region us-east-1
   ```
   Do this per region — SES identities are regional, and verifying in the wrong region is a common cause of `MessageRejected`. If you verified a domain, also remove the DKIM CNAME records from DNS.
6. **Delete the Security Group** once the instance is fully terminated (a group in use cannot be deleted):
   ```powershell
   aws ec2 delete-security-group --group-id sg-0123456789abcdef0
   ```
7. **Check for stragglers**: CloudWatch log groups, snapshots, and a key pair (`aws ec2 delete-key-pair`) if you created one only for this POC.

Nothing here leaves state on your dev machine — the app stores no credentials, and the in-memory DB disappears with the process. If SES production access was granted, it stays on the account; that is an account-level setting, not a resource, and there is no cost to leaving it.

## Troubleshooting

- **HTTP 500.19 / 500.31**: The Hosting Bundle is missing or the wrong version. Reinstall it and run `iisreset`.
- **Startup errors**: In `web.config`, set `stdoutLogEnabled="true"` and `stdoutLogFile=".\logs\stdout"`, then create the `logs` folder and give the app pool identity (`IIS AppPool\SesPoc`) write access.
- **502 with a credentials or IMDS error**: No role is attached, IMDS is disabled, or the request is not running on EC2.
- **502 with `MessageRejected` / "Email address is not verified"**: The sender isn't verified, or the account is in the sandbox and the recipient isn't verified.
- **502 with `AccessDenied`**: The role policy is missing `ses:SendEmail`, or the region, account or `FromAddress` condition doesn't match.
