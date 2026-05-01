![GitHub License](https://img.shields.io/github/license/we-kode/adreg?style=for-the-badge&link=https%3A%2F%2Fgithub.com%2Fwe-kode%2Fadreg%3Ftab%3DGPL-3.0-1-ov-file%23readme)
![GitHub Release](https://img.shields.io/github/v/release/we-kode/adreg?style=for-the-badge&include_prereleases&display_name=tag&link=https%3A%2F%2Fgithub.com%2Fwe-kode%2Fadreg%2Freleases)
![GitHub Actions Workflow Status](https://img.shields.io/github/actions/workflow/status/we-kode/adreg/docker-compose.yml?branch=master&style=for-the-badge)

# adreg
A project to register users via a registration link directly into Active Directory. Allows an admin to invite and add AD users via a registration link from the web app.

## Project structure

The Project consists two projects: 
1. **Admin-APP**: Allows one admin to generate an invitation link and send it to a user via mail. An admin can approve and reject users who registered via link. **This app should only be available in the internal network and not be exposed to the internal or any external network.**
2. **Registration-APP**: One user can register himself via a link generated via the admin app. This app is designed to be exposed to the net.

## Requirements

- A computer, vm or server which can run docker.
- Docker and docker compose set up.
- A reverse proxy to use TLS connection to the app. The app does not provide any possibility to run with tls certificates. It is designed to run behind a reverse proxy.
- Firewall rules to allow the registration app to be available from outside the network, if you want to allow users to register to your AD via internet.

## Setup

Download the `compose.yml` and the `.env`file, configure the variables inside the `.env` file and run `docker compose up -d` to start.

### Step 0: Download `compose.yml` and `.env` to a host folder.

```
curl -O https://raw.githubusercontent.com/we-kode/adreg/master/compose.yml
curl -O https://raw.githubusercontent.com/we-kode/adreg/master/.env
```

### Step 1: Configure Active Directory (LDAP / AD)

This project integrates directly with Active Directory / LDAP. The Admin app will use the configured LDAP connection to create users and add them to groups.

Important notes:

- Setting `unicodePwd` (password) typically requires LDAPS (TLS, port 636) and a certificate trusted by the client.
- The bind account (Service Account) must have permission to create users in the target OU and to modify group membership (`member` attribute).
- `SearchBase` is the base DN for searches (e.g. `DC=example,DC=local`).
- `UsersContainer` can be an OU relative to `SearchBase` (e.g. `OU=Users`) or a full DN (e.g. `OU=Users,DC=example,DC=local`).

Example `.env` configuration for Windows Active Directory (replace with your values):

```
# Active Directory / LDAP settings (Beispiel für Windows AD mit LDAPS)
AD__LdapUrl=ldaps://dc01.example.local:636
AD__BindDn=CN=adregsvc,OU=Service Accounts,DC=example,DC=local
AD__BindPassword=VerySecretPassword123!
AD__SearchBase=DC=example,DC=local
AD__UsersContainer=OU=Users
```

Felder erklärt:

- `AD__LdapUrl`: LDAPS-URL oder Host:Port. Beispiel: `ldaps://dc01.example.local:636` oder `ad.example.local:389` (LDAP, unsicher).
- `AD__BindDn`: Distinguished Name des Service-Kontos, z. B. `CN=adregsvc,OU=Service Accounts,DC=example,DC=local`.
- `AD__BindPassword`: Passwort des Service-Kontos.
- `AD__SearchBase`: Basis-DN für Suche (Root der Domäne), z. B. `DC=example,DC=local`.
- `AD__UsersContainer`: OU oder Container für neue Benutzer (relativ zum `SearchBase` oder absolute DN).

PowerShell-Beispiele (auf einem Rechner mit RSAT / Zugriff auf AD):

- Basis-DN der Domäne ermitteln:
```
Get-ADDomain | Select-Object DistinguishedName
```
- DN eines Users / Service-Accounts:
```
Get-ADUser -Identity adregsvc | Select-Object DistinguishedName
```
- Liste der Gruppen (Name + DN):
```
Get-ADGroup -Filter * | Select-Object Name, DistinguishedName
```

Benutzerrechte (minimal):

- Auf Ziel-OU: `Create Child Objects` (bzw. Create User).
- Auf Gruppen: Recht `Write Members` oder die Berechtigung, das `member`-Attribut zu ändern.
- Falls Passwörter per LDAPS gesetzt werden, muss LDAPS auf dem DC korrekt konfiguriert sein (Zertifikat usw.).

LDAPS / Zertifikate:

- Für `ldaps://` benötigt der Domain Controller ein Zertifikat, das vom Client vertraut wird (z. B. von interner CA).
- In Testumgebungen ist ein selbstsigniertes Zertifikat möglich, der Client muss dieses Zertifikat allerdings als vertrauenswürdig einrichten.

Wie `ADService` Gruppenwerte akzeptiert:

- Akzeptierte Werte: vollständige DN der Gruppe (z. B. `CN=IT-Helpdesk,OU=Groups,DC=example,DC=local`) oder nur der `cn`-Wert (z. B. `IT-Helpdesk`).
- Wenn kein `=` in der Gruppenangabe vorhanden ist, sucht `ADService` per `cn`.

Fehlerbehandlung / Fallback:

- Wenn `unicodePwd` nicht gesetzt werden kann (kein LDAPS), wird der Account trotzdem erstellt, das Passwort-Setzen schlägt fehl und wird geloggt. Setze das Passwort dann manuell oder per Skript.

Kurz-Checkliste vor Inbetriebnahme:
1. LDAPS auf dem DC testen (z. B. `openssl s_client -connect dc01.example.local:636`).
2. Bind-Account-DN + Passwort verifizieren.
3. `SearchBase` prüfen (Domänen-DN).
4. `UsersContainer` existiert und der Bind-Account kann dort Benutzer anlegen.
5. Gruppen im AD sind sichtbar / durchsuchbar per `cn`.

### Step 2: Setup the `.env`
1. Configure all required variables for the mail SMTP server and Active Directory (see example above).
2. Optional and default values you can update as you prefer.
3. Startup the apps. The admin app runs by default on port 8081 and the registration app on port 8082.

```
