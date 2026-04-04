![GitHub License](https://img.shields.io/github/license/we-kode/adreg?style=for-the-badge&link=https%3A%2F%2Fgithub.com%2Fwe-kode%2Fadreg%3Ftab%3DGPL-3.0-1-ov-file%23readme)
![GitHub Release](https://img.shields.io/github/v/release/we-kode/adreg?style=for-the-badge&include_prereleases&display_name=tag&link=https%3A%2F%2Fgithub.com%2Fwe-kode%2Fadreg%2Freleases)
![GitHub Actions Workflow Status](https://img.shields.io/github/actions/workflow/status/we-kode/adreg/docker-compose?branch=master&style=for-the-badge)

# adreg
A project to register user via registration link to midpoint. Allows to invite and add AD user via registration link from web app.

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

### Step 1: Setup [midpoint](https://docs.evolveum.com/midpoint)

1. First you need to create the following folders on the host:
```
mkdir -p midpoint/home midpoint/data midpoint/secrets 
```

2. Create a database user and password file
```
echo "user" > midpoint/secrets/db_user
echo "password" > midpoint/secrets/db_password
```

3. Startup midpoint and configure a user which you will use to call the api from within the admin app. Midpoint will run on the port 8085 on the host by default.

### Step 2: Setup the .env
1. Configure all required variables for the mail smtp server and the midpoint server
2. Optional and default values you can update on your free will
3. Startup the apps. The admin app run default on the port 8081 and the registration app on the port 8082
