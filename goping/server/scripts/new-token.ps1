# Issues a new one-time GoPing server token via the admin API.

Usage (from the server folder):

```powershell
.\scripts\new-token.ps1
.\scripts\new-token.ps1 -BaseUrl http://localhost:8080 -AdminSecret <secret> -Label "Alice"
```

Options:

- `-BaseUrl` — server base URL (default `http://localhost:8080`)
- `-AdminSecret` — admin secret from `.env` (default: interactive prompt)
- `-Label` — token label (optional, shown in admin list)

The issued token looks like `GOPING-Ab3_x-9Qw2Z0` and is valid for ONE
activation per server lifetime (the first `/api/v1/auth/token` call binds it
to a new user; afterwards it opens that same user). Copy the printed value
now — it is shown only once.

Requires `ADMIN_SECRET` to be set on the server.
