# Selenium Grid Swiss Knife

The application (also referred as SSK - Selenium Swiss Knife) is indended to fill various gaps in Selenium/WebDriver ecosystem and currently it can:

  1. forwarding requests to multiple grids with a main purpose to switch trafic from one instance to another
  2. split the trafic between multiple consumers and enforce per-host and per-user quotas.

## Configuration

Here the example of basic configuration:

  ```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Warning"
    }
  },
  "AllowedHosts": "*",
  "App": {
    "Hosts": [
      {
        "HostUri": "http://192.168.1.132:4444",
        "Limit": 30
      }
    ],
    "Users": [
      {
        "Name": "anonymous",
        "MaxSessions": 2
      },
      {
        "Name": "admin",
        "Secret": "pwd123",
        "MaxSessions": 100
      },
      {
        "Name": "app",
        "Secret": "stryker",
        "MaxSessions": 5
      }
    ]
  }
}
  ```

Here we define single host with a maximal sessions limit set to **30**. And a few "applications".

### User quotas

The first entry in "Users" list defines global quota for non-authorized apps. In this sample all such apps will share just two sessions.
Subsequent records define two more users with higher quotas.

> Total sum of all quotas can exceed the host quota. In this case both limits will be enforced.

### Authentication

Basic authentication is currently implemented, see the sample below on how to pass user/password.
```bash
SELENIUM_REMOTE_URL=http://admin:pwd123@localhost:5099 yarn ctest --workers 20
```

## Endpoints

### /stats

Provides basic stats about SSK status - user and host stats.

```json
{
    "quota": {
        "users": [
            {
                "name": "anonymous",
                "activeSessions": 2,
                "maxSessions": 2,
                "waitingInQueue": 8
            },
            {
                "name": "admin",
                "activeSessions": 0,
                "maxSessions": 100,
                "waitingInQueue": 0
            },
            {
                "name": "ar",
                "activeSessions": 0,
                "maxSessions": 5,
                "waitingInQueue": 0
            }
        ]
    },
    "workers": {
        "available": 30,
        "waitingForJob": 28
    }
}
```

### /status, /ui/

SSK simply forwards these two endpoints to expose basic info about SeleniumGrid health.