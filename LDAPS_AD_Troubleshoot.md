I'll take a quick look at the AD service code so I can explain the configuration precisely.

# Troubleshooting LDAPS for Active Directory

**What the app does** (see `Shared/Services/ADService.cs:377-400`): it performs a simple bind (`AuthType.Basic`) — using TLS when the URL is `ldaps://`, otherwise unencrypted. No other authentication methods are supported.

**What this means for observed errors:**

| Screenshot 3 | Error | Meaning |
|---|---|---|
| `ldap://` (389) | `Strong(er) authentication required (8)` — `LDAP signing requirement` | Your domain controller (DC) forbids simple binds over unencrypted port 389. This has been the AD default since the March 2020 hardening guidance. Port 389 would require SASL with signing/sealing — **the app does not support this**. |
| `ldaps://` (636) | `Can't contact LDAP server (-1)` | Either the DC has **no LDAPS certificate installed** (port 636 doesn't respond), or the certificate is self-signed and tools like `ldapsearch` reject it. |
| `ldap://` second attempt | `Invalid credentials (49) data 52e` | Wrong password (52e = `ERROR_LOGON_FAILURE`). In the first test the same command returned "Strong auth required" — the server didn't even validate credentials then, so it couldn't have "confirmed" the password. Re-type the password at the prompt exactly. |

The app error shown in Screenshot 2 has the same root cause as Screenshot 3 (second test): the app tries `ldaps://` but can't reach the server (missing cert or LDAPS endpoint) → `LDAP server is unavailable`.

# Steps to fix

**1. Verify whether LDAPS is running on the DC** (run on the AD host or any reachable host):
```bash
openssl s_client -connect ad.domain.org:636 -showcerts </dev/null
```
- Connection refused / timeout → LDAPS is not active on the DC. Issue a server authentication certificate on the DC (for example via AD CS) and reboot LSASS; then try again.
- A certificate is returned but it's reported as "self signed" → trust of the certificate/CA is the issue.

**2. Verify an LDAPS bind while skipping certificate validation:**
```bash
LDAPTLS_REQCERT=never ldapsearch -x -H ldaps://ad.domain.org:636 \
  -D "CN=user,OU=Users,DC=ad,DC=domain,DC=org" \
  -W -b "DC=ad,DC=domain,DC=org" "(sAMAccountName=user)"
```
- If this works → the Bind DN and password are correct; only the certificate is not trusted by the client.
- If you get `data 52e` again → compare the password stored in KeePass with the one in `.env` (`***`) and check the service account for lockout or expired password.
- Alternatively, try the bind DN as UPN: `-D "user@ad.domain.org"`.

**3. Check from inside the container** (container networking differs from the host):
```bash
docker exec -it <adreg-container> sh -c "getent hosts ad.domain.org; nc -zv ad.domain.org 636"
```
If the container doesn't resolve `ad.domain.org` or cannot reach port 636, no `.env` change will help — DNS/routing or a direct IP entry is required.

# Recommended `.env`

After you confirmed step 2 — the changes compared to your original are noted below:

```
AD__ServerType=ActiveDirectory
AD__LdapUrl=ldaps://ad.domain.org:636
AD__BindDn=CN=user,OU=Users,DC=ad,DC=domain,DC=org
AD__BindPassword=***
AD__SearchBase=DC=ad,DC=domain,DC=org
AD__UsersContainer=OU=Users
AD__GroupsContainer=OU=Groups
# The DC's certificate is not issued by a public CA → disable validation
# until the AD-CA root certificate is available in the container truststore.
AD__AllowInvalidCertificate=true
```

A cleaner option (instead of `AllowInvalidCertificate=true`) is to add the issuing Root-CA as a `.crt` into the image (`/usr/local/share/ca-certificates/`) and run `update-ca-certificates` in the Dockerfile — then set `AllowInvalidCertificate=false`.

If step 1 shows that the DC has no LDAPS cert at all, that's the real blocker — the app cannot authenticate against a hardened AD without LDAPS.


# Certificate handling inside the container

You do not have to run `update-ca-certificates`. On Linux the `LdapConnection` from `System.DirectoryServices.Protocols` uses **libldap (OpenLDAP)** under the hood — libldap reads TLS truststore paths from environment variables. That means a mounted certificate plus two environment variables is sufficient, without changing the Dockerfile or running as root.

# Option A (recommended): mount the CA cert and set `LDAPTLS_CACERT`

**1. Export the AD CA certificate** (on the domain controller, PowerShell as Administrator):
```powershell
Get-ChildItem Cert:\LocalMachine\Root |
  Where-Object Subject -like "*domain*" |
  Export-Certificate -FilePath C:\ad-ca.crt -Type CERT
```
Or extract the certificate chain from the host with `openssl s_client`:
```bash
openssl s_client -connect ad.domain.org:636 -showcerts </dev/null 2>/dev/null \
  | sed -n '/BEGIN CERTIFICATE/,/END CERTIFICATE/p' > /root/adreg/certs/ad-ca.crt
```
The latter yields the whole chain; that is fine — libldap accepts a bundle in one file.

**2. Adjust `compose.yml` (for both services):**

```yaml
services:

  wekode.adreag.admin:
    image: ${IMAGE_ADMIN}
    build:
      context: ./AdminApp
      dockerfile: AdminApp/Dockerfile
      args:
       - UID=${UID}
       - GID=${GID}
       - DOT_NET_BUILD_CONFIG=Release
    container_name: wekode.adreag.admin
    restart: unless-stopped
    user: ${UID}:${GID}
    ports:
      - "${PORT_ADMIN}:8080"
    env_file:
      - .env
    environment:
      - LDAPTLS_CACERT=/certs/ad-ca.crt
      - LDAPTLS_REQCERT=demand
    volumes:
      - ./data:/data
      - ./certs/ad-ca.crt:/certs/ad-ca.crt:ro
      - /etc/localtime:/etc/localtime:ro
    networks:
      - internal

  wekode.adreag:
    image: ${IMAGE_REGISTER}
    build:
      context: ./RegistrationApp
    container_name: wekode.adreag
    restart: unless-stopped
    ports:
      - "${PORT_REGISTER}:8080"
    env_file:
      - .env
    environment:
      - LDAPTLS_CACERT=/certs/ad-ca.crt
      - LDAPTLS_REQCERT=demand
    volumes:
      - ./data:/data
      - ./certs/ad-ca.crt:/certs/ad-ca.crt:ro
      - /etc/localtime:/etc/localtime:ro
    networks:
      - internal

networks:
  internal:
    driver: bridge
```

**3. `.env`:**
```
AD__AllowInvalidCertificate=false
```
You now validate certificates properly — `AllowInvalidCertificate=true` is no longer needed.

**4. Verify** (inside the container as the app user is possible):
```bash
docker exec -it wekode.adreag.admin sh -c \
 'LDAPTLS_CACERT=/certs/ad-ca.crt ldapsearch -x -H ldaps://ad.domain.org:636 \
  -D "CN=user,OU=Users,DC=ad,DC=domain,DC=org" \
  -w "$AD__BindPassword" -b "DC=ad,DC=domain,DC=org" "(sAMAccountName=user)"'
```
(If `ldapsearch` isn't present in the image: a simple `openssl s_client -connect ad.domain.de:636 -CAfile /certs/ad-ca.crt` already shows `Verify return code: 0 (ok)` — that's sufficient proof.)

# Option B (fallback): run `update-ca-certificates` at container startup via an entrypoint

If libldap in your image ignores the environment variables for any reason, you can start the container as root, install the cert into the system store and then drop privileges to the app user — done via a mounted entrypoint. **Note:** remove `user: 1001:1001` in this case, otherwise the container starts as the app user and cannot run `update-ca-certificates`.

**1. Create a script**: `./entrypoints/inject-ca.sh`
```bash
#!/bin/sh
set -e
cp /certs/ad-ca.crt /usr/local/share/ca-certificates/ad-ca.crt
update-ca-certificates >/dev/null 2>&1 || true
exec setpriv --reuid="${APP_UID}" --regid="${APP_GID}" --init-groups \
     dotnet /app/AdminApp.dll
```
(In the RegistrationApp container use `dotnet /app/RegistrationApp.dll` — adjust the path to match the image's `WORKDIR`/`ENTRYPOINT`.)

`setpriv` exists in Debian-based .NET images; alternatively use `su -s /bin/sh -c "..." appuser` if available.

**2. `compose.yml`** (Admin example, Register analogous):
```yaml
  wekode.adreag.admin:
    image: ${IMAGE_ADMIN}
    container_name: wekode.adreag.admin
    restart: unless-stopped
    # user: removed — container must start as root
    entrypoint: ["/usr/local/bin/inject-ca.sh"]
    environment:
      - APP_UID=${UID}
      - APP_GID=${GID}
    ports:
      - "${PORT_ADMIN}:8080"
    env_file:
      - .env
    volumes:
      - ./data:/data
      - ./certs/ad-ca.crt:/certs/ad-ca.crt:ro
      - ./entrypoints/inject-ca.sh:/usr/local/bin/inject-ca.sh:ro
      - /etc/localtime:/etc/localtime:ro
    networks:
      - internal
```

# Which option to choose?

Choose **Option A**. It is:
- **non-root** — you don't need to remove the `user:` directive, and `setpriv` is unnecessary
- **idempotent** — no init script repeatedly writing into the image on each restart
- **explicit** — the certificate path is declared in `compose.yml` instead of hidden in the rootfs
- **compatible with read-only filesystem hardening** if you enable that later

Use Option B only if your .NET library absolutely ignores libldap environment variables — unlikely, but it's a fallback plan.