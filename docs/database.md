# Server database

Aerochat Server stores durable account and chat history data in SQLite through EF Core.
The database is migrated automatically during server startup before HTTP endpoints are
served.

## Location and configuration

By default the server creates a per-user database at:

```text
%LOCALAPPDATA%\Aerochat\server.db
```

Override the location with the `Chat` connection string. ASP.NET Core configuration
supports environment variables and command-line configuration, for example:

```text
ConnectionStrings__Chat=Data Source=C:\AerochatData\server.db
```

or:

```text
dotnet run --project Aerochat.Server -- --ConnectionStrings:Chat "Data Source=C:\AerochatData\server.db"
```

The server forces SQLite foreign-key enforcement on for both the default and configured
connection strings. The parent directory of the default path is created automatically;
create and secure a custom database directory as part of deployment.

## Schema

The initial migration creates:

- `users`: provider-backed identities, with a unique `(provider, provider_user_id)` key.
- `conversations`: direct, group, and server-channel conversation metadata.
- `participants`: conversation membership with a composite primary key.
- `messages`: message content and edit/delete timestamps, indexed by conversation and
  descending creation time.

Deleting a conversation cascades to its participants and messages. Deleting a user is
restricted while authored messages exist. OAuth upserts preserve a user's local ID and
`created_at` while updating profile fields and `updated_at`.

## Development and operations

Use a separate configured database for tests and local smoke runs; do not point those
runs at the default per-user file. To inspect the applied schema with SQLite:

```text
sqlite3 C:\path\to\server.db ".tables"
sqlite3 C:\path\to\server.db "PRAGMA foreign_keys;"
```

The expected foreign-key pragma value is `1`. EF migration files are checked into
`Aerochat.Server/Data/Migrations`; schema changes should add a new migration rather
than editing an already deployed migration.

For the current tool-independent workflow, add a timestamped `Migration` class and
update `ChatDbModelSnapshot.cs` in the same change, then run the real-SQLite migration
tests. Contributors who have `dotnet-ef` installed may generate those files normally,
but the server build and runtime never depend on that global tool. Every production
startup calls `Database.Migrate()` and fails before serving HTTP if migration fails.

## Backups

Stop the server before copying `server.db`, or use SQLite's online backup API. Copying
only the main file while WAL mode is active can produce an incomplete backup; include
the matching `-wal` and `-shm` files when an offline stop is impossible. Database files
and backups contain account/profile data and message history and must remain outside
git with deployment secrets.

## Source and test map

- `Aerochat.Server/Data/ChatDb.cs`: fluent schema and value conversions.
- `Aerochat.Server/Data/Entities/`: persisted entities and relationships.
- `Aerochat.Server/Data/Migrations/`: checked-in migration and model snapshot.
- `Aerochat.Server/Auth/OAuth/EfExternalUserStore.cs`: durable OAuth identity upsert.
- `Aerochat.Server/Program.cs`: connection selection, DI lifetimes, and startup migrate.
- `Aerochat.Server.Tests/ChatDbTests.cs`: real-SQLite schema/constraint/relationship tests.
- `Aerochat.Server.Tests/ExternalUserStoreTests.cs` and related partials: in-memory,
  durable restart, and unique-race store behavior.
